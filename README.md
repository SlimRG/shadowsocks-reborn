# shadowsocks-reborn for Windows

<img src="Shadowsocks.Windows.WinUI/Shell/TrayAssets/ss32Fill.png" alt="Shadowsocks logo" width="32">

**English** | [Русский](README.ru.md) | [简体中文](README.zh-CN.md) | [中文使用说明](https://github.com/SlimRG/shadowsocks-reborn/wiki/ZH-User-Guide)

`shadowsocks-reborn` is a Windows-focused continuation of the classic Shadowsocks for Windows v4 client. Current builds target **.NET 10**, **WinUI 3 / Windows App SDK**, **x64**, and Windows 10 build 19041 or newer.

> This is an independent fork, not the upstream `shadowsocks/shadowsocks-windows` repository.

If no complete Shadowsocks server is configured, server-dependent DNS, PAC and Windows system-proxy enable actions stay unavailable; adding a valid server unlocks them. A new DNSCrypt configuration starts with every first-use option enabled except IPv6, while any DNSCrypt settings already persisted on disk are preserved exactly during configuration-schema upgrades. Long-running shell operations such as Start on Boot, Administrator capture/UAC and update actions display explicit busy progress. WinUI navigation and privilege glyphs use the Windows 10-compatible Segoe MDL2 Assets set.

## Current release: 5.2.31

Release 5.2.31 includes working Administrator-mode transparent DNS interception/routing for the `System`, `Direct`, `Proxy`, `CustomDoh` and `DnsCrypt` policies, DNSCrypt/DoH management with ODoH disabled, hardened automatic self-update, duplicate-autostart protection, and the current WinUI 3 shell. It also tightens CI/release gates around .NET servicing, NuGet vulnerability auditing, Windows 10 build 19041 compatibility and published EXE version metadata.

See [Changelog](https://github.com/SlimRG/shadowsocks-reborn/wiki/EN-Changelog) for the full release history.

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
- SIP003 plugin manager with built-in `xray-plugin`, `v2ray-plugin` and `qtun` choices plus manual ZIP/TAR.GZ import; `ss://` import offers to install a missing known catalog plugin before the server is saved; trusted catalog packages auto-check for updates once per day when not in use and can also be checked manually.
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
- .NET 10 SDK 10.0.303 or newer when building from source (release builds require the .NET 10.0.11 security baseline).

The published product is self-contained and does not require a separately installed .NET runtime.

## Solution layout

- `Shadowsocks.Core` — protocol, encryption, configuration model, PAC/GeoSite logic, routing models, localization and storage abstractions.
- `Shadowsocks.Windows` — file-storage bootstrap, WinINet/system proxy, startup, UAC/Admin capture, WinDivert runtime, hotkeys and other Windows integration.
- `Shadowsocks.Windows.WinUI` — standalone WinUI-specific shell/tray/QR/power integration with no Core/Windows project dependency.
- `Shadowsocks.WinUI` — WinUI 3 application shell and product publish project (`Shadowsocks.exe`).
- `Shadowsocks.NetworkService` — isolated elevated WinDivert helper embedded into release builds.
- `Shadowsocks.UnitTests` — Core/Windows tests without presentation dependencies.

See [Architecture](https://github.com/SlimRG/shadowsocks-reborn/wiki/EN-Architecture) for project boundaries and runtime flow.

## Build

`Directory.Build.props` is the single source of the three-part product version. `ApplicationInfo.Version`, manifests and release notes are validated against it.

Use .NET SDK 10.0.303 or newer on Windows:

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
.\packaging\Validate-Repository.ps1
.\packaging\Build-Release.ps1
```

The canonical GitHub Release asset is `Shadowsocks-win-x64.zip`, with `Shadowsocks-win-x64.zip.sha256` published beside it.

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

The configuration backend is `%LOCALAPPDATA%\Shadowsocks\settings.json` with an atomic `settings.backup.json`. SIP003 packages installed from the Plugins page are stored in `%LOCALAPPDATA%\Shadowsocks\Plugins` and resolved by plugin id when a server starts. Catalog packages retain trusted GitHub release provenance and may be updated automatically once per day while not in use; when both release tags are parseable versions, a lower candidate version is rejected rather than installed. Manual ZIP/TAR.GZ imports never opt into background network updates. Old `HKCU\Software\Shadowsocks Reborn\Settings` values are ignored. Localization uses only the `i18n.csv` embedded inside `Shadowsocks.exe`; no second catalog is extracted.

The Settings page shows the active storage path and provides a single **Open** button that opens that directory in either normal or Clean Mode.

Rename the executable so its file name ends in `p` before `.exe` to start **Clean Mode**, for example `Shadowsocksp.exe` or `Shadowsocks-cleanp.exe`. Clean Mode redirects settings, caches, PAC data, logs, installed plugin packages, runtime files and helper/component-update working data to a unique `%TEMP%\Shadowsocks\Clean\...` session and removes that session best-effort on Quit. Start with Windows is unavailable in Clean Mode.

In normal mode, Start with Windows copies the verified product EXE to `%LOCALAPPDATA%\Shadowsocks\Startup\Shadowsocks.exe`; the Windows Run integration points to that stable copy. Startup migrates a legacy-only matching `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` entry to the canonical entry, removes stale duplicates, and disabling Start with Windows removes matching legacy entries as well. An executable-identity-independent process gate prevents the LocalAppData startup copy and the original EXE from both starting a controller and racing for the same local port. Windows Restart Manager is kept mutually exclusive with Start with Windows. Both mechanisms continuously record the shell's actual UI state using `--start-visible` or `--start-hidden`, so reboot/logon restores the same state that existed before shutdown: an open window returns open, while a tray-only session stays in the tray. `WM_QUERYENDSESSION` snapshots the state before Windows closes the HWND, preventing system shutdown from being misinterpreted as a user close-to-tray action.

See [Storage policy](https://github.com/SlimRG/shadowsocks-reborn/wiki/EN-Storage-Policy) for the complete layout and cleanup rules.

## Application updates

Update downloads are staged as `*.download`; the file handle is closed before the temporary file is atomically promoted to its canonical name on Windows. SHA-256 sidecars may contain either `<hash>  Shadowsocks-win-x64.zip` or just the 64-character hexadecimal digest.

Application updates are automatic by default. After startup, the app checks GitHub Releases and selects only a **strictly newer numeric version** than the installed build (`5.2.22 < 5.2.31`, so 5.2.22 is ignored; `5.2.32 > 5.2.31`, so 5.2.32 is eligible), requires the exact `Shadowsocks-win-x64.zip` plus `.sha256` sidecar, verifies the SHA-256, ZIP layout and payload file version, then stages the new single-file EXE under `%TEMP%\Shadowsocks\Updates`. Before launch, the staged updater receives its own SHA-256; the source process keeps that staged file open without write/delete sharing across `Process.Start`/UAC and passes the digest through the internal update handoff. The updater re-verifies its own staged image before replacing the installed product. It then waits for the current process to exit, preserves rollback state, replaces the product EXE and starts the installed new copy. That installed copy removes the temporary updater transaction. If Start with Windows launched the LocalAppData startup copy, update eligibility uses the higher of the running copy version and the recorded primary EXE FileVersion, so a stale startup copy cannot treat an older release as newer. The updater then checks the target FileVersion again immediately before replacement and refuses equal-version or downgrade payloads. The recorded primary EXE is updated instead of only the startup copy.

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

Local GeoSite + EasyList/ABP routing is **always managed C#**. The Local PAC file contains only a minimal `FindProxyForURL` funnel into `ManagedHttpProxyService`; it does not evaluate filter rules. Application rules run first, then the immutable GeoSite + `user-rule.txt` snapshot in `Shadowsocks.Routing.FilterEngine` makes the authoritative `DIRECT`/`PROXY` decision. The historical bundled `abp.js`, the compiled-PAC compatibility backend, the backend selector, and the executable custom `abp.txt` override have all been removed. When the settings schema changes, removed properties are pruned automatically during canonical migration. **Online PAC is separate:** if explicitly enabled, the user-selected external PAC program remains authoritative because arbitrary PAC JavaScript cannot be losslessly converted to ABP/EasyList rules. The PAC / GeoSite page exposes **Open user-rule.txt** in Local mode; user rules are evaluated before GeoSite defaults and applied automatically when the file changes. Local managed-routing diagnostics, user-rule editing and GeoSite source controls are hidden while Online PAC is active.

Administrator Mode now sends the same managed rule set to the elevated NetworkService. Ordinary unmatched TCP is marked `Deferred` at SYN time and reflected into the transparent relay; the relay inspects only the initial clear-text routing metadata to obtain HTTP `Host` or TLS ClientHello SNI, then opens either a direct connection or the Shadowsocks SOCKS5 path. There is **no TLS decryption, certificate installation or MITM**. HTTP requests can be matched with path/query information; TLS/SNI routing is hostname-only. Explicit application rules retain priority. UDP, TCP DNS/53 and protocols without usable Host/SNI keep deterministic fallback behavior; rule-file changes coalesce and restart only the active capture child through the already-elevated broker.

`ManagedHttpProxyService` handles HTTP/1.1 and HTTPS `CONNECT`. HTTPS is tunneled as bytes, so HTTP/2 negotiated inside TLS does not require a local HTTP/2 parser. FTP gatewaying is not implemented.

### DNS policy contract

The current 5.2.31 configuration/IPC contract contains five DNS policy values, and Administrator Mode implements transparent routing for classic DNS captured on UDP/TCP port 53 through `Shadowsocks.NetworkService` + WinDivert:

| Policy | Current behavior |
| --- | --- |
| `System` | Keeps captured classic DNS on its original/system destination without policy-specific redirection. |
| `Direct` | Can keep the original destination or redirect captured DNS to configured primary/fallback IPv4/IPv6 resolvers. The selected DNS endpoint can optionally be reached through the local Shadowsocks SOCKS5 path. |
| `Proxy` | Routes captured classic DNS through Shadowsocks. |
| `CustomDoh` | Bridges captured DNS wire messages to the configured HTTPS DoH endpoint; the DoH upstream can independently be routed through Shadowsocks. |
| `DnsCrypt` | Redirects captured DNS to the dynamically allocated local `dnscrypt-proxy` listener and operates fail-closed when the secure runtime is unavailable. |

Transparent system-wide interception is therefore **implemented in 5.2.31**, but it requires **Administrator Mode**. User Mode still uses Shadowsocks-managed DNS/DNSCrypt where applicable, but it does not intercept arbitrary system DNS. Automatic Game Mode pauses system-wide interception while Admin capture is suspended. The transparent interception covers classic DNS on UDP/TCP 53; application-internal DoH, DoT and DoQ are not generically intercepted.

The DNS page manages the optional signed `dnscrypt-proxy` component. `Automatic` selects a concrete DNSCrypt/DoH resolver from the signed public catalog using DNSSEC, no-log, unfiltered and address-family constraints, then pins it in `server_names`. Resolver-catalog maintenance resolves source hostnames through **Cloudflare DoH over Shadowsocks**, with **Google DoH over Shadowsocks** as fallback, downloads the catalog and `.minisig` through the same tunnel, verifies the pinned DNSCrypt Minisign key, and only then exposes the local authenticated cache to `dnscrypt-proxy`. The catalog profile and active runtime both use `bootstrap_resolvers = []` and `ignore_system_dns = true`; the catalog profile additionally uses `urls = []`, so dnscrypt-proxy cannot fall back to remote source lookup or system/plaintext bootstrap. `Manual` exposes signed DNSCrypt and DoH entries with protocol/country/address-family/privacy filters; ODoH remains disabled. DNSCrypt component release discovery, ZIP download and Minisign download also use this Cloudflare→Google DoH-over-Shadowsocks transport, including GitHub redirect hosts, so installation/update does not intentionally depend on the Windows resolver for `api.github.com` or release assets.

On first enable, DNSCrypt download/verification/install/activation keeps a dedicated animated operation card visible; unavailable DNSCrypt settings stay hidden until activation completes.

The DNS page also provides `Test DNSCrypt` and a DNS privacy self-test. The local listener allocator verifies that one loopback port is usable by both UDP and TCP. DNSCrypt PID/port changes are propagated to NetworkService at runtime. DNSCrypt is fail-closed during startup, restart, recovery and runtime failure instead of silently downgrading intercepted traffic to plaintext system DNS. Fragmented datagrams that would require DNS/proxy rewriting are dropped as a whole so later fragments cannot bypass policy; direct fragmented traffic remains direct.

WinDivert 2.2.2 is downloaded only from the pinned official release URL; the extracted x64 DLL/driver must match release-pinned SHA-256 digests before loading. A read-only FLOW observer supplies endpoint PID ownership for NETWORK-layer routing, with IP Helper lookup as fallback.

DNSCrypt Automatic mode prefers a compatible resolver in the country of the active Shadowsocks server and falls back to the best compatible signed-catalog resolver. Resolver country is derived only from endpoint IP GeoIP data; resolver names/descriptions are not treated as geographic metadata. Manual resolver latency is measured asynchronously and the active resolver uses dnscrypt-proxy's RTT when available. Automatic component update checks are persisted and run no more than once per 24 hours; initial installation remains explicit. When DNSCrypt itself is routed through Shadowsocks, Shadowsocks/forward-proxy endpoints must be IP literals to prevent DNS bootstrap recursion.

## Documentation

- [User guide (English)](https://github.com/SlimRG/shadowsocks-reborn/wiki/EN-User-Guide)
- [Руководство пользователя (RU)](https://github.com/SlimRG/shadowsocks-reborn/wiki/RU-User-Guide)
- [中文使用说明](https://github.com/SlimRG/shadowsocks-reborn/wiki/ZH-User-Guide)

Long-form Markdown is maintained in this dedicated GitHub Wiki rather than duplicated under the application repository. GitHub issue/PR templates remain under `.github/` because GitHub requires those paths.


- [Architecture](https://github.com/SlimRG/shadowsocks-reborn/wiki/EN-Architecture) — project boundaries and runtime architecture.
- [Storage policy](https://github.com/SlimRG/shadowsocks-reborn/wiki/EN-Storage-Policy) — LocalAppData, Clean Mode, component staging and application self-update storage rules.
- [Windows 11 / WinUI guide](https://github.com/SlimRG/shadowsocks-reborn/wiki/EN-Windows-11-WinUI-Guide) — current WinUI design/implementation rules.
- [Contributing](https://github.com/SlimRG/shadowsocks-reborn/wiki/EN-Contributing) — development rules.
- [Release checklist](https://github.com/SlimRG/shadowsocks-reborn/wiki/EN-Release-Checklist) — release validation.
- [Security](https://github.com/SlimRG/shadowsocks-reborn/wiki/EN-Security) — security reporting and sensitive areas.
- [Changelog](https://github.com/SlimRG/shadowsocks-reborn/wiki/EN-Changelog) — fork changes; upstream history remains in [`CHANGES`](https://github.com/SlimRG/shadowsocks-reborn/blob/main/CHANGES).

## License

`shadowsocks-reborn` is distributed under **GPL-3.0-or-later**. See [License](LICENSE.txt) and the authoritative [LICENSE.txt](https://github.com/SlimRG/shadowsocks-reborn/blob/main/LICENSE.txt). Third-party components retain their own licenses; see [Third-party notices](THIRD-PARTY-NOTICES.txt).
