# ============================================================================
#  pack-release.ps1 - 生成**可复现**的发布 zip，并自带完整性断言
#
#  与 tools\pack.ps1 的分工：
#    pack.ps1        面向"本机出一套完整安装包"，默认把 1 GB 模型、CUDA 运行库、
#                    Inno Setup 安装包都带上，产物 1.6 GB 级，适合对外发行大包。
#    pack-release.ps1（本脚本）面向"可复现 + 可校验"的发布 zip，只做一件事：
#                    dotnet publish → 汇总运行环境 → 打 zip → 逐项断言。
#                    模型默认不入包（-IncludeModel），产物 150 MB 级，便于上传/回归。
#
#  用法：
#    pwsh -File tools\pack-release.ps1
#        自包含 win-x64、含 modules\、不含模型 → 目标是"干净机器解压即跑"
#
#    pwsh -File tools\pack-release.ps1 -SelfContained:$false
#        框架依赖包（约 10 MB），目标机须自装 .NET 8 桌面运行时
#
#    pwsh -File tools\pack-release.ps1 -Runtime win-arm64
#        换目标架构（需要能拉取 win-arm64 的运行时包）
#
#    pwsh -File tools\pack-release.ps1 -IncludeModel:$true
#        把 models\ 一起打进包（1 GB 起，慎用）
#
#    pwsh -File tools\pack-release.ps1 -OutDir D:\release
#        输出到别处（默认 <仓库>\dist）
#
#  注意：PowerShell 5.1 的 `-File` 模式**传不了布尔值**（-SelfContained:$false 会被
#        当成字符串，绑 [bool] 直接报错）。要传 false 请用 `pwsh -Command` 调用，
#        或改用本脚本另给的不带值开关：-FrameworkOnly / -WithModel / -NoModules。
#
#  产物：
#    <OutDir>\AICompanion-<版本>-<runtime>-<selfcontained|framework>\     分发目录
#    <OutDir>\AICompanion-<版本>-<runtime>-<selfcontained|framework>.zip  发布包
#
#  退出码：0 = 全部断言通过；非 0 = 缺东西/发布失败（错误信息为中文）
# ============================================================================

[CmdletBinding()]
param(
    # 目标架构。win-arm64 需要能取到对应运行时包（联网或已缓存）。
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64',

    # 是否自包含 .NET 8 运行时。true = 目标机免装运行时（约 150 MB）；false = 需装（约 10 MB）。
    [bool]$SelfContained = $true,

    # 是否把 models\ 打进包。默认 false：LLM 模型 1 GB 起，塞进 zip 既慢又不便于分发。
    [bool]$IncludeModel = $false,

    # 是否把 modules\（llm-cuda / llm-vulkan 等）打进包。默认 true：缺了就没有对话能力。
    [bool]$IncludeModules = $true,

    # 输出目录。留空 = <仓库>\dist。相对路径按仓库根解析。
    [string]$OutDir = '',

    # ---- 下面是给 PowerShell 5.1 `-File` 模式准备的不带值开关（覆盖上面的布尔参数）----
    [switch]$FrameworkOnly,   # 等价于 -SelfContained:$false
    [switch]$WithModel,       # 等价于 -IncludeModel:$true
    [switch]$NoModules,       # 等价于 -IncludeModules:$false
    [switch]$NoVcRuntime,     # 不把 MSVC 运行库（msvcp140/vcruntime140）打进包
    [switch]$NoSlim           # 不删 .pdb、不裁语言目录
)

$ErrorActionPreference = 'Stop'

if ($FrameworkOnly) { $SelfContained = $false }
if ($WithModel) { $IncludeModel = $true }
if ($NoModules) { $IncludeModules = $false }

# 让中文在 PowerShell 5.1 的控制台里不变成乱码（不改则 GBK 控制台会吞字）
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

# System.IO.Compression：Windows PowerShell 5.1 默认不加载这两个程序集。
#   System.IO.Compression.dll            → ZipArchive / ZipArchiveMode / CompressionLevel
#   System.IO.Compression.FileSystem.dll → ZipFile
# 少加载任何一个，后面都会报 "Unable to find type [System.IO.Compression.ZipArchiveMode]"。
try { Add-Type -AssemblyName System.IO.Compression -ErrorAction Stop } catch { }
try { Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction Stop } catch { }

# ---------------------------------------------------------------- 工具函数

function Step([string]$text) {
    Write-Host ''
    Write-Host "==> $text" -ForegroundColor Cyan
}

function Note([string]$text) { Write-Host "    $text" }
function Good([string]$text) { Write-Host "    $text" -ForegroundColor Green }
function Warn([string]$text) { Write-Host "    ! $text" -ForegroundColor Yellow }

# 把字节数格式化成人类可读
function Get-SizeText([long]$bytes) {
    if ($bytes -ge 1GB) { return ('{0:N2} GB' -f ($bytes / 1GB)) }
    if ($bytes -ge 1MB) { return ('{0:N1} MB' -f ($bytes / 1MB)) }
    return ('{0:N0} KB' -f ($bytes / 1KB))
}

function Get-DirBytes([string]$path) {
    if (-not (Test-Path $path)) { return [long]0 }
    $sum = (Get-ChildItem $path -Recurse -File -ErrorAction SilentlyContinue | Measure-Object Length -Sum).Sum
    if ($null -eq $sum) { return [long]0 }
    return [long]$sum
}

# 为什么不用 Compress-Archive：
#   实测 Windows PowerShell 5.1 的 Compress-Archive 把条目名写成反斜杠
#   （bin\ai_asr.dll、modules\llm-cuda\llama-server.exe），而 ZIP 规范里路径分隔符
#   只有正斜杠。Windows 资源管理器能忍，7-Zip / macOS / Linux 的 unzip 不能——
#   它们会把整个 "bin\ai_asr.dll" 当成一个文件名，解压出来是一堆散落的怪文件。
#   这里手工建条目强制用 /，并按相对路径排序，保证清单稳定可比对。
function New-ReleaseZip([string]$SourceDir, [string]$TargetZip) {
    $src = (Resolve-Path -LiteralPath $SourceDir).Path.TrimEnd('\', '/')
    if (Test-Path -LiteralPath $TargetZip) { Remove-Item -LiteralPath $TargetZip -Force }

    $files = @(Get-ChildItem -LiteralPath $src -Recurse -File | Sort-Object FullName)
    if ($files.Count -eq 0) { throw "要压缩的目录是空的：$src" }

    $fs = [System.IO.File]::Open($TargetZip, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::ReadWrite)
    try {
        $archive = New-Object System.IO.Compression.ZipArchive($fs, [System.IO.Compression.ZipArchiveMode]::Create)
        try {
            foreach ($f in $files) {
                $rel = $f.FullName.Substring($src.Length + 1).Replace('\', '/')
                $entry = $archive.CreateEntry($rel, [System.IO.Compression.CompressionLevel]::Optimal)
                $out = $entry.Open()
                try {
                    $in = [System.IO.File]::OpenRead($f.FullName)
                    try { $in.CopyTo($out) } finally { $in.Dispose() }
                } finally { $out.Dispose() }
            }
        } finally { $archive.Dispose() }
    } finally { $fs.Dispose() }

    return $files.Count
}

# ---------------------------------------------------------------- 0. 路径与参数

$root = Split-Path -Parent $PSScriptRoot   # tools\ 的上一级 = 仓库根
$csproj = 'src\AICompanion.App\AICompanion.App.csproj'

if (-not (Test-Path (Join-Path $root $csproj))) {
    throw "定位不到仓库根目录（推导结果：$root）。pack-release.ps1 必须放在 <仓库>\tools\ 下，且 $csproj 必须存在。"
}
Set-Location $root

$versionMatch = Select-String -Path $csproj -Pattern '<Version>([^<]+)</Version>'
if (-not $versionMatch -or $versionMatch.Matches.Count -eq 0) {
    throw "无法从 $csproj 读取 <Version>，版本号缺失会导致包名不可复现。"
}
$version = $versionMatch.Matches[0].Groups[1].Value.Trim()
if (-not $version) { throw "从 $csproj 读到的 <Version> 是空字符串。" }

$flavor = if ($SelfContained) { 'selfcontained' } else { 'framework' }
$packName = "AICompanion-$version-$Runtime-$flavor"

$outRoot = if ([string]::IsNullOrWhiteSpace($OutDir)) {
    Join-Path $root 'dist'
} elseif ([System.IO.Path]::IsPathRooted($OutDir)) {
    $OutDir
} else {
    Join-Path $root $OutDir
}
$stage = Join-Path $outRoot $packName
$zipPath = Join-Path $outRoot "$packName.zip"

# 发布中间目录放 artifacts\ 而不是 src\...\bin\：
# 程序正在运行时，src\AICompanion.App\bin\ 里的 exe 被占用，dotnet publish 会失败。
$work = Join-Path $root "artifacts\pack-release\$packName"
$publishDir = Join-Path $work 'publish'
$objOut = Join-Path $work 'objbin'

Write-Host ''
Write-Host '========================================================' -ForegroundColor Cyan
Write-Host " AICompanion 发布打包  v$version" -ForegroundColor Cyan
Write-Host '========================================================' -ForegroundColor Cyan
Note "目标运行时：$Runtime"
Note "自包含：$SelfContained（.NET 8 运行时$(if ($SelfContained) { '打进包' } else { '由目标机提供' })）"
Note "含 modules：$IncludeModules"
Note "含模型：$IncludeModel"
Note "输出目录：$outRoot"

# ---------------------------------------------------------------- 1. 前置检查

Step '1/7 前置检查'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw '未找到 dotnet 命令。请安装 .NET 8 SDK（https://dotnet.microsoft.com/download/dotnet/8.0），并确认 dotnet 在 PATH 里。'
}
$dotnetVer = (& dotnet --version) 2>&1
Note "dotnet SDK：$dotnetVer"

# 这些是"发布流程之外、必须由仓库根目录提供"的原生库。
# 缺了不会有编译错误，但目标机上程序会静默降级（ASR 不可用 / 没形象），所以这里硬失败。
$nativeSources = @(
    @{ Path = 'Live2DCubismCore.dll'; Why = 'Live2D 渲染原生库（Cubism Core）' },
    @{ Path = 'bin\ai_asr.dll';       Why = '语音识别原生库（whisper.cpp 桥接）' },
    @{ Path = 'bin\libfvad.dll';      Why = 'WebRTC VAD 原生库' }
)
foreach ($item in $nativeSources) {
    $full = Join-Path $root $item.Path
    if (-not (Test-Path $full)) {
        throw "缺少 $($item.Path)（$($item.Why)）。它不是 dotnet publish 的产物，必须放在仓库里对应位置；从 Live2D SDK / third_party 构建取得后再打包。"
    }
    Note "有 $($item.Path)（$(Get-SizeText (Get-Item $full).Length)）"
}

# modules\ 是 LLM 后端的容器，程序按 AppPaths.Root\modules 扫描 module.json。
$moduleDirs = @()
if ($IncludeModules) {
    $modulesRoot = Join-Path $root 'modules'
    if (-not (Test-Path $modulesRoot)) {
        throw '缺少 modules\ 目录：程序靠它找 LLM 后端（llama-server.exe），没有它分发包无法对话。若确认要出无对话能力的包，请显式加 -IncludeModules:$false。'
    }
    $moduleDirs = @(Get-ChildItem $modulesRoot -Directory | Where-Object {
        Test-Path (Join-Path $_.FullName 'module.json')
    })
    if ($moduleDirs.Count -eq 0) {
        throw 'modules\ 下没有任何含 module.json 的模块目录。请先运行 tools\build-cuda.cmd 或 tools\build-vulkan.cmd 构建后端。'
    }

    $llmDirs = @($moduleDirs | Where-Object { $_.Name -like 'llm-*' })
    if ($llmDirs.Count -eq 0) {
        throw 'modules\ 下没有 llm-* 模块：对话功能会完全没有后端。请构建 modules\llm-cuda 或 modules\llm-vulkan。'
    }
    foreach ($d in $llmDirs) {
        $entry = Join-Path $d.FullName 'llama-server.exe'
        if (-not (Test-Path $entry)) {
            throw "模块 modules\$($d.Name) 缺 llama-server.exe（module.json 里声明的入口）。这是一个残缺模块，打出去只会让程序扫到它却起不来。"
        }
    }
    foreach ($d in $moduleDirs) {
        Note "模块 $($d.Name)：$(Get-SizeText (Get-DirBytes $d.FullName))"
    }
} else {
    Warn '按参数排除 modules\：此包将没有 LLM 后端，只能看形象不能对话。'
}

# ---------------------------------------------------------------- 2. 清理旧产物（幂等）

Step '2/7 清理上一次的产物'

foreach ($p in @($stage, $work)) {
    if (Test-Path $p) {
        Remove-Item $p -Recurse -Force
        Note "已删除 $p"
    }
}
if (Test-Path $zipPath) {
    Remove-Item $zipPath -Force
    Note "已删除 $zipPath"
}
New-Item -ItemType Directory -Path $outRoot -Force | Out-Null
New-Item -ItemType Directory -Path $publishDir -Force | Out-Null
New-Item -ItemType Directory -Path $objOut -Force | Out-Null
New-Item -ItemType Directory -Path $stage -Force | Out-Null

# ---------------------------------------------------------------- 3. dotnet publish

Step "3/7 dotnet publish（Release / $Runtime / self-contained=$($SelfContained.ToString().ToLowerInvariant())）"

$scFlag = $SelfContained.ToString().ToLowerInvariant()
# PublishReadyToRun 显式关掉：R2R 会让产物体积明显变大、且跨机器复现性差
# （不同 SDK 的 crossgen 结果不同，包哈希无法稳定比对）。
$publishArgs = @(
    'publish', $csproj,
    '-c', 'Release',
    '-r', $Runtime,
    '--self-contained', $scFlag,
    '-p:PublishReadyToRun=false',
    "-p:OutputPath=$objOut\",
    '-o', $publishDir,
    '--nologo', '-v', 'minimal'
)
Note ("dotnet " + ($publishArgs -join ' '))
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish 失败（退出码 $LASTEXITCODE）。常见原因：目标架构 $Runtime 的运行时包取不到（需联网还原）、或项目本身编译不过。"
}

$publishedExe = Join-Path $publishDir 'AICompanion.exe'
if (-not (Test-Path $publishedExe)) {
    throw "dotnet publish 没有产出 AICompanion.exe（目录：$publishDir）。"
}
Good "publish 完成：$publishDir（$(Get-SizeText (Get-DirBytes $publishDir))）"

# 自包含 vs 框架依赖，用产物本身说话，而不是只信参数
$hasCoreClr = Test-Path (Join-Path $publishDir 'coreclr.dll')
Note "产物里 $(if ($hasCoreClr) { '有' } else { '没有' }) coreclr.dll → 实际是$(if ($hasCoreClr) { '自包含' } else { '框架依赖' })构建"
if ($SelfContained -and -not $hasCoreClr) {
    throw '参数要求自包含，但 publish 产物里没有 coreclr.dll。目标机将因缺少运行时直接闪退，这属于致命缺陷，已中止打包。'
}
if (-not $SelfContained -and $hasCoreClr) {
    throw '参数要求框架依赖，但产物里混进了 coreclr.dll。包会虚胖且行为不可预期，已中止打包。'
}

# ---------------------------------------------------------------- 4. 汇总运行环境

Step '4/7 汇总运行环境（原生库 / 模块 / 配置 / 资源）'

# 4.1 主程序产物
Copy-Item (Join-Path $publishDir '*') $stage -Recurse -Force
Note "主程序产物 $(@(Get-ChildItem $publishDir -Recurse -File).Count) 个文件已拷入分发目录"

# 4.2 原生库 → bin\
# 程序默认配置里 asr.library_path = bin/ai_asr.dll、vad_library_path = bin/libfvad.dll，
# 少一层 bin\ 就等于告诉程序"没有 ASR"。这里只拷顶层文件，不递归（bin\cpu-backup 是开发机备用件）。
$binStage = Join-Path $stage 'bin'
New-Item -ItemType Directory -Path $binStage -Force | Out-Null
$binFiles = @(Get-ChildItem (Join-Path $root 'bin') -File -ErrorAction SilentlyContinue)
if ($binFiles.Count -eq 0) {
    throw '仓库根目录 bin\ 下没有任何文件，ai_asr.dll / libfvad.dll 不会被带上。'
}
foreach ($f in $binFiles) {
    Copy-Item $f.FullName -Destination $binStage -Force
}
Note "bin\ ← $($binFiles.Count) 个原生库（$(Get-SizeText (Get-DirBytes $binStage))）"

# 4.3 Live2D Cubism Core → 与 exe 同级
# csproj 里有 None Include + CopyToOutputDirectory，publish 通常已经带上了；这里兜底。
if (-not (Test-Path (Join-Path $stage 'Live2DCubismCore.dll'))) {
    Copy-Item (Join-Path $root 'Live2DCubismCore.dll') (Join-Path $stage 'Live2DCubismCore.dll') -Force
    Note 'Live2DCubismCore.dll 由脚本补拷（publish 未带）'
} else {
    Note 'Live2DCubismCore.dll 已由 publish 带入'
}

# 4.4 modules\ → modules\
# 程序按 <Root>\modules\<name>\module.json 发现后端；用户可自行增删目录来换后端。
if ($IncludeModules) {
    $modStage = Join-Path $stage 'modules'
    New-Item -ItemType Directory -Path $modStage -Force | Out-Null
    foreach ($d in $moduleDirs) {
        $target = Join-Path $modStage $d.Name
        Copy-Item $d.FullName $target -Recurse -Force
        Note "modules\$($d.Name) ← $(Get-SizeText (Get-DirBytes $target))"
    }
} else {
    $modStage = Join-Path $stage 'modules'
    New-Item -ItemType Directory -Path $modStage -Force | Out-Null
    @(
        '本包未包含 LLM 后端模块（打包时用了 -IncludeModules:$false）。',
        '',
        '要让对话能用，把后端模块目录放到本目录下，结构为：',
        '',
        '  modules\llm-vulkan\module.json',
        '  modules\llm-vulkan\llama-server.exe',
        '  modules\llm-vulkan\ggml*.dll / llama*.dll / mtmd.dll',
        '',
        'module.json 样例（kind 必须是 llm）：',
        '  { "name": "llm-vulkan", "kind": "llm", "display": "Vulkan 版对话模块", "requires": "vulkan", "entry": "llama-server.exe" }',
        '',
        '放好后重启程序，在「设置 → 拓展模块」里能看到并按硬件切换。'
    ) | Set-Content -Path (Join-Path $modStage 'README.txt') -Encoding UTF8
    Note 'modules\README.txt（说明用户如何自行放入后端）'
}

# 4.5 MSVC 运行库（app-local 部署）
# 实测（读导入表）：ai_asr.dll / libfvad.dll / llama-server.exe / llama*.dll / ggml-*.dll
# 全部依赖 MSVCP140.dll + VCRUNTIME140.dll + VCRUNTIME140_1.dll。
# Windows 只自带 UCRT（api-ms-win-crt-*），**不带**这几个 —— 干净机器上缺它们，
# ASR 与 LLM 都会加载失败，而主窗口照常弹出，症状极难排查。
# 所以这里把运行库按 app-local 方式随包，而不是要求用户去装 VC++ 2015-2022 可再发行包。
if (-not $NoVcRuntime) {
    $crtNames = @('msvcp140.dll', 'vcruntime140.dll', 'vcruntime140_1.dll')
    $crtTargets = @($stage)
    if ($IncludeModules -and (Test-Path (Join-Path $stage 'modules'))) {
        # llama-server.exe 是**独立进程**，它的 DLL 搜索从自己的目录开始，
        # 放在包根目录它找不到，必须也放进各模块目录。
        $crtTargets += @(Get-ChildItem (Join-Path $stage 'modules') -Directory | Select-Object -ExpandProperty FullName)
    }
    $copiedCrt = @()
    foreach ($name in $crtNames) {
        $src = Join-Path $env:SystemRoot "System32\$name"
        if (-not (Test-Path $src)) {
            Warn "本机 System32 里没有 $name，无法随包。目标机若缺 VC++ 2015-2022 x64 运行库，ASR/LLM 会加载失败。"
            continue
        }
        foreach ($dst in $crtTargets) {
            Copy-Item $src -Destination (Join-Path $dst $name) -Force
        }
        $copiedCrt += $name
    }
    if ($copiedCrt.Count -gt 0) {
        $crtBytes = 0
        foreach ($n in $copiedCrt) {
            $probe = Join-Path $stage $n
            if (Test-Path $probe) { $crtBytes += (Get-Item $probe).Length * $crtTargets.Count }
        }
        Note "MSVC 运行库 ← $($copiedCrt -join ', ')（包根 + $($crtTargets.Count - 1) 个模块目录，共 $(Get-SizeText $crtBytes)）"
    }
}

# 4.6 配置：模板 → config.yaml
# 仓库根目录的 config.yaml 是开发机上的个人配置（含本机绝对路径、端口、音色），
# 绝不能进包；包里的 config.yaml 一律来自 Core 内嵌的出厂模板。
$tpl = Join-Path $root 'src\AICompanion.Core\Configuration\config.default.yaml'
if (-not (Test-Path $tpl)) {
    throw "找不到配置模板 $tpl。发布包的 config.yaml 必须由模板生成，不能用仓库根目录的个人配置顶替。"
}
$cfgTarget = Join-Path $stage 'config.yaml'
if (Test-Path $cfgTarget) {
    # 只有在"目标目录不是新建的"时才会走到这里（正常流程 stage 已被清空）。
    Warn '目标已存在 config.yaml，保留原文件不覆盖（避免冲掉用户已有配置）。'
} else {
    Copy-Item $tpl $cfgTarget -Force
    Note 'config.yaml ← 出厂模板（不是开发机配置）'
}
Copy-Item $tpl (Join-Path $stage 'config.default.yaml') -Force
Note 'config.default.yaml ← 出厂模板（供用户对照/恢复）'

# 4.7 运行期目录的骨架
# 程序要在自己目录下写 logs\ 与 Assets\appearance.json，目录得能建出来。
# 但**不能**把开发机上的个人状态打进去（窗口位置、日志、待审知识）。
foreach ($junk in @('logs', 'Assets\appearance.json', 'Knowledge\.pending')) {
    $p = Join-Path $stage $junk
    if (Test-Path $p) {
        Remove-Item $p -Recurse -Force
        Note "已剔除运行期个人状态：$junk"
    }
}

$assetsDir = Join-Path $stage 'Assets'
New-Item -ItemType Directory -Path (Join-Path $assetsDir 'Default') -Force | Out-Null
$appIco = Join-Path $root 'Assets\app.ico'
if (Test-Path $appIco) { Copy-Item $appIco (Join-Path $assetsDir 'app.ico') -Force }
@(
    '把角色立绘放这里（PNG / JPG 均可，建议带透明通道）。',
    '',
    '程序按以下顺序取形象，缺哪一级就退到下一级：',
    '  1. Live2D 模型（config.yaml 的 live2d.default_model_path）',
    '  2. 本目录里的静态立绘（Assets\Default\）',
    '  3. 纯色块',
    '',
    '第三方立绘涉及他人著作权，本包不附带，请自行准备。'
) | Set-Content -Path (Join-Path $assetsDir 'Default\README.txt') -Encoding UTF8

New-Item -ItemType Directory -Path (Join-Path $stage 'Knowledge') -Force | Out-Null
@(
    '知识库目录（热重载）：放 .md / .txt，程序会自动索引。',
    '',
    '本包只带空目录，不含任何个人知识条目。'
) | Set-Content -Path (Join-Path $stage 'Knowledge\README.txt') -Encoding UTF8

if (Test-Path (Join-Path $root 'README.md')) {
    Copy-Item (Join-Path $root 'README.md') $stage -Force
    Note 'README.md'
}

# 4.8 模型
if ($IncludeModel) {
    $modelsRoot = Join-Path $root 'models'
    if (-not (Test-Path $modelsRoot)) {
        throw '参数要求打包模型，但仓库根目录没有 models\。'
    }
    $modelStage = Join-Path $stage 'models'
    New-Item -ItemType Directory -Path $modelStage -Force | Out-Null
    foreach ($d in @(Get-ChildItem $modelsRoot -Directory)) {
        Copy-Item $d.FullName (Join-Path $modelStage $d.Name) -Recurse -Force
        Note "models\$($d.Name) ← $(Get-SizeText (Get-DirBytes (Join-Path $modelStage $d.Name)))"
    }
} else {
    $modelStage = Join-Path $stage 'models'
    New-Item -ItemType Directory -Path $modelStage -Force | Out-Null
    @(
        '本包不含模型（模型 1 GB 起，不随包分发）。按需放到下面这些位置，结构与仓库一致：',
        '',
        '  models\llm\*.gguf           对话模型（必需，否则只能看形象）',
        '  models\asr\ggml-base.bin    语音识别模型（可选，约 141 MB）',
        '  models\tts\*.wav            参考音频（可选，走 GPT-SoVITS/CosyVoice 时用）',
        '  models\piper\                Piper 音色（可选，本仓库不提供 piper.exe）',
        '',
        '放好后重启程序；启动日志会写明每一项是"就位"还是"缺失降级"。'
    ) | Set-Content -Path (Join-Path $modelStage 'README.txt') -Encoding UTF8
    Note 'models\README.txt（模型不入包，附放置说明）'
}

# 4.9 瘦身：删符号与多余语言资源
if (-not $NoSlim) {
    $pdbs = @(Get-ChildItem $stage -Recurse -File -Filter '*.pdb' -ErrorAction SilentlyContinue)
    if ($pdbs.Count -gt 0) {
        $freed = ($pdbs | Measure-Object Length -Sum).Sum
        $pdbs | Remove-Item -Force
        Note "删除 $($pdbs.Count) 个 .pdb，省 $(Get-SizeText $freed)"
    }

    # WPF 为 14 种语言各带一份卫星程序集，本项目只需要中文
    $keepLangs = @('zh-Hans', 'zh-Hant')
    $langDirs = @(Get-ChildItem $stage -Directory | Where-Object {
        $_.Name -match '^[a-z]{2}(-[A-Z]{2})?$' -and ($_.Name -notin $keepLangs)
    })
    if ($langDirs.Count -gt 0) {
        $freed = 0
        foreach ($d in $langDirs) {
            $freed += (Get-ChildItem $d.FullName -Recurse -File -ErrorAction SilentlyContinue | Measure-Object Length -Sum).Sum
            Remove-Item $d.FullName -Recurse -Force
        }
        Note "删除 $($langDirs.Count) 个语言目录，省 $(Get-SizeText $freed)"
    }
}

# 4.10 版本与环境说明落盘
$runtimeLines = @(
    "AICompanion $version"
    "构建时间：$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
    "打包主机：$env:COMPUTERNAME / dotnet SDK $dotnetVer"
    ''
    '--- 运行环境 ---'
    "目标运行时：$Runtime"
    "自包含：$($SelfContained.ToString().ToLowerInvariant())（$(if ($SelfContained) { '含 .NET 8 运行时，目标机免装' } else { '需目标机安装 .NET 8 桌面运行时 x64' })）"
    "MSVC 运行库：$(if ($NoVcRuntime) { '未随包（目标机需装 VC++ 2015-2022 x64）' } else { '随包（msvcp140/vcruntime140/vcruntime140_1，app-local）' })"
    "LLM 后端模块：$(if ($IncludeModules) { ($moduleDirs | ForEach-Object { $_.Name }) -join ', ' } else { '未包含' })"
    "模型：$(if ($IncludeModel) { '随包' } else { '未随包（见 models\README.txt）' })"
    "Live2D Cubism Core：随包（授权归 Live2D，商业分发需自行确认）"
    ''
    '启动：AICompanion.exe'
)
$runtimeLines | Set-Content -Path (Join-Path $stage 'VERSION.txt') -Encoding UTF8
Note 'VERSION.txt（版本 + 运行环境）'

$stageBytes = Get-DirBytes $stage
$stageCount = @(Get-ChildItem $stage -Recurse -File).Count
Good "分发目录：$stage（$stageCount 个文件，$(Get-SizeText $stageBytes)）"

# ---------------------------------------------------------------- 5. 打 zip

Step '5/7 生成 zip'

$zippedCount = New-ReleaseZip -SourceDir $stage -TargetZip $zipPath
if (-not (Test-Path $zipPath)) {
    throw "压缩失败：没有生成 $zipPath。"
}
$zipBytes = (Get-Item $zipPath).Length
Good "已生成 $zipPath（$zippedCount 个条目，$(Get-SizeText $zipBytes)）"

# ---------------------------------------------------------------- 6. 校验（核心）

Step '6/7 校验 zip 内容'

# System.IO.Compression 在 PS 5.1 里要显式加载
try { Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction Stop } catch { }

$archive = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
try {
    # 目录条目（Name 为空）不计入文件清单。
    # 顺手把 \ 归一成 /：即使换了压缩实现（或包是别人用 Compress-Archive 打的），
    # 断言也不该因为分隔符写法而误报"缺失"。
    $rawEntries = @($archive.Entries | Where-Object { $_.Name -ne '' })
    $backslashCount = @($rawEntries | Where-Object { $_.FullName.Contains('\') }).Count
    $entries = @($rawEntries | ForEach-Object {
        [PSCustomObject]@{ FullName = $_.FullName.Replace('\', '/'); Length = [long]$_.Length }
    })
    $names = @($entries | ForEach-Object { $_.FullName })
    $totalUncompressed = [long](($entries | Measure-Object Length -Sum).Sum)

    if ($backslashCount -gt 0) {
        Write-Host "    ! 有 $backslashCount 个条目名用反斜杠（不符合 ZIP 规范，非 Windows 解压工具会出问题）" -ForegroundColor Yellow
    } else {
        Note '条目分隔符全为正斜杠（符合 ZIP 规范）'
    }

    Write-Host ''
    Write-Host '---- zip 内文件清单 ----' -ForegroundColor Cyan
    foreach ($e in ($entries | Sort-Object FullName)) {
        Write-Host ("  {0,10}  {1}" -f (Get-SizeText $e.Length), $e.FullName)
    }
    Write-Host '---- 清单结束 ----' -ForegroundColor Cyan
    Write-Host ''
    Note "文件数：$($entries.Count)"
    Note "解压后总大小：$(Get-SizeText $totalUncompressed)"
    Note "zip 体积：$(Get-SizeText $zipBytes)"
    Note "压缩率：$(if ($totalUncompressed -gt 0) { '{0:N1}%' -f (100.0 * $zipBytes / $totalUncompressed) } else { 'n/a' })"

    # ---- 逐项断言 ----
    # Required = $false 的项只做提示，不计入失败（例如可选的 libfvad / 语言目录）。
    $checks = @(
        @{ Key = 'AICompanion.exe';        Pattern = '^AICompanion\.exe$';                    Required = $true;  Why = '主程序入口' }
        @{ Key = 'Live2DCubismCore.dll';   Pattern = '(^|/)Live2DCubismCore\.dll$';           Required = $true;  Why = 'Live2D 渲染原生库' }
        @{ Key = 'ai_asr.dll';             Pattern = '(^|/)ai_asr\.dll$';                     Required = $true;  Why = '语音识别原生库' }
        @{ Key = 'modules/llm-*/llama-server.exe'; Pattern = '^modules/llm-[^/]+/llama-server\.exe$'; Required = $IncludeModules; Why = 'LLM 后端入口' }
        @{ Key = 'config.yaml';            Pattern = '^config\.yaml$';                        Required = $true;  Why = '用户配置（出厂模板）' }
        @{ Key = 'coreclr.dll';            Pattern = '(^|/)coreclr\.dll$';                    Required = $SelfContained; Why = '自带 .NET 运行时' }
        # 以下只提示
        @{ Key = 'libfvad.dll';            Pattern = '(^|/)libfvad\.dll$';                    Required = $false; Why = 'VAD 原生库' }
        @{ Key = 'hostfxr.dll';            Pattern = '(^|/)hostfxr\.dll$';                    Required = $false; Why = '自包含宿主' }
        @{ Key = 'config.default.yaml';    Pattern = '(^|/)config\.default\.yaml$';           Required = $false; Why = '模板副本' }
        @{ Key = 'VERSION.txt';            Pattern = '(^|/)VERSION\.txt$';                    Required = $false; Why = '版本与环境说明' }
        @{ Key = 'module.json';            Pattern = '(^|/)module\.json$';                    Required = $IncludeModules; Why = '模块清单（程序靠它发现后端）' }
        @{ Key = 'msvcp140.dll';           Pattern = '(^|/)msvcp140\.dll$';                   Required = (-not $NoVcRuntime); Why = 'MSVC 运行库' }
    )

    Write-Host ''
    Write-Host '---- 断言 ----' -ForegroundColor Cyan
    $failures = @()
    foreach ($c in $checks) {
        $hits = @($names | Where-Object { $_ -match $c.Pattern })
        $isRequired = [bool]$c.Required
        if ($hits.Count -gt 0) {
            $extra = if ($hits.Count -gt 1) { "（$($hits.Count) 个）" } else { '' }
            Write-Host "    [通过] $($c.Key)$extra —— $($c.Why)" -ForegroundColor Green
        } elseif ($isRequired) {
            Write-Host "    [缺失] $($c.Key) —— $($c.Why)（必需）" -ForegroundColor Red
            $failures += "$($c.Key)（$($c.Why)）"
        } else {
            Write-Host "    [提示] $($c.Key) 不在包里 —— $($c.Why)（可选）" -ForegroundColor DarkGray
        }
    }

    # 额外结构校验：每个 llm-* 模块必须同时有 module.json 与 llama-server.exe
    if ($IncludeModules) {
        $llmNames = @($names | Where-Object { $_ -match '^modules/llm-[^/]+/' } |
            ForEach-Object { ($_ -split '/')[1] } | Sort-Object -Unique)
        foreach ($name in $llmNames) {
            $hasJson = $names -contains "modules/$name/module.json"
            $hasExe = $names -contains "modules/$name/llama-server.exe"
            if ($hasJson -and $hasExe) {
                Write-Host "    [通过] 模块 $name 结构完整（module.json + llama-server.exe）" -ForegroundColor Green
            } else {
                Write-Host "    [缺失] 模块 $name 结构不完整（module.json=$hasJson, llama-server.exe=$hasExe）" -ForegroundColor Red
                $failures += "模块 $name 结构不完整"
            }
        }

        # CUDA 后端的 cublas 不在 modules\llm-cuda 里，属已知需外部依赖，只提示不失败
        $cudaEntries = @($names | Where-Object { $_ -match '^modules/llm-cuda/' })
        if ($cudaEntries.Count -gt 0) {
            $hasCublas = @($names | Where-Object { $_ -match 'cublas64_\d+\.dll$' }).Count -gt 0
            if (-not $hasCublas) {
                Write-Host '    [提示] llm-cuda 模块未随包 cublas64_*.dll：目标机需装 CUDA 运行时或退回 llm-vulkan/CPU' -ForegroundColor DarkGray
            }
        }
    }

    if ($failures.Count -gt 0) {
        Write-Host ''
        Write-Host "校验失败：缺 $($failures.Count) 项必需内容" -ForegroundColor Red
        foreach ($f in $failures) { Write-Host "  - $f" -ForegroundColor Red }
        Write-Host "包不可用于分发：$zipPath" -ForegroundColor Red
        Write-Host '修好上面几项后重跑本脚本（打包流程是幂等的，直接重跑即可）。' -ForegroundColor Red
        Write-Host ''
        $archive.Dispose()
        exit 1
    }
} finally {
    if ($archive) { try { $archive.Dispose() } catch { } }
}

# ---------------------------------------------------------------- 7. 运行环境说明

Step '7/7 运行环境'

Write-Host ''
Write-Host '---- 这个包在目标机器上需要什么 ----' -ForegroundColor Cyan
Write-Host "    目标运行时：$Runtime（$(if ($Runtime -eq 'win-x64') { '64 位 x64 Windows 10/11' } else { 'Windows on ARM' })）"
Write-Host "    是否自包含：$($SelfContained.ToString().ToLowerInvariant()) —— $(if ($SelfContained) { '.NET 8 桌面运行时已在包内，目标机无需安装任何 .NET' } else { '目标机必须先装 .NET 8 桌面运行时（Microsoft.WindowsDesktop.App 8.0.x，x64），否则双击即闪退' })"
Write-Host "    MSVC 运行库：$(if ($NoVcRuntime) { '未随包 —— 需目标机装 VC++ 2015-2022 x64 可再发行包，否则 ai_asr.dll / llama-server.exe 加载失败' } else { '已随包（app-local 部署 msvcp140 / vcruntime140 / vcruntime140_1）—— 目标机无需装 VC++ 可再发行包' })"
if ($IncludeModules) {
    $hasCudaModule = @($moduleDirs | Where-Object { $_.Name -like '*cuda*' }).Count -gt 0
    $hasVulkanModule = @($moduleDirs | Where-Object { $_.Name -like '*vulkan*' }).Count -gt 0
    if ($hasVulkanModule) {
        Write-Host '    Vulkan 驱动：需要 —— llm-vulkan 模块依赖 vulkan-1.dll，由显卡驱动提供；老驱动/虚拟机里可能没有，此时请改用 llm-cuda 或 CPU'
    }
    if ($hasCudaModule) {
        Write-Host '    NVIDIA 驱动：需要 —— llm-cuda 模块依赖 nvidia-smi 与 CUDA 驱动；未随包 cublas64_13.dll，需另装 CUDA 运行时'
    }
} else {
    Write-Host '    GPU 驱动：不适用 —— 本包不含 LLM 后端模块'
}
Write-Host "    是否含模型：$($IncludeModel.ToString().ToLowerInvariant()) —— $(if ($IncludeModel) { '模型在包内，解压即可对话' } else { '需用户自行放 models\llm\*.gguf，否则只能看形象' })"
Write-Host '    Live2D 授权：包内含 Live2DCubismCore.dll，其授权归 Live2D 公司；公开分发前须自行确认 Cubism SDK 的许可条款'
Write-Host ('-' * 62) -ForegroundColor Cyan

Write-Host ''
Write-Host "打包成功" -ForegroundColor Green
Write-Host "    分发目录：$stage"
Write-Host "    发布包：  $zipPath（$(Get-SizeText $zipBytes)，$($entries.Count) 个文件）"
Write-Host "    文件数/体积断言：全部通过" -ForegroundColor Green
Write-Host ''

exit 0
