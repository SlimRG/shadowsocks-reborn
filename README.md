# Shadowsocks for Windows

<img src="shadowsocks-csharp/Resources/ssw128.png" alt="Shadowsocks logo" width="64">

**English** | [Русский](README.ru.md) | [中文说明](https://github.com/shadowsocks/shadowsocks-windows/wiki/Shadowsocks-Windows-%E4%BD%BF%E7%94%A8%E8%AF%B4%E6%98%8E)

This repository keeps the Shadowsocks for Windows **v4** client architecture and incrementally ports it to **.NET 10**. The goal is compatibility with current Windows/.NET while preserving the v4 transport, encryption, configuration, plugin and server-selection behavior.

## Features

- PAC and Global Windows system-proxy modes.
- Local SOCKS5 and HTTP proxy endpoints.
- GeoSite-based PAC generation and custom user rules.
- SIP003 plugins and UDP relay.
- Server switching strategies.
- QR-code import/export and online configuration sources.
- Global hotkeys.
- Localized WinForms/WPF UI.
- Log viewer with a built-in traffic chart.

## Requirements and architecture

- Target framework: `net10.0-windows10.0.19041.0`.
- Publish RID: `win-x86`.
- .NET 10 Desktop Runtime (x86) is required for the framework-dependent publish.
- The current publish profile remains `win-x86`; there is no longer an in-process x86 crypto DLL requirement.
- `privoxy.exe` is bundled as an x86 helper process.

The explicit Windows TFM is required by the current ReactiveUI/System.Reactive stack so the Windows dispatcher implementation is selected correctly. ChaCha20-Poly1305 additionally requires platform support reported by `ChaCha20Poly1305.IsSupported`.

## Port status

- The application and test projects target `.NET 10` on Windows.
- Supported AEAD methods are implemented by `AEADBclEncryptor` using `System.Security.Cryptography`.
- The old `libsscrypto.dll`, OpenSSL, mbedTLS and libsodium wrappers are not part of the active crypto path. Their legacy source filenames are explicitly excluded in the SDK-style project so stale files left by an in-place archive update cannot be compiled accidentally.
- System proxy changes are performed in-process through WinINet; the old `sysproxy.exe` helpers are not used.
- The UI is currently **mixed WinForms/WPF**. A complete Windows 11 WPF/Metro UI migration is not finished yet.
- The supplied publish profile is currently **framework-dependent, untrimmed, x86 and single-file**. **NativeAOT is not enabled yet.**

## Build

Use a .NET 10 SDK on Windows:

```cmd
dotnet restore .\shadowsocks-windows.sln
dotnet build .\shadowsocks-windows.sln -c Release -p:Platform=x86
dotnet test .\test\ShadowsocksTest.csproj -c Release -p:Platform=x86
```

Publish with the supplied profile:

```cmd
dotnet publish .\shadowsocks-csharp\shadowsocks-csharp.csproj -c Release -p:Platform=x86 -p:PublishProfile=FolderProfile
```

Output:

```text
shadowsocks-csharp\bin\x86\Release\net10.0-windows10.0.19041.0\win-x86\publish\
```

The publish is framework-dependent, untrimmed and single-file. `PublishAot` is not enabled in the current profile.

### Cleaning an upgraded working tree

The repository is intended to tolerate extracting a newer archive over an older checkout. Known retired settings and native-crypto source files are explicitly excluded from compilation. If Visual Studio still shows errors originating from one of these retired files, close/reload the solution and delete stale `bin`/`obj` directories before rebuilding.

Do **not** restore `libsscrypto.dll` or fix the retired OpenSSL/mbedTLS/libsodium wrappers just to make those stale files compile; the active implementation is `Encryption\AEAD\AEADBclEncryptor.cs`.

## Cryptography

AEAD cryptography uses `System.Security.Cryptography` through `Encryption/AEAD/AEADBclEncryptor.cs` instead of the legacy bundled `libsscrypto.dll`:

- `aes-128-gcm`, `aes-192-gcm`, `aes-256-gcm` use `AesGcm`;
- `chacha20-ietf-poly1305` uses `ChaCha20Poly1305`;
- password-to-key derivation keeps the legacy Shadowsocks MD5 construction for protocol compatibility;
- session subkeys use HKDF-SHA1 with `ss-subkey`, as required by the classic Shadowsocks AEAD protocol.

`xchacha20-ietf-poly1305` has been removed. Old local configurations using it are changed to `chacha20-ietf-poly1305` when loaded; the remote server must be configured to use the same method.

## Configuration

Application settings are stored in `gui-config.json`, including Log Viewer state.

`Shadowsocks.dll.config` is not used. The old `ApplicationSettingsBase`/`app.config` settings path has been removed.

`Shadowsocks.pdb` is optional at runtime. Keep it while testing to retain source file and line information in stack traces.

## System proxy

The legacy `sysproxy.exe` / `sysproxy64.exe` path has been removed. System proxy settings are applied directly through WinINet.

The selected mode is persisted in `gui-config.json`:

- Disabled: `enabled = false`
- Global: `enabled = true`, `global = true`
- PAC: `enabled = true`, `global = false`

At startup and after internal reloads, Shadowsocks reapplies the saved mode and reads the effective Windows state back. Tray/menu state follows the effective state, not only the JSON flags.

On full exit, the system proxy state captured before Shadowsocks started is restored. The selected Shadowsocks mode remains saved and is reapplied on the next start.

## PAC

PAC rules are generated from the GeoSite database from [v2fly/domain-list-community](https://github.com/v2fly/domain-list-community).

Custom rules belong in `user-rule.txt`; generated `pac.txt` may be replaced after a GeoSite update.

Relevant settings include `geositeDirectGroups`, `geositeProxiedGroups` and `geositePreferDirect`.

## Plugins and HTTP forwarding

Configure a plugin executable in the server editor. Relative and absolute paths are supported.

Privoxy startup is synchronized with the local HTTP forwarder so normal traffic is not accepted before Privoxy starts listening. During reload, active forwarding handlers are closed before Privoxy is stopped.

See the upstream guide for [non-SIP003 plugins](https://github.com/shadowsocks/shadowsocks-windows/wiki/Working-with-non-SIP003-standard-Plugin).

## UDP

Applications that do not support SOCKS5 UDP directly may require a routing tool such as SocksCap or ProxyCap.

Listener cancellation during normal stop/reload is treated as expected shutdown. Unexpected socket failures are still logged.

## Localization

The classic WinForms UI uses `shadowsocks-csharp/Data/i18n.csv`. WPF views use `shadowsocks-csharp/Localization/Strings*.resx`.

When adding user-visible text, add it to the appropriate localization source instead of leaving a hard-coded UI string.

## Diagnostics

Runtime logging uses NLog. Real `Win32Exception` failures are logged with the native error code, HRESULT and stack trace.

`ERROR_PARTIAL_COPY (299)` is ignored only at the specific cross-bitness process-inspection call where it is expected.

For runtime reports, keep the complete WARN/ERROR entry and stack trace. Keeping `Shadowsocks.pdb` next to the executable makes those traces substantially more useful.

## Bundled native components

| Component | Architecture | Purpose |
| --- | --- | --- |
| `privoxy.exe` | x86 | Local HTTP-to-SOCKS bridge |

The current port does not bundle `libsscrypto.dll` and does not use `sysproxy.exe`, `sysproxy64.exe`, Costura, `System.Windows.Forms.DataVisualization`, `System.Data.SqlClient` or `sni.dll`.

## License

Shadowsocks for Windows is distributed under the [GNU General Public License v3.0](LICENSE.txt). Third-party components remain under their respective licenses.
