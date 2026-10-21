# ============================================================================
#  install.ps1 - 免安装工具（不需要 Inno Setup）的部署脚本
#
#  由 tools\pack.ps1 复制进分发包根目录。用法：
#    powershell -ExecutionPolicy Bypass -File install.ps1
#    powershell -ExecutionPolicy Bypass -File install.ps1 -NoDesktopShortcut
#
#  做的事：
#    1) 把当前目录的文件复制到 %LOCALAPPDATA%\Programs\AICompanion
#    2) 建开始菜单 / 桌面快捷方式
#    3) 在"应用和功能"里注册卸载项（并生成 uninstall.ps1）
#    4) 可选：开机自启
#
#  为什么装到 LOCALAPPDATA：
#    程序运行时要在自己目录下写 logs 与 Assets\appearance.json，
#    装到 Program Files 会因为没有写权限而失败。按用户安装不需要管理员，也更省事。
# ============================================================================

[CmdletBinding()]
param(
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'Programs\AICompanion'),
    [switch]$NoDesktopShortcut,
    [switch]$AllUsers
)

$ErrorActionPreference = 'Stop'
$source = $PSScriptRoot
$exeName = 'AICompanion.exe'

if ($AllUsers) {
    $InstallDir = Join-Path $env:ProgramFiles 'AICompanion'
    Write-Warning 'AllUsers 模式需要管理员权限，且程序目录不可写，日志会落到 %LOCALAPPDATA%。'
}

Write-Host ''
Write-Host '=== AI 桌面伴侣 安装程序 ===' -ForegroundColor Cyan
Write-Host "    源目录：$source"
Write-Host "    安装到：$InstallDir"

if (-not (Test-Path (Join-Path $source $exeName))) {
    throw "源目录里找不到 $exeName，请在分发包根目录运行本脚本。"
}

# ---------------------------------------------------------------- 1. 检查运行时
$runtimeFound = $false
try {
    $versions = & dotnet --list-runtimes 2>$null
    $runtimeFound = ($versions | Select-String -Pattern 'Microsoft\.WindowsDesktop\.App 8\.').Count -gt 0
} catch { }

if (-not $runtimeFound) {
    Write-Warning '未检测到 .NET 8 桌面运行时，程序可能无法启动。'
    Write-Host '    下载：https://dotnet.microsoft.com/download/dotnet/8.0'
    $answer = Read-Host '    仍要继续安装吗？(y/N)'
    if ($answer -notin @('y', 'Y')) { throw '用户取消安装。' }
}

# ---------------------------------------------------------------- 2. 复制文件
Write-Host ''
Write-Host '==> 复制文件'

if (Test-Path $InstallDir) {
    # 保留用户数据：logs 与 appearance.json
    Write-Host '    目标目录已存在，保留 logs 与 Assets\appearance.json'
}

New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
Copy-Item (Join-Path $source '*') $InstallDir -Recurse -Force

# 分发包里的 installer 脚本不要留在安装目录
foreach ($junk in @('install.ps1', 'uninstall.ps1')) {
    $p = Join-Path $InstallDir $junk
    if (Test-Path $p) { Remove-Item $p -Force }
}

Write-Host "    已复制到 $InstallDir"

# ---------------------------------------------------------------- 3. 快捷方式
Write-Host ''
Write-Host '==> 创建快捷方式'

$shell = New-Object -ComObject WScript.Shell
$target = Join-Path $InstallDir $exeName

$startMenuDir = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\AI 桌面伴侣'
New-Item -ItemType Directory -Path $startMenuDir -Force | Out-Null

$lnk = $shell.CreateShortcut((Join-Path $startMenuDir 'AI 桌面伴侣.lnk'))
$lnk.TargetPath = $target
$lnk.WorkingDirectory = $InstallDir
$lnk.Description = 'AI 桌面伴侣'
$lnk.Save()

if (-not $NoDesktopShortcut) {
    $desktop = [Environment]::GetFolderPath('Desktop')
    $lnk2 = $shell.CreateShortcut((Join-Path $desktop 'AI 桌面伴侣.lnk'))
    $lnk2.TargetPath = $target
    $lnk2.WorkingDirectory = $InstallDir
    $lnk2.Save()
    Write-Host '    已创建桌面快捷方式'
}

Write-Host '    已创建开始菜单快捷方式'

# ---------------------------------------------------------------- 4. 卸载信息
Write-Host ''
Write-Host '==> 注册卸载信息'

$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\AICompanion'
New-Item -Path $uninstallKey -Force | Out-Null

$version = '0.9.0'
$versionFile = Join-Path $InstallDir 'VERSION.txt'
if (Test-Path $versionFile) {
    $first = (Get-Content $versionFile -Encoding UTF8 | Select-Object -First 1)
    if ($first -match '(\d+\.\d+\.\d+)') { $version = $Matches[1] }
}

Set-ItemProperty -Path $uninstallKey -Name 'DisplayName' -Value 'AI 桌面伴侣'
Set-ItemProperty -Path $uninstallKey -Name 'DisplayVersion' -Value $version
Set-ItemProperty -Path $uninstallKey -Name 'InstallLocation' -Value $InstallDir
Set-ItemProperty -Path $uninstallKey -Name 'DisplayIcon' -Value $target
Set-ItemProperty -Path $uninstallKey -Name 'Publisher' -Value 'AICompanion'
Set-ItemProperty -Path $uninstallKey -Name 'EstimatedSize' -Value ([int]((Get-ChildItem $InstallDir -Recurse -File | Measure-Object Length -Sum).Sum / 1KB))

$uninstallScript = Join-Path $InstallDir 'uninstall.ps1'
$uninstallCmd = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$uninstallScript`""
Set-ItemProperty -Path $uninstallKey -Name 'UninstallString' -Value $uninstallCmd
Set-ItemProperty -Path $uninstallKey -Name 'QuietUninstallString' -Value $uninstallCmd

# 生成卸载脚本
$uninstallBody = @'
# AI 桌面伴侣 卸载脚本（安装时自动生成）
$ErrorActionPreference = 'Continue'
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path

Write-Host ''
Write-Host '=== 卸载 AI 桌面伴侣 ===' -ForegroundColor Cyan

Get-Process AICompanion, GameMonitor -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 1

$menuDir = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\AI 桌面伴侣'
if (Test-Path $menuDir) { Remove-Item $menuDir -Recurse -Force }

$desktop = Join-Path ([Environment]::GetFolderPath('Desktop')) 'AI 桌面伴侣.lnk'
if (Test-Path $desktop) { Remove-Item $desktop -Force }

$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
if ((Get-ItemProperty -Path $runKey -ErrorAction SilentlyContinue).AICompanion) {
    Remove-ItemProperty -Path $runKey -Name 'AICompanion' -ErrorAction SilentlyContinue
}

Remove-Item 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\AICompanion' -Recurse -Force -ErrorAction SilentlyContinue

# 先把自己复制到临时目录再删安装目录，否则脚本文件自身被占用
$tempCopy = Join-Path $env:TEMP 'aicompanion-uninstall.ps1'
Copy-Item $MyInvocation.MyCommand.Path $tempCopy -Force
Start-Process powershell.exe -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$tempCopy`"" -Wait
'@
$uninstallBody | Set-Content -Path $uninstallScript -Encoding UTF8

# 真正删除目录的部分单独放，避免脚本删自己
$finalBody = @'
Start-Sleep -Seconds 1
if (Test-Path $dir) { Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue }
Write-Host '    已删除安装目录'
Write-Host '    卸载完成。' -ForegroundColor Green
'@
Add-Content -Path $uninstallScript -Value $finalBody -Encoding UTF8

Write-Host '    已注册到"应用和功能"'

# ---------------------------------------------------------------- 5. 开机自启
Write-Host ''
$startup = Read-Host '是否开机自动启动？(y/N)'
if ($startup -in @('y', 'Y')) {
    $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    New-Item -Path $runKey -Force | Out-Null
    Set-ItemProperty -Path $runKey -Name 'AICompanion' -Value "`"$target`""
    Write-Host '    已加入开机自启'
} else {
    Write-Host '    未设置开机自启（可稍后手动添加）'
}

Write-Host ''
Write-Host '=== 安装完成 ===' -ForegroundColor Green
Write-Host "    程序位置：$InstallDir"
Write-Host '    首次运行前，请确认 models\llm 里有 .gguf 对话模型。'
Write-Host ''
$launch = Read-Host '现在启动吗？(Y/n)'
if ($launch -notin @('n', 'N')) {
    Start-Process -FilePath $target -WorkingDirectory $InstallDir
}
