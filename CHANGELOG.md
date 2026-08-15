# Changelog

All notable changes in the Reborn fork are documented here. The original upstream Shadowsocks for Windows history is retained in `CHANGES`.

## [5.0.0] - 2026-08-15

### Platform

- Ported the Windows client and tests to .NET 10.
- Set the Windows baseline to Windows 10 2004 / build 19041.
- Removed x86 configurations; application, tests and network helper are x64-only.
- Added a deterministic Release publish path for the elevated NetworkService helper.

### Networking

- Removed Privoxy and replaced HTTP/HTTPS CONNECT forwarding with managed .NET code.
- Removed external `sysproxy.exe` helpers and moved system-proxy changes to WinINet.
- Added application-aware `Proxy`, `Direct` and `Block` routing.
- Added Admin Mode with transparent TCP/UDP capture through WinDivert.
- Added explicit WinDivert capture confirmation after successful `WinDivertOpen`.
- Changed Game Mode into an automatic compatibility state driven by configured process/path patterns; it is no longer a selectable traffic mode.
- Added automatic Admin Mode restoration after the matching Game Mode application exits.
- Fixed clean-first-run handling: an empty placeholder server no longer enters SIP003/capture exclusion logic or starts TCP/UDP proxy relays, preventing null-reference errors and repeated `No server configured` warnings.

### PAC and GeoSite

- Removed embedded GeoSite data.
- Added configurable GeoSite sources with per-source runtime caching.
- Added optional adjacent `.sha256sum` verification.
- Added cached Online PAC delivery through the local PAC endpoint.

### Cryptography

- Migrated supported AEAD methods to `System.Security.Cryptography`.
- Removed the active dependency on `libsscrypto`, OpenSSL, mbedTLS and libsodium wrappers.
- Removed `xchacha20-ietf-poly1305`; legacy local configuration is migrated to `chacha20-ietf-poly1305`.

### Packaging and repository

- Renamed the solution to `shadowsocks-reborn.sln` and the main executable to `shadowsocks-reborn.exe`.
- Split the codebase into `Shadowsocks.Engine`, `Shadowsocks.UI`, `Shadowsocks.NetworkService` and `Shadowsocks.UnitTests`.
- Removed WinForms/WPF dependencies from `Shadowsocks.Engine`; user interaction, QR screen capture, hotkeys and UI localization now live behind the UI boundary.
- Added `RuntimeEnvironment` and `IUserInteractionService` abstractions so a future WinUI 3 shell can reuse the engine without depending on the current WinForms/WPF presentation layer.
- Added GitHub Actions CI and tagged draft-release packaging.
- Added a PowerShell release builder that produces a ZIP and SHA-256 checksum.
- Added repository preflight validation for linked `.resx` resources before CI/release builds.
- Aligned CI/release restore and publish operations on the explicit `win-x64` RID so required .NET and Windows Desktop runtime packs are restored before `publish --no-restore`.
- Updated update-checker/project links to `SlimRG/shadowsocks-reborn`.
- Refreshed README, contribution guidance, issue templates and release metadata.

### Known limitations

- The UI is still mixed WinForms/WPF; the full Windows 11 WPF/Metro migration is not complete.
- Transparent DNS routing is not implemented yet even though the configuration/IPC contract contains DNS policy modes.
- Product releases currently contain both `shadowsocks-reborn.exe` and `Shadowsocks.NetworkService.exe`.
