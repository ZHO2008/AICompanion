@echo off
setlocal enabledelayedexpansion
cd /d "%~dp0.."

REM ============================================================================
REM  build_fvad.bat - build bin\libfvad.dll from third_party\libfvad
REM
REM  libfvad ships a CMake project that only produces a STATIC library and its
REM  header declares no __declspec(dllexport), so a shared build would export
REM  nothing on Windows. third_party\fvad_shim therefore compiles libfvad's 13
REM  sources directly into a DLL and exports the 6 public functions via a .def.
REM
REM  Uses Ninja (serial NMake is slow and pointless for such a small target).
REM  ASCII only -- see build_native.bat for why.
REM ============================================================================

set "VCVARS=D:\VS 2026\project\VC\Auxiliary\Build\vcvars64.bat"
set "CMAKE=%~dp0..\tools\toolchain\cmake\bin\cmake.exe"
set "NINJA_DIR=%~dp0..\tools\toolchain\ninja"
set "SRC=%~dp0..\third_party\fvad_shim"
set "BUILD=%~dp0..\third_party\fvad_shim\build"

if not exist "%CMAKE%" (echo [ERROR] cmake not found & exit /b 1)
if not exist "%NINJA_DIR%\ninja.exe" (echo [ERROR] ninja not found & exit /b 1)
if not exist "%SRC%\CMakeLists.txt" (echo [ERROR] fvad_shim missing & exit /b 1)
if not exist "%~dp0..\third_party\libfvad\src\fvad.c" (echo [ERROR] libfvad source missing & exit /b 1)

call "%VCVARS%" >nul 2>&1
if errorlevel 1 (echo [ERROR] vcvars64.bat failed & exit /b 1)

set "PATH=%NINJA_DIR%;%PATH%"

echo [1/2] Configuring fvad_shim ...
"%CMAKE%" -S "%SRC%" -B "%BUILD%" -G Ninja -DCMAKE_BUILD_TYPE=Release
if errorlevel 1 (echo [ERROR] configure failed & exit /b 1)

echo [2/2] Building ...
"%CMAKE%" --build "%BUILD%"
if errorlevel 1 (echo [ERROR] build failed & exit /b 1)

if not exist "%~dp0..\bin\libfvad.dll" (
    echo [ERROR] bin\libfvad.dll not produced
    exit /b 1
)

echo.
echo [OK] bin\libfvad.dll
endlocal
exit /b 0
