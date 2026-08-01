# HA Companion for Windows - one-line installer / updater
#
#   irm https://raw.githubusercontent.com/Elias0505/ha-companion-windows/main/install.ps1 | iex
#
# Options via environment variables (set before running):
#   $env:HACOMPANION_AUTOSTART = '1'   -> also register the app to start with Windows
#   $env:GH_TOKEN = '<token>'          -> install from a private fork/repo (maintainers/testing)
#
# The script downloads the latest GitHub release (self-contained win-x64 build),
# installs it to %LOCALAPPDATA%\Programs\HaCompanion and creates a Start Menu
# shortcut. Running it again updates an existing installation in place.
# Your settings are stored separately in %LOCALAPPDATA%\HaCompanion and are
# never touched by install or update.

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

$repo = 'Elias0505/ha-companion-windows'
$dest = Join-Path $env:LOCALAPPDATA 'Programs\HaCompanion'

# --- prerequisites -----------------------------------------------------------
# Windows 10 2004 (build 19041) or newer is required by the Windows App SDK.
$build = [Environment]::OSVersion.Version.Build
if ($build -lt 19041) {
    throw "HA Companion requires Windows 10 version 2004 (build 19041) or newer. This PC is on build $build."
}

# The app bundles .NET and the Windows App SDK (self-contained). The only
# external requirement is the WebView2 Evergreen Runtime (preinstalled on
# Windows 11; may be missing on Windows 10). Detect it the way Microsoft
# documents (EdgeUpdate client registry keys) and install it via the official
# bootstrapper only if it is missing.
function Test-WebView2 {
    $keys = @(
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}',
        'HKLM:\SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}',
        'HKCU:\SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}'
    )
    foreach ($k in $keys) {
        $pv = (Get-ItemProperty -Path $k -Name pv -ErrorAction SilentlyContinue).pv
        if ($pv -and $pv -ne '0.0.0.0') { return $true }
    }
    return $false
}

if (-not (Test-WebView2)) {
    Write-Host 'WebView2 Runtime is missing - installing it via the official Microsoft bootstrapper (a UAC prompt may appear)...'
    $wv2 = Join-Path $env:TEMP 'MicrosoftEdgeWebView2Setup.exe'
    Invoke-WebRequest -Uri 'https://go.microsoft.com/fwlink/p/?LinkId=2124703' -OutFile $wv2
    Start-Process -FilePath $wv2 -ArgumentList '/install' -Wait
    Remove-Item -Path $wv2 -Force -ErrorAction SilentlyContinue
    if (-not (Test-WebView2)) {
        Write-Warning 'WebView2 Runtime could not be verified. The app will still install, but the Lovelace view needs WebView2 (https://developer.microsoft.com/microsoft-edge/webview2/).'
    }
}
# -----------------------------------------------------------------------------

$headers = @{ 'User-Agent' = 'HaCompanion-Installer' }
if ($env:GH_TOKEN) { $headers['Authorization'] = "Bearer $($env:GH_TOKEN)" }

Write-Host 'Looking up the latest HA Companion release...'
$release = Invoke-RestMethod -Uri "https://api.github.com/repos/$repo/releases/latest" -Headers $headers
$asset = $release.assets | Where-Object { $_.name -like 'HaCompanion-*-win-x64.zip' } | Select-Object -First 1
if (-not $asset) {
    throw "No win-x64 release asset found in $($release.tag_name). Please report this at https://github.com/$repo/issues"
}

$zip = Join-Path $env:TEMP $asset.name
Write-Host ("Downloading {0} ({1:N1} MB)..." -f $asset.name, ($asset.size / 1MB))
if ($env:GH_TOKEN) {
    # Private repos require the asset API endpoint with octet-stream accept header
    $dlHeaders = @{ 'User-Agent' = 'HaCompanion-Installer'; 'Authorization' = "Bearer $($env:GH_TOKEN)"; 'Accept' = 'application/octet-stream' }
    Invoke-WebRequest -Uri $asset.url -Headers $dlHeaders -OutFile $zip
} else {
    Invoke-WebRequest -Uri $asset.browser_download_url -Headers $headers -OutFile $zip
}

$running = Get-Process -Name 'HaCompanion' -ErrorAction SilentlyContinue
if ($running) {
    Write-Host 'Stopping the running HA Companion instance...'
    $running | Stop-Process -Force
    Start-Sleep -Milliseconds 800
}

if (Test-Path $dest) { Remove-Item -Path $dest -Recurse -Force }
New-Item -ItemType Directory -Force -Path $dest | Out-Null
Expand-Archive -Path $zip -DestinationPath $dest -Force
Remove-Item -Path $zip -Force

$exe = Join-Path $dest 'HaCompanion.exe'
if (-not (Test-Path $exe)) { throw "HaCompanion.exe not found after extraction - the release asset layout changed?" }

$shell = New-Object -ComObject WScript.Shell
$lnkPath = Join-Path $shell.SpecialFolders.Item('Programs') 'HA Companion.lnk'
$lnk = $shell.CreateShortcut($lnkPath)
$lnk.TargetPath = $exe
$lnk.WorkingDirectory = $dest
$lnk.Description = 'HA Companion for Windows'
$lnk.Save()

if ($env:HACOMPANION_AUTOSTART -eq '1') {
    Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'HaCompanion' -Value ('"' + $exe + '"')
    Write-Host 'Autostart enabled (HKCU Run key).'
}

Write-Host ''
Write-Host ("HA Companion {0} installed to {1}" -f $release.tag_name, $dest)
Write-Host 'A Start Menu shortcut "HA Companion" was created.'
Write-Host 'Note: the binary is not code-signed yet - Windows SmartScreen may warn on first start ("More info" -> "Run anyway").'
Write-Host 'Uninstall: quit the app, delete the folder above and the Start Menu shortcut. Settings live in %LOCALAPPDATA%\HaCompanion.'
Write-Host ''
Start-Process -FilePath $exe -WorkingDirectory $dest
