# shadowsocks-reborn for Windows

<img src="docs/assets/shadowsocks.png" alt="Shadowsocks logo" width="64">

**English** | [Русский](README.ru.md)

`shadowsocks-reborn` is a Windows-focused continuation of the classic Shadowsocks for Windows v4 client. The 5.x line targets **.NET 10**, **WinUI 3 / Windows App SDK**, **x64**, and Windows 10 build 19041 or newer.

> This is an independent fork, not the upstream `shadowsocks/shadowsocks-windows` repository.

## Highlights

- Native WinUI 3 desktop interface; WinForms and WPF are no longer part of the product UI.
- Unpackaged, self-contained, win-x64, single-file distribution.
- Release layout contains exactly `Shadowsocks.exe`.
- File-backed per-user configuration under `%LOCALAPPDATA%\Shadowsocks`; the product directory is not used as mutable storage.
- Settings, caches, PAC data, logs and startup copy under `%LOCALAPPDATA%\Shadowsocks` in normal mode.
- Rufus-style Clean Mode (`...p.exe`) redirects all writable application state to a disposable `%TEMP%\Shadowsocks\Clean\...` session.
- Managed HTTP/1.1 proxy and HTTPS `CONNECT`; Privoxy/sysproxy are retired.
- Per-application `Proxy`, `Direct` and `Block` routing.
- Transparent TCP/UDP capture in Admin Mode through WinDivert.
- Automatic Game Mode that suspends Admin capture while configured applications are running.
- Game discovery suggestions for Steam, Epic Games, GOG and Xbox libraries; manual rules remain available.
- SIP003 plugin manager with built-in `xray-plugin`, `v2ray-plugin` and `qtun` choices plus manual ZIP/TAR.GZ import; installed packages live under the active storage root.
- UDP relay, QR import/export, hotkeys and a single embedded CSV localization catalog.

## Traffic modes

Only two modes are selectable:

- **User Mode** — no elevation and no NetworkService extraction. Routing applies to traffic that reaches the local/system proxy.
- **Admin Mode** — requests UAC, materializes the embedded `Shadowsocks.NetworkService.exe` under the active storage root, validates it, launches it elevated, and enables transparent TCP/UDP capture through WinDivert.

**Game Mode is an automatic compatibility state, not a third traffic mode.** When Admin Mode is selected and a configured game/application starts, WinDivert capture is suspended. Admin capture is restored automatically after the application exits.

The Traffic page exposes configured/runtime mode, NetworkService state, WinDivert state, TCP/UDP capture state and redirect ports.

## Requirements

- Windows 10 2004 / build 19041 or newer, or Windows 11;
- x64 Windows;
- .NET 10 SDK only when building from source.

The published product is self-contained and does not require a separately installed .NET runtime.

## Solution layout

- `Shadowsocks.Core` — protocol, encryption, configuration model, PAC/GeoSite logic, routing models, localization and storage abstractions.
- `Shadowsocks.Windows` — file-storage bootstrap, WinINet/system proxy, startup, UAC/Admin capture, WinDivert runtime, hotkeys and other Windows integration.
- `Shadowsocks.Windows.WinUI` — WinUI-specific Windows shell/tray integration.
- `Shadowsocks.WinUI` — WinUI 3 application shell and product publish project (`Shadowsocks.exe`).
- `Shadowsocks.NetworkService` — isolated elevated WinDivert helper embedded into release builds.
- `Shadowsocks.UnitTests` — Core/Windows tests without presentation dependencies.

See [ARCHITECTURE.md](ARCHITECTURE.md) for project boundaries and runtime flow.

## Build

Use a .NET 10 SDK on Windows:

```powershell
dotnet restore .\shadowsocks-reborn.sln -p:Platform=x64 -r win-x64
dotnet build .\shadowsocks-reborn.sln -c Release -p:Platform=x64 -m:1 --no-restore
dotnet test .\Shadowsocks.UnitTests\Shadowsocks.UnitTests.csproj -c Release -p:Platform=x64 --no-build
```

Publish the product:

```powershell
dotnet restore .\Shadowsocks.WinUI\Shadowsocks.WinUI.csproj -p:Platform=x64 -p:PublishProfile=FolderProfile -r win-x64
dotnet publish .\Shadowsocks.WinUI\Shadowsocks.WinUI.csproj -c Release -p:Platform=x64 -p:PublishProfile=FolderProfile -r win-x64 --self-contained true --no-restore
```

Or build the release ZIP and SHA-256 file:

```powershell
.\packaging\Build-Release.ps1 -Version v5.1.0
```

The final publish directory and release ZIP must contain exactly:

```text
Shadowsocks.exe
```

DLLs, PDBs, runtime JSON files, icons and `Shadowsocks.NetworkService.exe` sidecars are rejected by release validation.

## Storage and startup

Normal mode stores all persistent application-owned state under:

```text
%LOCALAPPDATA%\Shadowsocks
```

The configuration backend is `%LOCALAPPDATA%\Shadowsocks\settings.json` with an atomic `settings.backup.json`. SIP003 packages installed from the Plugins page are stored in `%LOCALAPPDATA%\Shadowsocks\Plugins` and resolved by plugin id when a server starts. Old `HKCU\Software\Shadowsocks Reborn\Settings` values are ignored. Localization uses only the `i18n.csv` embedded inside `Shadowsocks.exe`; no second catalog is extracted.

The Settings page shows the active storage path and provides a single **Open** button that opens that directory in either normal or Clean Mode.

Rename the executable so its file name ends in `p` before `.exe` to start **Clean Mode**, for example `Shadowsocksp.exe` or `Shadowsocks-5.0p.exe`. Clean Mode redirects settings, caches, PAC data, logs, installed plugin packages, runtime files and helper/update working data to a unique `%TEMP%\Shadowsocks\Clean\...` session and removes that session best-effort on Quit. Start with Windows is unavailable in Clean Mode.

In normal mode, Start with Windows copies the verified product EXE to `%LOCALAPPDATA%\Shadowsocks\Startup\Shadowsocks.exe`; the Windows Run integration points to that stable copy.

See [STORAGE_POLICY.md](STORAGE_POLICY.md) for the complete layout and cleanup rules.

## Embedded NetworkService

Release builds publish `Shadowsocks.NetworkService` as a self-contained single-file helper and embed it into `Shadowsocks.exe`.

User Mode never extracts the helper. Admin Mode extracts it on demand below the active storage root (`%LOCALAPPDATA%\Shadowsocks\Temp\NetworkService\...` in normal mode, the Clean Mode session in Clean Mode). Extraction is serialized, SHA-256 validated and followed by an elevated control-pipe version handshake. The helper is guarded while in use and removed best-effort when the broker stops; stale runtime directories are pruned later.

Development builds may use a separate helper from the build output. That fallback is not part of the release package.

## WinDivert verification

Admin Mode is reported active only after the elevated capture process successfully opens WinDivert. A successful startup logs a message similar to:

```text
WinDivert capture confirmed (start): Admin capture active.; TCP redirect port=..., UDP redirect port=...
```

For a functional test, add `curl.exe -> Block`, disable the Windows system proxy temporarily, and compare:

```cmd
curl.exe -4 --noproxy "*" https://example.com
```

The direct request should bypass application routing in User Mode and be blocked in Admin Mode.

## PAC, HTTP forwarding and DNS

Local PAC uses configured GeoSite sources and persistent cache data. Online PAC is downloaded through Shadowsocks and served to WinINet from the local `/pac` endpoint.

`ManagedHttpProxyService` handles HTTP/1.1 and HTTPS `CONNECT`. HTTPS is tunneled as bytes, so HTTP/2 negotiated inside TLS does not require a local HTTP/2 parser. FTP gatewaying is not implemented.

The configuration contract includes `System`, `Direct`, `Proxy` and `CustomDoh` DNS policy values, but transparent DNS interception/routing is not implemented in 5.1.0.

## Documentation

- [ARCHITECTURE.md](ARCHITECTURE.md) — project boundaries and runtime architecture.
- [STORAGE_POLICY.md](STORAGE_POLICY.md) — LocalAppData/Clean Mode/Temp rules and migration.
- [WINDOWS11_UI_GUIDE.md](WINDOWS11_UI_GUIDE.md) — current WinUI design/implementation rules.
- [CONTRIBUTING.md](CONTRIBUTING.md) — development rules.
- [RELEASE_CHECKLIST.md](RELEASE_CHECKLIST.md) — release validation.
- [SECURITY.md](SECURITY.md) — security reporting and sensitive areas.
- [CHANGELOG.md](CHANGELOG.md) — fork changes; upstream history remains in `CHANGES`.

## License

`shadowsocks-reborn` is distributed under the [GNU General Public License v3.0](LICENSE.txt). Third-party components retain their own licenses.
