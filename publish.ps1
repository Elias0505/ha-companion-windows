#!/usr/bin/env pwsh
# Publish a self-contained build of HA Companion for Windows.
# The result is a folder with a double-clickable HaCompanion.exe that needs
# NO .NET runtime and NO Windows App SDK install on the target PC.
#
# Prerequisite (to build): the .NET 9 SDK.
# Usage:  ./publish.ps1

$ErrorActionPreference = "Stop"
Push-Location $PSScriptRoot
try {
    Write-Host "Publishing self-contained Release build..." -ForegroundColor Cyan
    dotnet publish src/HaCompanion.App/HaCompanion.App.csproj -c Release -r win-x64 -p:Platform=x64

    $out = "src/HaCompanion.App/bin/x64/Release/net9.0-windows10.0.19041.0/win-x64/publish"
    Write-Host ""
    Write-Host "Done. Double-click the app here:" -ForegroundColor Green
    Write-Host "  $out\HaCompanion.exe"
}
finally {
    Pop-Location
}
