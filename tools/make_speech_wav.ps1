# ============================================================================
#  make_speech_wav.ps1 - 生成一段中文测试语音到 artifacts\speech_zh.wav
#
#  为什么需要：tools\AsrPttRealProbe 要用"真实语音"验证按住说话链路，
#  但又不能在半夜用音箱放出来（吵人）。这里让 SAPI 只写 WAV 文件、不播放，
#  探针再把文件内容按 20ms 一块喂给真实的 whisper 引擎。
#
#  用法：
#    powershell -ExecutionPolicy Bypass -File tools\make_speech_wav.ps1
#    powershell -ExecutionPolicy Bypass -File tools\make_speech_wav.ps1 -Text "今天天气不错" -Out D:\tmp\x.wav
# ============================================================================

[CmdletBinding()]
param(
    [string]$Text = '今天天气不错，我们出去走走吧。',
    [string]$Out = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

if ([string]::IsNullOrWhiteSpace($Out)) {
    $Out = Join-Path $root 'artifacts\speech_zh.wav'
}

$dir = Split-Path -Parent $Out
if (-not (Test-Path $dir)) {
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
}

Add-Type -AssemblyName System.Speech

$synth = New-Object System.Speech.Synthesis.SpeechSynthesizer
$zh = $synth.GetInstalledVoices() |
    Where-Object { $_.Enabled -and $_.VoiceInfo.Culture.Name -like 'zh*' } |
    Select-Object -First 1

if ($zh) {
    $synth.SelectVoice($zh.VoiceInfo.Name)
    Write-Host "  使用语音：$($zh.VoiceInfo.Name)"
} else {
    Write-Host '  警告：没有中文语音，合成的中文可能不准确。' -ForegroundColor Yellow
}

$synth.Rate = -1   # 稍慢一点，识别率更稳

$synth.SetOutputToWaveFile($Out)
$synth.Speak($Text)
$synth.SetOutputToNull()
$synth.Dispose()

if (-not (Test-Path $Out)) {
    throw "生成失败：$Out"
}

$size = (Get-Item $Out).Length
Write-Host "  已生成：$Out（$([math]::Round($size / 1KB, 0)) KB）"
Write-Host ''
Write-Host '  下一步：'
Write-Host '    tools\AsrPttRealProbe\bin\Release\net8.0\AsrPttRealProbe.exe'
Write-Host '  （不带参数时默认读 artifacts\speech_zh.wav）'