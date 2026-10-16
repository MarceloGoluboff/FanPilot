# Publishes a self-contained single-file FanPilot.exe to publish\ and builds the installer into dist\.
param([switch]$SkipInstaller)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = "$root\src\FanPilot\FanPilot.csproj"

# Directory.Build.props is the single source for the version.
$version = (dotnet msbuild $project -getProperty:Version).Trim()
if ($LASTEXITCODE -or -not $version) { throw 'Could not read the version from Directory.Build.props' }
Write-Host "FanPilot $version"

# The installer ships everything in publish\, so start clean.
Remove-Item "$root\publish" -Recurse -Force -ErrorAction SilentlyContinue

dotnet publish $project `
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

& $iscc "/DAppVersion=$version" "$root\installer\FanPilot.iss"
if ($LASTEXITCODE) { throw "ISCC failed ($LASTEXITCODE)" }

Get-ChildItem "$root\dist\FanPilot-Setup-$version.exe" | ForEach-Object { Write-Host "Installer: $($_.FullName)" }
