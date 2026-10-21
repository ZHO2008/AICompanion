# ============================================================================
#  ptt_smoke.ps1 - 按住说话（PTT）的端到端冒烟测试
#
#  做的事：
#    1) 确认 AICompanion 正在运行
#    2) 记下当前时间
#    3) 模拟"按住"配置里的按键 N 毫秒（默认 Home）
#    4) 按时间戳筛出这段期间的新日志，断言出现 PTT 按下/松开，
#       并区分三种结果：识别出文字 / 按太短 / 按住了但没收到音频
#
#  用法：
#    powershell -ExecutionPolicy Bypass -File tools\ptt_smoke.ps1
#    powershell -ExecutionPolicy Bypass -File tools\ptt_smoke.ps1 -HoldMs 2500 -KeyName Home
#
#  两个已知的模拟限制（不是程序的问题）：
#    - keybd_event 注入的按键，Windows 约 60-100 ms 后就会当"已放开"，
#      所以按住期间要每 40 ms 重发一次"按下"（真实键盘按住本来就会自动重复）。
#    - 即便如此，注入的按住状态也可能被系统打断，实测无法稳定模拟数秒长按。
#      因此"长时间按住 + 说话"这一步请在真实键盘上自己按一次验证。
#
#  注意：脚本只模拟按键，不会替你说话。按住期间没有真实语音时，
#  日志显示"误触"或"识别结果为空"都属正常。
# ============================================================================

[CmdletBinding()]
param(
    [int]$HoldMs = 2000,
    [string]$KeyName = 'Home',
    [int]$WaitMs = 8000
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class PttKeys
{
    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    private const uint KeyUp = 0x0002;

    public static void Down(byte vk) { keybd_event(vk, 0, 0, UIntPtr.Zero); }

    public static void Up(byte vk) { keybd_event(vk, 0, KeyUp, UIntPtr.Zero); }

    public static byte VirtualKey(string name)
    {
        switch (name.ToLowerInvariant())
        {
            case "home": return 0x24;
            case "end": return 0x23;
            case "space": return 0x20;
            case "enter": return 0x0D;
            case "f1": return 0x70;
            case "f2": return 0x71;
            case "f3": return 0x72;
            case "f4": return 0x73;
            default: return 0x24;
        }
    }
}
'@ -Language CSharp

Write-Host ''
Write-Host '=== 按住说话冒烟测试 ===' -ForegroundColor Cyan

$proc = Get-Process AICompanion -ErrorAction SilentlyContinue
if (-not $proc) {
    throw 'AICompanion 没有运行，先启动程序再测。'
}

Write-Host "    进程 PID：$($proc.Id)"

function Find-Log {
    $candidates = @(
        (Join-Path $root 'logs\app'),
        (Join-Path $env:LOCALAPPDATA 'AICompanion\logs\app')
    )

    foreach ($dir in $candidates) {
        if (-not (Test-Path $dir)) { continue }
        $f = Get-ChildItem $dir -Filter 'app-*.log' -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if ($f) { return $f }
    }

    return $null
}

$log = Find-Log
if (-not $log) {
    throw '找不到日志文件。'
}

Write-Host "    日志：$($log.FullName)"

function Read-Log {
    $fs = [System.IO.File]::Open($log.FullName, 'Open', 'Read', 'ReadWrite')
    try {
        $sr = New-Object System.IO.StreamReader($fs, [System.Text.Encoding]::UTF8)
        $content = $sr.ReadToEnd()
        $sr.Close()
        return $content
    } finally {
        $fs.Close()
    }
}

# 用时间戳而不是字符偏移来取"新增日志"：
# 日志有大小上限，写满会滚动截断，字符偏移会错位甚至读到旧内容。
$t0 = (Get-Date).AddSeconds(-1)
Write-Host "    起始时间：$($t0.ToString('HH:mm:ss'))"

$vk = [PttKeys]::VirtualKey($KeyName)
Write-Host ''
Write-Host "==> 按住 $KeyName（$HoldMs ms）"

[PttKeys]::Down($vk)
$holdSw = [System.Diagnostics.Stopwatch]::StartNew()

while ($holdSw.ElapsedMilliseconds -lt $HoldMs) {
    Start-Sleep -Milliseconds 40
    [PttKeys]::Down($vk)
}

[PttKeys]::Up($vk)
$holdSw.Stop()

Write-Host "    已松开（实际保持 $($holdSw.ElapsedMilliseconds) ms），等待识别…"
Start-Sleep -Milliseconds $WaitMs

Write-Host ''
Write-Host '==> 新增日志'

$hitDown = $false
$hitUp = $false
$hitShort = $false
$hitNoAudio = $false
$hitText = $null
$shown = 0

foreach ($raw in ((Read-Log) -split "`n")) {
    if ($raw.Trim() -eq '') { continue }
    if ($raw.Length -lt 19) { continue }

    $stamp = [datetime]::MinValue
    if (-not [datetime]::TryParse($raw.Substring(0, 19), [ref]$stamp)) { continue }
    if ($stamp -lt $t0) { continue }

    $clean = $raw -replace '^\S+ \S+ ', ''

    if ($clean -match 'PTT 按下') { $hitDown = $true }
    if ($clean -match 'PTT 松开') { $hitUp = $true }
    if ($clean -match '判定为误触') { $hitShort = $true }
    if ($clean -match '只收到') { $hitNoAudio = $true }
    if ($clean -match 'PTT 识别成功："(.+)"') { $hitText = $Matches[1] }

    if ($clean -match 'PTT|ASR|AudioCapture|按住说话') {
        Write-Host "    $clean"
        $shown++
    }
}

if ($shown -eq 0) {
    Write-Host '    （没有相关日志）'
}

Write-Host ''
Write-Host '==> 结论'

$ok = $true

if ($hitDown) {
    Write-Host '    [通过] 按键被识别为"按下"，开始录音' -ForegroundColor Green
} else {
    Write-Host '    [失败] 没有捕获到按下事件（按键配置是否与脚本的 -KeyName 一致？）' -ForegroundColor Red
    $ok = $false
}

if ($hitUp) {
    Write-Host '    [通过] 松开被识别，录音收尾' -ForegroundColor Green
} else {
    Write-Host '    [失败] 没有捕获到松开事件' -ForegroundColor Red
    $ok = $false
}

if ($hitText) {
    Write-Host "    [通过] 识别到文字：$hitText" -ForegroundColor Green
} elseif ($hitNoAudio) {
    Write-Host '    [注意] 按住了但没有收到音频——麦克风可能被占用或异常' -ForegroundColor Red
} elseif ($hitShort) {
    Write-Host '    [说明] 判定为误触（按住期间没说话 / 模拟的长按被系统打断，属正常范围）' -ForegroundColor Yellow
} else {
    Write-Host '    [说明] 没有识别结果（可能没说清，或模型还在加载）' -ForegroundColor Yellow
}

Write-Host ''
if ($ok) {
    Write-Host '=== PTT 链路正常 ===' -ForegroundColor Green
} else {
    Write-Host '=== 存在问题，见上 ===' -ForegroundColor Red
}