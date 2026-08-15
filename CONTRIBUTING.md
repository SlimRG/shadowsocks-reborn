# Contributing

Keep changes focused and easy to review. Avoid mixing unrelated protocol, UI, packaging and routing changes in one pull request.

## Development baseline

- Windows 10 2004 (build 19041) or newer.
- .NET 10 SDK.
- x64 only.
- Target framework: `net10.0-windows10.0.19041.0`.
- Mixed WinForms/WPF desktop application.

The main application publish is framework-dependent and single-file. `Shadowsocks.NetworkService` is published separately as a self-contained x64 single-file helper.

## Build and test

Run from a clean or cleaned working tree:

```cmd
dotnet restore .\shadowsocks-windows.sln -p:Platform=x64 -r win-x64
dotnet build .\shadowsocks-windows.sln -c Release -p:Platform=x64 -m:1
dotnet test .\test\ShadowsocksTest.csproj -c Release -p:Platform=x64 --no-build
```

For packaging/runtime changes also run:

```powershell
.\packaging\Build-Release.ps1 -Version dev
```

A successful compile is not enough for runtime-sensitive code. Exercise the path you changed.

## Compatibility rules

- Preserve existing Shadowsocks v4 protocol/configuration behavior unless the change explicitly requires otherwise.
- Keep settings in `gui-config.json`; do not reintroduce `ApplicationSettingsBase`, `app.config` or `Shadowsocks.dll.config`.
- Keep system-proxy integration in-process; do not reintroduce `sysproxy.exe` / `sysproxy64.exe`.
- Do not reintroduce Privoxy. HTTP/CONNECT forwarding belongs in `ManagedHttpProxyService`.
- Keep supported AEAD methods on `System.Security.Cryptography` unless there is a concrete compatibility reason to add a native backend.
- Keep classic Shadowsocks AEAD wire compatibility: MD5 password derivation and HKDF-SHA1 with `ss-subkey` are protocol requirements.
- Keep WinDivert optional. User Mode must start and operate without WinDivert files present.
- Game Mode is automatic compatibility behavior, not a third selectable traffic mode.
- Do not claim DNS policy enforcement until transparent DNS routing is actually implemented.
- Do not claim the Windows 11 WPF/Metro migration is complete while WinForms surfaces remain.

## Runtime checks

For system-proxy changes, test Disabled/PAC/Global switching, persistence, restart and restoration on full exit.

For PAC changes, test Local PAC, Online PAC cache reuse, first-download failure and update/reload behavior.

For managed HTTP proxy changes, test normal HTTP, HTTPS `CONNECT`, repeated reloads and process-based `Proxy` / `Direct` / `Block` rules.

For Admin Mode changes, test:

- UAC accept and cancel;
- WinDivert first download and cached startup;
- TCP and UDP routing;
- Shadowsocks/plugin process exclusions;
- `Proxy`, `Direct` and `Block` application rules;
- automatic Game Mode entry when a configured process starts;
- WinDivert teardown while Game Mode is active;
- automatic Admin Mode restoration after the application exits.

For WPF/ReactiveUI changes, open every affected view at runtime.

## Localization

User-visible strings must be localizable:

- WinForms/tray/menu: `shadowsocks-csharp/Data/i18n.csv`;
- WPF: `shadowsocks-csharp/Localization/Strings*.resx`.

Do not add a hard-coded UI message when it belongs in one of these localization sources.

## Code style

The repository uses `.editorconfig` as the baseline. Keep UTF-8 BOM and CRLF for Windows source/text files; shell scripts remain LF.

Before opening a PR:

```cmd
git diff --check
```

Also remove stale `bin`/`obj` directories when changing target frameworks, publish settings or WPF dependencies.

## Pull requests

Include:

- what changed and why;
- exact build/test commands used;
- runtime scenarios tested;
- screenshots for visible UI changes;
- documentation/localization updates when behavior changed.

Never include passwords, server addresses, subscription URLs, PAC secrets or private configuration data in logs or screenshots.
