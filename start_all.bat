@echo off
setlocal enabledelayedexpansion
cd /d "%~dp0"

REM ============================================================================
REM  start_all.bat - AI Companion launcher
REM
REM  Differences from the manual's version (all deliberate, see README.md):
REM
REM  1) This script does NOT start llama-server.
REM     The manual's script launches it with "-ngl 99", but the manual also
REM     requires -ngl to be computed by GPUMemoryManager at startup. Those two
REM     are mutually exclusive: when the script runs, the app has not computed
REM     ngl yet, and "-ngl 99" is guaranteed to OOM on an 8 GB card.
REM     The app therefore owns the whole llama-server lifecycle, which is also
REM     what makes OOM fallback possible at all: only a process we started
REM     ourselves gives us its stderr handle and a Process object to kill.
REM
REM  2) Chatterbox cleanup now works.
REM     The original used:
REM       taskkill /f /im python.exe /fi "WINDOWTITLE eq chatterbox*"
REM     That filter never matches, because a process started with "start /b"
REM     has no window and its window title is N/A. We instead resolve the
REM     process listening on the chatterbox port and kill that one.
REM
REM  3) The model path is read from config.yaml instead of being hard-coded,
REM     so the script and the configuration cannot drift apart.
REM
REM  4) ASCII only. See the header of build_native.bat for why non-ASCII text
REM     in a batch file is unsafe here.
REM ============================================================================

echo ============================================================
echo   AI Companion - starting up
echo ============================================================
echo.

REM ---------------------------------------------------------------- 1. cleanup
echo [1/5] Cleaning up residual processes...

taskkill /f /im llama-server.exe > nul 2>&1
if %errorlevel%==0 echo       - killed a leftover llama-server.exe

REM NOTE: this port must match tts.chatterbox.port in config.yaml (default 5000).
for /f "tokens=5" %%p in ('netstat -ano -p TCP ^| findstr /r /c:"127.0.0.1:5000 .*LISTENING"') do (
    echo       - killing PID %%p which holds port 5000
    taskkill /f /pid %%p > nul 2>&1
)

echo       waiting 5 seconds for ports and VRAM to be released...
timeout /t 5 /nobreak > nul

REM ------------------------------------------------- 2. read model path from config
echo [2/5] Reading llm.model_path from config.yaml...

set "MODEL_REL="
if exist "config.yaml" (
    REM Only the first match is kept. The template lists the llm section first;
    REM asr and piper also have model_path keys with the same indent, so we lock
    REM onto the first hit rather than trying to parse YAML in batch.
    for /f "usebackq tokens=1,* delims=:" %%a in (`findstr /b /c:"  model_path:" "config.yaml"`) do (
        if not defined MODEL_REL set "MODEL_REL=%%b"
    )
)

if defined MODEL_REL (
    set "MODEL_REL=!MODEL_REL: =!"
    set "MODEL_REL=!MODEL_REL:"=!"
) else (
    echo       [WARN] could not parse llm.model_path, using the default.
    set "MODEL_REL=models/qwen2.5-7b-q4_k_m.gguf"
)

set "MODEL_REL=!MODEL_REL:/=\!"
echo       model file: !MODEL_REL!

REM ---------------------------------------------------------------- 3. validate
echo [3/5] Checking that the LLM model file exists...

REM Robustness net: if the YAML parse above failed (it is only a findstr-based
REM heuristic), fall back to the single .gguf that actually exists in models\llm.
REM Without this, one odd config.yaml line means the whole app refuses to start.
if not exist "!MODEL_REL!" (
    echo       [WARN] configured model not found, scanning models\llm\*.gguf ...
    set "AUTO_MODEL="
    for %%f in (models\llm\*.gguf) do (
        if not defined AUTO_MODEL set "AUTO_MODEL=%%~ff"
    )
    if defined AUTO_MODEL (
        set "MODEL_REL=!AUTO_MODEL!"
        echo       auto-detected: !MODEL_REL!
    )
)

if not exist "!MODEL_REL!" (
    echo.
    echo   [ERROR] LLM model file not found: !MODEL_REL!
    echo.
    echo   Put a GGUF model there, or change llm.model_path in config.yaml.
    echo   The manual classifies a missing LLM model as a fatal gap, so startup
    echo   is blocked here on purpose.
    echo.
    pause
    exit /b 1
)

echo       model file present.

REM ---------------------------------------------------------------- 4. GameMonitor
echo [4/5] Starting GameMonitor...

set "GAMEMONITOR_EXE="
if exist "GameMonitor.exe" set "GAMEMONITOR_EXE=GameMonitor.exe"
if not defined GAMEMONITOR_EXE if exist "src\GameMonitor\bin\Release\net8.0\GameMonitor.exe" set "GAMEMONITOR_EXE=src\GameMonitor\bin\Release\net8.0\GameMonitor.exe"
if not defined GAMEMONITOR_EXE if exist "src\GameMonitor\bin\Debug\net8.0\GameMonitor.exe" set "GAMEMONITOR_EXE=src\GameMonitor\bin\Debug\net8.0\GameMonitor.exe"

if defined GAMEMONITOR_EXE (
    start "GameMonitor" /b "!GAMEMONITOR_EXE!"
    echo       started: !GAMEMONITOR_EXE!
) else (
    echo       [WARN] GameMonitor.exe not found, game detection will be unavailable.
    echo              The app will show a "game detection service disconnected" bubble.
)

timeout /t 1 /nobreak > nul

REM ---------------------------------------------------------------- 5. main app
echo [5/5] Starting the main application...
echo.
echo   Note: llama-server is started by the app itself using the computed -ngl,
echo         so you will not see it launched from here. If the LLM fails to come
echo         up, check the app bubbles and the logs\app directory.
echo.

set "APP_EXE="
if exist "AICompanion.exe" set "APP_EXE=AICompanion.exe"
if not defined APP_EXE if exist "src\AICompanion.App\bin\Release\net8.0-windows\AICompanion.exe" set "APP_EXE=src\AICompanion.App\bin\Release\net8.0-windows\AICompanion.exe"
if not defined APP_EXE if exist "src\AICompanion.App\bin\Debug\net8.0-windows\AICompanion.exe" set "APP_EXE=src\AICompanion.App\bin\Debug\net8.0-windows\AICompanion.exe"

if not defined APP_EXE (
    echo   [ERROR] AICompanion.exe not found. Build it first:
    echo           dotnet build AICompanion.sln -c Release
    pause
    exit /b 1
)

start "" "!APP_EXE!"

echo ============================================================
echo   Startup sequence finished.
echo   The app minimises to the system tray; double-click the tray
echo   icon to show the character.
echo   Quit from the tray menu so llama-server is reclaimed properly.
echo ============================================================

endlocal
exit /b 0
