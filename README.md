# Shadowsocks Reborn for Windows

<img src="shadowsocks-csharp/Resources/ssw128.png" alt="Shadowsocks logo" width="64">

**English** | [Русский](README.ru.md)

`shadowsocks-reborn` is a Windows-focused continuation of the classic Shadowsocks for Windows v4 client. The current codebase targets **.NET 10**, **Windows 10 2004 (build 19041) or newer**, and **x64 only** while preserving the classic Shadowsocks protocol/configuration model.

> Current release line: **5.0**. This fork is not the upstream `shadowsocks/shadowsocks-windows` repository.

## Highlights

- .NET 10 desktop application targeting `net10.0-windows10.0.19041.0`.
- x64-only application, tests and elevated network helper.
- BCL AEAD crypto through `System.Security.Cryptography`; retired native crypto wrappers are no longer used.
- In-process WinINet system-proxy integration; `sysproxy.exe` is removed.
- Managed HTTP/1.1 and HTTPS `CONNECT` proxy; Privoxy is removed.
- Local PAC with configurable GeoSite sources and cached Online PAC.
- Application-aware `Proxy`, `Direct` and `Block` routing.
- Optional transparent TCP/UDP capture in Admin Mode through WinDivert.
- Automatic Game Mode that temporarily tears down WinDivert while configured applications are running.
- SIP003 plugins, classic Shadowsocks UDP relay, QR import/export, hotkeys and localized WinForms/WPF UI.

## Traffic modes

The tray exposes only two selectable traffic modes:

- **User Mode** — no elevation and no driver. Application rules apply to traffic that reaches the Windows/local HTTP proxy. Applications that bypass the system proxy and arbitrary UDP/QUIC sockets are not transparently captured.
- **Admin Mode** — requests UAC, downloads the official x64 WinDivert runtime on demand and starts the elevated `Shadowsocks.NetworkService.exe` broker. TCP/UDP traffic is classified by application and routed as `Proxy`, `Direct` or `Block`.

**Game Mode is not a third traffic mode.** It is an automatic compatibility state. When Admin Mode is selected and a configured process/path pattern starts, the WinDivert capture child is stopped and the WinDivert driver service is removed. When the matching application exits, Admin Mode is restored automatically.

The menu reports the runtime state as `WinDivert: active`, `paused (game running)` or `inactive`.

## WinDivert verification

A successful Admin Mode startup is logged only after the elevated capture child has completed `WinDivertOpen`. Look for a message similar to:

```text
WinDivert capture confirmed (start): Admin capture active.; TCP redirect port=..., UDP redirect port=...
```

For a functional A/B test, add a `Block` rule for `curl.exe`, disable the Windows system proxy for the test, then compare a direct request in User Mode and Admin Mode:

```cmd
curl.exe -4 --noproxy "*" https://example.com
```

The request should bypass application routing in User Mode and be blocked in Admin Mode.

## Requirements

For running a release build:

- Windows 10 version 2004 / build 19041 or newer, or Windows 11;
- x64 OS;
- .NET 10 Desktop Runtime x64 for the main application.

`Shadowsocks.NetworkService.exe` is published self-contained and does not require a separate .NET runtime. WinDivert is optional and is downloaded only when Admin Mode is enabled.

## Build

Use a .NET 10 SDK on Windows:

```cmd
dotnet restore .\shadowsocks-windows.sln -p:Platform=x64 -r win-x64
dotnet build .\shadowsocks-windows.sln -c Release -p:Platform=x64 -m:1
dotnet test .\test\ShadowsocksTest.csproj -c Release -p:Platform=x64 --no-build
```

Publish the application with the supplied profile:

```cmd
dotnet publish .\shadowsocks-csharp\shadowsocks-csharp.csproj -c Release -p:Platform=x64 -p:PublishProfile=FolderProfile -r win-x64 --no-self-contained
```

Or build the complete release package and SHA-256 file:

```powershell
.\packaging\Build-Release.ps1 -Version v5.0.0
```

The product publish contains the main framework-dependent single-file application plus the self-contained single-file elevated helper. The release archive must therefore keep `Shadowsocks.exe` and `Shadowsocks.NetworkService.exe` together.

## Configuration and runtime data

- Main settings: `gui-config.json`.
- Custom PAC rules: `user-rule.txt`.
- Local GeoSite and Online PAC data are cached at runtime and are not embedded in the executable.
- In portable mode, optional WinDivert files are stored below the local `runtime` directory; otherwise they are stored below `%LOCALAPPDATA%\Shadowsocks\runtime`.

The repository intentionally excludes retired `ApplicationSettingsBase`, Privoxy, sysproxy and native crypto artifacts from compilation/publish so an old in-place checkout cannot silently reintroduce them.

## PAC and HTTP forwarding

Local PAC downloads configured GeoSite sources through the active Shadowsocks connection and keeps per-source caches. Online PAC is also downloaded through Shadowsocks and served to WinINet from the local `/pac` endpoint so Windows does not need direct access to the remote PAC host.

`ManagedHttpProxyService` handles HTTP/1.1 and HTTPS `CONNECT` directly in managed code. HTTPS remains a byte tunnel, so HTTP/2 negotiated inside TLS works without an HTTP/2 parser in the local proxy. FTP gatewaying is not implemented.

## DNS status

The configuration/IPC contract already contains `System`, `Direct`, `Proxy` and `CustomDoh` DNS policy modes, but transparent DNS interception/routing is **not implemented in 5.0.0**. Do not rely on these settings for DNS enforcement yet.

## UI status

The UI is still a **mixed WinForms/WPF** application. Migration to a complete Windows 11 WPF/Metro interface is not finished.

## Development

See [CONTRIBUTING.md](CONTRIBUTING.md) before opening a pull request. Use [RELEASE_CHECKLIST.md](RELEASE_CHECKLIST.md) for releases. Current release history is in [CHANGELOG.md](CHANGELOG.md); the original upstream history is retained in `CHANGES`.

## Security and privacy

Do not post passwords, server addresses, subscription URLs, PAC secrets or full private configurations in public issues. See [SECURITY.md](SECURITY.md) for reporting guidance.

## License

Shadowsocks for Windows is distributed under the [GNU General Public License v3.0](LICENSE.txt). Third-party components retain their own licenses.
