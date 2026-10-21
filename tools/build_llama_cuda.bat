@echo off
setlocal enabledelayedexpansion
cd /d "%~dp0.."

REM ============================================================================
REM  build_llama_cuda.bat - build llama-server with CUDA (sm_120) using Ninja
REM
REM  Why this script exists separately from build_native.bat:
REM  the NMake-based path in build_native.bat is *serial*. Compiling llama.cpp's
REM  CUDA backend means hundreds of .cu files, many of them heavily templated
REM  fattn instances -- serially it measured ~30 minutes just to reach 26%,
REM  i.e. roughly 2 hours total. Ninja builds the same tree in parallel.
REM
REM  This machine: 8 physical / 16 logical cores, 15.3 GB RAM.
REM  Default 6 parallel jobs: nvcc is memory hungry (1-2 GB per process), so
REM  going to 16 risks swapping. Pass a different job count as the first arg.
REM
REM  Output goes to third_party\llama.cpp\build-cuda first; the existing CPU
REM  build in bin\ is backed up to bin\cpu-backup\ before being replaced, so a
REM  CUDA failure always leaves a working CPU llama-server behind.
REM
REM  ASCII only -- see the header of build_native.bat for why.
REM ============================================================================

set "VCVARS=D:\VS 2026\project\VC\Auxiliary\Build\vcvars64.bat"
set "CMAKE=%~dp0..\tools\toolchain\cmake\bin\cmake.exe"
set "NINJA_DIR=%~dp0..\tools\toolchain\ninja"
set "BIN=%~dp0..\bin"
set "LLAMA_SRC=%~dp0..\third_party\llama.cpp"
set "LLAMA_BUILD=%~dp0..\third_party\llama.cpp\build-cuda"

set "JOBS=%~1"
if "%JOBS%"=="" set "JOBS=6"

if not exist "%CMAKE%" (echo [ERROR] cmake not found: %CMAKE% & exit /b 1)
if not exist "%NINJA_DIR%\ninja.exe" (echo [ERROR] ninja not found: %NINJA_DIR%\ninja.exe & exit /b 1)
if not exist "%VCVARS%" (echo [ERROR] vcvars not found: %VCVARS% & exit /b 1)

echo ============================================================
echo   Initialising MSVC environment
echo ============================================================
call "%VCVARS%" >nul 2>&1
if errorlevel 1 (echo [ERROR] vcvars64.bat failed & exit /b 1)

set "PATH=%NINJA_DIR%;%PATH%"
for /f "tokens=*" %%v in ('ninja --version') do echo   ninja %%v
echo   parallel jobs: %JOBS%
echo.

REM ---- back up the current CPU build so we can always fall back ----
if exist "%BIN%\llama-server.exe" (
    if not exist "%BIN%\cpu-backup" mkdir "%BIN%\cpu-backup"
    copy /y "%BIN%\llama-server.exe" "%BIN%\cpu-backup\" >nul 2>&1
    for %%f in ("%BIN%\*.dll") do copy /y "%%f" "%BIN%\cpu-backup\" >nul 2>&1
    echo   current CPU build backed up to bin\cpu-backup\
)

echo ============================================================
echo   [1/3] Configure: Ninja + CUDA, arch sm_120
echo ============================================================
"%CMAKE%" -S "%LLAMA_SRC%" -B "%LLAMA_BUILD%" -G Ninja -DCMAKE_BUILD_TYPE=Release -DLLAMA_BUILD_TESTS=OFF -DLLAMA_BUILD_EXAMPLES=OFF -DLLAMA_BUILD_SERVER=ON -DLLAMA_CURL=OFF -DGGML_CUDA=ON -DCMAKE_CUDA_ARCHITECTURES=120
if errorlevel 1 (echo [ERROR] configure failed & exit /b 1)

echo.
echo ============================================================
echo   [2/3] Build with %JOBS% parallel jobs
echo ============================================================
"%CMAKE%" --build "%LLAMA_BUILD%" --parallel %JOBS%
if errorlevel 1 (echo [ERROR] build failed & exit /b 1)

echo.
echo ============================================================
echo   [3/3] Install into bin\
echo ============================================================
if not exist "%LLAMA_BUILD%\bin\llama-server.exe" (
    echo [ERROR] llama-server.exe not produced
    exit /b 1
)

copy /y "%LLAMA_BUILD%\bin\llama-server.exe" "%BIN%\llama-server.exe" >nul
if errorlevel 1 (echo [ERROR] copy failed & exit /b 1)

for %%f in ("%LLAMA_BUILD%\bin\*.dll") do (
    if exist "%%f" copy /y "%%f" "%BIN%\" >nul 2>&1
)

echo [OK] bin\llama-server.exe replaced with the CUDA build
echo.
dir /b "%BIN%"
echo.
echo   Verify GPU offload with:
echo     bin\llama-server.exe -m models\llm\qwen2.5-1.5b-instruct-q4_k_m.gguf -ngl 99 -c 512
echo   It should print something like "ggml_cuda_init: found 1 CUDA devices"
endlocal
exit /b 0
