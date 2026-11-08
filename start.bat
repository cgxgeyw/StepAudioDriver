@echo off
setlocal
cd /d "%~dp0"

set "EXE=src\SK02.UI\bin\Debug\net8.0-windows\SK02.UI.exe"

if not exist "%EXE%" (
    echo Building SK02.UI ^(first run^)...
    dotnet build SK02AudioDriver.sln -c Debug
    if errorlevel 1 (
        echo.
        echo Build failed. Make sure the .NET 8 SDK is installed.
        pause
        exit /b 1
    )
)

start "" "%EXE%"
