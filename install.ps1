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

# --- uninstaller + "Installed apps" entry -----------------------------------
# Settings -> Apps -> Installed apps is built from the Uninstall registry keys, so a
# per-user install belongs under HKCU. Everything below is rewritten on every run
# (install AND update), so the values always match what is actually on disk.
#
# uninstall.ps1 is embedded here instead of shipped in the release ZIP for two reasons:
# this script is fetched fresh from main on every run (so the copy is always current),
# and older releases whose ZIP predates the uninstaller would otherwise get an
# "Installed apps" entry pointing at a file that does not exist.
$uninstallPs1 = @'
# SPDX-License-Identifier: AGPL-3.0-only
#
# HA Companion for Windows - uninstaller
#
# Removes the per-user installation made by install.ps1: the program folder
# %LOCALAPPDATA%\Programs\HaCompanion, the Start Menu shortcut, the autostart entry,
# the toast-notification registration and the "Installed apps" entry.
#
# Windows starts this for you: Settings -> Apps -> Installed apps -> HA Companion -> Uninstall.
# By hand:
#   powershell -NoProfile -ExecutionPolicy Bypass -File uninstall.ps1 [-Silent] [-KeepData]
#     -Silent    no questions, no dialogs (used by QuietUninstallString / winget).
#                Without -KeepData this also removes your settings.
#     -KeepData  keep %LOCALAPPDATA%\HaCompanion (settings, token, tiles, rules, logs)
#
# Everything that could belong to another copy of the app (a manual unzip, a dev build)
# is left alone: every step checks that the thing it removes really points into the
# folder this uninstaller owns.

param(
    [switch]$Silent,
    [switch]$KeepData,
    [switch]$FromTemp   # internal: set when we already run from %TEMP%
)

$ErrorActionPreference = 'Continue'
$dest = Join-Path $env:LOCALAPPDATA 'Programs\HaCompanion'
$arpKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\HaCompanion'
$problems = @()

function Show-Dialog {
    param([string]$Text, [string]$Title, [int]$Flags)
    if ($Silent) { return -1 }
    try {
        # WScript.Shell is always present and works from a hidden console, unlike a
        # WinForms MessageBox (which needs an assembly load and a message pump).
        return (New-Object -ComObject WScript.Shell).Popup($Text, 0, $Title, $Flags)
    } catch { return -1 }
}

function Test-InDest {
    param([string]$Path)
    if (-not $Path) { return $false }
    $clean = $Path.Trim('"').Trim()
    return $clean.ToLowerInvariant().StartsWith($dest.ToLowerInvariant())
}

# --- step 0: run from %TEMP% so we may delete our own program folder ---------
if (-not $FromTemp) {
    $tempCopy = Join-Path $env:TEMP 'hacompanion-uninstall.ps1'
    Copy-Item -LiteralPath $PSCommandPath -Destination $tempCopy -Force
    $psExe = Join-Path $PSHOME 'powershell.exe'
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$tempCopy`"", '-FromTemp')
    if ($Silent) { $argList += '-Silent' }
    if ($KeepData) { $argList += '-KeepData' }
    $child = Start-Process -FilePath $psExe -ArgumentList $argList -WindowStyle Hidden -PassThru -Wait
    exit $child.ExitCode
}

# --- step 1: stop the installed app (never a copy running from elsewhere) ----
$running = @(Get-Process -Name 'HaCompanion' -ErrorAction SilentlyContinue |
             Where-Object { Test-InDest $_.Path })
foreach ($p in $running) {
    try { $p.CloseMainWindow() | Out-Null } catch { }
}
Start-Sleep -Milliseconds 500
foreach ($p in $running) {
    try { if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force -ErrorAction Stop } } catch { }
}
# The WebView2 host processes keep the profile folders open a moment longer.
Start-Sleep -Seconds 2

# --- step 2: autostart entry ------------------------------------------------
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$runValue = (Get-ItemProperty -Path $runKey -Name 'HaCompanion' -ErrorAction SilentlyContinue).HaCompanion
if ($runValue -and (Test-InDest $runValue)) {
    Remove-ItemProperty -Path $runKey -Name 'HaCompanion' -ErrorAction SilentlyContinue
}

# --- step 3: Start Menu shortcut --------------------------------------------
$lnk = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\HA Companion.lnk'
if (Test-Path $lnk) {
    $target = $null
    try { $target = (New-Object -ComObject WScript.Shell).CreateShortcut($lnk).TargetPath } catch { }
    if (-not $target -or (Test-InDest $target)) { Remove-Item $lnk -Force -ErrorAction SilentlyContinue }
}

# --- step 4: toast-notification registration --------------------------------
# AppNotificationManager.Register() (unpackaged) writes
#   HKCU\Software\Classes\CLSID\{guid}\LocalServer32 = "<exe>" ----AppNotificationActivated:
#   HKCU\Software\Classes\AppUserModelId\<aumid>     CustomActivator = {guid}
# The app has no Unregister() call, so clean up here - but only entries whose
# command really points into the folder we are removing.
$ourClsids = @()
Get-ChildItem 'HKCU:\Software\Classes\CLSID' -ErrorAction SilentlyContinue | ForEach-Object {
    $server = Join-Path $_.PSPath 'LocalServer32'
    if (Test-Path $server) {
        $cmd = (Get-ItemProperty -Path $server -Name '(default)' -ErrorAction SilentlyContinue).'(default)'
        if ($cmd -and (Test-InDest $cmd)) { $ourClsids += $_.PSChildName }
    }
}
foreach ($clsid in $ourClsids) {
    Remove-Item -Path ('HKCU:\Software\Classes\CLSID\' + $clsid) -Recurse -Force -ErrorAction SilentlyContinue
}
if ($ourClsids.Count -gt 0) {
    Get-ChildItem 'HKCU:\Software\Classes\AppUserModelId' -ErrorAction SilentlyContinue | ForEach-Object {
        $activator = (Get-ItemProperty -Path $_.PSPath -Name 'CustomActivator' -ErrorAction SilentlyContinue).CustomActivator
        if ($activator -and ($ourClsids -contains $activator.Trim())) {
            Remove-Item -Path $_.PSPath -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

# --- step 5: user data (settings, token, tiles, rules, logs) ----------------
$dataDir = Join-Path $env:LOCALAPPDATA 'HaCompanion'
$removeData = -not $KeepData
if ($removeData -and -not $Silent -and (Test-Path $dataDir)) {
    $answer = Show-Dialog -Title 'Uninstall HA Companion' -Flags (4 + 32 + 4096) -Text @"
Also remove your settings?

This deletes $dataDir - your Home Assistant token, tiles, shortcuts, automations, notification rules and logs.

Choose No to keep them for a later reinstall.
"@
    if ($answer -eq 7) { $removeData = $false }   # 6 = Yes, 7 = No, -1 = no dialog possible
}
if ($removeData -and (Test-Path $dataDir)) {
    Remove-Item -Path $dataDir -Recurse -Force -ErrorAction SilentlyContinue
    if (Test-Path $dataDir) { $problems += "settings folder could not be removed: $dataDir" }
}

# --- step 6: the "Installed apps" entry -------------------------------------
if (Test-Path $arpKey) { Remove-Item -Path $arpKey -Recurse -Force -ErrorAction SilentlyContinue }

# --- step 7: the program folder ---------------------------------------------
if (Test-Path $dest) {
    for ($i = 0; $i -lt 5 -and (Test-Path $dest); $i++) {
        Remove-Item -Path $dest -Recurse -Force -ErrorAction SilentlyContinue
        if (Test-Path $dest) { Start-Sleep -Seconds 1 }
    }
}

# --- step 8: fallback for a stubborn folder (still no admin rights needed) ---
if (Test-Path $dest) {
    # Something still holds a file open. Rename it out of the way and let a detached
    # command remove it once the lock is gone.
    $stale = $dest + '.old-' + (Get-Date -Format 'yyyyMMddHHmmss')
    try {
        Rename-Item -Path $dest -NewName (Split-Path $stale -Leaf) -ErrorAction Stop
        Start-Process -FilePath 'cmd.exe' `
            -ArgumentList '/c', 'timeout /t 5 /nobreak >nul & rmdir /s /q "' + $stale + '"' `
            -WindowStyle Hidden
    } catch {
        $problems += "program folder is still in use: $dest"
    }
}

# --- step 9: result ----------------------------------------------------------
$note = 'If you had PC sensors enabled, remove the mobile_app device in Home Assistant as well (Settings -> Devices & Services -> Mobile App).'
if ($problems.Count -gt 0) {
    Show-Dialog -Title 'HA Companion' -Flags (0 + 48) -Text ("Uninstall finished with warnings:`n`n" + ($problems -join "`n") + "`n`n" + $note) | Out-Null
    exit 1
}
Show-Dialog -Title 'HA Companion' -Flags (0 + 64) -Text ("HA Companion was removed.`n`n" + $note) | Out-Null
exit 0
'@
$uninstallPath = Join-Path $dest 'uninstall.ps1'
Set-Content -LiteralPath $uninstallPath -Value $uninstallPs1 -Encoding ASCII

$psExe = Join-Path $PSHOME 'powershell.exe'   # never rely on PATH when Windows starts this
$uninstallCmd = '"{0}" -NoProfile -ExecutionPolicy Bypass -File "{1}"' -f $psExe, $uninstallPath
$sizeKb = [int]((Get-ChildItem -LiteralPath $dest -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1KB)

$arp = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\HaCompanion'
New-Item -Path $arp -Force | Out-Null
Set-ItemProperty -Path $arp -Name 'DisplayName'          -Value 'HA Companion for Windows'
Set-ItemProperty -Path $arp -Name 'DisplayVersion'       -Value ($release.tag_name -replace '^v', '')
Set-ItemProperty -Path $arp -Name 'Publisher'            -Value 'Elias0505'
Set-ItemProperty -Path $arp -Name 'DisplayIcon'          -Value ($exe + ',0')
Set-ItemProperty -Path $arp -Name 'InstallLocation'      -Value $dest
Set-ItemProperty -Path $arp -Name 'InstallDate'          -Value (Get-Date -Format 'yyyyMMdd')
Set-ItemProperty -Path $arp -Name 'URLInfoAbout'         -Value ('https://github.com/' + $repo)
Set-ItemProperty -Path $arp -Name 'HelpLink'             -Value ('https://github.com/' + $repo + '/issues')
Set-ItemProperty -Path $arp -Name 'UninstallString'      -Value $uninstallCmd
Set-ItemProperty -Path $arp -Name 'QuietUninstallString' -Value ($uninstallCmd + ' -Silent')
# EstimatedSize is a DWORD in KILOBYTES - passing bytes shows an absurd size in Settings.
Set-ItemProperty -Path $arp -Name 'EstimatedSize' -Value $sizeKb -Type DWord
Set-ItemProperty -Path $arp -Name 'NoModify'      -Value 1 -Type DWord
Set-ItemProperty -Path $arp -Name 'NoRepair'      -Value 1 -Type DWord

# A copy running from somewhere else (manual unzip, self-built) is not managed here.
$foreign = @(Get-Process -Name 'HaCompanion' -ErrorAction SilentlyContinue |
             Where-Object { $_.Path -and -not $_.Path.ToLowerInvariant().StartsWith($dest.ToLowerInvariant()) })
if ($foreign.Count -gt 0) {
    Write-Warning ('Another copy of HA Companion is running from ' + $foreign[0].Path +
                   ' - this installer only manages ' + $dest)
}

Write-Host ''
Write-Host ("HA Companion {0} installed to {1}" -f $release.tag_name, $dest)
Write-Host 'A Start Menu shortcut "HA Companion" was created.'
Write-Host 'Note: the binary is not code-signed yet - Windows SmartScreen may warn on first start ("More info" -> "Run anyway").'
Write-Host 'Uninstall: Settings -> Apps -> Installed apps -> HA Companion for Windows -> Uninstall.'
Write-Host ''
Start-Process -FilePath $exe -WorkingDirectory $dest
