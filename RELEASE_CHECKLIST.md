# Release checklist

Use this checklist for every `shadowsocks-reborn` 5.x release.

## Source and metadata

- [ ] Release version matches all versioned projects, `ApplicationInfo.Version`, WinUI/NetworkService manifests and the dated `CHANGELOG.md` release heading.
- [ ] `CHANGELOG.md` describes user-visible changes and known limitations.
- [ ] `packaging\Validate-Repository.ps1` passes.
- [ ] Validator confirms the root `appsettings.json` is embedded in `Shadowsocks.Core` as `Shadowsocks.Core.appsettings.json`.
- [ ] No WinForms/WPF project, package, namespace or build flag has returned.
- [ ] No credentials, private subscriptions, PAC secrets, tokens or generated user state are committed.

## Build and tests

```powershell
.\packaging\Validate-Repository.ps1

dotnet restore .\shadowsocks-reborn.sln -p:Platform=x64 -r win-x64
dotnet build .\shadowsocks-reborn.sln -c Release -p:Platform=x64 -m:1 --no-restore
dotnet test .\Shadowsocks.UnitTests\Shadowsocks.UnitTests.csproj -c Release -p:Platform=x64 --no-build

.\packaging\Build-Release.ps1 -Version v5.1.0
```

- [ ] Restore/build/test succeeds on Windows x64 with .NET 10 SDK.
- [ ] Storage/rollback and PluginManager tests pass.
- [ ] Release packaging succeeds without manual file copying.

## Release layout

- [ ] `artifacts\publish` contains exactly `Shadowsocks.exe`.
- [ ] Release ZIP contains exactly `Shadowsocks.exe`.
- [ ] No NetworkService sidecar, DLL, `.deps.json`, `.runtimeconfig.json`, PDB, ICO, `gui-config.json`, Privoxy, sysproxy or retired crypto artifact exists beside the EXE.
- [ ] Generated SHA-256 matches the release ZIP.

Expected distribution:

```text
Release/
└── Shadowsocks.exe
```

## Clean install

Use a clean Windows test account or remove test-only state:

```text
%LOCALAPPDATA%\Shadowsocks
%TEMP%\Shadowsocks
```

- [ ] Start `Shadowsocks.exe` from an otherwise empty directory.
- [ ] User Mode works without creating files beside the EXE.
- [ ] `%LOCALAPPDATA%\Shadowsocks\settings.json` is created after configuration is saved.
- [ ] `settings.backup.json` appears after subsequent settings writes and rollback remains valid.
- [ ] App data/cache/log directories appear only under `%LOCALAPPDATA%\Shadowsocks` in normal mode.
- [ ] Old `HKCU\Software\Shadowsocks Reborn\Settings` values are ignored.
- [ ] No external `i18n.csv` is created or loaded.
- [ ] Switch through `ru-RU`, `zh-CN`, `zh-TW`, `ja`, `ko` and `fr`; main window, tray, dialogs and tooltips render localized strings without empty labels or English fallback caused by a missing catalog value.
- [ ] User Mode does not extract NetworkService.

## Legacy migration and rollback

- [ ] Start normal mode with a valid legacy `gui-config.json` beside the EXE and no `settings.json`.
- [ ] Servers, proxy/routing settings, UI preferences and other configuration survive migration.
- [ ] A migration backup is created under `%LOCALAPPDATA%\Shadowsocks\Migration`.
- [ ] Writable legacy files are removed best-effort.
- [ ] Migration also succeeds from a read-only source directory.
- [ ] Corrupting `settings.json` recovers from `settings.backup.json` / the configuration rollback snapshot.
- [ ] Legacy PAC/cache/log sidecars migrate to their documented locations.
- [ ] Legacy/external `i18n.csv` is not migrated and does not affect localization.

## Clean Mode

- [ ] Rename the release to `Shadowsocksp.exe` (or another stem ending in `p`) and launch it.
- [ ] The active data root is a unique `%TEMP%\Shadowsocks\Clean\...` directory.
- [ ] `settings.json`, PAC/cache, GeoSite, installed plugin packages, logs, WinDivert runtime and helper/update working files remain below that Temp session.
- [ ] Normal `%LOCALAPPDATA%\Shadowsocks` configuration is not read, copied or modified.
- [ ] Old Shadowsocks application Registry configuration is ignored.
- [ ] Start on Boot is disabled in Settings and tray; backend enable attempts fail.
- [ ] Quit removes the Clean Mode session directory after logs/helpers are closed.
- [ ] A stale abandoned Clean Mode directory is pruned on a later launch after retention.

## Traffic and helper runtime

- [ ] Main-window User/Admin controls apply immediately; Admin shows the UAC shield.
- [ ] Tray User/Admin commands match main-window behavior.
- [ ] Admin Mode approval creates the helper only below the active root (`%LOCALAPPDATA%\Shadowsocks\Temp\NetworkService\...` in normal mode).
- [ ] Helper SHA-256 and control-pipe version handshake succeed before capture commands.
- [ ] `WinDivert capture confirmed` appears after successful capture startup.
- [ ] Traffic page reports correct NetworkService, WinDivert, TCP/UDP and redirect-port state.
- [ ] Game Mode suspends capture while a configured trigger runs without changing configured Admin selection.
- [ ] Admin capture restores when the trigger exits.
- [ ] Broker shutdown removes the current helper run directory best-effort.

## Game discovery, server UI and plugins

- [ ] Games page can discover available Steam/Epic Games/GOG/Xbox candidates.
- [ ] Discovery adds nothing until the user explicitly chooses Add/Save.
- [ ] Manual game/application rules remain available alongside suggestions.
- [ ] `ss://`/online-config servers do not expose Show password.
- [ ] Manually configured servers retain Show password.
- [ ] Server Name is shown above Server IP and the Plugin field is a dropdown containing `None`, installed plugins and any preserved legacy value.
- [ ] Plugins page lists `xray-plugin`, `v2ray-plugin` and `qtun` and can install a supported Windows x64 catalog package.
- [ ] Manual plugin import accepts ZIP and TAR.GZ, rejects unsafe archive paths/packages without a selectable EXE, rejects TAR links/special entries, and installs below the active `Plugins` directory.
- [ ] Removing a managed plugin removes its package and it disappears from new server plugin selections.
- [ ] Logs toolbar is always visible and has no Font or Show toolbar command.

## Start with Windows

- [ ] Verify these checks in normal mode only; Clean Mode must keep the option unavailable.
- [ ] Enable autostart from a normal folder, read-only source and removable/USB source.
- [ ] `%LOCALAPPDATA%\Shadowsocks\Startup\Shadowsocks.exe` is created and SHA-256 matches the running product.
- [ ] `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\Shadowsocks Reborn` points only to the LocalAppData copy and includes `--start-hidden`.
- [ ] Launching a newer EXE manually refreshes the startup copy without changing the Run-key location.

## Single instance

- [ ] Start one copy, then launch the same/different copy from another folder or USB path.
- [ ] The second process redirects activation to the existing process and exits.
- [ ] A plain second launch brings the existing WinUI window to the foreground and shows the localized Fluent already-running `ContentDialog`; no legacy Win32 `MessageBox` appears.
- [ ] Repeating the second launch while another modal dialog is open falls back to the WinUI shell status UI without crashing.
- [ ] No second settings writer or competing helper extraction appears.

## Read-only/removable operation

- [ ] Run with only `Shadowsocks.exe` in a read-only directory.
- [ ] Repeat from a removable drive.
- [ ] Server edits, theme changes, PAC generation, GeoSite download, logs and updates still work.
- [ ] No writable sidecar appears beside the EXE.
- [ ] Managed SIP003 plugins resolve from `%LOCALAPPDATA%\Shadowsocks\Plugins` in normal mode; legacy absolute path/PATH plugins still launch with working data under the active root (`%LOCALAPPDATA%\Shadowsocks\Temp\Working` in normal mode).

## Cleanup

- [ ] Current NetworkService runtime is removed after broker shutdown.
- [ ] Stale helper runtime is pruned according to runtime retention rules.
- [ ] Startup logs older than 2 days are pruned.
- [ ] Update/working entries older than 7 days are pruned.
- [ ] Normal-mode LocalAppData settings/caches/logs and WinDivert runtime survive normal application exit by design.
- [ ] Clean Mode storage does not survive Quit by design.

## WinUI regression checks

- [ ] With Russian selected, every tray command is translated from the embedded catalog; no external `i18n.csv` is read or generated.
- [ ] Import an `ss://` URL while the Servers page is open; the new server appears without restarting the application, including when the page has unrelated unsaved edits.
- [ ] Invoke Servers, Logs, About/Updates and other window-opening commands from the tray while another application is in front; the existing Shadowsocks window restores and comes to the foreground.
- [ ] `Logs` contains `Verbose Logging` and `Show Plugin Output`; these preferences do not appear in Settings or tray menus.
- [ ] `About & updates` contains `Check for Updates at Startup` and the pre-release option; update-at-startup is enabled by default for a new configuration.
- [ ] A clean restore/build uses Windows App SDK 2.4.0 / WinUI 2.3.6 without `UseUwp`, `Windows.UI.Core` or unsafe mixed XAML projections.
- [ ] Run `dotnet list .\shadowsocks-reborn.sln package --outdated` and review any remaining updates before tagging; do not adopt prerelease packages unintentionally.

## GitHub release

- [ ] Push the release tag only after CI is green.
- [ ] Release workflow creates a draft release.
- [ ] Review generated notes, ZIP contents and SHA-256.
- [ ] Publish the draft only after smoke tests pass on the exact release artifact.

## Main-window import and sharing

- [ ] Verify Servers exposes **Server Name** and **Share Server Config** for the selected server.
- [ ] Verify unnamed configured servers receive stable unique `Server N` names without overwriting explicit names.
- [ ] Verify all main pages start at the same left gutter when the navigation pane changes display mode.
- [ ] Verify **Verbose Logging** and **Show Plugin Output** are on one row in Logs.
- [ ] Verify options/actions shared by tray and main UI use the same localized labels.
- [ ] Verify Share / QR can delete a server using the row `×` button.
- [ ] Verify QR import from screen, image file and clipboard image.
- [ ] Verify text `ss://` import and **Paste URL from clipboard**.
- [ ] Verify **Copy Local PAC URL** is available on PAC / GeoSite and is absent from the tray.
- [ ] Verify QR screen scan and clipboard URL import are absent from the tray.
- [ ] Verify Online Config is directly reachable from the main navigation.
