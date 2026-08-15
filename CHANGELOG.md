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

- Added GitHub Actions CI and tagged draft-release packaging.
- Added a PowerShell release builder that produces a ZIP and SHA-256 checksum.
- Added repository preflight validation for linked `.resx` resources before CI/release builds.
- Updated update-checker/project links to `SlimRG/shadowsocks-reborn`.
- Refreshed README, contribution guidance, issue templates and release metadata.

### Known limitations

- The UI is still mixed WinForms/WPF; the full Windows 11 WPF/Metro migration is not complete.
- Transparent DNS routing is not implemented yet even though the configuration/IPC contract contains DNS policy modes.
- Product releases currently contain both `Shadowsocks.exe` and `Shadowsocks.NetworkService.exe`.
