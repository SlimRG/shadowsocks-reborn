# Shadowsocks Reborn 5.2.22

Shadowsocks Reborn 5.2.22 focuses on DNS privacy, automatic-update reliability, WinUI stability, and release hardening.

## Highlights

- Added and hardened DNSCrypt/DoH resolver management while keeping ODoH disabled.
- Fixed DNSCrypt loopback-port allocation on Windows by selecting UDP-first and verifying the same port over TCP.
- Fixed Windows automatic-update downloads so `.download` streams are disposed before atomic promotion, eliminating the `File.Move` sharing violation.
- Verified a real 5.2.21 -> 5.2.22 self-update successfully.
- SHA-256 sidecars accept both a plain 64-character digest and standard `sha256sum` format.
- Hardened the staged updater across UAC handoff with SHA-256 verification, rollback, relaunch, and cleanup.
- Fixed `ss://` protocol registration for executable paths containing spaces.
- Kept Windows 10 build 19041+ compatibility while building the WinUI application against the newer Windows SDK.
- Improved the selectable RichTextBlock Logs viewer and removed stale legacy ListView validation assumptions.
- Strengthened CI/release gates: PowerShell parser validation, NuGet vulnerability auditing, exact FileVersion checks, single-file packaging checks, and repository-contract validation.
- Completed DNSCrypt management localization across all shipped locales.

## Platform

- Windows 10 version 2004 / build 19041 or newer
- Windows 11
- x64 only

## Release assets

- `Shadowsocks-win-x64.zip`
- `Shadowsocks-win-x64.zip.sha256`

The `.sha256` sidecar must contain the SHA-256 digest for the exact ZIP asset.
