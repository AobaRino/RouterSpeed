@echo off
setlocal
rem Builds RouterSpeedPlugin.dll (x64) with the Visual Studio C++ toolchain ("Desktop
rem development with C++" workload, which brings cl.exe and the Windows SDK).
rem Run from a plain command prompt; the first Visual Studio with vcvars64.bat is used.
set "VCVARS="
for /f "usebackq delims=" %%i in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -all -prerelease -products * -property installationPath`) do (
    if not defined VCVARS if exist "%%i\VC\Auxiliary\Build\vcvars64.bat" set "VCVARS=%%i\VC\Auxiliary\Build\vcvars64.bat"
)
if not defined VCVARS (echo Visual Studio C++ tools not found. & exit /b 1)
call "%VCVARS%" >nul 2>&1 || exit /b 1
if not exist build mkdir build
cl /nologo /std:c++17 /EHsc /O2 /W4 /wd4100 /utf-8 /DUNICODE /D_UNICODE /LD /Fo:build\ /Fe:build\RouterSpeedPlugin.dll RouterSpeedPlugin.cpp /link /DEF:RouterSpeedPlugin.def
exit /b %ERRORLEVEL%
