# HA Companion for Windows

A **fully native Windows 11 companion app for [Home Assistant](https://www.home-assistant.io/)** — built in C# with **.NET 9 + WinUI 3 (Windows App SDK)**. Fluent design (Mica/Acrylic), live REST + WebSocket integration, configurable quick-action tiles, a dashboard, system-tray presence and native notifications. **No WebView as the main UI** — this is real native XAML, not a wrapped web page.

> Status: **early MVP, work in progress.** The first milestone is: settings → live connection → dashboard with quick-action tiles → tray → notifications. Jump Lists, global hotkeys and Widgets follow.

*Not affiliated with or endorsed by the Home Assistant project / Nabu Casa. "Home Assistant" is a trademark of its respective owners; this app is an independent client "for Home Assistant".*

---

## Features (MVP)

- 🎛 **Quick-action tiles** — toggle lights/switches/scenes, see live state at a glance
- 📊 **Dashboard** — your favourite entities, updated in real time
- 🔌 **Live connection** — REST for actions + WebSocket for push state updates (auto-reconnect)
- 🪟 **Native Fluent UI** — Mica backdrop, WinUI 3 controls, light/dark aware
- 🔔 **Native notifications** — Windows toast notifications for state changes you care about
- 🧰 **System tray** — stays out of the way, quick access from the notification area
- 🔒 **Token stored securely** — long-lived token kept in the Windows Credential Locker, never in a plaintext config

### Planned

- 📌 Jump Lists (right-click taskbar quick actions)
- ⌨️ Global hotkeys
- 🧩 Windows Widgets board support
- 📦 MSIX packaging + signed releases

---

## Architecture

Clean **MVVM**, split into two projects:

| Project | Target | What |
|---|---|---|
| `HaCompanion.Core` | `net9.0` (platform-neutral) | Home Assistant REST + WebSocket clients, models, connection service. No UI, unit-testable, builds on any OS. |
| `HaCompanion.App` | `net9.0-windows` (WinUI 3) | Views, ViewModels, tray, notifications, secure settings store, DI composition root. Windows-only. |

- **DI**: `Microsoft.Extensions.DependencyInjection`
- **MVVM**: `CommunityToolkit.Mvvm` (source-generated observable properties & commands)
- **Tray**: `H.NotifyIcon.WinUI`

---

## Run it on your PC

**Only prerequisite: the [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0).** No Visual Studio, no separate "Windows App SDK" install — WinUI 3 is restored automatically from NuGet. Windows 10 (19041+) or Windows 11.

```powershell
# from the repo root — build & launch (Debug)
./run.ps1
```

Or manually:

```powershell
dotnet run --project src/HaCompanion.App -c Debug -r win-x64 -p:Platform=x64
```

### Make a double-click .exe (nothing to install on the target PC)

```powershell
./publish.ps1
```

This produces a **self-contained** build — the resulting `HaCompanion.exe` bundles the .NET runtime *and* the Windows App SDK, so it runs on any Windows 11 PC with nothing pre-installed.

> `HaCompanion.Core` builds on Linux/macOS too (handy for CI/tests). The WinUI 3 app requires Windows.

## First run

1. Open **Settings**.
2. Enter your Home Assistant base URL (e.g. `https://homeassistant.local:8123` or `http://192.168.x.x:8123`).
3. Paste a **Long-Lived Access Token** (Home Assistant → your profile → *Long-lived access tokens* → *Create token*).
4. Connect. Pick the entities you want as quick-action tiles.

---

## License

**GNU Affero General Public License v3.0 only (AGPL-3.0-only).** See [`LICENSE`](LICENSE).

In short: you may use, study, share and modify this software freely — but **if you distribute it or run a modified version as a network service, you must release your complete corresponding source under the AGPL as well.** This deliberately prevents anyone from taking this code, closing it, and reselling it as a proprietary product.

### 🏢 Commercial / proprietary use

Want to use this in a **closed-source or commercial product** without the AGPL's copyleft obligations? That requires a **separate commercial license** — please get in touch. See [`COMMERCIAL.md`](COMMERCIAL.md).

## Contributing

Contributions are welcome! By contributing you agree to the **Developer Certificate of Origin** and to license your contribution under the project's terms so the maintainer can keep offering commercial licenses. See [`CONTRIBUTING.md`](CONTRIBUTING.md).
