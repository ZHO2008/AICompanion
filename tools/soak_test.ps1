# 2 小时连续稳定性测试
# 每分钟采样一次内存/CPU/句柄/错误数；每 5 分钟做一次 UI 交互（功能栏开合、缩放、模式切换）。
# 结果写到 logs\soak\soak.csv，结束时追加 SUMMARY 段。

$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent $PSScriptRoot   # tools\.. = 仓库根
$outDir = Join-Path $root 'logs\soak'
New-Item -ItemType Directory -Path $outDir -Force | Out-Null
$csv = Join-Path $outDir 'soak.csv'

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class SoakWin
{
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder sb, int m);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

    public static IntPtr FindPet(uint pid)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, l) =>
        {
            uint p; GetWindowThreadProcessId(h, out p);
            if (p != pid) return true;
            var t = new StringBuilder(256); GetWindowTextW(h, t, 256);
            if (!t.ToString().Contains("AI 伴侣")) return true;
            found = h; return false;
        }, IntPtr.Zero);
        return found;
    }

    public static int[] Rect(IntPtr h)
    {
        RECT r; GetWindowRect(h, out r);
        return new int[] { r.Left, r.Top, r.Right, r.Bottom };
    }

    public static void RightClick(int x, int y)
    {
        SetCursorPos(x, y); System.Threading.Thread.Sleep(80);
        mouse_event(0x0008u, 0, 0, 0, UIntPtr.Zero); System.Threading.Thread.Sleep(40);
        mouse_event(0x0010u, 0, 0, 0, UIntPtr.Zero); System.Threading.Thread.Sleep(300);
    }
}
'@ -Language CSharp

function Get-LogStats {
    $logDir = Join-Path $root 'logs\app'
    if (-not (Test-Path $logDir)) { return @{ Err = 0; Warn = 0; KB = 0 } }
    $files = Get-ChildItem $logDir -Filter 'app-*.log' -ErrorAction SilentlyContinue
    $err = 0; $warn = 0; $kb = 0
    foreach ($f in $files) {
        $kb += [math]::Round($f.Length / 1KB, 1)
        try {
            $fs = [System.IO.File]::Open($f.FullName, 'Open', 'Read', 'ReadWrite')
            $sr = New-Object System.IO.StreamReader($fs, [System.Text.Encoding]::UTF8)
            $content = $sr.ReadToEnd()
            $sr.Close(); $fs.Close()
            $err += ([regex]::Matches($content, '\[ERR\]')).Count
            $warn += ([regex]::Matches($content, '\[WRN\]')).Count
        } catch { }
    }
    return @{ Err = $err; Warn = $warn; KB = $kb }
}

if (-not (Test-Path $csv)) {
    'ts,minute,ws_mb,priv_mb,handles,threads,cpu_pct,errors,warns,log_kb,note' | Set-Content -Path $csv -Encoding UTF8
}

$startProc = Get-Process AICompanion -ErrorAction SilentlyContinue
if (-not $startProc) {
    'AICompanion not running; aborting soak test.' | Add-Content -Path $csv -Encoding UTF8
    exit 1
}
$procId = $startProc.Id
$startTime = Get-Date
$lastCpu = $startProc.TotalProcessorTime.TotalSeconds
$lastTime = Get-Date
$baseline = @{ Ws = $startProc.WorkingSet64; Priv = $startProc.PrivateMemorySize64; Handles = $startProc.HandleCount }
$note = 'start'

for ($minute = 1; $minute -le 120; $minute++) {
    Start-Sleep -Seconds 60

    $p = Get-Process -Id $procId -ErrorAction SilentlyContinue
    if (-not $p) {
        "$(Get-Date -Format s),$minute,0,0,0,0,0,-1,-1,0,PROCESS_GONE" | Add-Content -Path $csv -Encoding UTF8
        break
    }

    $now = Get-Date
    $cpuNow = $p.TotalProcessorTime.TotalSeconds
    $cpuPct = if (($now - $lastTime).TotalSeconds -gt 0) {
        [math]::Round(($cpuNow - $lastCpu) / ($now - $lastTime).TotalSeconds * 100, 1)
    } else { 0 }
    $lastCpu = $cpuNow; $lastTime = $now

    $stats = Get-LogStats
    $note = ''

    # 每 5 分钟做一次 UI 交互压测
    if ($minute % 5 -eq 0) {
        try {
            $hwnd = [SoakWin]::FindPet([uint32]$procId)
            if ($hwnd -ne [IntPtr]::Zero) {
                $r = [SoakWin]::Rect($hwnd)
                $cx = [int](($r[0] + $r[2]) / 2)
                $cy = [int]($r[1] + ($r[3] - $r[1]) * 0.45)

                # 功能栏 开 → 点缩放 → 关
                [SoakWin]::RightClick($cx, $cy)
                $uiaRoot = [System.Windows.Automation.AutomationElement]::RootElement
                $barCond = New-Object System.Windows.Automation.PropertyCondition(
                    [System.Windows.Automation.AutomationElement]::NameProperty, '功能栏')
                $bar = $uiaRoot.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $barCond)
                if ($bar -ne $null) {
                    $plusCond = New-Object System.Windows.Automation.PropertyCondition(
                        [System.Windows.Automation.AutomationElement]::NameProperty, '＋')
                    $minusCond = New-Object System.Windows.Automation.PropertyCondition(
                        [System.Windows.Automation.AutomationElement]::NameProperty, '−')
                    $plus = $bar.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $plusCond)
                    if ($plus -ne $null) { $plus.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep -Milliseconds 400 }
                    $bar2 = $uiaRoot.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $barCond)
                    if ($bar2 -ne $null) {
                        $minus = $bar2.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $minusCond)
                        if ($minus -ne $null) { $minus.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep -Milliseconds 400 }
                    }
                }
                [SoakWin]::RightClick($cx, $cy)
                $note = 'ui'
            }
        } catch {
            $note = 'ui-error'
        }
    }

    "$(Get-Date -Format s),$minute,$([math]::Round($p.WorkingSet64/1MB,1)),$([math]::Round($p.PrivateMemorySize64/1MB,1)),$($p.HandleCount),$($p.Threads.Count),$cpuPct,$($stats.Err),$($stats.Warn),$($stats.KB),$note" |
        Add-Content -Path $csv -Encoding UTF8
}

# 汇总
$endProc = Get-Process -Id $procId -ErrorAction SilentlyContinue
$rows = Import-Csv $csv -ErrorAction SilentlyContinue
$mid = $rows | Where-Object { $_.minute -eq '60' } | Select-Object -First 1
$last = $rows | Select-Object -Last 1
$summary = @(
    '',
    'SUMMARY',
    "started=$(Get-Date -Format s)",
    "duration_min=$(((Get-Date) - $startTime).TotalMinutes.ToString('F0'))",
    "baseline_ws_mb=$([math]::Round($baseline.Ws/1MB,1))",
    "baseline_handles=$($baseline.Handles)",
    "mid_ws_mb=$($mid.ws_mb) mid_handles=$($mid.handles)",
    "end_ws_mb=$($last.ws_mb) end_handles=$($last.handles)",
    "max_ws_mb=$(($rows | ForEach-Object { [double]$_.ws_mb } | Measure-Object -Maximum).Maximum)",
    "max_handles=$(($rows | ForEach-Object { [int]$_.handles } | Measure-Object -Maximum).Maximum)",
    "errors_total=$($last.errors) warns_total=$($last.warns)",
    "process_alive=$([bool]$endProc)"
)
$summary | Add-Content -Path $csv -Encoding UTF8
