# Third-party notices

HA Companion for Windows bundles or depends on the following third-party components.
Their licenses are permissive and compatible with this project's AGPL-3.0-only license.

## Bundled assets

### Material Design Icons (webfont)
- Files: `src/HaCompanion.App/Assets/Mdi/materialdesignicons-webfont.ttf`, `mdi-map.json`
- Project: https://pictogrammers.com/library/mdi/ (Pictogrammers / `@mdi/font`)
- License: **Apache License 2.0** — https://github.com/Templarian/MaterialDesign-Webfont/blob/master/LICENSE
- Used to render each Home Assistant entity's icon natively.

### Website fonts (`site/assets/fonts/`, self-hosted subsets)

- **Inter** — https://rsms.me/inter/ — **SIL Open Font License 1.1**
- **Noto Sans SC / Noto Sans Devanagari / Noto Sans Arabic** — https://fonts.google.com/noto — **SIL Open Font License 1.1**
- Subset with fonttools to exactly the glyphs the page prints; served only from this site.

## NuGet dependencies

| Package | License |
| --- | --- |
| Microsoft.WindowsAppSDK (WinUI 3, WebView2) | MIT |
| Microsoft.Windows.SDK.BuildTools | MIT |
| CommunityToolkit.Mvvm | MIT |
| H.NotifyIcon.WinUI | MIT |
| Microsoft.Extensions.* (DependencyInjection, Logging) | MIT |
| System.Security.Cryptography.ProtectedData | MIT |

Full license texts are distributed with the respective packages via NuGet.

Home Assistant is a trademark of its respective owners. This project is an
independent client "for Home Assistant" and is not affiliated with or endorsed by
the Home Assistant project or Nabu Casa.
