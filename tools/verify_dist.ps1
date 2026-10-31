# ============================================================================
#  verify_dist.ps1 - 验证分发包能在"干净环境"下跑起来
#
#  **已废弃**：必需文件清单还停在模块化之前（bin\llama-server.exe、默认随包的
#  Live2DCubismCore.dll），验证当前包只会误报。出包/自检请用 tools\pack-release.ps1
#  （自带断言，退出码 0 即通过）。文件保留只为对照旧文档；运行时立刻 exit 1。
#
#  用法：
#    powershell -ExecutionPolicy Bypass -File tools\verify_dist.ps1
#    powershell -ExecutionPolicy Bypass -File tools\verify_dist.ps1 -DistDir dist\AICompanion-0.9.1-win-x64
#
#  为什么需要它：
#    开发时程序依赖仓库根目录（config.yaml、Assets、Hiyori、models 都在那），
#    很容易出现"仓库里能跑、发出去跑不了"。这个脚本把分发包复制到临时目录，
#    从**别的**工作目录启动它，确认不靠仓库环境也能起来。
#
#  检查项：
#    1) 必需文件齐全（exe / config.yaml / Live2DCubismCore.dll / bin\llama-server.exe）
#    2) 进程能从任意 CWD 启动并存活
#    3) 日志能写出来（LogRoot 生效），且没有 [ERR]
#    4) 主窗口出现
# ============================================================================

[CmdletBinding()]
param(
    [string]$DistDir,
    [int]$WaitSeconds = 40
)

# ---------------------------------------------------------------- 已废弃（强制拦截）
# 它的必需文件清单还写着 bin\llama-server.exe 与 Live2DCubismCore.dll：
# 前者在模块化之后已经搬进 modules\，后者默认不再随公开包分发。
# 拿它验证现在的包只会得到一串误报；而它要验证的"异目录启动"能力，
# pack-release.ps1 的断言 + 手动解压启动已经覆盖。
Write-Host ''
Write-Host '已废弃，请用 tools\pack-release.ps1' -ForegroundColor Red
Write-Host '  原因：verify_dist.ps1 的必需文件清单（bin\llama-server.exe、Live2DCubismCore.dll）' -ForegroundColor Yellow
Write-Host '        与模块化之后的包结构不一致，验证现在的包只会误报。' -ForegroundColor Yellow
Write-Host '  要验证出包结果： powershell -ExecutionPolicy Bypass -File tools\pack-release.ps1' -ForegroundColor Yellow
Write-Host '        （脚本自带断言，退出码 0 即通过），再解压 zip 双击 AICompanion.exe。' -ForegroundColor Yellow
Write-Host '  说明见 docs\打包与分发.md（§3 参数、§3.4 断言、§7 验收清单）。' -ForegroundColor Yellow
Write-Host ''
exit 1

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

if (-not $DistDir) {
    $candidate = Get-ChildItem (Join-Path $root 'dist') -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -like 'AICompanion-*' -and $_.Name -notlike '*.zip' } |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1

    if (-not $candidate) { throw 'dist 下没有分发包，请先运行 tools\pack.ps1。' }
    $DistDir = $candidate.FullName
}

Write-Host ''
Write-Host '=== 分发包验证 ===' -ForegroundColor Cyan
Write-Host "    包目录：$DistDir"

# ---------------------------------------------------------------- 1. 必需文件
Write-Host ''
Write-Host '==> 检查必需文件'

$required = @(
    'AICompanion.exe',
    'AICompanion.dll',
    'AICompanion.Core.dll',
    'GameMonitor.exe',
    'config.yaml',
    'bin\llama-server.exe',
    'bin\ai_asr.dll'
)

$missing = @()
foreach ($item in $required) {
    if (Test-Path (Join-Path $DistDir $item)) {
        Write-Host "    [OK] $item"
    } else {
        Write-Host "    [缺失] $item" -ForegroundColor Red
        $missing += $item
    }
}

if ($missing.Count -gt 0) {
    throw "分发包缺少 $($missing.Count) 个必需文件，先修 pack.ps1。"
}

# ---------------------------------------------------------------- 1.5 授权素材
Write-Host ''
Write-Host '==> 检查授权素材（应当由用户自行提供，不在包里）'

$licensed = @(
    'Live2DCubismCore.dll',
    'Hiyori',
    'Assets\Default\character.png',
    'Assets\source'
)

$stillThere = @()
foreach ($item in $licensed) {
    if (Test-Path (Join-Path $DistDir $item)) {
        Write-Host "    [不该有] $item —— 该项目需单独授权，不应随包分发" -ForegroundColor Red
        $stillThere += $item
    }
}

if ($stillThere.Count -gt 0) {
    throw "分发包里仍含 $($stillThere.Count) 个需单独授权的组件，先修 pack.ps1（自用版可加 -IncludeLicensedAssets）。"
}
Write-Host '    [OK] 未包含需单独授权的素材' -ForegroundColor Green

# 素材说明必须存在，否则用户不知道要自己下什么
$notes = Join-Path $DistDir '素材说明.md'
if (-not (Test-Path $notes)) { throw '分发包缺少 素材说明.md，用户会不知道要自行下载哪些素材。' }
Write-Host '    [OK] 含 素材说明.md'

# ---------------------------------------------------------------- 2. 复制到临时目录
Write-Host ''
Write-Host '==> 复制到临时目录并异目录启动'

$tempParent = Join-Path $env:TEMP ('aicompanion-verify-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$tempApp = Join-Path $tempParent 'app'
New-Item -ItemType Directory -Path $tempApp -Force | Out-Null
Copy-Item (Join-Path $DistDir '*') $tempApp -Recurse -Force

# 故意用另一个目录做 CWD：如果程序依赖 CWD，这里就会失败
$foreignCwd = Join-Path $tempParent 'elsewhere'
New-Item -ItemType Directory -Path $foreignCwd -Force | Out-Null

Write-Host "    临时程序目录：$tempApp"
Write-Host "    工作目录（故意不同）：$foreignCwd"

$exe = Join-Path $tempApp 'AICompanion.exe'
$proc = Start-Process -FilePath $exe -WorkingDirectory $foreignCwd -PassThru

Write-Host "    已启动，PID $($proc.Id)，等待 $WaitSeconds 秒…"
Start-Sleep -Seconds $WaitSeconds

# ---------------------------------------------------------------- 3. 存活与窗口
Write-Host ''
Write-Host '==> 检查进程与窗口'

$alive = Get-Process -Id $proc.Id -ErrorAction SilentlyContinue
if (-not $alive) {
    throw '进程已退出：分发包启动失败（看下面的日志）。'
}

Write-Host "    [OK] 进程存活，内存 $([math]::Round($alive.WorkingSet64/1MB,1)) MB，句柄 $($alive.HandleCount)"

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class VerifyWin
{
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder sb, int m);

    public static bool HasWindow(uint pid)
    {
        bool found = false;
        EnumWindows((h, l) =>
        {
            uint p; GetWindowThreadProcessId(h, out p);
            if (p != pid) return true;
            var t = new StringBuilder(256); GetWindowTextW(h, t, 256);
            if (t.ToString().Contains("AI 伴侣")) { found = true; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
'@ -Language CSharp

if ([VerifyWin]::HasWindow([uint32]$proc.Id)) {
    Write-Host '    [OK] 主窗口已出现'
} else {
    Write-Host '    [警告] 没找到"I AI 伴侣"窗口，可能还在初始化' -ForegroundColor Yellow
}

# ---------------------------------------------------------------- 4. 日志
Write-Host ''
Write-Host '==> 检查日志'

$logDirs = @(
    (Join-Path $tempApp 'logs\app'),
    (Join-Path $env:LOCALAPPDATA 'AICompanion\logs\app')
)

$foundLog = $null
foreach ($dir in $logDirs) {
    $log = Get-ChildItem $dir -Filter 'app-*.log' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($log) { $foundLog = $log; break }
}

if (-not $foundLog) {
    throw '没有生成日志：LogRoot 可能没生效。'
}

Write-Host "    [OK] 日志：$($foundLog.FullName)"

$fs = [System.IO.File]::Open($foundLog.FullName, 'Open', 'Read', 'ReadWrite')
$sr = New-Object System.IO.StreamReader($fs, [System.Text.Encoding]::UTF8)
$content = $sr.ReadToEnd()
$sr.Close(); $fs.Close()

$errLines = @($content -split "`n" | Where-Object { $_ -match '\[ERR\]' })

# 轻量包（-IncludeModels:$false）本来就不带模型，"模型缺失"是预期状态，不该算缺陷；
# 否则官方分发包每次首启都会看起来像坏了。
$hasLlm = @(Get-ChildItem (Join-Path $tempApp 'models\llm') -Filter '*.gguf' -ErrorAction SilentlyContinue).Count -gt 0
$hasAsr = Test-Path (Join-Path $tempApp 'models\asr\ggml-base.bin')
$expectedPatterns = @()
if (-not $hasLlm) { $expectedPatterns += 'LLM 模型文件缺失' }
if (-not $hasAsr) { $expectedPatterns += '语音识别模型缺失' }

function Test-ExpectedMissing([string]$line) {
    foreach ($k in $expectedPatterns) {
        if ($line -match [regex]::Escape($k)) { return $true }
    }
    return $false
}

$errors = @($errLines | Where-Object { -not (Test-ExpectedMissing $_) })
$expectedHits = @($errLines | Where-Object { Test-ExpectedMissing $_ })

Write-Host "    [$(if ($errors.Count -eq 0) { 'OK' } else { '注意' })] 错误行：$($errors.Count)"
$errors | Select-Object -First 3 | ForEach-Object { Write-Host "        $($_.Trim())" -ForegroundColor Yellow }
if ($expectedHits.Count -gt 0) {
    Write-Host "    [预期] 另有 $($expectedHits.Count) 行属于'模型未随包附带'（轻量包的正常状态）" -ForegroundColor DarkGray
}

$startup = ($content -split "`n" | Where-Object { $_ -match '启动完成' } | Select-Object -First 1)
if ($startup) { Write-Host "    $($startup.Trim())" }

# ---------------------------------------------------------------- 5. 收尾
Write-Host ''
Write-Host '==> 清理'

Get-Process -Id $proc.Id -ErrorAction SilentlyContinue | Stop-Process -Force
Get-Process GameMonitor -ErrorAction SilentlyContinue | Where-Object {
    $_.Path -and $_.Path.StartsWith($tempApp)
} | Stop-Process -Force

# llama-server 是程序的子进程，杀主进程不会连带杀掉它——漏了会留下一个占着模型和显存的进程
$strays = @(Get-Process llama-server -ErrorAction SilentlyContinue | Where-Object {
    $_.Path -and $_.Path.StartsWith($tempApp)
})
if ($strays.Count -gt 0) {
    $strays | Stop-Process -Force -ErrorAction SilentlyContinue
    Write-Host "    已清理 $($strays.Count) 个 llama-server 子进程"
}
Start-Sleep -Seconds 2

Remove-Item $tempParent -Recurse -Force -ErrorAction SilentlyContinue
Write-Host '    已停止进程并删除临时目录'

Write-Host ''
Write-Host '=== 验证通过 ===' -ForegroundColor Green
