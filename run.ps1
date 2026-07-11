#!/usr/bin/env pwsh
# Build & run HA Companion for Windows (Debug) on your PC.
#
# Prerequisite: the .NET 9 SDK  ->  https://dotnet.microsoft.com/download/dotnet/9.0
# Everything else (WinUI 3 / Windows App SDK) is restored automatically from NuGet.
#
# Usage:  right-click -> "Run with PowerShell", or in a terminal:  ./run.ps1

$ErrorActionPreference = "Stop"
Push-Location $PSScriptRoot
try {
    Write-Host "Building & launching HA Companion (Debug)..." -ForegroundColor Cyan
    dotnet run --project src/HaCompanion.App/HaCompanion.App.csproj -c Debug -r win-x64 -p:Platform=x64
}
finally {
    Pop-Location
}
