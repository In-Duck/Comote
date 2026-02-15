@echo off
setlocal
cd /d "%~dp0"

echo ========================================================
echo       Comote Installer Auto-Builder
echo ========================================================

:: 1. Check for Inno Setup Compiler
set "ISCC_PATH="
if exist "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" set "ISCC_PATH=C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
if exist "C:\Program Files\Inno Setup 6\ISCC.exe" set "ISCC_PATH=C:\Program Files\Inno Setup 6\ISCC.exe"

if "%ISCC_PATH%"=="" (
    echo [ERROR] Inno Setup Compiler (ISCC.exe) not found!
    echo Please install Inno Setup 6+ from: https://jrsoftware.org/isdl.php
    pause
    exit /b 1
)

echo [INFO] Found Inno Setup at: "%ISCC_PATH%"
echo.

:: 2. Publish .NET Projects (Release SingleFile)
echo [1/4] Publishing Host...
dotnet publish ..\Host -c Release -r win-x64 -p:PublishSingleFile=true --self-contained true -o "..\publish\Host-single"
if %errorlevel% neq 0 (
    echo [ERROR] Failed to publish Host project.
    pause
    exit /b 1
)

echo [2/4] Publishing Viewer...
dotnet publish ..\Viewer -c Release -r win-x64 -p:PublishSingleFile=true --self-contained true -o "..\publish\Viewer-single"
if %errorlevel% neq 0 (
    echo [ERROR] Failed to publish Viewer project.
    pause
    exit /b 1
)

:: 3. Compile Installers using ISCC
echo.
echo [3/4] Compiling Host Installer...
"%ISCC_PATH%" "Host\Host_Installer.iss"
if %errorlevel% neq 0 (
    echo [ERROR] Failed to compile Host Installer.
    pause
    exit /b 1
)

echo [4/4] Compiling Viewer Installer...
"%ISCC_PATH%" "Viewer\Viewer_Installer.iss"
if %errorlevel% neq 0 (
    echo [ERROR] Failed to compile Viewer Installer.
    pause
    exit /b 1
)

echo.
echo ========================================================
echo       SUCCESS! Installers created.
echo ========================================================
echo Host Setup:   Distribution\Host\ComoteHost_Setup.exe
echo Viewer Setup: Distribution\Viewer\ComoteViewer_Setup.exe
echo.
pause
