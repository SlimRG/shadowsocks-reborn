# shadowsocks-reborn for Windows

<img src="assets/shadowsocks.png" alt="Shadowsocks logo" width="64">

**English** | [Русский](README.ru.md)

`shadowsocks-reborn` is a Windows-focused continuation of the classic Shadowsocks for Windows v4 client. The 5.x line targets **.NET 10**, **WinUI 3 / Windows App SDK**, **x64**, and Windows 10 build 19041 or newer.

> This is an independent fork, not the upstream `shadowsocks/shadowsocks-windows` repository.

## Current release: 5.2.22

Release 5.2.22 consolidates the DNSCrypt/DoH management work, keeps ODoH disabled, fixes release-blocking updater/test/localization regressions, hardens staged self-update integrity across UAC handoff, and tightens CI/release gates around .NET servicing, NuGet vulnerability auditing, Windows 10 build 19041 compatibility and published EXE version metadata. The Logs viewer is the current selectable `RichTextBlock` implementation; retired `ListView` parity tokens are no longer part of the UI contract.

See [CHANGELOG.md](CHANGELOG.md) for the full release history.

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
- .NET 10 SDK 10.0.303 or newer when building from source (release builds require the .NET 10.0.11 security baseline).

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
.\packaging\Build-Release.ps1 -Version 5.2.22
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

The configuration backend is `%LOCALAPPDATA%\Shadowsocks\settings.json` with an atomic `settings.backup.json`. SIP003 packages installed from the Plugins page are stored in `%LOCALAPPDATA%\Shadowsocks\Plugins` and resolved by plugin id when a server starts. Old `HKCU\Software\Shadowsocks Reborn\Settings` values are ignored. Localization uses only the `i18n.csv` embedded inside `Shadowsocks.exe`; no second catalog is extracted.

The Settings page shows the active storage path and provides a single **Open** button that opens that directory in either normal or Clean Mode.

Rename the executable so its file name ends in `p` before `.exe` to start **Clean Mode**, for example `Shadowsocksp.exe` or `Shadowsocks-5.0p.exe`. Clean Mode redirects settings, caches, PAC data, logs, installed plugin packages, runtime files and helper/component-update working data to a unique `%TEMP%\Shadowsocks\Clean\...` session and removes that session best-effort on Quit. Start with Windows is unavailable in Clean Mode.

In normal mode, Start with Windows copies the verified product EXE to `%LOCALAPPDATA%\Shadowsocks\Startup\Shadowsocks.exe`; the Windows Run integration points to that stable copy.

See [STORAGE_POLICY.md](STORAGE_POLICY.md) for the complete layout and cleanup rules.

## Application updates

Application updates are automatic by default. After startup, the app checks GitHub Releases, selects an eligible newer version, requires the exact `Shadowsocks-win-x64.zip` plus `.sha256` sidecar, verifies the SHA-256, ZIP layout and payload file version, then stages the new single-file EXE under `%TEMP%\Shadowsocks\Updates`. Before launch, the staged updater receives its own SHA-256; the source process keeps that staged file open without write/delete sharing across `Process.Start`/UAC and passes the digest through the internal update handoff. The updater re-verifies its own staged image before replacing the installed product. It then waits for the current process to exit, preserves rollback state, replaces the product EXE and starts the installed new copy. That installed copy removes the temporary updater transaction. If Start with Windows launched the LocalAppData startup copy, the recorded primary EXE is updated instead of only the startup copy.

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

The DNS page can manage the optional signed `dnscrypt-proxy` component. Its `Automatic` resolver mode selects a concrete DNSCrypt/DoH resolver from the signed public catalog using the configured DNSSEC, no-log, unfiltered and address-family constraints, then pins that resolver in `server_names`; the active runtime therefore keeps `bootstrap_resolvers = []` and does not fall back to plaintext system DNS. Manual mode exposes signed DNSCrypt and DoH catalog entries with protocol filtering; resolver-catalog refresh is an explicit maintenance operation and may use one-shot bootstrap DNS only before a signed catalog cache exists, while the active DNS runtime itself always uses `bootstrap_resolvers = []` and `ignore_system_dns = true`. The DNS page provides both a local `Test DNSCrypt` health check and a DNS privacy self-test covering Administrator interception, fail-closed/system-DNS isolation, upstream/transport and active runtime bootstrap configuration. Starting with 5.2.0, Admin Mode transparently intercepts UDP/TCP port 53 through WinDivert and forwards it to the dynamic local DNSCrypt listener. The listener port allocator verifies that the selected loopback port is simultaneously usable by UDP and TCP and avoids relying on TCP/UDP dynamic ranges being identical on Windows hosts. DNSCrypt PID/port changes are propagated at runtime, Game Mode pauses system-wide interception, and DNSCrypt is always fail-closed: plaintext DNS is blocked during startup, restart, recovery, or runtime failure instead of silently downgrading to system DNS. WinDivert 2.2.2 is downloaded only from the pinned official release URL and the extracted x64 DLL/driver must match the release-pinned SHA-256 digests before they can be loaded. A read-only WinDivert FLOW observer supplies endpoint PID ownership for NETWORK-layer routing, with IP Helper lookup as fallback. Fragmented datagrams that would require transparent DNS/proxy rewriting are dropped as a whole so later fragments cannot bypass policy; direct fragmented traffic remains direct. `Direct`, `Proxy` and `CustomDoh` are exposed as working DNS policies: Direct can preserve the original destination or transparently redirect captured UDP/TCP DNS to primary/fallback IPv4/IPv6 resolvers, with an optional route through the local Shadowsocks SOCKS5 path; Proxy sends captured DNS through Shadowsocks, and Custom DoH bridges captured DNS wire messages to the configured HTTPS endpoint with its own optional Shadowsocks route. DNSCrypt Automatic mode prefers a compatible resolver in the country of the active Shadowsocks server and falls back to the best compatible signed-catalog resolver when no country match is available; resolver countries are derived only from their endpoint IPs through GeoIP and never from resolver names or descriptions. Anycast is not guessed from textual metadata because `dnscrypt-proxy -list-all -json` does not expose it as a structured property. Manual selection may use DNSCrypt or DoH entries from the signed catalog and includes protocol, country, address-family, DNSSEC, no-log and unfiltered filters; ODoH remains disabled. DNSCrypt manual selection resolver latency is measured asynchronously and the active resolver uses dnscrypt-proxy's actual RTT when available. DNSCrypt also runs in User Mode for Shadowsocks-managed hostname resolution; transparent system-wide DNS interception still requires Administrator Mode. The transparent interception covers classic DNS on UDP/TCP port 53; application-internal DoH/DoT/DoQ traffic is not generically intercepted. Automatic component update checks are persisted and run no more than once per 24 hours, including clock-skew recovery; initial installation is always explicit. When DNSCrypt itself is routed through Shadowsocks, Shadowsocks/forward-proxy endpoints must be IP literals to prevent DNS bootstrap recursion.

## Documentation

- [ARCHITECTURE.md](ARCHITECTURE.md) — project boundaries and runtime architecture.
- [STORAGE_POLICY.md](STORAGE_POLICY.md) — LocalAppData, Clean Mode, component staging and application self-update storage rules.
- [WINDOWS11_UI_GUIDE.md](WINDOWS11_UI_GUIDE.md) — current WinUI design/implementation rules.
- [CONTRIBUTING.md](CONTRIBUTING.md) — development rules.
- [RELEASE_CHECKLIST.md](RELEASE_CHECKLIST.md) — release validation.
- [SECURITY.md](SECURITY.md) — security reporting and sensitive areas.
- [CHANGELOG.md](CHANGELOG.md) — fork changes; upstream history remains in [`CHANGES`](https://github.com/SlimRG/shadowsocks-reborn/blob/main/CHANGES).

## License

`shadowsocks-reborn` is distributed under **GPL-3.0-or-later**. See [License](LICENSE.md) and the authoritative [LICENSE.txt](https://github.com/SlimRG/shadowsocks-reborn/blob/main/LICENSE.txt). Third-party components retain their own licenses; see [Third-party notices](THIRD-PARTY-NOTICES.md).
