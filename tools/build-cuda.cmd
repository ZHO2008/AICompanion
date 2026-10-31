@echo off
REM Build llama.cpp with the CUDA backend (same source tree as the Vulkan build,
REM so the two backends can be compared apples-to-apples).

setlocal

set "VSDIR=D:\VS 2026\project"
set "CMAKE=E:\DEEPSEEK\tools\build\cmake\bin\cmake.exe"
set "NINJA=%VSDIR%\Common7\IDE\CommonExtensions\Microsoft\CMake\Ninja\ninja.exe"
set "CUDA=C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v13.3"

call "%VSDIR%\VC\Auxiliary\Build\vcvars64.bat"
if errorlevel 1 goto :fail_vcvars

set "PATH=%CUDA%\bin;%PATH%"

where nvcc.exe
if errorlevel 1 goto :fail_nvcc

cd /d E:\DEEPSEEK

echo === configure (CUDA) ===
"%CMAKE%" -S third_party\llama.cpp -B build-cuda -G Ninja -DCMAKE_MAKE_PROGRAM="%NINJA%" -DCMAKE_BUILD_TYPE=Release -DGGML_CUDA=ON -DGGML_VULKAN=OFF -DLLAMA_BUILD_TESTS=OFF -DLLAMA_BUILD_EXAMPLES=OFF -DLLAMA_CURL=OFF -DCMAKE_CUDA_COMPILER="%CUDA%\bin\nvcc.exe"
if errorlevel 1 goto :fail_configure

echo === build llama-server (CUDA) ===
"%CMAKE%" --build build-cuda --target llama-server -j 8
if errorlevel 1 goto :fail_build

echo === artifacts ===
dir /b build-cuda\bin
echo [DONE]
exit /b 0

:fail_vcvars
echo [FAIL] vcvars64.bat
exit /b 1

:fail_nvcc
echo [FAIL] nvcc.exe not found
exit /b 1

:fail_configure
echo [FAIL] cmake configure
exit /b 1

:fail_build
echo [FAIL] cmake build
exit /b 1
