@echo off
echo Starting Gemini Dotnet publish and ZIP process...

REM Set variables
set PROJECT_DIR=E:\Development\GeminiDotnet
set PUBLISH_DIR=%PROJECT_DIR%\bin\Release\net8.0-windows\win-x64\publish

REM Step 1: Run dotnet publish
echo Running dotnet publish...
dotnet publish -p:PublishSingleFile=true -c Release -r win-x64 --self-contained true Gemini.csproj
if %ERRORLEVEL% NEQ 0 (
    echo Error: Publish failed!
    pause
    exit /b %ERRORLEVEL%
)

cp %PUBLISH_DIR%\gemini-dotnet.exe E:\Apps\

pause