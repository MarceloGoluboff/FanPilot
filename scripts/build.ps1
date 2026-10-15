# Publishes a self-contained single-file FanControl.exe to publish\ and builds the installer into dist\.
param([switch]$SkipInstaller)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
# The installer ships everything in publish\, so start clean.
Remove-Item "$root\publish" -Recurse -Force -ErrorAction SilentlyContinue

dotnet publish "$root\src\FanControl\FanControl.csproj" `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none `
    -o "$root\publish"
if ($LASTEXITCODE) { throw "dotnet publish failed ($LASTEXITCODE)" }

if ($SkipInstaller) { return }

$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 not found. Install it with: winget install JRSoftware.InnoSetup' }

& $iscc "$root\installer\FanControl.iss"
if ($LASTEXITCODE) { throw "ISCC failed ($LASTEXITCODE)" }

Get-ChildItem "$root\dist\*.exe" | ForEach-Object { Write-Host "Installer: $($_.FullName)" }
