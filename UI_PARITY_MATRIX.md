# WinUI migration parity record

The legacy WinForms/WPF presentation layer has been retired. This matrix is retained as a regression record for behavior that the WinUI product must continue to provide.

## Feature parity

| Feature | Legacy baseline | WinUI |
|---|:---:|:---:|
| Start | ✓ | ✓ |
| Tray | ✓ | ✓ |
| Server config | ✓ | ✓ |
| QR import | ✓ | ✓ |
| PAC | ✓ | ✓ |
| GeoSite | ✓ | ✓ |
| HTTP / Forward Proxy | ✓ | ✓ |
| User traffic mode | ✓ | ✓ |
| Admin traffic mode | ✓ | ✓ |
| WinDivert status | ✓ | ✓ |
| Game Mode | ✓ | ✓ |
| App routing | ✓ | ✓ |
| Hotkeys | ✓ | ✓ |
| Logs | ✓ | ✓ |
| Update | ✓ | ✓ |
| Localization | ✓ | ✓ |

Current WinUI improvements beyond the legacy baseline include immediate main-window User/Admin application, UAC shield affordance, game-library suggestions, structured Logs UI, LocalAppData/Clean Mode storage and stable LocalAppData autostart.

## Lifecycle parity

| Behavior | Legacy baseline | WinUI |
|---|:---:|:---:|
| Windows suspend stops controller | ✓ | ✓ |
| Resume restarts after compatibility delay with `systemWakeUp: true` | ✓ | ✓ |
| Startup delayed Online Config refresh | ✓ | ✓ |
| System Proxy retry/error interaction | ✓ | ✓ |
| Legacy `ss://` import warning | ✓ | ✓ |
| Controller clipboard boundary | ✓ | ✓ |

## Regression rule

`Shadowsocks.WinUI` is the only desktop presentation project. Do not reintroduce `UseWPF`, `UseWindowsForms`, WindowsDesktop SDK projects, WPF/WinForms namespaces or retired presentation-only packages.

This matrix is not a roadmap. New behavior belongs in `CHANGELOG.md`; current UI implementation rules belong in `WINDOWS11_UI_GUIDE.md`.
