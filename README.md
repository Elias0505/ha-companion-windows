# HA Companion for Windows

A **fully native Windows 11 companion app for [Home Assistant](https://www.home-assistant.io/)** — built in C# with **.NET 9 + WinUI 3 (Windows App SDK)**. Fluent design, a dark clean UI, live REST + WebSocket integration, an editable quick-action panel opened by a global hotkey, real Home-Assistant (MDI) icons, and your actual Lovelace dashboards embedded 1:1.

*Not affiliated with or endorsed by the Home Assistant project / Nabu Casa. "Home Assistant" is a trademark of its respective owners; this app is an independent client "for Home Assistant".*

---

## Features

- 🔥 **Quick panel (global hotkey)** — a configurable hotkey (default Win+Ctrl+H) slides a right-edge panel in as one unit; pick **Favourites** (editable pinned tiles) or any of your **HA dashboards** shown 1:1.
- 🎛 **Auto-detected tiles** — every actionable entity becomes a tile with its **real Home Assistant icon**, grouped by domain, live via WebSocket; tap to toggle.
- ⭐ **Editable & reorderable** — pin favourites, drag to reorder, add via search; layout persists.
- 🖥️ **HA Dashboards 1:1** — your real Lovelace dashboards embedded (WebView2), auto-logged-in with your token, no login prompt.
- 🌐 **6 UI languages** — English, Deutsch, Español, Français, 中文, हिन्दी — switch live in Settings.
- 🪟 **Dark, clean, native** — Mica main window, borderless acrylic-free panel, adjustable panel width.
- 🔌 **Live connection** — REST for actions + WebSocket for push updates (auto-reconnect).
- 🧰 **System tray** — right-click → Open / Quick panel / Exit; closing the window hides to tray.
- 🔒 **Token stored securely** — the long-lived token is encrypted with Windows DPAPI, never in plaintext.

### Planned

- 📌 Jump Lists · 🧩 Windows Widgets board · 📦 MSIX packaging + signed releases

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

## Credits

Entity icons use the [Material Design Icons](https://pictogrammers.com/library/mdi/) webfont (Apache-2.0). See [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md) for all bundled/third-party components and their licenses.
