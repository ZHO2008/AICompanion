# ============================================================================
#  pack-release.ps1 - 生成**可复现**的发布 zip，并自带完整性断言
#
#  与 tools\pack.ps1 的分工：
#    pack.ps1        **已废弃**（一运行就打印中文提示并 exit 1）。它不会带 modules\，
#                    产出的包没有对话后端；历史原因保留文件，方便对照旧文档。
#    pack-release.ps1（本脚本）面向"可复现 + 可校验"的发布 zip，只做一件事：
#                    dotnet publish → 汇总运行环境 → 打 zip → 逐项断言。
#                    模型默认不入包（-IncludeModel），产物 150 MB 级，便于上传/回归。
#
#  用法：
#    pwsh -File tools\pack-release.ps1
#        自包含 win-x64、含 modules\、不含模型、**不含** Live2D Cubism Core
#        → 目标是"干净机器解压即跑"，且默认避开发行授权项
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
#    pwsh -File tools\pack-release.ps1 -IncludeCubismCore:$true   # 或 -WithCubismCore
#        把 Live2DCubismCore.dll 打进包。**默认 false**：该 DLL 属 Live2D 授权项，
#        公开分发前必须自行确认 Cubism SDK 的许可条款。
#        不含时脚本会在包根生成 Live2DCubismCore.dll.README.txt，说明用户如何自备。
#
#    pwsh -File tools\pack-release.ps1 -OutDir D:\release
#        输出到别处（默认 <仓库>\dist）
#
#    pwsh -File tools\pack-release.ps1 -SkipSmokeTest
#        跳过第 8 步的"解压 → 真启动 → 读日志"冒烟断言，只做前 7 步的静态断言。
#        只在明确知道为什么跳过时用（例如本机没有可用的 llm 后端 / 要出纯离线包）。
#
#  注意：PowerShell 5.1 的 `-File` 模式**传不了布尔值**（-SelfContained:$false 会被
#        当成字符串，绑 [bool] 直接报错）。要传 false 请用 `pwsh -Command` 调用，
#        或改用本脚本另给的不带值开关：-FrameworkOnly / -WithModel / -NoModules /
#        -WithCubismCore / -NoVcRuntime / -NoSlim / -SkipSmokeTest。
#
#  产物：
#    <OutDir>\AICompanion-<版本>-<runtime>-<selfcontained|framework>\     分发目录
#    <OutDir>\AICompanion-<版本>-<runtime>-<selfcontained|framework>.zip  发布包
#
#  退出码：0 = 全部断言（含冒烟）通过；非 0 = 缺东西/发布失败/冒烟不过（错误信息为中文）
#
#  第 8 步"冒烟断言"为什么存在：
#    静态断言只能证明"文件都在"，证明不了"启动起来能用"。本仓库确实吃过
#    "AICompanion.exe 在、modules\ 在，但配置指向一个包里不存在的 llama-server.exe，
#    首启必然报「未找到 llama-server」"的亏。所以现在必须解压到临时目录、
#    以解压目录为工作目录真启动一次，读它自己写的 logs\app\*.log，
#    断言出现「启动完成」「LLM 就绪=True」，且不出现「未找到 llama-server」。
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

    # 是否把 Live2DCubismCore.dll 打进包。默认 **false**：该 DLL 属 Live2D 授权项，
    # 公开分发包默认不含，由用户自行从 Live2D 官网下载官方 Cubism Core 放进来。
    [bool]$IncludeCubismCore = $false,

    # 输出目录。留空 = <仓库>\dist。相对路径按仓库根解析。
    [string]$OutDir = '',

    # ---- 下面是给 PowerShell 5.1 `-File` 模式准备的不带值开关（覆盖上面的布尔参数）----
    [switch]$FrameworkOnly,   # 等价于 -SelfContained:$false
    [switch]$WithModel,       # 等价于 -IncludeModel:$true
    [switch]$NoModules,       # 等价于 -IncludeModules:$false
    [switch]$WithCubismCore,  # 等价于 -IncludeCubismCore:$true（Live2D 授权项，默认不带）
    [switch]$NoVcRuntime,     # 不把 MSVC 运行库（msvcp140/vcruntime140）打进包
    [switch]$NoSlim,          # 不删 .pdb、不裁语言目录

    # ---- 冒烟断言（第 8 步）----
    # 关掉它只跳过"解压 → 真启动 → 读日志"这一段；前面 7 步的静态断言照旧。
    # 只在明确知道为什么跳过时用（例如没有可用的 llm 后端、或要出纯离线包）。
    [switch]$SkipSmokeTest,

    # 冒烟等待上限（秒）。程序要拉 llama-server 并等它 /health 就绪，
    # 1.5B 模型在 Vulkan 上通常 5~20 秒；给到 90 秒是为了兼顾冷启动/机械盘。
    [int]$SmokeWaitSeconds = 90,

    # 冒烟时临时塞进解压目录的 GGUF 模型（包本身仍然不含模型）。
    # 留空 = 自动用 <仓库>\models\llm 下体积最小的那个 .gguf；找不到就跳过 LLM 就绪断言并说明原因。
    [string]$SmokeModelPath = '',

    # 冒烟用的临时工作目录。默认 <系统临时目录>\AICompanion-smoke，跑完必删。
    [string]$SmokeWorkDir = ''
)

$ErrorActionPreference = 'Stop'

if ($FrameworkOnly) { $SelfContained = $false }
if ($WithModel) { $IncludeModel = $true }
if ($NoModules) { $IncludeModules = $false }
if ($WithCubismCore) { $IncludeCubismCore = $true }

# 让中文在 PowerShell 5.1 的控制台里不变成乱码（不改则 GBK 控制台会吞字）
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

# System.IO.Compression：Windows PowerShell 5.1 默认不加载这两个程序集。
#   System.IO.Compression.dll            → ZipArchive / ZipArchiveMode / CompressionLevel
#   System.IO.Compression.FileSystem.dll → ZipFile
# 少加载任何一个，后面都会报 "Unable to find type [System.IO.Compression.ZipArchiveMode]"。
# 这里不再用 `catch { }` 空吞：加载失败必须当场说清楚，
# 否则故障会推迟到"打 zip 那一步"才以"找不到类型"的形式爆出来，极难定位。
try {
    Add-Type -AssemblyName System.IO.Compression -ErrorAction Stop
} catch {
    throw "无法加载程序集 System.IO.Compression（打 zip 必需）：$($_.Exception.Message)。请用 Windows PowerShell 5.1 或 PowerShell 7 运行本脚本。"
}

try {
    Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction Stop
} catch {
    throw "无法加载程序集 System.IO.Compression.FileSystem（打 zip 与校验必需）：$($_.Exception.Message)。"
}

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

# 目录体积。全部走 -LiteralPath：路径里出现 [ ] 时（-OutDir "D:\release[1]"、
# 用户名里带方括号……）Test-Path / Get-ChildItem 会把方括号当通配符，静默判成"不存在"。
function Get-DirBytes([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return [long]0 }
    $sum = (Get-ChildItem -LiteralPath $path -Recurse -File -ErrorAction SilentlyContinue |
        Measure-Object Length -Sum).Sum
    if ($null -eq $sum) { return [long]0 }
    return [long]$sum
}

# 一次遍历同时拿到"文件数 + 总字节"，避免对同一个目录做两遍 -Recurse 扫描。
function Get-DirStats([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return [PSCustomObject]@{ Count = 0; Bytes = [long]0 } }

    $count = 0
    $bytes = [long]0
    foreach ($f in Get-ChildItem -LiteralPath $path -Recurse -File -ErrorAction SilentlyContinue) {
        $count++
        $bytes += $f.Length
    }

    return [PSCustomObject]@{ Count = $count; Bytes = $bytes }
}

# 读一个**正被别的进程写着**的文件。
#
# 不能用 Get-Content / [System.IO.File]::ReadAllText：它们以 FileShare.Read 打开，
# 而程序写日志用的是 FileAccess.Write + FileShare.ReadWrite（见 FileLogger.cs），
# 两者共享模式不兼容 → 直接 IOException（"文件正由另一进程使用"）。
# 冒烟断言的全部意义就是在程序**还在跑**的时候读它的日志，所以必须用宽松共享打开。
# 这个坑实测踩过：脚本读日志抛异常被吞成空串，于是"日志里什么都没有"，
# 看起来像启动失败，其实是读不到。
function Read-SharedFileText([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return '' }

    $stream = $null
    try {
        $share = [System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete
        $stream = [System.IO.FileStream]::new($path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, $share)
        $reader = [System.IO.StreamReader]::new($stream, [System.Text.Encoding]::UTF8)
        try { return $reader.ReadToEnd() } finally { $reader.Dispose() }
    }
    catch {
        return ''
    }
    finally {
        if ($stream) { try { $stream.Dispose() } catch { } }
    }
}

# 从出厂配置里读一个"路径型"键，归一化成 zip 条目名的写法（正斜杠、去掉 ./）。
#
# 只在**打包脚本里**做这种粗粒度解析：配置是扁平的两级 YAML，
# 这里要的键（server_executable_path / default_texture_path / pet_image_path /
# vad_library_path）全项目各只出现一次，不值得为它引入一个 YAML 解析器。
function Get-ConfigPathValue([string]$configFile, [string]$key) {
    if (-not (Test-Path -LiteralPath $configFile)) { return $null }

    $pattern = '^\s*' + [regex]::Escape($key) + '\s*:\s*(.+)$'
    $match = Select-String -LiteralPath $configFile -Pattern $pattern -Encoding UTF8 | Select-Object -First 1
    if (-not $match) { return $null }

    $value = $match.Matches[0].Groups[1].Value.Trim()

    # 去掉行尾注释（配置里的 # 只会出现在值后面）
    $hash = $value.IndexOf('#')
    if ($hash -ge 0) { $value = $value.Substring(0, $hash).Trim() }

    $value = $value.Trim('"').Trim("'").Trim()
    if ([string]::IsNullOrWhiteSpace($value)) { return $null }

    $value = $value.Replace('\', '/')
    while ($value.StartsWith('./')) { $value = $value.Substring(2) }

    return $value
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

    # FileMode.Create 而不是 CreateNew：上面已经删过一次（并会报错），
    # 这里再因为"文件已存在"而失败只会掩盖真正的原因。
    $fs = [System.IO.File]::Open($TargetZip, [System.IO.FileMode]::Create, [System.IO.FileAccess]::ReadWrite)
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

# ---------------------------------------------------------------- 冒烟断言（第 8 步的核心）
#
# 为什么必须真启动一次：本仓库刚吃过"静态断言全绿、包却开箱不可用"的亏 ——
# AICompanion.exe 在、modules\ 在、config.yaml 在，但配置指着一个包里不存在的
# llama-server.exe，首启必然报"未找到 llama-server"，对话完全不可用。
# 只有"解压 → 在解压目录里启动 → 读它自己写的日志"才能把这种缺陷挡在发版之前。
#
# 约定：
#   * 全程在**临时目录**里做，绝不碰 dist\ 里的 stage 与仓库；
#   * 无论成功失败都杀掉进程、删掉临时目录（finally 里做）；
#   * 包默认不带模型，所以冒烟时会**临时**把一个 .gguf 拷进解压目录，
#     否则 llama-server 起不来、"LLM 就绪=True"这条断言就无从谈起；
#     拷进去的只是测试用的临时文件，zip 本身仍然不含模型。
function Invoke-PackageSmokeTest {
    param(
        [Parameter(Mandatory = $true)][string]$ZipPath,
        [Parameter(Mandatory = $true)][string]$WorkDir,
        [string]$ModelPath = '',
        [string]$RepoModelsDir = '',
        [string]$RepoRoot = '',
        [int]$WaitSeconds = 90
    )

    try {
        Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction Stop
    } catch {
        throw "冒烟断言需要 System.IO.Compression.FileSystem 才能解压：$($_.Exception.Message)"
    }

    $reasons = New-Object System.Collections.Generic.List[string]
    $notes = New-Object System.Collections.Generic.List[string]
    $extract = Join-Path $WorkDir 'app'
    $logPath = ''
    $proc = $null
    $passed = $false

    # 临时目录兜底：下面会 Remove-Item -Recurse -Force，必须确认它真的是"临时目录"。
    # 以前 -SmokeWorkDir 传什么就删什么，误传成仓库根目录（或某个盘的根）就是一次不可恢复的删除。
    try {
        $workFull = [System.IO.Path]::GetFullPath($WorkDir).TrimEnd('\', '/')
        if ($workFull -match '^[A-Za-z]:$') {
            throw "拒绝把磁盘根目录当作冒烟临时目录：$workFull"
        }
        if (-not [string]::IsNullOrWhiteSpace($RepoRoot)) {
            $rootFull = [System.IO.Path]::GetFullPath($RepoRoot).TrimEnd('\', '/')
            if ($workFull.Equals($rootFull, [System.StringComparison]::OrdinalIgnoreCase)) {
                throw "拒绝把仓库根目录当作冒烟临时目录（会被整体删除）：$workFull"
            }
            if ($rootFull.StartsWith($workFull + '\', [System.StringComparison]::OrdinalIgnoreCase)) {
                throw "拒绝把仓库的上级目录当作冒烟临时目录（会被整体删除）：$workFull"
            }
            if ($workFull.StartsWith($rootFull + '\', [System.StringComparison]::OrdinalIgnoreCase)) {
                # 在仓库内部不会删到源码，但会把解压副本塞进 git status，提醒一句。
                $notes.Add("冒烟临时目录在仓库内部（会出现在 git status 里，跑完自动删除）：$workFull")
            }
        }
    } catch {
        $reasons.Add("冒烟临时目录不可用：$($_.Exception.Message)")
        return [PSCustomObject]@{ Passed = $false; Reasons = $reasons; Notes = $notes; LogPath = ''; ExtractDir = '' }
    }

    try {
        # 1. 解压
        if (Test-Path -LiteralPath $WorkDir) { Remove-Item -LiteralPath $WorkDir -Recurse -Force }
        New-Item -ItemType Directory -Path $WorkDir -Force | Out-Null
        Write-Host "    解压到 $extract ..."
        [System.IO.Compression.ZipFile]::ExtractToDirectory($ZipPath, $extract)

        $exe = Join-Path $extract 'AICompanion.exe'
        if (-not (Test-Path -LiteralPath $exe)) {
            $reasons.Add('解压后没有 AICompanion.exe，无法冒烟启动。')
            return [PSCustomObject]@{ Passed = $false; Reasons = $reasons; Notes = $notes; LogPath = ''; ExtractDir = $extract }
        }

        # 2. 模型：包里有就用包里的；没有就从仓库临时拷一个进去（zip 不含模型是设计决定）
        $llmDir = Join-Path $extract 'models\llm'
        $packageModels = @()
        if (Test-Path -LiteralPath $llmDir) {
            $packageModels = @(Get-ChildItem -LiteralPath $llmDir -File -Filter '*.gguf' -ErrorAction SilentlyContinue)
        }

        if ($packageModels.Count -gt 0) {
            $notes.Add("解压目录里已有模型 $($packageModels[0].Name)，直接用它。")
        } else {
            $sourceModel = $null

            if (-not [string]::IsNullOrWhiteSpace($ModelPath)) {
                if (Test-Path -LiteralPath $ModelPath) {
                    $sourceModel = (Resolve-Path -LiteralPath $ModelPath).Path
                } else {
                    $notes.Add("指定的 -SmokeModelPath 不存在：$ModelPath")
                }
            }

            if ($null -eq $sourceModel -and -not [string]::IsNullOrWhiteSpace($RepoModelsDir) -and (Test-Path -LiteralPath $RepoModelsDir)) {
                # 取体积最小的那个：冒烟只需要"能加载"，不必拿 4 GB 的 7B 模型慢慢跑
                $candidate = Get-ChildItem -LiteralPath $RepoModelsDir -Recurse -File -Filter '*.gguf' -ErrorAction SilentlyContinue |
                    Sort-Object Length | Select-Object -First 1
                if ($candidate) { $sourceModel = $candidate.FullName }
            }

            if ($null -eq $sourceModel) {
                $reasons.Add(
                    '包内没有模型，仓库 models\llm 下也找不到 .gguf，无法验证「LLM 就绪=True」。' +
                    '请用 -SmokeModelPath 指定一个 .gguf，或用 -IncludeModel:$true 打含模型的包；' +
                    '确实不做冒烟时用 -SkipSmokeTest。')
                return [PSCustomObject]@{ Passed = $false; Reasons = $reasons; Notes = $notes; LogPath = ''; ExtractDir = $extract }
            }

            if (-not (Test-Path -LiteralPath $llmDir)) { New-Item -ItemType Directory -Path $llmDir -Force | Out-Null }
            $modelTarget = Join-Path $llmDir (Split-Path $sourceModel -Leaf)
            Write-Host "    临时拷入模型（仅用于本次冒烟，zip 不含模型）：$(Split-Path $sourceModel -Leaf)"
            Copy-Item -LiteralPath $sourceModel -Destination $modelTarget -Force
            $notes.Add("冒烟时临时拷入模型 $(Split-Path $sourceModel -Leaf)（zip 本体不含模型）。")

            # config.yaml 的 llm.model_path 若指到别的文件名，这里顺手对齐（只改解压出来的副本）。
            # llm 段在模板最前面，所以只改**第一个** model_path 行，别动 asr / piper / live2d 的。
            $cfg = Join-Path $extract 'config.yaml'
            $currentModelPath = Get-ConfigPathValue $cfg 'model_path'
            $expected = 'models/llm/' + (Split-Path $sourceModel -Leaf)
            if ((Test-Path -LiteralPath $cfg) -and $currentModelPath -ne $expected) {
                $lines = [System.IO.File]::ReadAllLines($cfg, [System.Text.Encoding]::UTF8)
                for ($i = 0; $i -lt $lines.Length; $i++) {
                    if ($lines[$i] -match '^\s*model_path\s*:') {
                        $indent = $lines[$i] -replace '\S.*$', ''
                        $lines[$i] = $indent + 'model_path: "' + $expected + '"'
                        break
                    }
                }
                [System.IO.File]::WriteAllLines($cfg, $lines, (New-Object System.Text.UTF8Encoding($false)))
                $notes.Add("已把解压副本的 llm.model_path 对齐为 $expected（只影响临时目录）。")
            }
        }

        # 3. 起进程（工作目录 = 解压目录，模拟用户双击）
        Write-Host "    启动 $exe（工作目录 $extract），最多等 ${WaitSeconds}s 让日志出现「启动完成」 ..."
        $proc = Start-Process -FilePath $exe -WorkingDirectory $extract -PassThru

        # 4. 轮询日志，直到出现"启动完成"或超时/进程退出
        $logDir = Join-Path $extract 'logs\app'
        $deadline = (Get-Date).AddSeconds($WaitSeconds)
        $finished = $false
        $logText = ''

        while ((Get-Date) -lt $deadline) {
            Start-Sleep -Milliseconds 700

            $logs = @(Get-ChildItem -LiteralPath $logDir -Filter '*.log' -ErrorAction SilentlyContinue |
                Sort-Object LastWriteTime -Descending)
            if ($logs.Count -gt 0) {
                $logPath = $logs[0].FullName
                # 程序可能**正在写**这个文件：必须用宽松共享打开（Read-SharedFileText）
                $logText = Read-SharedFileText $logPath
                if ($logText -and $logText.Contains('启动完成')) { $finished = $true; break }
            }

            if ($proc.HasExited) {
                # ExitCode 读不到（句柄已失效）时别让整个冒烟挂在属性访问上。
                $exitCode = -1
                try { $exitCode = $proc.ExitCode } catch { }

                if ($exitCode -eq 0) {
                    $notes.Add('进程已退出（退出码 0），未等到「启动完成」—— 见上面关于"单实例互斥"的说明。')
                } else {
                    # 非 0 退出=闪退；另外本仓库常有多个构建/测试同时跑，
                    # 它们惯用的 "Get-Process AICompanion | Stop-Process -Force" 也会把这里起的实例杀掉，
                    # 表现为退出码 -1 且日志停在"已启动进程"之后。
                    $notes.Add(
                        "进程已退出（退出码 $exitCode），未等到「启动完成」。" +
                        '若不是闪退，请确认没有别的构建/测试在同时跑（它们可能会 Stop-Process AICompanion）。')
                }

                break
            }
        }

        if (-not $finished -and $logPath -eq '') {
            # 进程很快就以 0 退出且一行日志都没写，最常见的原因是**单实例互斥**：
            # AICompanion 发现自己不是第一个实例时会 SignalExisting + Shutdown(0)，完全静默。
            # 这种情况不先关掉在跑的实例，冒烟永远过不了 —— 必须把原因说清楚。
            $exitHint = ''
            if ($proc -and $proc.HasExited -and $proc.ExitCode -eq 0) {
                $exitHint = ' 进程很快以退出码 0 结束且没有写日志 —— 典型原因是**已有另一个 AICompanion 实例在运行**' +
                    '（单实例互斥会让第二个实例静默退出）。请先结束所有 AICompanion.exe / llama-server.exe 再重跑。'
            }

            $reasons.Add(
                "${WaitSeconds}s 内没有生成 logs\app\*.log —— 程序可能根本没起来" +
                "（自包含运行时缺失 / 依赖 DLL 缺失 / 闪退）。解压目录：$extract$exitHint")
            return [PSCustomObject]@{ Passed = $false; Reasons = $reasons; Notes = $notes; LogPath = ''; ExtractDir = $extract }
        }

        # 5. 断言（日志读的是**解压目录里**那份，不是仓库的 logs\）
        Write-Host ''
        Write-Host '    ---- 冒烟断言（读解压目录里的日志）----' -ForegroundColor Cyan
        Write-Host "    日志：$logPath"

        if ($logText -and $logText.Contains('启动完成')) {
            Write-Host '    [通过] 日志出现「启动完成」' -ForegroundColor Green
        } else {
            Write-Host '    [失败] 日志没有「启动完成」—— 启动流程没走完' -ForegroundColor Red
            $reasons.Add("日志里没有「启动完成」（等待 ${WaitSeconds}s）")
        }

        if ($logText -and $logText.Contains('LLM 就绪=True')) {
            Write-Host '    [通过] 日志出现「LLM 就绪=True」' -ForegroundColor Green
        } else {
            Write-Host '    [失败] 日志没有「LLM 就绪=True」—— 对话后端没起来' -ForegroundColor Red
            $reasons.Add('日志里没有「LLM 就绪=True」（对话后端未就绪）')
        }

        if ($logText -and $logText.Contains('未找到 llama-server')) {
            Write-Host '    [失败] 日志出现「未找到 llama-server」—— 配置指向的后端不在包里或没被兜底发现' -ForegroundColor Red
            $reasons.Add('日志里出现「未找到 llama-server」')
        } else {
            Write-Host '    [通过] 日志不含「未找到 llama-server」' -ForegroundColor Green
        }

        # 额外提示（不计入失败）：启动问题项数与静态立绘是否就位
        if ($logText -match '启动问题 (\d+) 项') {
            $count = [int]$Matches[1]
            if ($count -eq 0) {
                Write-Host '    [通过] 日志出现「启动问题 0 项」' -ForegroundColor Green
            } else {
                Write-Host "    [提示] 日志写着「启动问题 $count 项」（不作为冒烟失败条件，但值得看一眼）" -ForegroundColor Yellow
            }
        }

        $avatarHits = @([regex]::Matches($logText, '角色形象：使用[^\r\n]*'))
        if ($avatarHits.Count -gt 0) {
            Write-Host "    [通过] 形象素材已就位：$($avatarHits[0].Value)" -ForegroundColor Green
        } else {
            Write-Host '    [提示] 日志里没有「角色形象：使用…」，可人工看截图确认立绘' -ForegroundColor Yellow
        }

        if ($logText -match '未找到任何角色形象素材|角色形象缺失') {
            Write-Host '    [失败] 日志出现「角色形象缺失」—— 静态立绘没进包或路径不对' -ForegroundColor Red
            $reasons.Add('日志出现「角色形象缺失」（静态立绘没生效）')
        }

        $passed = ($reasons.Count -eq 0)

        # 失败时把日志尾巴打出来：临时目录随后会被删掉，不在这里留证据就没得查了。
        if (-not $passed -and $logText) {
            Write-Host ''
            Write-Host '    ---- 日志最后 40 行 ----' -ForegroundColor Yellow
            $tail = @($logText -split "`r?`n") | Where-Object { $_ -ne '' } | Select-Object -Last 40
            foreach ($line in $tail) { Write-Host "    | $line" -ForegroundColor DarkGray }
            Write-Host '    ---- 日志结束 ----' -ForegroundColor Yellow
        }

        return [PSCustomObject]@{ Passed = $passed; Reasons = $reasons; Notes = $notes; LogPath = $logPath; ExtractDir = $extract }
    }
    catch {
        # 带上出错行号：冒烟失败时最想知道的就是"卡在哪一步"，
        # 只打一句异常消息（例如"找不到路径"）根本定位不到是解压、拷模型还是起进程。
        $where = ''
        if ($_.InvocationInfo -and $_.InvocationInfo.ScriptLineNumber) {
            $where = "（pack-release.ps1 第 $($_.InvocationInfo.ScriptLineNumber) 行）"
        }

        $reasons.Add("冒烟过程异常$where：$($_.Exception.Message)")
        return [PSCustomObject]@{ Passed = $false; Reasons = $reasons; Notes = $notes; LogPath = $logPath; ExtractDir = $extract }
    }
    finally {
        # 必须收尾：杀进程（含它拉起的 llama-server）+ 删临时目录，绝不留下跑着的程序
        try {
            if ($proc -and -not $proc.HasExited) {
                Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
            }
        } catch { }

        try {
            Get-Process -Name 'llama-server' -ErrorAction SilentlyContinue | ForEach-Object {
                try {
                    if ($_.Path -and $_.Path.StartsWith($extract, [System.StringComparison]::OrdinalIgnoreCase)) {
                        Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
                    }
                } catch { }
            }
        } catch { }

        Start-Sleep -Milliseconds 600

        for ($i = 0; $i -lt 6; $i++) {
            try {
                if (Test-Path -LiteralPath $WorkDir) { Remove-Item -LiteralPath $WorkDir -Recurse -Force -ErrorAction Stop }
                break
            } catch {
                Start-Sleep -Milliseconds 500
            }
        }

        if (Test-Path -LiteralPath $WorkDir) {
            Write-Host "    ! 临时目录没能删掉（进程还占着文件？）：$WorkDir —— 请手工删除" -ForegroundColor Yellow
        }
    }
}

# ---------------------------------------------------------------- 0. 路径与参数

$root = Split-Path -Parent $PSScriptRoot   # tools\ 的上一级 = 仓库根
$csproj = 'src\AICompanion.App\AICompanion.App.csproj'

if (-not (Test-Path (Join-Path $root $csproj))) {
    throw "定位不到仓库根目录（推导结果：$root）。pack-release.ps1 必须放在 <仓库>\tools\ 下，且 $csproj 必须存在。"
}
Set-Location $root

$versionMatch = Select-String -LiteralPath $csproj -Pattern '<Version>([^<]+)</Version>'
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
Note "含 Live2D Cubism Core：$IncludeCubismCore$(if (-not $IncludeCubismCore) { '（授权项，默认不含；包内会生成说明文件）' } else { '（公开分发前请自行确认 Live2D 授权）' })"
Note "输出目录：$outRoot"

# ---------------------------------------------------------------- 1. 前置检查

Step '1/8 前置检查'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw '未找到 dotnet 命令。请安装 .NET 8 SDK（https://dotnet.microsoft.com/download/dotnet/8.0），并确认 dotnet 在 PATH 里。'
}
$dotnetVer = (& dotnet --version) 2>&1
if ($LASTEXITCODE -ne 0) {
    throw "dotnet --version 退出码 $LASTEXITCODE：dotnet SDK 装坏了，或 PATH 指向了不可用的安装。输出：$dotnetVer"
}
Note "dotnet SDK：$dotnetVer"

# 这些是"发布流程之外、必须由仓库根目录提供"的原生库。
# 缺了不会有编译错误，但目标机上程序会静默降级（ASR 不可用），所以这里硬失败。
#
# Live2DCubismCore.dll **不在**这个列表里：它是 Live2D 的授权项，默认不打进公开包，
# 因此"仓库里有没有它"不再是出包的前提。加 -IncludeCubismCore 时才要求它存在。
$nativeSources = @(
    @{ Path = 'bin\ai_asr.dll';       Why = '语音识别原生库（whisper.cpp 桥接）' },
    @{ Path = 'bin\libfvad.dll';      Why = 'WebRTC VAD 原生库' }
)
foreach ($item in $nativeSources) {
    $full = Join-Path $root $item.Path
    if (-not (Test-Path -LiteralPath $full)) {
        throw "缺少 $($item.Path)（$($item.Why)）。它不是 dotnet publish 的产物，必须放在仓库里对应位置；从 third_party 构建取得后再打包。"
    }
    Note "有 $($item.Path)（$(Get-SizeText (Get-Item -LiteralPath $full).Length)）"
}

$cubismSource = Join-Path $root 'Live2DCubismCore.dll'
if ($IncludeCubismCore) {
    if (-not (Test-Path -LiteralPath $cubismSource)) {
        throw "参数要求打包 Live2DCubismCore.dll，但仓库根目录没有它。请从 Live2D 官网下载官方 Cubism Core（x64）放到 $root 后重试；或去掉 -IncludeCubismCore 打不带它的公开包。"
    }
    Note "有 Live2DCubismCore.dll（$(Get-SizeText (Get-Item -LiteralPath $cubismSource).Length)），将随包（授权归 Live2D）"
} else {
    Warn '按默认策略排除 Live2DCubismCore.dll（Live2D 授权项）：包内只放 Live2DCubismCore.dll.README.txt 说明如何自行放入。'
}

# modules\ 是 LLM 后端的容器，程序按 AppPaths.Root\modules 扫描 module.json。
$moduleDirs = @()
if ($IncludeModules) {
    $modulesRoot = Join-Path $root 'modules'
    if (-not (Test-Path -LiteralPath $modulesRoot)) {
        throw '缺少 modules\ 目录：程序靠它找 LLM 后端（llama-server.exe），没有它分发包无法对话。若确认要出无对话能力的包，请显式加 -IncludeModules:$false。'
    }
    $moduleDirs = @(Get-ChildItem -LiteralPath $modulesRoot -Directory | Where-Object {
        Test-Path -LiteralPath (Join-Path $_.FullName 'module.json')
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
        if (-not (Test-Path -LiteralPath $entry)) {
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

Step '2/8 清理上一次的产物'

# 全部用 -LiteralPath：-OutDir 里出现方括号（D:\release[1]）时，
# Test-Path / Remove-Item 会把 [1] 当成通配符 —— 要么判成"不存在"而跳过清理，
# 要么删错东西。清理失败也要给出可执行的原因（通常是程序还占着文件）。
foreach ($p in @($stage, $work)) {
    if (Test-Path -LiteralPath $p) {
        try {
            Remove-Item -LiteralPath $p -Recurse -Force -ErrorAction Stop
            Note "已删除 $p"
        } catch {
            throw "无法删除上一次的产物 $p：$($_.Exception.Message)。最常见的原因是上一次打出来的 AICompanion.exe / llama-server.exe 还在运行，请先结束它们再重跑。"
        }
    }
}
if (Test-Path -LiteralPath $zipPath) {
    try {
        Remove-Item -LiteralPath $zipPath -Force -ErrorAction Stop
        Note "已删除 $zipPath"
    } catch {
        throw "无法删除旧的发布包 $zipPath：$($_.Exception.Message)。请先关闭正在读取它的程序（解压工具 / 杀毒扫描 / 资源管理器预览）后重跑。"
    }
}
New-Item -ItemType Directory -Path $outRoot -Force | Out-Null
New-Item -ItemType Directory -Path $publishDir -Force | Out-Null
New-Item -ItemType Directory -Path $objOut -Force | Out-Null
New-Item -ItemType Directory -Path $stage -Force | Out-Null

# ---------------------------------------------------------------- 3. dotnet publish

Step "3/8 dotnet publish（Release / $Runtime / self-contained=$($SelfContained.ToString().ToLowerInvariant())）"

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
if (-not (Test-Path -LiteralPath $publishedExe)) {
    throw "dotnet publish 没有产出 AICompanion.exe（目录：$publishDir）。"
}
Good "publish 完成：$publishDir（$(Get-SizeText (Get-DirBytes $publishDir))）"

# 自包含 vs 框架依赖，用产物本身说话，而不是只信参数
$hasCoreClr = Test-Path -LiteralPath (Join-Path $publishDir 'coreclr.dll')
Note "产物里 $(if ($hasCoreClr) { '有' } else { '没有' }) coreclr.dll → 实际是$(if ($hasCoreClr) { '自包含' } else { '框架依赖' })构建"
if ($SelfContained -and -not $hasCoreClr) {
    throw '参数要求自包含，但 publish 产物里没有 coreclr.dll。目标机将因缺少运行时直接闪退，这属于致命缺陷，已中止打包。'
}
if (-not $SelfContained -and $hasCoreClr) {
    throw '参数要求框架依赖，但产物里混进了 coreclr.dll。包会虚胖且行为不可预期，已中止打包。'
}

# ---------------------------------------------------------------- 4. 汇总运行环境

Step '4/8 汇总运行环境（原生库 / 模块 / 配置 / 资源）'

# 4.1 主程序产物
# 不用 Copy-Item (Join-Path $publishDir '*'):
#   路径里带方括号时 '*' 通配会失效/匹配错，而 $publishDir 是拼出来的参数，不能假定没有方括号。
#   逐个顶层项拷过去，行为完全等价且不依赖通配符。
Get-ChildItem -LiteralPath $publishDir -Force | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $stage -Recurse -Force
}
Note "主程序产物 $(@(Get-ChildItem -LiteralPath $publishDir -Recurse -File).Count) 个文件已拷入分发目录"

# 4.2 原生库 → bin\
# 程序默认配置里 asr.library_path = bin/ai_asr.dll、vad_library_path = bin/libfvad.dll，
# 少一层 bin\ 就等于告诉程序"没有 ASR"。这里只拷顶层文件，不递归（bin\cpu-backup 是开发机备用件）。
$binStage = Join-Path $stage 'bin'
New-Item -ItemType Directory -Path $binStage -Force | Out-Null
$binFiles = @(Get-ChildItem -LiteralPath (Join-Path $root 'bin') -File -ErrorAction SilentlyContinue)
if ($binFiles.Count -eq 0) {
    throw '仓库根目录 bin\ 下没有任何文件，ai_asr.dll / libfvad.dll 不会被带上。'
}
foreach ($f in $binFiles) {
    Copy-Item -LiteralPath $f.FullName -Destination $binStage -Force
}
Note "bin\ ← $($binFiles.Count) 个原生库（$(Get-SizeText (Get-DirBytes $binStage))）"

# 4.3 Live2D Cubism Core → 与 exe 同级（**默认不含**，属授权项）
# csproj 里有 None Include + CopyToOutputDirectory，所以只要仓库根有那个 DLL，
# publish 就会把它带进来；不含时必须**删掉**，否则"默认不含"只是句空话。
$cubismTarget = Join-Path $stage 'Live2DCubismCore.dll'
$cubismReadme = Join-Path $stage 'Live2DCubismCore.dll.README.txt'
if ($IncludeCubismCore) {
    if (-not (Test-Path -LiteralPath $cubismTarget)) {
        Copy-Item -LiteralPath $cubismSource -Destination $cubismTarget -Force
        Note 'Live2DCubismCore.dll 由脚本补拷（publish 未带）'
    } else {
        Note 'Live2DCubismCore.dll 已由 publish 带入（随包）'
    }

    if (Test-Path -LiteralPath $cubismReadme) {
        Remove-Item -LiteralPath $cubismReadme -Force
    }
} else {
    if (Test-Path -LiteralPath $cubismTarget) {
        Remove-Item -LiteralPath $cubismTarget -Force
        Note 'Live2DCubismCore.dll 已从产物里删除（Live2D 授权项，默认不含）'
    }

    @(
        '关于 Live2DCubismCore.dll',
        '',
        '本发布包**不含** Live2DCubismCore.dll，因为它是 Live2D 公司的授权组件：',
        'Cubism Core 的分发受到 Live2D Cubism SDK 许可条款约束，与本程序的许可证无关。',
        '未经确认就随包再分发，风险由分发者承担。',
        '',
        '需要 Live2D 形象时，请自行从 Live2D 官网下载官方 Cubism Core：',
        '',
        '  1. 访问 https://www.live2d.com/download/cubism-sdk/ 并同意其许可条款；',
        '  2. 下载 Cubism SDK for Native（Windows x64）；',
        '  3. 从 SDK 里取出 Core\include 同级的 Live2DCubismCore.dll（x64 版）；',
        '  4. 把它放到**本文件所在的同一个目录**（即 AICompanion.exe 旁边）；',
        '  5. 重启 AICompanion.exe。',
        '',
        '没有这个 DLL 时程序仍然能正常启动：形象会按既有降级链退回静态立绘 / 纯色背景，',
        '对话、语音等功能不受影响。启动日志里会写明这一级降级的原因。',
        '',
        '如果是从源码自己打包，可以用 tools\pack-release.ps1 -IncludeCubismCore:$true（或 -WithCubismCore）',
        '把仓库根目录里已下载好的那份一起打进包。',
        '再次提醒：公开分发带该 DLL 的包之前，请自行确认 Live2D 的授权条款。'
    ) | Set-Content -Path $cubismReadme -Encoding UTF8
    Note 'Live2DCubismCore.dll.README.txt（说明用户如何自行放入官方 Cubism Core）'
}

# 4.4 modules\ → modules\
# 程序按 <Root>\modules\<name>\module.json 发现后端；用户可自行增删目录来换后端。
if ($IncludeModules) {
    $modStage = Join-Path $stage 'modules'
    New-Item -ItemType Directory -Path $modStage -Force | Out-Null
    foreach ($d in $moduleDirs) {
        $target = Join-Path $modStage $d.Name
        Copy-Item -LiteralPath $d.FullName -Destination $target -Recurse -Force
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
    if ($IncludeModules -and (Test-Path -LiteralPath (Join-Path $stage 'modules'))) {
        # llama-server.exe 是**独立进程**，它的 DLL 搜索从自己的目录开始，
        # 放在包根目录它找不到，必须也放进各模块目录。
        $crtTargets += @(Get-ChildItem -LiteralPath (Join-Path $stage 'modules') -Directory | Select-Object -ExpandProperty FullName)
    }
    $copiedCrt = @()
    foreach ($name in $crtNames) {
        $src = Join-Path $env:SystemRoot "System32\$name"
        if (-not (Test-Path -LiteralPath $src)) {
            Warn "本机 System32 里没有 $name，无法随包。目标机若缺 VC++ 2015-2022 x64 运行库，ASR/LLM 会加载失败。"
            continue
        }
        foreach ($dst in $crtTargets) {
            Copy-Item -LiteralPath $src -Destination (Join-Path $dst $name) -Force
        }
        $copiedCrt += $name
    }
    if ($copiedCrt.Count -gt 0) {
        $crtBytes = 0
        foreach ($n in $copiedCrt) {
            $probe = Join-Path $stage $n
            if (Test-Path -LiteralPath $probe) { $crtBytes += (Get-Item -LiteralPath $probe).Length * $crtTargets.Count }
        }
        Note "MSVC 运行库 ← $($copiedCrt -join ', ')（包根 + $($crtTargets.Count - 1) 个模块目录，共 $(Get-SizeText $crtBytes)）"
    }
}

# 4.6 配置：模板 → config.yaml
# 仓库根目录的 config.yaml 是开发机上的个人配置（含本机绝对路径、端口、音色），
# 绝不能进包；包里的 config.yaml 一律来自 Core 内嵌的出厂模板。
$tpl = Join-Path $root 'src\AICompanion.Core\Configuration\config.default.yaml'
if (-not (Test-Path -LiteralPath $tpl)) {
    throw "找不到配置模板 $tpl。发布包的 config.yaml 必须由模板生成，不能用仓库根目录的个人配置顶替。"
}
$cfgTarget = Join-Path $stage 'config.yaml'
if (Test-Path -LiteralPath $cfgTarget) {
    # 只有在"目标目录不是新建的"时才会走到这里（正常流程 stage 已被清空）。
    Warn '目标已存在 config.yaml，保留原文件不覆盖（避免冲掉用户已有配置）。'
} else {
    Copy-Item -LiteralPath $tpl -Destination $cfgTarget -Force
    Note 'config.yaml ← 出厂模板（不是开发机配置）'
}
Copy-Item -LiteralPath $tpl -Destination (Join-Path $stage 'config.default.yaml') -Force
Note 'config.default.yaml ← 出厂模板（供用户对照/恢复）'

# 4.7 运行期目录的骨架
# 程序要在自己目录下写 logs\ 与 Assets\appearance.json，目录得能建出来。
# 但**不能**把开发机上的个人状态打进去（窗口位置、日志、待审知识）。
foreach ($junk in @('logs', 'Assets\appearance.json', 'Knowledge\.pending')) {
    $p = Join-Path $stage $junk
    if (Test-Path -LiteralPath $p) {
        Remove-Item -LiteralPath $p -Recurse -Force
        Note "已剔除运行期个人状态：$junk"
    }
}

$assetsDir = Join-Path $stage 'Assets'
New-Item -ItemType Directory -Path (Join-Path $assetsDir 'Default') -Force | Out-Null
$appIco = Join-Path $root 'Assets\app.ico'
if (Test-Path -LiteralPath $appIco) { Copy-Item -LiteralPath $appIco -Destination (Join-Path $assetsDir 'app.ico') -Force }

# 4.7.1 静态立绘：**必须随包**，否则首启只剩"角色形象缺失"的纯色面板
#
# 真实缺陷：包内既没有 Live2D 模型（Hiyori\ 不入包），也没有 Assets\Default 里的立绘，
# 而 config.yaml 的 live2d.default_texture_path / ui.pet_image_path 都指向
# ./Assets/Default/default.png —— 指向一个包里不存在的文件，首启必然是一块空白。
#
# 现在随包的是**本项目自带的占位立绘**（tools\draw_avatar.ps1 用 System.Drawing
# 画出来的扁平矢量风角色，9 张共约 450 KB），作者与授权都属于本仓库，
# 可以合法再分发；用户把自己的图丢进 Assets\Default\ 同名覆盖即可替换。
$artSource = Join-Path $root 'Assets\Default'
$artDefault = Join-Path $artSource 'default.png'
$artProfile = Join-Path $artSource 'AvatarProfile.json'
$artExpressions = Join-Path $artSource 'Expressions'

if (-not (Test-Path -LiteralPath $artDefault)) {
    throw "缺少出厂立绘 Assets\Default\default.png —— 而 config.yaml 的 live2d.default_texture_path 与 ui.pet_image_path 都指向它，不随包就会首启「角色形象缺失」。请先运行 tools\draw_avatar.ps1 生成（可复现的占位立绘），再重跑本脚本。"
}

Copy-Item -LiteralPath $artDefault -Destination (Join-Path $assetsDir 'Default\default.png') -Force
Note "Assets\Default\default.png ← 出厂占位立绘（$(Get-SizeText (Get-Item -LiteralPath $artDefault).Length)）"

if (Test-Path -LiteralPath $artProfile) {
    Copy-Item -LiteralPath $artProfile -Destination (Join-Path $assetsDir 'Default\AvatarProfile.json') -Force
    Note 'Assets\Default\AvatarProfile.json ← 动作/口型参数映射'
}

if (Test-Path -LiteralPath $artExpressions) {
    $exprTarget = Join-Path $assetsDir 'Default\Expressions'
    New-Item -ItemType Directory -Path $exprTarget -Force | Out-Null
    $exprFiles = @(Get-ChildItem -LiteralPath $artExpressions -File -Filter '*.png')
    foreach ($f in $exprFiles) { Copy-Item -LiteralPath $f.FullName -Destination $exprTarget -Force }
    if ($exprFiles.Count -gt 0) {
        Note "Assets\Default\Expressions\ ← $($exprFiles.Count) 张表情图（$(Get-SizeText (Get-DirBytes $exprTarget))）"
    }
}

@(
    '静态立绘目录。程序按以下顺序取形象，缺哪一级就退到下一级：',
    '  1. 用户用「更换外观」指定的图片（记录在 Assets\appearance.json，优先）',
    '  2. Live2D 模型（config.yaml 的 live2d.default_model_path，模型不随包）',
    '  3. 本目录里的静态立绘（任意文件名，default* 优先，PNG/JPG/BMP/GIF/TIF）',
    '  4. 纯色块',
    '',
    '本目录里的 default.png 是**程序自带的占位立绘**（扁平矢量风，由 tools\draw_avatar.ps1 生成，',
    '授权随本程序，可自由再分发）。想换成自己的角色：',
    '  - 直接覆盖 Assets\Default\default.png（保持文件名，config.yaml 不必改）；',
    '  - 或丢一张任意名字的 png/jpg 进来，删掉/改名 default.png —— 程序会扫本目录取图；',
    '  - 想换 Live2D 模型：把模型目录放到程序根目录，改 live2d.default_model_path，',
    '    并按包根「素材说明.md」自备 Live2DCubismCore.dll。',
    '',
    '第三方立绘/模型涉及他人著作权，替换时请自行确认授权。'
) | Set-Content -Path (Join-Path $assetsDir 'Default\README.txt') -Encoding UTF8

New-Item -ItemType Directory -Path (Join-Path $stage 'Knowledge') -Force | Out-Null
@(
    '知识库目录（热重载）：放 .md / .txt，程序会自动索引。',
    '',
    '本包只带空目录，不含任何个人知识条目。'
) | Set-Content -Path (Join-Path $stage 'Knowledge\README.txt') -Encoding UTF8

if (Test-Path -LiteralPath (Join-Path $root 'README.md')) {
    Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination $stage -Force
    Note 'README.md'
}

# 4.8 模型
if ($IncludeModel) {
    $modelsRoot = Join-Path $root 'models'
    if (-not (Test-Path -LiteralPath $modelsRoot)) {
        throw '参数要求打包模型，但仓库根目录没有 models\。'
    }
    $modelStage = Join-Path $stage 'models'
    New-Item -ItemType Directory -Path $modelStage -Force | Out-Null
    foreach ($d in @(Get-ChildItem -LiteralPath $modelsRoot -Directory)) {
        Copy-Item -LiteralPath $d.FullName -Destination (Join-Path $modelStage $d.Name) -Recurse -Force
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
    $pdbs = @(Get-ChildItem -LiteralPath $stage -Recurse -File -Filter '*.pdb' -ErrorAction SilentlyContinue)
    if ($pdbs.Count -gt 0) {
        $freed = ($pdbs | Measure-Object Length -Sum).Sum
        # 逐个删：路径用 -LiteralPath，且不依赖管道绑定（空数组时管道会报参数绑定失败）。
        foreach ($pdb in $pdbs) { Remove-Item -LiteralPath $pdb.FullName -Force }
        Note "删除 $($pdbs.Count) 个 .pdb，省 $(Get-SizeText $freed)"
    }

    # WPF 为 14 种语言各带一份卫星程序集，本项目只需要中文
    $keepLangs = @('zh-Hans', 'zh-Hant')
    $langDirs = @(Get-ChildItem -LiteralPath $stage -Directory | Where-Object {
        $_.Name -match '^[a-z]{2}(-[A-Z]{2})?$' -and ($_.Name -notin $keepLangs)
    })
    if ($langDirs.Count -gt 0) {
        $freed = 0
        foreach ($d in $langDirs) {
            $freed += (Get-ChildItem -LiteralPath $d.FullName -Recurse -File -ErrorAction SilentlyContinue | Measure-Object Length -Sum).Sum
            Remove-Item -LiteralPath $d.FullName -Recurse -Force
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
    "Live2D Cubism Core：$(if ($IncludeCubismCore) { '随包（授权归 Live2D，商业分发需自行确认）' } else { '未随包（Live2D 授权项，见 Live2DCubismCore.dll.README.txt）' })"
    ''
    '启动：AICompanion.exe'
)
$runtimeLines | Set-Content -Path (Join-Path $stage 'VERSION.txt') -Encoding UTF8
Note 'VERSION.txt（版本 + 运行环境）'

# 4.11 素材说明.md —— 代码里多处提示"详见同目录的素材说明.md"，那个文件必须真的在
#
# 真实缺陷：日志与气泡都在说"详见同目录的「素材说明.md」"，而包里从来没有这个文件
# （旧的 tools\pack.ps1 会生成，pack-release.ps1 漏了）——用户照着提示去翻包根，
# 什么也找不到。这里补上，内容与 docs\打包与分发.md 的口径保持一致：
# 哪些素材随包、哪些要自备、各自的授权归谁、从哪一步取。
$materialsNotes = @(
    '# 素材说明',
    '',
    "本包（AICompanion $version，$Runtime / $(if ($SelfContained) { '自包含' } else { '框架依赖' })）里",
    '哪些素材是自带的、哪些要你自备，一并写清楚。与仓库 `docs\打包与分发.md` 的口径一致。',
    '',
    '## 1. 一句话结论',
    '',
    '| 素材 | 本包是否包含 | 没有时程序会怎样 |',
    '| --- | --- | --- |',
    '| 静态立绘 `Assets\Default\default.png` | **包含**（程序自带的占位立绘） | —— |',
    '| Live2D 模型（`*.model3.json` + 贴图） | **不包含** | 用静态立绘显示，功能不受影响 |',
    '| `Live2DCubismCore.dll`（Live2D 官方原生库） | **不包含**（授权项） | 只影响 Live2D 渲染，静态立绘照常显示 |',
    '| LLM 模型 `models\llm\*.gguf` | **不包含**（1 GB 起） | 只能看形象，对话不可用 |',
    '| ASR 模型 `models\asr\ggml-base.bin` | **不包含**（约 141 MB） | 语音识别不可用，文字对话照常 |',
    '| Piper 音色 `models\piper\` | **不包含** | TTS 走 Windows SAPI 兜底 |',
    '',
    '## 2. 静态立绘（随包，可自由替换）',
    '',
    '`Assets\Default\default.png` 是**程序自带的占位立绘**：扁平矢量风的角色剪影，',
    '由仓库里的 `tools\draw_avatar.ps1` 用 System.Drawing 生成（可复现），',
    '作者与授权都属于本程序，可随包自由再分发。',
    '',
    '替换方法（三选一，都不用改代码）：',
    '',
    '1. 直接覆盖 `Assets\Default\default.png`（保持文件名）；',
    '2. 丢一张任意名字的 `png/jpg/bmp/gif/tif` 进 `Assets\Default\`；程序按',
    '   "名字以 default 开头优先 → 否则取最新修改时间"的顺序挑；',
    '3. 用程序里的「更换外观」按钮挑一张图（结果写进 `Assets\appearance.json`，优先于上面两条）。',
    '',
    '> 第三方立绘涉及他人著作权，替换前请自行确认授权；本包只为你准备了自带的占位图，',
    '> 不代你分发别人的画。',
    '',
    '## 3. Live2D 模型（不随包，自备）',
    '',
    'Live2D 模型由 Live2D 公司及其样例作者授权，**不属于本程序的许可证范围**，',
    '因此不随包分发。需要时：',
    '',
    '1. 取得模型目录（官方样例里的 `Hiyori\` 最省事），整个目录放到程序根目录，',
    '   形如 `Hiyori\Hiyori.model3.json` + `Hiyori.2048\texture_00.png` + `*.motion3.json`；',
    '2. 把 `config.yaml` 的 `live2d.default_model_path` 指到那个 `*.model3.json`，例如：',
    '   `default_model_path: "./Hiyori/Hiyori.model3.json"`；',
    '3. 还需要下面第 4 节的 `Live2DCubismCore.dll`，否则渲染器起不来，程序会退回静态立绘；',
    '4. 重启程序。日志里应出现「Live2D 渲染器已就绪」与「已切换到 Live2D 渲染」。',
    '',
    '官方样例模型的下载页与许可条款见 Live2D 官网（Cubism SDK / 示例模型页面）。',
    '',
    '## 4. `Live2DCubismCore.dll`（不随包，自备）',
    '',
    '它是 Live2D Cubism SDK 的原生核心，**分发受 Live2D Cubism SDK 许可条款约束**，',
    '与本程序的许可证无关；未经确认就随包再分发，风险由分发者承担。所以默认包不含它，',
    '包内另有 `Live2DCubismCore.dll.README.txt` 记录同样的说明。',
    '',
    '自取步骤：',
    '',
    '1. 访问 https://www.live2d.com/download/cubism-sdk/ 并同意其许可条款；',
    '2. 下载 **Cubism SDK for Native**（Windows x64）；',
    '3. 从 SDK 里取出 x64 版 `Live2DCubismCore.dll`；',
    '4. 放到**本文件所在的同一个目录**（即 `AICompanion.exe` 旁边）；',
    '5. 重启 `AICompanion.exe`。',
    '',
    '没有它时程序照常启动：形象按"Live2D → 静态立绘 → 纯色块"的降级链走，对话与语音不受影响。',
    '',
    '## 5. 对话模型（不随包）',
    '',
    '`models\llm\*.gguf` 自带独立授权（Qwen / DeepSeek 等），且 1 GB 起，故不随包。',
    '放置位置与仓库一致，例如 `models\llm\qwen2.5-1.5b-instruct-q4_k_m.gguf`，',
    '并让 `config.yaml` 的 `llm.model_path` 指向它；详见包内 `models\README.txt`。',
    '',
    '## 6. 语音素材（不随包）',
    '',
    '- ASR：`models\asr\ggml-base.bin`（约 141 MB，whisper.cpp 格式）；',
    '- TTS：`models\tts\*.wav` 参考音频（GPT-SoVITS / CosyVoice 用）、`models\piper\` 音色。',
    '  本仓库不提供 `piper.exe`，默认 TTS 走 Windows SAPI，Piper 属可选增强。',
    '',
    '## 7. 对话后端模块（随包）',
    '',
    '`modules\` 下是 llama-server 后端（Vulkan / CUDA）。它们由 llama.cpp（MIT）构建，',
    '随包分发没有额外授权问题；`config.yaml` 默认用 `modules/llm-vulkan/llama-server.exe`',
    '（Vulkan 由显卡驱动提供，干净机器可用），CUDA 模块是可选项，可在「设置 → 拓展模块」切换。',
    '',
    '---',
    '',
    '以上说明与仓库 `docs\打包与分发.md` 的第 2 / 5 / 6 节一致；两者若不一致，以仓库文档为准。'
)
$materialsNotes | Set-Content -Path (Join-Path $stage '素材说明.md') -Encoding UTF8
Note '素材说明.md（哪些素材随包、哪些自备、授权归谁）'

# 一次遍历拿到"文件数 + 总字节"：以前 Get-DirBytes 与 Get-ChildItem -Recurse 各扫一遍 stage，
# 700 多个文件就是两遍全目录遍历（机械盘上能差出几百毫秒）。
$stageStats = Get-DirStats $stage
$stageBytes = $stageStats.Bytes
$stageCount = $stageStats.Count
Good "分发目录：$stage（$stageCount 个文件，$(Get-SizeText $stageBytes)）"

# ---------------------------------------------------------------- 5. 打 zip

Step '5/8 生成 zip'

$zippedCount = New-ReleaseZip -SourceDir $stage -TargetZip $zipPath
if (-not (Test-Path -LiteralPath $zipPath)) {
    throw "压缩失败：没有生成 $zipPath。"
}
$zipBytes = (Get-Item -LiteralPath $zipPath).Length
Good "已生成 $zipPath（$zippedCount 个条目，$(Get-SizeText $zipBytes)）"

# ---------------------------------------------------------------- 6. 校验（核心）

Step '6/8 校验 zip 内容'

# System.IO.Compression 在 PS 5.1 里要显式加载（前面已加过一次，这里保证独立可用）
try {
    Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction Stop
} catch {
    throw "校验阶段需要 System.IO.Compression.FileSystem：$($_.Exception.Message)"
}

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
        # Live2D Cubism Core 默认不含（授权项）。含与不含的要求相反，两条都要能真的失败：
        #   含 → 必须有 DLL，且不能同时留着"请自备"的说明（否则说明在骗人）；
        #   不含 → 必须有说明文件，且**必须没有** DLL（下面"反向断言"里再兜一道）。
        @{ Key = 'Live2DCubismCore.dll';   Pattern = '(^|/)Live2DCubismCore\.dll$';           Required = $IncludeCubismCore; Why = 'Live2D 渲染原生库（仅 -IncludeCubismCore 时必需）' }
        @{ Key = 'Live2DCubismCore.dll.README.txt'; Pattern = '(^|/)Live2DCubismCore\.dll\.README\.txt$'; Required = (-not $IncludeCubismCore); Why = '不含 Cubism Core 时的自备说明' }
        @{ Key = 'ai_asr.dll';             Pattern = '(^|/)ai_asr\.dll$';                     Required = $true;  Why = '语音识别原生库' }
        @{ Key = 'modules/llm-*/llama-server.exe'; Pattern = '^modules/llm-[^/]+/llama-server\.exe$'; Required = $IncludeModules; Why = 'LLM 后端入口' }
        @{ Key = 'config.yaml';            Pattern = '^config\.yaml$';                        Required = $true;  Why = '用户配置（出厂模板）' }
        @{ Key = 'coreclr.dll';            Pattern = '(^|/)coreclr\.dll$';                    Required = $SelfContained; Why = '自带 .NET 运行时' }
        # 以下只提示
        @{ Key = 'libfvad.dll';            Pattern = '(^|/)libfvad\.dll$';                    Required = $false; Why = 'VAD 原生库' }
        @{ Key = 'hostfxr.dll';            Pattern = '(^|/)hostfxr\.dll$';                    Required = $false; Why = '自包含宿主' }
        @{ Key = 'config.default.yaml';    Pattern = '(^|/)config\.default\.yaml$';           Required = $false; Why = '模板副本' }
        @{ Key = 'VERSION.txt';            Pattern = '(^|/)VERSION\.txt$';                    Required = $false; Why = '版本与环境说明' }
        @{ Key = 'models/README.txt';      Pattern = '^models/README\.txt$';                  Required = (-not $IncludeModel); Why = '模型不入包时的放置说明' }
        @{ Key = 'module.json';            Pattern = '(^|/)module\.json$';                    Required = $IncludeModules; Why = '模块清单（程序靠它发现后端）' }
        @{ Key = 'msvcp140.dll';           Pattern = '(^|/)msvcp140\.dll$';                   Required = (-not $NoVcRuntime); Why = 'MSVC 运行库' }
        # 出厂形象与素材说明：配置指向它们、日志也在提示"详见素材说明.md"，缺了就是开箱不可用。
        @{ Key = 'Assets/Default/default.png'; Pattern = '^Assets/Default/default\.png$';      Required = $true;  Why = '出厂静态立绘（没有 Live2D 模型时的首启形象）' }
        @{ Key = '素材说明.md';            Pattern = '^素材说明\.md$';                        Required = $true;  Why = '素材授权与自备说明（代码多处提示要读它）' }
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

    # ---- 一致性断言：config.yaml 里写的路径，包里必须真的有 ----
    #
    # 这条断言针对的正是"静态断言全绿、程序却开箱不可用"的那类缺陷：
    #   * llm.server_executable_path 曾写 bin/llama-server.exe，而包里后端在 modules\；
    #   * live2d.default_texture_path / ui.pet_image_path 曾指向包里没有的 default.png。
    # 只看"AICompanion.exe 在不在"是查不出来的，必须拿配置去对文件清单。
    Write-Host ''
    Write-Host '---- 配置与包内容一致性 ----' -ForegroundColor Cyan
    $configInStage = Join-Path $stage 'config.yaml'
    $referencedPaths = @(
        @{ Key = 'server_executable_path'; Why = 'LLM 后端入口（缺了首启必报「未找到 llama-server」）'; Required = $IncludeModules }
        @{ Key = 'default_texture_path';  Why = '静态立绘（缺了首启只剩纯色面板）';                   Required = $true }
        @{ Key = 'pet_image_path';        Why = '角色图片（缺了靠静态立绘兜底）';                     Required = $true }
        @{ Key = 'vad_library_path';      Why = 'WebRTC VAD 原生库（缺了回退能量 VAD）';              Required = $false }
    )
    foreach ($r in $referencedPaths) {
        $value = Get-ConfigPathValue $configInStage $r.Key
        if (-not $value) {
            Write-Host "    [提示] config.yaml 里的 $($r.Key) 为空或没写 —— $($r.Why)" -ForegroundColor DarkGray
            continue
        }

        $hit = @($names | Where-Object { $_ -eq $value }).Count -gt 0
        if ($hit) {
            Write-Host "    [通过] $($r.Key) = $value 在包里 —— $($r.Why)" -ForegroundColor Green
        } elseif ([bool]$r.Required) {
            Write-Host "    [缺失] $($r.Key) = $value **不在包里** —— $($r.Why)" -ForegroundColor Red
            $failures += "config.yaml 的 $($r.Key) 指向 $value，但包里没有这个文件（$($r.Why)）"
        } else {
            Write-Host "    [提示] $($r.Key) = $value 不在包里 —— $($r.Why)（可选）" -ForegroundColor DarkGray
        }
    }

    # ---- 反向断言：这些"绝不该出现"的东西混进来时，必须让脚本失败 ----
    # 只断言"必需项存在"是不够的：开发机的 logs\、appearance.json、以及被明确排除的
    # 模型 / Cubism Core 一旦漏进包，正向断言全绿而包是脏的。
    $forbidden = @(
        @{ Key = 'Assets/appearance.json'; Pattern = '(^|/)appearance\.json$'; Why = '开发机的界面覆盖状态（窗口位置等个人数据）' }
        @{ Key = 'logs/';                  Pattern = '^logs/';                   Why = '开发机运行日志' }
        @{ Key = 'Knowledge/.pending/';    Pattern = '(^|/)\.pending/';          Why = '开发机的待审核知识' }
    )
    if (-not $IncludeModel) {
        $forbidden += @{ Key = 'models/ 里的模型文件'; Pattern = '^models/.+\.(gguf|bin|onnx|pth|ckpt)$'; Why = '本次打包明确排除模型' }
    }
    if (-not $IncludeCubismCore) {
        $forbidden += @{ Key = 'Live2DCubismCore.dll'; Pattern = '(^|/)Live2DCubismCore\.dll$'; Why = 'Live2D 授权项，本次打包明确排除' }
    } else {
        $forbidden += @{ Key = 'Live2DCubismCore.dll.README.txt'; Pattern = '(^|/)Live2DCubismCore\.dll\.README\.txt$'; Why = 'DLL 已随包，不该再留"请自备"的说明（会自相矛盾）' }
    }
    foreach ($c in $forbidden) {
        $hits = @($names | Where-Object { $_ -match $c.Pattern })
        if ($hits.Count -eq 0) {
            Write-Host "    [通过] 不含 $($c.Key) —— $($c.Why)" -ForegroundColor Green
        } else {
            Write-Host "    [混入] 含 $($c.Key)（$($hits.Count) 个）—— $($c.Why)" -ForegroundColor Red
            $failures += "混入了 $($c.Key)（$($c.Why)）"
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
        Write-Host "校验失败：$($failures.Count) 项不满足要求（缺必需项 / 混入排除项）" -ForegroundColor Red
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

Step '7/8 运行环境'

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
if ($IncludeCubismCore) {
    Write-Host '    Live2D：包内**含** Live2DCubismCore.dll，其授权归 Live2D 公司；公开分发前须自行确认 Cubism SDK 的许可条款'
} else {
    Write-Host '    Live2D：包内**不含** Live2DCubismCore.dll（授权项）。用户需从 Live2D 官网下载官方 Cubism Core 放到程序根目录，见包内 Live2DCubismCore.dll.README.txt；没有它时形象降级为静态立绘/纯色块，其它功能不受影响'
}
Write-Host ('-' * 62) -ForegroundColor Cyan

# ---------------------------------------------------------------- 8. 冒烟断言

$smokeSummary = '未执行（-SkipSmokeTest）'

if ($SkipSmokeTest) {
    Step '8/8 冒烟断言（已跳过）'
    Warn '按 -SkipSmokeTest 跳过"解压 → 真启动 → 读日志"。静态断言已通过，但"能不能跑"未经验证。'
} else {
    Step '8/8 冒烟断言（解压 → 真启动 → 读日志）'

    $smokeRoot = if ([string]::IsNullOrWhiteSpace($SmokeWorkDir)) {
        # 必须在仓库外：仓库里出现解压副本会污染 git status，也容易被误当成交付物。
        # 用系统临时目录而不是硬编码 C:\Temp：后者在部分机器上不存在（或没有写权限），
        # 而 GetTempPath 一定存在、且跟随用户的 TEMP 设置。
        Join-Path ([System.IO.Path]::GetTempPath()) "AICompanion-smoke-$version-$flavor"
    } else {
        $SmokeWorkDir
    }

    # 说明清楚"这个目录会被整体删除"，免得有人以为它是输出目录。
    Note "临时目录：$smokeRoot（结束后删除）"
    Note "等待上限：${SmokeWaitSeconds}s"

    $smoke = Invoke-PackageSmokeTest `
        -ZipPath $zipPath `
        -WorkDir $smokeRoot `
        -ModelPath $SmokeModelPath `
        -RepoModelsDir (Join-Path $root 'models\llm') `
        -RepoRoot $root `
        -WaitSeconds $SmokeWaitSeconds

    foreach ($n in $smoke.Notes) { Note $n }

    if ($smoke.Passed) {
        Good '冒烟通过：解压即可启动，LLM 就绪=True，没有「未找到 llama-server」'
        $smokeSummary = '通过（解压启动，LLM 就绪=True，无「未找到 llama-server」）'
    } else {
        Write-Host ''
        Write-Host "冒烟失败：$($smoke.Reasons.Count) 项不满足要求" -ForegroundColor Red
        foreach ($r in $smoke.Reasons) { Write-Host "  - $r" -ForegroundColor Red }
        Write-Host ''
        Write-Host '这个包**不能发**：静态断言只证明"文件都在"，冒烟才证明"启动起来能用"。' -ForegroundColor Red
        Write-Host '定位建议：' -ForegroundColor Red
        Write-Host '  * 没有 logs\app\*.log        → 双击就闪退，查自包含运行时 / VC 运行库' -ForegroundColor Red
        Write-Host '  * 有「未找到 llama-server」  → llm.server_executable_path 与包内 modules\ 不一致' -ForegroundColor Red
        Write-Host '  * 没有「LLM 就绪=True」      → 后端起来了但 /health 没通，查模型路径与显存' -ForegroundColor Red
        Write-Host '  * 有「角色形象缺失」        → Assets\Default\default.png 没进包' -ForegroundColor Red
        Write-Host ''
        Write-Host "包未通过冒烟：$zipPath" -ForegroundColor Red
        Write-Host "体积/文件数：$(Get-SizeText $zipBytes)，$($entries.Count) 个文件；解压目录：$($smoke.ExtractDir)" -ForegroundColor Red
        exit 1
    }
}

Write-Host ''
Write-Host "打包成功" -ForegroundColor Green
Write-Host "    分发目录：$stage"
Write-Host "    发布包：  $zipPath（$(Get-SizeText $zipBytes)，$($entries.Count) 个文件）"
Write-Host "    文件数/体积断言：全部通过" -ForegroundColor Green
Write-Host "    冒烟断言：$smokeSummary"
Write-Host ''

exit 0
