param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$publishDir = Join-Path $root 'artifacts\publish\win-x64'
$installerDir = Join-Path $root 'artifacts\installer'
$toolsDir = Join-Path $root 'artifacts\tools'
$wix = Join-Path $toolsDir 'wix.exe'

if (Test-Path $publishDir) {
    Remove-Item $publishDir -Recurse -Force
}

dotnet publish (Join-Path $root 'src\Step.UI\Step.UI.csproj') `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=false `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $publishDir `
    --nologo

if (-not (Test-Path $wix)) {
    New-Item -ItemType Directory -Force $toolsDir | Out-Null
    dotnet tool install --tool-path $toolsDir wix --version 5.0.2
}

New-Item -ItemType Directory -Force $installerDir | Out-Null

& $wix build (Join-Path $PSScriptRoot 'StepAudioDriver.wxs') `
    -arch x64 `
    -o (Join-Path $installerDir 'StepAudioDriver-1.9.0.msi')

$msi = Join-Path $installerDir 'StepAudioDriver-1.9.0.msi'
$hash = (Get-FileHash $msi -Algorithm SHA256).Hash
"$hash  StepAudioDriver-1.9.0.msi" |
    Set-Content (Join-Path $installerDir 'StepAudioDriver-1.9.0.sha256') -Encoding ASCII

Write-Host "MSI: $msi"
