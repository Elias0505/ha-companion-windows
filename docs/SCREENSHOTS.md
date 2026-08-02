# Screenshots

A tour through HA Companion for Windows. All shots were taken on Windows 11 (dark theme) against a real Home Assistant installation.

## Quick panel

Press the global hotkey (default `Win+Ctrl+H`) anywhere in Windows and your Home Assistant slides in over whatever you are doing — pinnable, resizable, and gone again with the same hotkey. No window to find, no browser tab.

![Quick panel sliding in over the Windows desktop](media/quick-panel-desktop.gif)

Recorded on a real 4K desktop at 60 fps (GPU capture), replayed here at 40 fps.
**[Same clip as MP4](media/quick-panel-demo.mp4)** if you want it sharper.

Close-up of the panel itself:

![Quick panel demo](media/quick-panel-demo.gif)

The panel can show your favourite entity tiles or a full Lovelace dashboard of your choice:

<img src="media/quick-panel.png" width="420" alt="Quick panel showing a Lovelace overview dashboard" />
<img width="514" height="1079" alt="image" src="https://github.com/user-attachments/assets/a9a76e49-d1bd-4f26-a25b-34d86c48b1ad" />



## Start

Favourites, category filters and live tiles for every entity Home Assistant exposes:

![Start page](media/start.png)

## Windows → HA automations

Build rules like "when the webcam turns on, set the meeting light" or "when I lock the PC, turn everything off" — triggered by real Windows events (lock/unlock, idle, fullscreen, mic/camera use, power state, schedules):

![Automations](media/automations.png)

## My PC — the PC as a Home Assistant device

The app registers your PC via the `mobile_app` integration: 11 live sensors (locked, idle, mic/camera in use, foreground app, ...) plus opt-in commands Home Assistant can send back — lock, sleep, volume, monitor off and more:

![My PC](media/my-pc.png)

## Shortcuts

Assign a system-wide key combination to any device, scene or script:

![Shortcuts](media/shortcuts.png)

## HA Dashboards

Your full Lovelace dashboards, rendered 1:1 in a native window (WebView2) — switchable per dashboard:

![HA Dashboards](media/ha-dashboards.png)

## Settings

Everything lives on one page: the connection (base URL, long-lived token, optional self-signed certificate), the quick panel (default view, width, auto-hide, hotkey), language and autostart, the opt-in PC sensors, a token-free configuration backup, and a redacted diagnostics report:

![Settings](media/settings.png)

The token is only ever shown masked and is stored encrypted with Windows DPAPI — it never appears in the backup or in a diagnostics report.
