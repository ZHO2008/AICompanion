@echo off
setlocal enabledelayedexpansion
cd /d "%~dp0"

REM ============================================================================
REM  build_native.bat - build native components (ai_asr.dll + llama-server.exe)
REM
REM  Output is copied to bin\ so config.yaml never needs to change regardless of
REM  which CMake generator or output layout is used.
REM
REM  Usage:
REM     build_native.bat            build everything (ai_asr + llama-server, CUDA first)
REM     build_native.bat asr        build only ai_asr (the whisper.cpp shim)
REM     build_native.bat llama      build only llama-server
REM     build_native.bat llama cpu  build llama-server, force CPU build
REM
REM  IMPORTANT - this file is intentionally ASCII-only.
REM  Batch files with non-ASCII text are unsafe on this machine:
REM    * saved as UTF-8, cmd.exe reads them with the OEM code page and the
REM      non-ASCII comments corrupt the parse;
REM    * saved as GBK, double-byte characters whose second byte is 0x5E / 0x26 /
REM      0x7C (i.e. caret, ampersand, pipe) are treated as control characters by
REM      cmd.exe and silently swallow neighbouring characters.
REM  Chinese documentation lives in README.md instead.
REM
REM  Why vcvars + NMake instead of the Visual Studio generator:
REM  This machine has two Visual Studio installs and each is incomplete:
REM    * D:\VS 2026\project      has full MSVC 14.51 and the Windows SDK, but the
REM                              SDK sits in a non-standard path
REM                              (SDK\ScopeCppSDK\vc15\SDK) so CMake's
REM                              "Visual Studio" generator cannot find it;
REM    * C:\...\2022\Community   has cl.exe but an incomplete include dir
REM                              (not even stdio.h).
REM  vcvars64.bat knows where the SDK actually is, so we use it plus NMake.
REM ============================================================================

set "VCVARS=D:\VS 2026\project\VC\Auxiliary\Build\vcvars64.bat"
set "CMAKE=%~dp0tools\toolchain\cmake\bin\cmake.exe"
set "BIN=%~dp0bin"
set "ROOT=%~dp0"

if not exist "%BIN%" mkdir "%BIN%"

if not exist "%CMAKE%" (
    echo [ERROR] Portable CMake not found: %CMAKE%
    echo         Run tools\Fetcher first and unpack it into tools\toolchain\cmake.
    exit /b 1
)

if not exist "%VCVARS%" (
    echo [ERROR] vcvars64.bat not found: %VCVARS%
    echo         Adjust the VCVARS variable at the top of this script.
    exit /b 1
)

set "TARGET=%~1"
if "%TARGET%"=="" set "TARGET=all"
set "LLAMA_BACKEND=%~2"

echo ============================================================
echo   Initialising MSVC environment
echo ============================================================
call "%VCVARS%" >nul 2>&1
if errorlevel 1 (
    echo [ERROR] vcvars64.bat failed.
    exit /b 1
)
echo   Environment ready.
echo.

if /i "%TARGET%"=="asr"   goto build_asr
if /i "%TARGET%"=="llama" goto build_llama
if /i "%TARGET%"=="all"   goto build_asr

echo [ERROR] Unknown target "%TARGET%". Valid values: all / asr / llama
exit /b 1

REM ------------------------------------------------------------------ ai_asr
:build_asr
echo ============================================================
echo   [1/2] Building ai_asr (whisper.cpp shim, CPU)
echo ============================================================
set "ASR_BUILD=%ROOT%third_party\ai_asr\build"

"%CMAKE%" -S "%ROOT%third_party\ai_asr" -B "%ASR_BUILD%" -G "NMake Makefiles" -DCMAKE_BUILD_TYPE=Release
if errorlevel 1 (
    echo [ERROR] ai_asr configure failed.
    exit /b 1
)

"%CMAKE%" --build "%ASR_BUILD%"
if errorlevel 1 (
    echo [ERROR] ai_asr build failed.
    exit /b 1
)

if exist "%ASR_BUILD%\ai_asr.dll" (
    copy /y "%ASR_BUILD%\ai_asr.dll" "%BIN%\ai_asr.dll" >nul
    echo [OK] bin\ai_asr.dll
) else (
    echo [ERROR] ai_asr.dll not produced.
    exit /b 1
)

if /i "%TARGET%"=="asr" goto done
echo.

REM ------------------------------------------------------------------ llama-server
:build_llama
echo ============================================================
echo   [2/2] Building llama-server
echo ============================================================
set "LLAMA_BUILD=%ROOT%third_party\llama.cpp\build"
set "LLAMA_COMMON=-DLLAMA_BUILD_TESTS=OFF -DLLAMA_BUILD_EXAMPLES=OFF -DLLAMA_BUILD_SERVER=ON -DLLAMA_CURL=OFF"

if /i "%LLAMA_BACKEND%"=="cpu" goto llama_cpu

echo   Trying CUDA build (arch sm_120, for RTX 50 series) ...
"%CMAKE%" -S "%ROOT%third_party\llama.cpp" -B "%LLAMA_BUILD%" -G "NMake Makefiles" -DCMAKE_BUILD_TYPE=Release %LLAMA_COMMON% -DGGML_CUDA=ON -DCMAKE_CUDA_ARCHITECTURES=120
if errorlevel 1 (
    echo   [WARN] CUDA configure failed, falling back to CPU.
    goto llama_cpu
)

"%CMAKE%" --build "%LLAMA_BUILD%"
if errorlevel 1 (
    echo   [WARN] CUDA build failed, cleaning up and falling back to CPU.
    rmdir /s /q "%LLAMA_BUILD%" >nul 2>&1
    goto llama_cpu
)

goto llama_collect

:llama_cpu
echo   Building CPU version ...
"%CMAKE%" -S "%ROOT%third_party\llama.cpp" -B "%LLAMA_BUILD%" -G "NMake Makefiles" -DCMAKE_BUILD_TYPE=Release %LLAMA_COMMON% -DGGML_CUDA=OFF
if errorlevel 1 (
    echo [ERROR] llama.cpp configure failed.
    exit /b 1
)

"%CMAKE%" --build "%LLAMA_BUILD%"
if errorlevel 1 (
    echo [ERROR] llama.cpp build failed.
    exit /b 1
)

:llama_collect
set "LLAMA_EXE="
if not defined LLAMA_EXE if exist "%LLAMA_BUILD%\bin\llama-server.exe" set "LLAMA_EXE=%LLAMA_BUILD%\bin\llama-server.exe"
if not defined LLAMA_EXE if exist "%LLAMA_BUILD%\bin\Release\llama-server.exe" set "LLAMA_EXE=%LLAMA_BUILD%\bin\Release\llama-server.exe"
if not defined LLAMA_EXE if exist "%LLAMA_BUILD%\tools\server\Release\llama-server.exe" set "LLAMA_EXE=%LLAMA_BUILD%\tools\server\Release\llama-server.exe"

if not defined LLAMA_EXE (
    echo   [WARN] llama-server.exe not found in the build tree.
    exit /b 1
)

copy /y "%LLAMA_EXE%" "%BIN%\llama-server.exe" >nul
echo [OK] bin\llama-server.exe

REM CUDA builds also need the ggml/CUDA runtime DLLs next to the exe.
for %%f in ("%LLAMA_BUILD%\bin\*.dll") do (
    if exist %%f copy /y %%f "%BIN%\" >nul 2>&1
)

:done
echo.
echo ============================================================
echo   Native build finished. Contents of bin\:
echo ============================================================
dir /b "%BIN%"
echo.
echo   Note: if llama-server is a CPU build, the companion app detects it at
echo         startup and reports CPU mode truthfully instead of claiming GPU.
endlocal
exit /b 0
