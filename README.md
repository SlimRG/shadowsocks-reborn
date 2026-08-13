# Shadowsocks for Windows

<img src="shadowsocks-csharp/Resources/ssw128.png" alt="Shadowsocks logo" width="64">

**English** | [Русский](README.ru.md) | [中文说明](https://github.com/shadowsocks/shadowsocks-windows/wiki/Shadowsocks-Windows-%E4%BD%BF%E7%94%A8%E8%AF%B4%E6%98%8E)

This repository contains the Shadowsocks Windows v4 client migrated to **.NET 10** while preserving the original architecture and behavior as closely as possible.

## Features

- System proxy configuration.
- PAC and global proxy modes.
- GeoSite and user-defined PAC rules.
- Local SOCKS5 and HTTP proxy endpoints.
- Server auto-switching strategies.
- UDP relay.
- SIP003 plugins.
- Global hotkeys.
- QR-code import/export.
- Online configuration support.

## Requirements

- Windows supported by .NET 10 Desktop.
- .NET 10 Desktop Runtime (x86) for the framework-dependent published build.
- Microsoft Visual C++ Redistributable (x86) may be required by the bundled native components.

> The application is intentionally built as **x86** because the bundled `libsscrypto.dll` is 32-bit. Privoxy is also bundled as an x86 helper process, while both x86 and x64 variants of `sysproxy` are included.

## Download and publish

Published builds are produced as a framework-dependent, x86, single-file executable.

```powershell
dotnet publish .\shadowsocks-csharp\shadowsocks-csharp.csproj `
  -c Release `
  -p:Platform=x86 `
  -p:PublishProfile=FolderProfile
```

The publish output is written under:

```text
shadowsocks-csharp\bin\x86\Release\net10.0-windows\win-x86\publish\
```

## Basic usage

1. Start Shadowsocks and find its icon in the notification area.
2. Add one or more servers from the **Servers** menu.
3. Select **Enable System Proxy** to route applications that use the Windows system proxy.
4. Alternatively, configure an application manually to use the local SOCKS5 or HTTP proxy at `127.0.0.1:1080` by default. The local port can be changed in the server settings.

## PAC

PAC rules are generated from the GeoSite database from [v2fly/domain-list-community](https://github.com/v2fly/domain-list-community).

Two modes are supported:

- **Whitelist mode** (`geositePreferDirect = false`, default): domains from the direct groups bypass the proxy; unmatched domains use the proxy.
- **Blacklist mode** (`geositePreferDirect = true`): domains from the proxied groups use the proxy; direct-group exceptions bypass it; unmatched domains connect directly.

Relevant configuration properties in `gui-config.json`:

- `geositeDirectGroups` — initialized with `cn` and `geolocation-!cn@cn`.
- `geositeProxiedGroups` — initialized with `geolocation-!cn`.
- `geositePreferDirect` — selects whitelist/blacklist behavior.

### User-defined PAC rules

Use `user-rule.txt` for custom PAC rules. Direct edits to generated `pac.txt` can be overwritten when the GeoSite database is updated.

For Microsoft Store/UWP applications, Windows may require importing the Internet Explorer/system proxy into WinHTTP from an elevated terminal:

```cmd
netsh winhttp import proxy source=ie
```

## Server auto switching

Available strategies include:

1. Load balancing — select a server randomly.
2. High availability — prefer a server with lower latency and packet loss.
3. Total package loss — use availability statistics to select a server.

Custom strategies can implement the `IStrategy` interface.

## UDP

Applications that do not support SOCKS5 UDP directly may require software such as SocksCap or ProxyCap to route their UDP traffic through Shadowsocks.

## Multiple instances

To run multiple instances independently, place each copy in a different directory and configure a different local port. Instance identification is derived deterministically from the executable path so IPC and single-instance behavior remain stable on modern .NET.

## Plugins

Configure a plugin executable path, relative or absolute, in the server editor. Forward proxy settings are not used while a SIP003 plugin is active.

See the upstream documentation for [non-SIP003 plugins](https://github.com/shadowsocks/shadowsocks-windows/wiki/Working-with-non-SIP003-standard-Plugin).

## Global hotkeys

Hotkeys can be registered automatically at startup. If multiple Shadowsocks instances are running, use different key combinations for each instance.

- Focus a hotkey field and press the desired combination to assign it.
- Press **Backspace** to clear the current combination.
- Green indicates successful registration.
- Yellow indicates a conflict with another application.

## Development

The solution targets `net10.0-windows` and keeps the application and tests on x86.

```powershell
dotnet restore .\shadowsocks-windows.sln
dotnet build .\shadowsocks-windows.sln -c Release -p:Platform=x86
dotnet test .\test\ShadowsocksTest.csproj -c Release -p:Platform=x86
```

Migration-specific details are documented in [NET10-MIGRATION.md](NET10-MIGRATION.md).

## Main managed dependencies

| Component | Purpose |
| --- | --- |
| ReactiveUI / ReactiveUI.WPF | WPF MVVM and bindings |
| WPFLocalizeExtension | WPF localization |
| MdXaml / AvalonEdit | Markdown rendering |
| Newtonsoft.Json | Configuration and API JSON |
| NLog | Logging |
| Google.Protobuf | GeoSite data model |
| ZXing.Net | QR-code support |
| GlobalHotKeyCore | Global keyboard shortcuts |
| Caseless.Fody / Fody | Case-insensitive string comparison weaving |

## Bundled native components

| Component | Architecture | Purpose |
| --- | --- | --- |
| `libsscrypto.dll` | x86 | Shadowsocks native cryptography |
| `privoxy.exe` | x86 | Local HTTP-to-SOCKS bridge |
| `sysproxy.exe` | x86 | Windows system-proxy helper |
| `sysproxy64.exe` | x64 | Windows system-proxy helper on 64-bit Windows |

## License

Shadowsocks for Windows is distributed under the [GNU General Public License v3.0](LICENSE.txt).

The repository also contains third-party components under their respective licenses. See their upstream projects for details.
