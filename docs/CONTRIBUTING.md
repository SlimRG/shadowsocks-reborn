# Contributing

`shadowsocks-reborn` targets .NET 10, WinUI 3, Windows 10 build 19041+ and x64 only. Release validation currently requires .NET SDK 10.0.303 or newer so self-contained artifacts include the .NET 10.0.11 servicing/security baseline.

## Required validation

Before submitting changes, run on Windows:

```powershell
.\packaging\Validate-Repository.ps1

dotnet restore .\shadowsocks-reborn.sln -p:Platform=x64 -r win-x64 -p:NuGetAudit=true -p:NuGetAuditMode=all
dotnet build .\shadowsocks-reborn.sln -c Release -p:Platform=x64 -m:1 --no-restore
dotnet test .\Shadowsocks.UnitTests\Shadowsocks.UnitTests.csproj -c Release -p:Platform=x64 --no-build
```

For deployment/storage changes, also run:

```powershell
.\packaging\Build-Release.ps1 -Version 5.2.22
```

## Architecture rules

- Put platform-neutral protocol/configuration logic in `Shadowsocks.Core`.
- Put Windows API integration in `Shadowsocks.Windows`.
- Put WinUI-specific shell/tray integration in `Shadowsocks.Windows.WinUI` or `Shadowsocks.WinUI`.
- Do not reintroduce WinForms or WPF.
- Keep `Shadowsocks.NetworkService` as an isolated elevated helper; do not turn it into a normal executable `ProjectReference` of the product.
- User/Admin are the only selectable traffic modes. Game Mode remains automatic.
- Pages interact through controller/services; keep persistence and Windows Registry integration behind dedicated services (simple shell actions such as opening the current data folder are fine).

See [ARCHITECTURE.md](ARCHITECTURE.md).

## Storage rules

Follow [STORAGE_POLICY.md](STORAGE_POLICY.md).

- Persistent configuration goes through `ISettingsStore` / `JsonFileSettingsStore` under the active storage root.
- Persistent mutable files go below `%LOCALAPPDATA%\Shadowsocks`.
- All application-owned writable files go below the active storage root: LocalAppData in normal mode, the Clean Mode Temp session in Clean Mode.
- Managed SIP003 packages belong under `Plugins\<plugin-id>` in that active root. Manual plugin import accepts ZIP and TAR.GZ packages and must keep path-traversal rejection covered by tests for both formats.
- Start with Windows uses `%LOCALAPPDATA%\Shadowsocks\Startup\Shadowsocks.exe` in normal mode and must remain unavailable in Clean Mode.
- Product code must not write configuration, PAC/cache/log/helper/localization files beside the release EXE.
- Do not add executable-side compatibility/migration reads. Product state and managed components come only from the current storage/services contracts.

## WinUI and localization

- Use native WinUI/Fluent controls and existing shell patterns; see [WINDOWS11_UI_GUIDE.md](WINDOWS11_UI_GUIDE.md).
- Preserve established WinUI behavior covered by tests and repository validation unless it is being intentionally redesigned. When implementation primitives change (for example `ListView` → selectable `RichTextBlock`), update validator tokens and the corresponding Markdown guidance together.
- User-visible strings use `ILocalizationService` / the CSV localization path.
- Keep stable enum/configuration values separate from localized display text.
- Preserve the seven-column localization schema: `en,ru-RU,zh-CN,zh-TW,ja,ko,fr`.
- New user-visible keys require English plus complete `ru-RU`, `zh-CN`, `zh-TW`, `ja`, `ko` and `fr` translations; release validation rejects missing translated cells.

## Product publish

Release packaging is one-file:

```text
Release/
└── Shadowsocks.exe
```

Publish with the supplied profile/script. Do not add helper EXEs, DLLs, runtime JSON, PDBs or icons to the release directory. NetworkService is embedded into `Shadowsocks.exe` and materialized only when Admin Mode requires it.

## Pull requests

Changes to networking, storage, startup, Admin capture, deployment or release validation should update the relevant living Markdown (`README`, `ARCHITECTURE`, `SECURITY`, `STORAGE_POLICY`, `WINDOWS11_UI_GUIDE`, `RELEASE_CHECKLIST`, `CHANGELOG`) in the same change and state:

- user-visible behavior changed;
- User/Admin/Game Mode paths tested;
- settings schema/rollback impact;
- LocalAppData/Clean-Mode/Temp path impact;
- exact build/test/publish commands used;
- known limitations or follow-up work.

Never commit or post passwords, server addresses, private subscription URLs, PAC secrets, tokens or generated private user configuration.
