# Security policy

## Reporting a vulnerability

Do not publish an exploitable vulnerability, credentials or private proxy configuration in a public issue.

Use GitHub private vulnerability reporting when available. Otherwise open the smallest safe public report possible, mark it as security-related, and omit sensitive values.

A useful report includes:

- affected version/commit;
- Windows version/build;
- whether User Mode, Admin Mode or Game Mode is involved;
- reproduction steps;
- sanitized logs.

Remove passwords, server addresses, subscription URLs, PAC secrets, tokens and full private configuration before sharing diagnostics.

## Security-sensitive areas

Changes deserve additional review when they touch:

- settings rollback, schema changes or file-storage durability;
- startup executable copying and HKCU Run registration;
- NetworkService embedding/materialization;
- SHA-256 verification, extraction mutex or helper guard handle;
- UAC/elevation and named-pipe authentication/version handshake;
- WinDivert installation/runtime cleanup, pinned runtime hashes and FLOW/NETWORK ownership mapping;
- application self-update download/checksum/version validation, staged-updater digest/handle protection, temporary updater handoff, rollback, UAC and cleanup;
- release supply-chain gates: serviced .NET SDK/runtime baseline, fail-closed NuGet vulnerability audit and final EXE version verification;
- fragmented-packet handling and mandatory fail-closed DNSCrypt enforcement;
- active DNSCrypt runtimes disable plaintext bootstrap/system-DNS fallback; Automatic mode must resolve and pin a concrete resolver set from the signed catalog after applying DNSSEC, no-log, unfiltered and address-family constraints; resolver geography comes only from endpoint-IP GeoIP;
- SIP003 catalog downloads, ZIP/TAR extraction and managed plugin storage;
- SIP003 executable discovery/launch;
- secret display/import behavior.

User Mode must remain usable without extracting or launching the elevated NetworkService helper.

Application self-update is fail-closed. It accepts only the canonical GitHub release ZIP and matching SHA-256 sidecar from this repository, verifies the ZIP contains exactly one root `Shadowsocks.exe`, and validates the payload version. The source process hashes the staged updater, holds it open without write/delete sharing across process creation/UAC, and passes the expected digest through the internal handoff; the updater verifies its own staged image again before touching the installed product. It then waits for the old PID, preserves a rollback copy while replacing the target, and starts the installed new copy; only that installed copy removes the updater transaction. If Start with Windows launched the LocalAppData copy, the update target is the recorded original product EXE rather than the startup copy.

Release restore is also fail-closed: `NU1900`-`NU1904` are release-blocking when `NuGetAudit=true`, so either a known vulnerability or an unavailable vulnerability feed prevents packaging. Release builds require .NET SDK 10.0.303 or newer (the .NET 10.0.11 servicing baseline), and packaging verifies the final `Shadowsocks.exe` FileVersion against the requested release version.

Product settings belong in `%LOCALAPPDATA%\Shadowsocks\settings.json` in normal mode or the Clean Mode Temp session. Mutable data never belongs beside the release EXE. Registry writes are reserved for explicit Windows integration such as autostart/protocol/system-proxy behavior.

Managed SIP003 plugins are third-party executables. Manual import accepts ZIP and TAR.GZ packages, and archive extraction must reject paths escaping the staging directory as well as TAR links/special entries. Installing a package must not silently move mutable files beside `Shadowsocks.exe`; managed packages remain under the active `Plugins` storage directory.

## Scope

This repository provides a client application. It does not operate proxy servers. Provider availability, account access, server-side configuration and third-party service policy are outside the client security scope.
