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

- configuration migration/rollback or file-storage durability;
- startup executable copying and HKCU Run registration;
- NetworkService embedding/materialization;
- SHA-256 verification, extraction mutex or helper guard handle;
- UAC/elevation and named-pipe authentication/version handshake;
- WinDivert installation/runtime cleanup;
- update download/extraction;
- SIP003 executable discovery/launch;
- secret display/import behavior.

User Mode must remain usable without extracting or launching the elevated NetworkService helper.

Product settings belong in `%LOCALAPPDATA%\Shadowsocks\settings.json` in normal mode or the Clean Mode Temp session. Mutable data never belongs beside the release EXE. Registry writes are reserved for explicit Windows integration such as autostart/protocol/system-proxy behavior.

## Scope

This repository provides a client application. It does not operate proxy servers. Provider availability, account access, server-side configuration and third-party service policy are outside the client security scope.
