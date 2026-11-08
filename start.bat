@echo off
setlocal
cd /d "%~dp0"

set "EXE=src\Step.UI\bin\Debug\net8.0-windows\StepAudioDriver.exe"

if not exist "%EXE%" (
    echo Building Step.UI ^(first run^)...
    dotnet build StepAudioDriver.sln -c Debug
    if errorlevel 1 (
        echo.
        echo Build failed. Make sure the .NET 8 SDK is installed.
        pause
        exit /b 1
    )
)

start "" "%EXE%"
