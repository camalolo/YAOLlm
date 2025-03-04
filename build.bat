@echo off
echo Starting Gemini publish and ZIP process...

REM Set variables
set PROJECT_DIR=E:\Development\GeminiC#
set PUBLISH_DIR=%PROJECT_DIR%\bin\Release\net8.0-windows\win-x64\publish
set ZIP_NAME=GeminiApp.zip

REM Step 1: Run dotnet publish
echo Running dotnet publish...
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
if %ERRORLEVEL% NEQ 0 (
    echo Error: Publish failed!
    pause
    exit /b %ERRORLEVEL%
)

pause