# Release checklist

Use this checklist for every `shadowsocks-reborn` 5.x release.

## Source and metadata

- [ ] Release version matches all versioned projects, `ApplicationInfo.Version`, WinUI/NetworkService manifests and the dated `CHANGELOG.md` release heading.
- [ ] `CHANGELOG.md`, README EN/RU, architecture/security/storage/UI guidance and this checklist reflect the current release behavior when the corresponding subsystem changed.
- [ ] `packaging\Validate-Repository.ps1` passes.
- [ ] Validator confirms the root `appsettings.json` is embedded in `Shadowsocks.Core` as `Shadowsocks.Core.appsettings.json`.
- [ ] No WinForms/WPF project, package, namespace or build flag has returned.
- [ ] No credentials, private subscriptions, PAC secrets, tokens or generated user state are committed.

## Build and tests

```powershell
.\packaging\Validate-Repository.ps1

dotnet restore .\shadowsocks-reborn.sln -p:Platform=x64 -r win-x64 -p:NuGetAudit=true -p:NuGetAuditMode=all
dotnet build .\shadowsocks-reborn.sln -c Release -p:Platform=x64 -m:1 --no-restore
dotnet test .\Shadowsocks.UnitTests\Shadowsocks.UnitTests.csproj -c Release -p:Platform=x64 --no-build

.\packaging\Build-Release.ps1 -Version 5.2.22
```

- [ ] `dotnet --version` is 10.0.303 or newer and therefore includes the .NET 10.0.11 security fixes.
- [ ] Restore/build/test succeeds on Windows x64; NuGet audit warnings NU1900-NU1904 are treated as release-blocking errors, so an unavailable vulnerability feed also blocks release.
- [ ] Storage/rollback and PluginManager tests pass.
- [ ] Release packaging succeeds without manual file copying.

## Release layout

- [ ] `artifacts\publish` contains exactly `Shadowsocks.exe`.
- [ ] Release ZIP contains exactly `Shadowsocks.exe`.
- [ ] GitHub Release publishes it as `Shadowsocks-win-x64.zip` with `Shadowsocks-win-x64.zip.sha256`.
- [ ] No NetworkService sidecar, DLL, `.deps.json`, `.runtimeconfig.json`, PDB, ICO, `gui-config.json`, Privoxy, sysproxy or retired crypto artifact exists beside the EXE.
- [ ] Generated SHA-256 matches the release ZIP.
- [ ] Published `Shadowsocks.exe` FileVersion is exactly `5.2.22.0`.

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

## Settings rollback and retired compatibility

- [ ] Corrupting `settings.json` recovers from `settings.backup.json` / the configuration rollback snapshot.
- [ ] `gui-config.json` beside the EXE is ignored; no migration directory or executable-side PAC/cache/log import is created.
- [ ] Old application Registry settings under `HKCU\Software\Shadowsocks Reborn\Settings` remain ignored.
- [ ] External `i18n.csv` is ignored and never migrated.
- [ ] Pre-SIP002 `ss://` links are rejected; current SIP002 links round-trip.
- [ ] SIP003 execution resolves only plugin ids installed through `PluginManager`; arbitrary absolute-path/PATH plugin fallback is rejected.

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
- [ ] Forward SOCKS5 proxy works with NO AUTH and RFC 1929 username/password authentication, including fragmented responses and IPv4/IPv6/DOMAIN reply addresses.
- [ ] Forward HTTP CONNECT handles authenticated proxies, fragmented response headers and proxy responses that coalesce the first tunnel bytes with `200 Connection Established`; those tunnel bytes are delivered intact.
- [ ] SOCKS5 UDP IPv4/IPv6/DOMAIN destinations are parsed before strategy selection; malformed/fragmented datagrams are dropped and a failed association can be recreated.
- [ ] Admin Mode approval creates the helper only below the active root (`%LOCALAPPDATA%\Shadowsocks\Temp\NetworkService\...` in normal mode).
- [ ] Helper SHA-256 and control-pipe version handshake succeed before capture commands.
- [ ] `WinDivert capture confirmed` appears after successful capture startup.
- [ ] Admin Mode downloads the fixed official WinDivert 2.2.2-A GitHub release asset, enforces bounded archive/runtime sizes, exact x64 archive paths and x64 PE validation before loading it.
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
- [ ] Server Name is shown above Server IP and the Plugin field is a dropdown containing `None`, installed plugins and any configured plugin that is currently unavailable.
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
- [ ] Automatic update from a protected directory keeps the staged updater locked across the UAC prompt, verifies its SHA-256 again in updater mode, replaces the primary EXE, relaunches it and removes the update transaction/backup.

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
- [ ] Managed SIP003 plugins resolve from `%LOCALAPPDATA%\Shadowsocks\Plugins` in normal mode; an unmanaged absolute-path/PATH value is rejected rather than executed.

## Cleanup

- [ ] Current NetworkService runtime is removed after broker shutdown.
- [ ] Stale helper runtime is pruned according to runtime retention rules.
- [ ] Startup logs older than 2 days are pruned.
- [ ] Component/working entries and `%TEMP%\Shadowsocks\Updates` application self-update transactions older than the retention window are pruned.
- [ ] Normal-mode LocalAppData settings/caches/logs and WinDivert runtime survive normal application exit by design.
- [ ] Clean Mode storage does not survive Quit by design.

## WinUI regression checks

- [ ] With Russian selected, every tray command is translated from the embedded catalog; no external `i18n.csv` is read or generated.
- [ ] Import an `ss://` URL while the Servers page is open; the new server appears without restarting the application, including when the page has unrelated unsaved edits.
- [ ] Enable **Associate ss:// Links** while running the product from a directory whose path contains spaces; verify the registered shell command is `"<full path>\Shadowsocks.exe" --open-url "%1"` and opening an `ss://` link activates/imports through the existing instance.
- [ ] Invoke Servers, Logs, About/Updates and other window-opening commands from the tray while another application is in front; the existing Shadowsocks window restores and comes to the foreground.
- [ ] `Logs` contains `Verbose Logging` and `Show Plugin Output`; these preferences do not appear in Settings or tray menus.
- [ ] Logs use the selectable `RichTextBlock` viewer (`IsTextSelectionEnabled = true`) with horizontal scrolling, styled WARN/ERROR/FATAL output and multiline continuation; no `ListViewSelectionMode` dependency has returned.
- [ ] `About & updates` contains `Automatically install updates` and the pre-release option; automatic install is enabled by default for a new configuration.
- [ ] A clean restore/build uses Windows App SDK 2.4.0 / WinUI 2.3.6 without `UseUwp`, `Windows.UI.Core` or unsafe mixed XAML projections.
- [ ] Smoke-test the final x64 artifact on Windows 10 2004 / build 19041: startup, tray, main window, server import and proxy enable/disable work without a newer-OS API failure.
- [ ] Run `dotnet list .\shadowsocks-reborn.sln package --outdated` and review any remaining updates before tagging; do not adopt prerelease packages unintentionally.

## Application self-update

- [ ] A release check sends the required GitHub API headers and ignores drafts; prereleases are included only when enabled, and stable wins over prerelease at the same numeric version.
- [ ] Only exact assets `Shadowsocks-win-x64.zip` and `Shadowsocks-win-x64.zip.sha256` from this repository's GitHub release-download path are accepted.
- [ ] Missing/invalid SHA-256, wrong sidecar filename, wrong payload version, extra ZIP entries or a non-root executable fail without closing the running app.
- [ ] With automatic updates enabled, a newer release downloads/verifies/stages automatically and launches `%TEMP%\Shadowsocks\Updates\<transaction>\Shadowsocks.Update.exe --update`; only a successful handoff triggers application shutdown.
- [ ] Before UAC/process handoff, the staged updater is SHA-256 hashed and held without write/delete sharing; tampering with the staged file after verification is detected, and the updater re-verifies its own image against the internal handoff digest before replacement.
- [ ] The temporary new EXE waits for the old PID, preserves rollback state, replaces the old product EXE and launches the installed new copy.
- [ ] The installed new copy waits for the updater PID and removes the temporary transaction/rollback files.
- [ ] If replacement or launch fails, the previous executable is restored and restarted where possible.
- [ ] If the process was launched from `%LOCALAPPDATA%\Shadowsocks\Startup\Shadowsocks.exe`, the primary EXE recorded by `--startup-origin` is updated; the stable startup copy is then refreshed from the new primary EXE.
- [ ] Rename the product to `Shadowsocksp.exe` and verify self-update replaces that Clean Mode executable while staging survives Clean Mode session teardown.
- [ ] When the product directory requires elevation, cancelling UAC leaves the currently running application alive; accepting UAC allows replacement.

## DNSCrypt / DNS interception

- [ ] `packaging\Validate-DnsCrypt.ps1` passes as part of repository validation.
- [ ] First DNSCrypt installation occurs only after an explicit user action and downloads the official Windows x64 release.
- [ ] DNSCrypt transition fail-closed: while enabling/restarting/stopping the runtime, UDP/TCP 53 is blocked until the local DNSCrypt relay is ready; no plaintext fallback is observable.
- [ ] Tampered archive/signature tests fail closed; no `dnscrypt-proxy.exe`, `minisign.exe` or libsodium sidecar is present in the repository/publish/ZIP.
- [ ] Delete/corrupt the cached WinDivert DLL or driver: Admin Mode redownloads them; a payload with the wrong SHA-256 is rejected before WinDivert is loaded.
- [ ] About & updates can open the embedded third-party notices from the final single-file executable.
- [ ] About & updates can open the embedded project GPL license from the final single-file executable.
- [ ] Embedded legal-resource tests pass and `LICENSE.txt` / `THIRD-PARTY-NOTICES.md` match the current dependency set.
- [ ] Automatic DNSCrypt resolves a concrete DNSCrypt/DoH resolver set from the signed catalog after applying DNSSEC, no-log, unfiltered and IPv4/IPv6 filters; the runtime pins that set in `server_names`, uses `bootstrap_resolvers = []`, and keeps `ignore_system_dns = true`; resolver-catalog refresh is the only profile allowed to contain one-shot plaintext bootstrap resolvers.
- [ ] DNS privacy self-test passes in Administrator Mode and reports active DNS interception, the selected filtered upstream, secure transport, blocked plaintext fallback and disabled active-runtime bootstrap.
- [ ] Automatic DNSCrypt maintenance performs no GitHub check more frequently than the persisted 24-hour interval and does not install an absent component.
- [ ] In User Mode, selecting DNSCrypt starts and health-checks its runtime for Shadowsocks-managed hostname resolution; entering Administrator Mode additionally enables transparent UDP/TCP DNS interception.
- [ ] `Route DNSCrypt through Shadowsocks` rejects hostname Shadowsocks/forward-proxy endpoints and accepts IP literals.
- [ ] In Administrator Mode verify all DNS policies: Direct preserves the original DNS destination or uses the configured primary/fallback resolvers and obeys its optional Shadowsocks route, Proxy sends UDP/TCP 53 through Shadowsocks, Custom DoH sends DNS wire messages to the configured HTTPS endpoint and obeys its independent Shadowsocks route, and DNSCrypt redirects to its dynamic loopback listener; Automatic uses a concrete filtered resolver set from the signed catalog and Manual supports signed DNSCrypt/DoH resolvers while ODoH remains disabled; DHCP, mDNS and LLMNR remain direct.
- [ ] Verify IPv4 and IPv6 fragmented DNS/proxy datagrams never partially bypass transparent policy: rewrite-required datagrams are dropped as a unit, while direct fragmented traffic remains direct.
- [ ] Stress DNSCrypt crash/restart and shared local-port scenarios; FLOW-layer PID attribution identifies new endpoints and ambiguous ownership falls back without guessing a PID.
- [ ] Kill `dnscrypt-proxy.exe`: classic UDP/TCP DNS/53 remains blocked with no plaintext/system-DNS fallback until runtime recovery updates the captured PID/port.
- [ ] Kill CaptureChild/force a WinDivert receive or send failure: UI status becomes inactive during recovery and capture is restored with bounded `1s -> 3s -> 10s` retries.
- [ ] Game Mode pauses transparent DNS interception without forcing a second UAC prompt and restores the latest DNSCrypt PID/port when the game exits.
- [ ] Suspend/resume cancels in-flight DNSCrypt maintenance before capture/runtime teardown and restarts maintenance after controller resume.
- [ ] Clean Mode keeps DNSCrypt component, runtime TOML and update staging below the disposable Temp session and removes the session on Quit. Verify the `.session.lock` is exclusive while running and the full session tree disappears after normal Quit.

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
