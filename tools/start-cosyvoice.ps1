<#
.SYNOPSIS
    启动 CosyVoice 2/3 的官方 FastAPI 服务（Windows 原生，不需要 WSL / Docker）。

.DESCRIPTION
    程序本身只当 HTTP 客户端（tts.engine = "cosyvoice"），服务要单独跑。
    默认参数对应本机已经装好的环境：
      Python   E:\py310\python.exe                     （3.10，deps 已装）
      源码     E:\cosyvoice
      权重     E:\cosyvoice\pretrained_models\Fun-CosyVoice3-0.5B
    换机器时按需改这三个路径。

    注意：上游 runtime/python/fastapi/server.py 有一个"把上传 wav 读成 tensor 再传给
    inference_*"的问题，而 frontend.load_wav 只认路径，会报
    TypeError: Invalid file: tensor([...])。本仓库依赖的副本已就地打补丁
    （上传落地成临时文件再传路径），若你重新 clone 了 CosyVoice，需要照 §27.4 重打一次。

.PARAMETER Port
    监听端口，默认 50000；要跟 config.yaml 里 tts.cosyvoice.port 一致。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\start-cosyvoice.ps1
#>
param(
    [string]$Python = 'E:\py310\python.exe',
    [string]$Repo = 'E:\cosyvoice',
    [string]$ModelDir = 'E:\cosyvoice\pretrained_models\Fun-CosyVoice3-0.5B',
    [int]$Port = 50000
)

$ErrorActionPreference = 'Stop'

$server = Join-Path $Repo 'runtime\python\fastapi\server.py'
if (-not (Test-Path $server)) {
    Write-Host "[失败] 找不到服务入口：$server" -ForegroundColor Red
    exit 1
}

if (-not (Test-Path $Python)) {
    Write-Host "[失败] 找不到 Python：$Python" -ForegroundColor Red
    exit 1
}

if (-not (Test-Path $ModelDir)) {
    Write-Host "[失败] 找不到模型目录：$ModelDir" -ForegroundColor Red
    exit 1
}

# 端口已被占用时直接提示，避免起第二个进程抢同一个端口。
$busy = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue
if ($busy) {
    $owner = (Get-Process -Id $busy[0].OwningProcess -ErrorAction SilentlyContinue).ProcessName
    Write-Host "[提示] 端口 $Port 已被占用（进程：$owner），大概率服务已经在跑。" -ForegroundColor Yellow
    Write-Host "       要重启就先结束它：Stop-Process -Id $($busy[0].OwningProcess) -Force"
    exit 0
}

Write-Host "启动 CosyVoice：" -ForegroundColor Cyan
Write-Host "  Python   $Python"
Write-Host "  源码     $Repo"
Write-Host "  模型     $ModelDir"
Write-Host "  端口     $Port"
Write-Host ""
Write-Host "首次启动要加载 4 GB 权重，CPU 上约 10-20 秒；之后每句合成约 20-40 秒。" -ForegroundColor DarkGray
Write-Host "按 Ctrl+C 停止。" -ForegroundColor DarkGray
Write-Host ""

# 工作目录必须在仓库根，server.py 用相对路径拼 third_party/Matcha-TTS。
Set-Location $Repo
& $Python $server --port $Port --model_dir $ModelDir
exit $LASTEXITCODE