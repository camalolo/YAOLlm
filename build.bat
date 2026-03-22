@echo off
echo Starting YAOLlm publish and ZIP process...

REM Set variables
set PROJECT_DIR=E:\Development\CSharp\YAOLlm
set PUBLISH_DIR=%PROJECT_DIR%\bin\Release\net8.0-windows\win-x64\publish

REM Step 1: Run dotnet publish
echo Running dotnet publish...
dotnet publish -p:PublishSingleFile=true -c Release -r win-x64 --self-contained true YAOLlm.csproj
if %ERRORLEVEL% NEQ 0 (
    echo Error: Publish failed!
    pause
    exit /b %ERRORLEVEL%
)

cp %PUBLISH_DIR%\YAOLlm.exe E:\Apps\

pause