@echo off
title CTRL Cleaner — Build
color 0A
set DOTNET_CLI_UI_LANGUAGE=en-US
set DOTNET_LANGUAGE=en-US

echo.
echo  ==========================================
echo   CTRL Cleaner Builder
echo  ==========================================
echo.

where dotnet >nul 2>&1
if %errorlevel% neq 0 (
    echo [x] .NET SDK not found. Download at: https://dot.net/download
    pause & exit /b 1
)

echo [*] Building...
cd /d "%~dp0CtrlCleaner"

dotnet publish -c Release -r win-x64 --self-contained true ^
    /p:PublishSingleFile=true ^
    /p:IncludeNativeLibrariesForSelfExtract=true ^
    -o "%~dp0dist"

if %errorlevel% neq 0 (
    echo.
    echo [x] Build failed!
    pause & exit /b 1
)

echo.
echo  ==========================================
echo   [+] Build OK!
echo   [+] Output: dist\CtrlCleaner.exe
echo   [+] mp4 embedded in exe — no external dependencies!
echo  ==========================================
echo.
echo  Run dist\CtrlCleaner.exe as Administrator.
echo.
pause
