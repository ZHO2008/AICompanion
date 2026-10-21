# ============================================================================
#  pack.ps1 - 生成可分发目录、zip 与安装包
#
#  默认行为（"带所有环境"）：
#    - self-contained：自带 .NET 8 桌面运行时，目标机器不需要装任何东西
#    - ReadyToRun：启动更快（体积略增）
#    - 含模型：LLM + ASR 一起打进安装包，装完即可对话
#
#  用法：
#    powershell -ExecutionPolicy Bypass -File tools\pack.ps1
#    powershell -ExecutionPolicy Bypass -File tools\pack.ps1 -IncludeModels:$false   （约 190 MB）
#    powershell -ExecutionPolicy Bypass -File tools\pack.ps1 -SelfContained:$false   （约 60 MB）
#    powershell -ExecutionPolicy Bypass -File tools\pack.ps1 -NoInstaller            只出目录与 zip
#
#  产物：
#    dist\AICompanion-<版本>-win-x64\        可直接运行的分发目录
#    dist\AICompanion-<版本>-win-x64.zip
#    dist\AICompanion-<版本>-full-setup.exe  Inno Setup 安装包
# ============================================================================

[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',

    [bool]$SelfContained = $true,
    [bool]$IncludeModels = $true,
    [bool]$ReadyToRun = $true,
    [bool]$ExcludeSymbols = $true,

    [bool]$Zip = $false,
    [switch]$NoInstaller
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

function Step([string]$text) {
    Write-Host ''
    Write-Host "==> $text" -ForegroundColor Cyan
}

function SizeOf([string]$path) {
    if (-not (Test-Path $path)) { return '0 MB' }
    $bytes = (Get-ChildItem $path -Recurse -File -ErrorAction SilentlyContinue | Measure-Object Length -Sum).Sum
    if ($bytes -ge 1GB) { return ('{0:N2} GB' -f ($bytes / 1GB)) }
    return ('{0:N1} MB' -f ($bytes / 1MB))
}

# ---------------------------------------------------------------- 0. 前置检查
Step '检查前置条件'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw '未找到 dotnet SDK，请先安装 .NET 8 SDK。'
}

# 发布输出重定向到 artifacts\pkgbin（见下方 -p:OutputPath），程序运行中也能打包。
$running = Get-Process AICompanion -ErrorAction SilentlyContinue
if ($running) {
    Write-Host "    注意：AICompanion 正在运行（PID $($running.Id)）。发布输出已重定向，打包可继续。" -ForegroundColor Yellow
}

$version = (Select-String -Path 'src\AICompanion.App\AICompanion.App.csproj' -Pattern '<Version>([^<]+)</Version>').Matches[0].Groups[1].Value
if (-not $version) { $version = '0.0.0' }

$suffix = if ($IncludeModels) { 'full' } else { 'slim' }
$distName = "AICompanion-$version-$Runtime"
$stage = Join-Path $root "dist\$distName"
$pkgBin = Join-Path $root 'artifacts\pkgbin'
$zipPath = Join-Path $root "dist\$distName.zip"

Write-Host "    版本：$version"
Write-Host "    自带运行时：$SelfContained　ReadyToRun：$ReadyToRun"
Write-Host "    含模型：$IncludeModels　分发目录：$distName"

if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Path $stage -Force | Out-Null

# ---------------------------------------------------------------- 1. 发布主程序
Step '发布主程序'

& dotnet publish 'src\AICompanion.App\AICompanion.App.csproj' `
    -c $Configuration -r $Runtime `
    --self-contained ($SelfContained.ToString().ToLowerInvariant()) `
    -p:PublishReadyToRun=$($ReadyToRun.ToString().ToLowerInvariant()) `
    -p:OutputPath=$pkgBin\ `
    -o $stage --nologo -v q

if ($LASTEXITCODE -ne 0) { throw '主程序发布失败。' }

# ---------------------------------------------------------------- 2. 发布监控
Step '发布 GameMonitor'

# 必须与主程序同目录：AppHost 只在 AppPaths.Root / BaseDirectory 找它
& dotnet publish 'src\GameMonitor\GameMonitor.csproj' `
    -c $Configuration -r $Runtime `
    --self-contained ($SelfContained.ToString().ToLowerInvariant()) `
    -p:PublishReadyToRun=$($ReadyToRun.ToString().ToLowerInvariant()) `
    -p:OutputPath=$pkgBin\gm\ `
    -o $stage --nologo -v q

if ($LASTEXITCODE -ne 0) { throw 'GameMonitor 发布失败。' }

# ---------------------------------------------------------------- 3. 运行期资源
Step '拷贝配置与资源'

Copy-Item 'config.yaml' $stage -Force

$defaultCfg = 'src\AICompanion.Core\Configuration\config.default.yaml'
if (Test-Path $defaultCfg) { Copy-Item $defaultCfg (Join-Path $stage 'config.default.yaml') -Force }

foreach ($dir in @('Assets', 'Hiyori', 'Knowledge')) {
    if (Test-Path $dir) {
        Copy-Item $dir $stage -Recurse -Force
        Write-Host "    + $dir\ ($(SizeOf $dir))"
    }
}

if (Test-Path 'game_list.txt') { Copy-Item 'game_list.txt' $stage -Force }
if (Test-Path 'README.md') { Copy-Item 'README.md' $stage -Force }
if (Test-Path 'Live2DCubismCore.dll') { Copy-Item 'Live2DCubismCore.dll' $stage -Force }

if (Test-Path 'bin') {
    $runtimeDir = Join-Path $stage 'bin'
    New-Item -ItemType Directory -Path $runtimeDir -Force | Out-Null
    Get-ChildItem 'bin' -File | Copy-Item -Destination $runtimeDir -Force
    Write-Host "    + bin\ ($((Get-ChildItem 'bin' -File).Count) 个原生库，$(SizeOf 'bin'))"
}

# ---------------------------------------------------------------- 4. 模型
Step ($(if ($IncludeModels) { '打包模型（LLM + ASR）' } else { '跳过模型' }))

if ($IncludeModels) {
    if (Test-Path 'models') {
        $modelStage = Join-Path $stage 'models'
        New-Item -ItemType Directory -Path $modelStage -Force | Out-Null
        Get-ChildItem 'models' -Directory | ForEach-Object {
            Copy-Item $_.FullName $modelStage -Recurse -Force
            Write-Host "    + models\$($_.Name) ($(SizeOf $_.FullName))"
        }
    } else {
        Write-Warning '  models 目录不存在，跳过。'
    }
} else {
    $modelDir = Join-Path $stage 'models'
    New-Item -ItemType Directory -Path $modelDir -Force | Out-Null
    @(
        '把模型放这里（结构与仓库一致）：',
        '',
        '  models\llm\*.gguf                 对话模型（必需）',
        '  models\asr\ggml-base.bin          语音识别模型（可选）',
        '  models\piper\zh_CN\*.onnx         Piper 语音合成（可选）',
        '',
        '或用 tools\pack.ps1 -IncludeModels 直接打包进去。'
    ) | Set-Content -Path (Join-Path $modelDir 'README.txt') -Encoding UTF8
}

# ---------------------------------------------------------------- 4.5 免安装部署脚本
if (Test-Path 'installer\install.ps1') {
    Copy-Item 'installer\install.ps1' $stage -Force
    Write-Host '    + install.ps1（免 Inno Setup 的部署方式）'
}

# ---------------------------------------------------------------- 4.6 瘦身
# ---------------------------------------------------------------- 4.55 精简语言资源
# WPF/框架为 14 种语言各带一份卫星程序集（每种约 1.2 MB），本项目只需中文。
Step '精简语言资源'
$keepLangs = @('zh-Hans', 'zh-Hant')
$langDirs = @(Get-ChildItem $stage -Directory | Where-Object {
    $_.Name -match '^[a-z]{2}(-[A-Z]{2})?$' -and ($_.Name -notin $keepLangs)
})
if ($langDirs.Count -gt 0) {
    $freedBytes = 0
    foreach ($d in $langDirs) {
        $freedBytes += (Get-ChildItem $d.FullName -Recurse -File -ErrorAction SilentlyContinue | Measure-Object Length -Sum).Sum
        Remove-Item $d.FullName -Recurse -Force -ErrorAction SilentlyContinue
    }
    Write-Host "    删除 $($langDirs.Count) 个语言目录，节省 $([math]::Round($freedBytes/1MB,1)) MB"
}

if ($ExcludeSymbols) {
    Step '排除调试符号'
    $pdbs = Get-ChildItem $stage -Recurse -File -Filter '*.pdb' -ErrorAction SilentlyContinue
    if ($pdbs) {
        $freed = ($pdbs | Measure-Object Length -Sum).Sum
        $pdbs | Remove-Item -Force -ErrorAction SilentlyContinue
        Write-Host "    删除 $($pdbs.Count) 个 .pdb，节省 $([math]::Round($freed/1MB,1)) MB"
    }
}

# ---------------------------------------------------------------- 5. 版本信息
Step '写版本信息'

@(
    "AICompanion $version"
    "构建时间：$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
    "目标运行时：$Runtime（self-contained=$SelfContained）"
    "含模型：$IncludeModels"
    "ReadyToRun：$ReadyToRun"
    ''
    '启动：AICompanion.exe'
) | Set-Content -Path (Join-Path $stage 'VERSION.txt') -Encoding UTF8

$stageSize = SizeOf $stage
Write-Host "    分发目录体积：$stageSize"

# ---------------------------------------------------------------- 6. 压缩
if ($Zip) {
    Step '生成 zip'

    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zipPath -CompressionLevel Optimal

    Write-Host "    $zipPath（$([math]::Round((Get-Item $zipPath).Length/1MB,1)) MB）"
}

# ---------------------------------------------------------------- 7. 安装包
if (-not $NoInstaller) {
    Step '编译安装包（Inno Setup 6）'

    $iscc = @(
        'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
        'C:\Program Files\Inno Setup 6\ISCC.exe'
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1

    if ($iscc) {
        $issArgs = @(
            "/DMyVersion=$version",
            "/DStageDir=$stage",
            "/DSelfContained=$($SelfContained.ToString().ToLowerInvariant())",
            "/DIncludeModels=$($IncludeModels.ToString().ToLowerInvariant())",
            "/DOutSuffix=$suffix",
            "/DIslPath=$root\installer\ChineseSimplified.isl",
            'installer\AICompanion.iss'
        )
        & $iscc @issArgs | Select-Object -Last 6 | ForEach-Object { '    ' + $_ }

        if ($LASTEXITCODE -eq 0) {
            $setup = Get-ChildItem (Join-Path $root 'dist') -Filter "AICompanion-$version-$suffix-setup.exe" -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($setup) { Write-Host "    $($setup.FullName)（$([math]::Round($setup.Length/1MB,1)) MB）" -ForegroundColor Green }
        } else {
            Write-Warning '    Inno Setup 编译失败，请查看上方输出。'
        }
    } else {
        Write-Host '    未安装 Inno Setup 6，跳过安装包。'
        Write-Host '    下载：https://jrsoftware.org/isdl.php'
    }
}

Step '完成'
Write-Host "    分发目录：$stage（$stageSize）"
if ($Zip) { Write-Host "    压缩包：  $zipPath" }
