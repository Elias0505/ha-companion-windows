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
    # Windows PowerShell by absolute path - $PSHOME would be pwsh.exe's folder if someone
    # started this script from PowerShell 7, and powershell.exe does not live there.
    $psExe = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
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
