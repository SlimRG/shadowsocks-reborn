# Changelog

Fork-specific changes are tracked here. Historical upstream Shadowsocks for Windows changes remain in [`CHANGES`](https://github.com/SlimRG/shadowsocks-reborn/blob/main/CHANGES).

## [Unreleased]

## [5.2.22] - 2026-08-21

- Fixed the final false-positive updater release-validator regex: automatic update stream disposal is now guarded by the Windows regression test instead of brittle source formatting. A real 5.2.21 -> 5.2.22 self-update was verified successfully.
- Fixed automatic update download promotion on Windows: the temporary `.download` stream is fully disposed before `File.Move`, eliminating the sharing violation observed during 5.2.21 -> 5.2.22 update tests. SHA-256 sidecars now accept both standard `sha256sum` format and a plain 64-hex digest.
- Fixed DNSCrypt loopback-port allocation on Windows/GitHub Actions hosts where TCP and UDP dynamic/excluded ranges differ: allocation is now UDP-first, verifies the same numeric port over TCP, falls back to a dual-bind application-port candidate when needed, and preserves the last socket failure for diagnostics.
- Relaxed the Bouncy Castle Bzip2 notice validator to verify the legal contract (`modified Bzip2` + `Apache License 2.0`) instead of one obsolete wording.
- Fixed the Phase 10 NetworkService version release-validator check after version response ownership moved from `Program.cs` to `NetworkServiceResponses.cs`; validation now also requires the versioned-ping regression test.
- Fixed invalid multiline PowerShell boolean expressions in `Validate-DnsCrypt.ps1`; the release parser gate now passes this DNSCrypt validation block instead of stopping before repository validation.
- Fixed a release-validator PowerShell interpolation parser error and added a CI/release PowerShell parser gate for every `packaging/*.ps1` script.
- Synchronized the living Markdown documentation with the final 5.2.22 implementation: updater integrity/UAC handoff, release security gates, current selectable `RichTextBlock` Logs contract, storage architecture and release verification are now documented consistently.
- Fixed the release validator after the Logs viewer migration from `ListView` to selectable `RichTextBlock`: validation now enforces `IsTextSelectionEnabled = true` instead of the retired `ListViewSelectionMode.None` implementation token.
- Fixed a release-blocking self-updater compilation regression: `UpdateChecker` now resolves `SelfUpdater` from the service namespace, and launching the installed updated executable uses a valid explicit `Process.Start` null check instead of an invalid standalone null-coalescing expression.
- Fixed the DNSCrypt TOML test so `odoh_servers = false` is no longer mistaken for a `doh_servers = false` line, and added the missing resolver-filter translation in every shipped locale.
- Kept the automatic updater contract release-safe: canonical `Shadowsocks-win-x64.zip`/`.sha256` verification, PID handoff, rollback, replacement of the primary product EXE, relaunch, and cleanup remain required by repository validation.
- Synchronized the product version across Core, Windows infrastructure, WinUI integration, NetworkService, manifests and `ApplicationInfo` so all shipped assemblies identify as 5.2.22 / 5.2.22.0.
- Strengthened release validation around updater compilation invariants, `ss://` protocol-command quoting, single-file packaging and Windows 10 build 19041 compatibility metadata.
- Refreshed release workflow defaults, packaging commands, checklist examples and third-party notice metadata for 5.2.22.
- Raised the release SDK security floor to .NET SDK 10.0.303 (runtime 10.0.11), made NuGet audit availability/vulnerability findings `NU1900`-`NU1904` fail CI/release restores, and verify the final `Shadowsocks.exe` FileVersion exactly matches `5.2.22.0` before packaging.
- Hardened automatic update handoff against local staged-payload tampering: the verified updater is held read-only/non-deletable across `Process.Start`/UAC, its SHA-256 is carried as an internal handoff argument, and the updater re-verifies its own staged image before replacing the installed executable.

## [5.2.21] - 2026-08-21

- Replaced the legacy SOCKS5 forward-proxy handshake with an RFC 1928/1929 implementation: username/password authentication now works, domain-name replies are accepted, and fragmented socket reads/writes are handled correctly.
- Replaced the stale browser impersonation User-Agent in HTTP CONNECT with the product User-Agent, made CONNECT writes/header reads fragmentation-safe, preserved tunnel bytes received with the proxy response, and added forward-proxy regression tests.
- Hardened the UDP relay: SOCKS5 UDP destinations now participate in server-strategy selection, malformed/fragmented datagrams are rejected, failed associations can be recreated, encryptors are disposed deterministically, and the borrowed generic LRU cache was replaced with a purpose-built bounded association cache.
- Replaced the download-to-Explorer update flow with a strict automatic single-file self-updater: canonical GitHub ZIP/SHA-256 assets are verified, the new EXE performs a PID handoff with rollback, replaces the product binary, launches the installed copy and is then removed by that new copy.
- Start-with-Windows now records the primary product EXE in the Run command so an automatic update launched from the stable LocalAppData startup copy updates the real product binary instead of only the startup copy.
- Removed retired executable-side configuration/PAC/cache migration, pre-SIP002 URL handling, unmanaged plugin PATH/absolute-path fallback, old IPC/thread utility code and stale updater compatibility fallbacks; release validation rejects their return.
- Rebuilt the embedded product license and third-party notices for the actual 5.2.21 dependency set; About now reads validated non-empty legal resources through `EmbeddedResources`.
- Fixed `ss://` protocol registration so Windows quotes both the product executable path and the imported URL placeholder; associations now remain valid when `Shadowsocks.exe` is installed in a path containing spaces, and release validation prevents the unsafe command from returning.
- Locked WinUI release validation to the intended compatibility contract: compile against the Windows 26100 SDK while keeping `SupportedOSPlatformVersion` and `TargetPlatformMinVersion` at Windows 10 build 19041 for both WinUI projects, with the Windows 10/11 manifest compatibility declaration required.

### DNS and servers

- DNS page now starts with live Windows DNS status (active adapters, configured DNS servers, selected Shadowsocks DNS policy and transparent-interception state), adds localized tooltips/accessibility help for DNS controls, and shows only the settings relevant to the currently selected DNS mode.
- DNSCrypt `Automatic` resolver selection now resolves a concrete DNSCrypt/DoH server from the signed catalog using the configured DNSSEC, no-log, unfiltered and address-family constraints. It prefers a resolver in the active Shadowsocks server country, falls back to the best compatible catalog entry, and refreshes the choice when the active server/strategy changes.
- Added DNS privacy diagnostics and a self-test that verifies DNSCrypt health, Administrator DNS interception, fail-closed/system-DNS isolation, active-runtime bootstrap settings, filtered Automatic resolver pinning and the current secure transport.
- Server list labels now show the friendly server name plus a country flag and no longer append the server IP/hostname. Country metadata is resolved best-effort over HTTPS and cached in memory.
- Added a DNS-page `Test DNSCrypt` health-check action for the currently running local DNSCrypt listener.
- Added built-in DoH provider presets for Cloudflare, Google Public DNS, Quad9 (secure/unfiltered), AdGuard DNS (default/unfiltered) and Mullvad while retaining a custom HTTPS endpoint option.
- Added a `Show DNS Logs` logging option, disabled by default; DNS lifecycle/upstream events follow this switch, while raw dnscrypt-proxy resolver probes/RTT diagnostics additionally require `Verbose Logging`, and application-level DNS errors remain visible.
- Restored severity-aware log highlighting in the WinUI log viewer while keeping selectable text, wrapping control and horizontal scrolling.
- NetworkService helper extraction now uses a stable version/SHA-256 path instead of a PID/GUID path, preventing Windows Firewall from treating every Admin Mode start as a different executable.
- Direct DNS can now use primary/fallback IPv4/IPv6 resolver presets or custom addresses and can optionally route those selected DNS endpoints through the local Shadowsocks SOCKS5 path; with no explicit server, the original DNS destination can also be routed through Shadowsocks.
- Custom DoH now has an independent option to route its HTTPS upstream through the local Shadowsocks SOCKS5 endpoint.
- DNSCrypt resolver UX now shows the active Automatic resolver immediately, keeps ODoH disabled, exposes DNSCrypt/DoH protocol plus country/address-family/privacy filters for manual catalog browsing, measures manual-list latency in the background without blocking the UI, and replaces the active resolver's estimate with DNSCrypt Proxy's actual RTT when available.
- DNSCrypt settings changes are debounced and applied without locking the page; failed manual resolver changes use a bounded rollback and surface the last dnscrypt-proxy upstream error instead of an indefinite loading state.
- DNSCrypt resolver country metadata is derived exclusively from resolver endpoint IPs via batched GeoIP lookup; resolver names/descriptions are never used as geographic hints, and Anycast is neither represented as a pseudo-country nor guessed from description text.
- Final DNS regression pass reuses the verified resolver-source cache for temporary catalog/settings validation processes, removes duplicate DNS-page refreshes during background RTT probing, normalizes legacy manual resolver selections to supported DNSCrypt/DoH entries, and prevents stale manual resolver metadata from being persisted.
- Log rendering now keeps warning/error emphasis on multiline continuation and stack-trace lines and emphasizes the severity token while retaining theme-aware colors.
- DNSCrypt resolver mode is now stored explicitly instead of being inferred from `serverNames`; legacy stale manual selections migrate to Automatic, manual DNSCrypt/DoH selections are health-validated before persistence, and Automatic resolves a concrete server from the signed catalog using the configured DNSSEC, no-log, unfiltered and address-family filters instead of bypassing those filters with a Cloudflare-only pin.
- `Show Plugin Output` no longer reloads Shadowsocks or restarts SIP003; plugin stdout/stderr is captured asynchronously and filtered dynamically, plugin shutdown waits are bounded, and logging-level toggles no longer restart the network stack.
- DNSCrypt install/update candidate validation now verifies the exact candidate version first, then resolves an eligible resolver from that candidate's signed catalog before its runtime health check, so Automatic mode remains provider-neutral without breaking first install or component upgrades.

### Release and localization

- Moved repository Markdown documentation under `docs/` as the canonical GitHub/Wiki source, added `Home.md` and `_Sidebar.md`, repaired relative/resource paths, and kept GitHub functional templates under `.github/`.
- Completed the Russian localization audit, corrected misplaced license translations, and added repository validation that rejects CJK/Japanese/Korean text accidentally placed in the `ru-RU` column.

## [5.2.0] - 2026-08-18
- Logs page responsiveness: logging-only switches no longer trigger full page refreshes; log file I/O is background/coalesced and RichTextBlock rendering is capped to prevent UI hangs under verbose/plugin output.

### DNS and privacy

- Fixed an Administrator Mode DNSCrypt leak window: classic UDP/TCP 53 is now fail-closed before DNSCrypt startup, never falls back to plaintext system DNS, and Shadowsocks/SIP003 DNS queries no longer bypass DNSCrypt interception.
- Added signed, managed DNSCrypt Proxy installation from the official `DNSCrypt/dnscrypt-proxy` Windows x64 releases, with SHA-256/Minisign verification, safe extraction, resolver selection and rollback-safe activation.
- Added health-checked DNSCrypt runtime lifecycle with dynamic loopback ports, non-blocking normal startup, prepared-version `-check` validation, crash recovery, Job Object cleanup and optional routing through the local Shadowsocks SOCKS5 endpoint.
- Added the DNS page and tray controls for System, Direct, DNS-through-Shadowsocks, Custom DoH and DNSCrypt policies, plus install/update/reinstall/remove, DNSSEC/no-log/no-filter/IPv6, resolver selection, fail-closed policy and automatic component updates.
- Added Administrator Mode transparent UDP/TCP port 53 policy routing through WinDivert for Proxy, Custom DoH and DNSCrypt, while Direct remains direct; DNSCrypt runtime also remains available in User Mode for Shadowsocks-managed hostname resolution.
- Added automatic DNSCrypt update maintenance with a persisted minimum 24-hour check interval; the first component installation remains an explicit user action.
- Added bootstrap recursion protection for DNSCrypt-over-Shadowsocks and hardened WinDivert/CaptureChild fault propagation, UDP/TCP relay lifecycle, runtime rollback and process supervision.

### Release and maintenance

- Added DNSCrypt Clean Mode integration tests and release validators that reject bundled `dnscrypt-proxy`, native Minisign/libsodium helpers, invalid DNS policy values and missing third-party notices.
- Added embedded project and third-party license notices for the one-file executable, including Bouncy Castle and its modified Bzip2 component.
- Hardened WinDivert acquisition to the fixed official GitHub 2.2.2-A release asset with bounded streaming download, exact x64 archive paths, pinned SHA-256 verification for both x64 runtime payloads and PE architecture validation before loading.
- Added fragment-safe transparent routing: fragmented datagrams that require DNS/proxy rewriting are dropped as a whole instead of partially rewriting the first fragment, while direct fragment flows remain direct.
- Added a read-only WinDivert FLOW-layer ownership observer so new TCP/UDP endpoints are attributed to their process ID before NETWORK-layer routing; IP Helper lookup remains a safe fallback for pre-existing or ambiguous flows.
- Hardened automatic-update clock handling against implausible future timestamps and added a testable Clean Mode session lifecycle with exclusive lock ownership and deterministic tree cleanup.
- Suspend/shutdown now cancels active and queued DNSCrypt management transactions before WinDivert/DNSCrypt teardown and resumes the coordinator only on the next controller start lifecycle.

## [5.1.0] - 2026-08-18

### Storage

- Added managed SIP003 plugin storage below the active `Plugins` directory; normal mode uses `%LOCALAPPDATA%\Shadowsocks\Plugins` and Clean Mode uses its disposable Temp session.
- Replaced the application Registry settings backend with atomic `settings.json` / `settings.backup.json` under `%LOCALAPPDATA%\Shadowsocks`; old Shadowsocks Registry configuration is intentionally ignored.
- Localization now uses only the embedded `i18n.csv`; external LocalAppData overrides are no longer loaded or migrated.
- Added Rufus-style Clean Mode: an executable stem ending in `p` redirects all writable application state to a disposable `%TEMP%\Shadowsocks\Clean\...` session and cleans it on Quit.
- Added explicit LocalAppData/Clean Mode folder actions to Settings and disabled Start with Windows in Clean Mode at UI, tray, and backend levels.

### Fixed

- Replaced the legacy second-launch Win32 MessageBox with a localized Fluent WinUI `ContentDialog` owned by the already-running instance; the existing window is restored/foregrounded first, with an InfoBar fallback if another modal dialog is active.
- Fixed global hotkey modifier detection on the WinUI/.NET 10 frontend by using the Win32 key-state path.
- Main-window User/Admin traffic controls now apply immediately like tray commands; Administrator mode carries the UAC shield affordance.
- Product single-file publish removes PDB files copied from referenced projects before validating the one-EXE release layout; normal build symbols remain unchanged.
- Restored historical lower-case percent escapes when generating SIP002 `ss://` links for compatibility with existing URL tests/links.

### Changed

- Added a Plugins page with built-in `xray-plugin`, `v2ray-plugin` and `qtun` installation plus manual ZIP/TAR.GZ import; Servers now selects installed plugins from a dropdown and shows Server Name above Server IP.
- Added localized WinUI tooltips and matching accessibility help text across navigation, server management, traffic, Game Mode, PAC/GeoSite, online configuration, hotkeys, sharing, logs, settings and update controls.
- Completed all six non-English localization columns (`ru-RU`, `zh-CN`, `zh-TW`, `ja`, `ko`, `fr`) for every active embedded UI key and added release validation that rejects missing translations or placeholder mismatches.
- Moved `Verbose Logging` and `Show Plugin Output` to the Logs page, and `Check for Updates at Startup` to About & updates.
- Updated the pinned dependency graph to Windows App SDK 2.4.0, Microsoft.WindowsAppSDK.WinUI 2.3.6, NLog 6.2.0, Fody 6.9.3, Google.Protobuf 3.35.1, Newtonsoft.Json 13.0.4, System.Drawing.Common 10.0.11, Microsoft.NET.Test.Sdk 18.9.0, Windows SDK BuildTools 10.0.28000.2526, WinUIEx 2.9.3, ZXing.Net 0.16.11, MSTest 4.3.3 and System.Management 10.0.11.
- Updated NLog file archiving to the NLog 6 `ArchiveSuffixFormat` configuration.
- Start with Windows now uses a SHA-256-verified copy at `%LOCALAPPDATA%\Shadowsocks\Startup\Shadowsocks.exe`; legacy Run entries are migrated and the copy is refreshed when a newer product EXE is launched manually.
- Game Mode keeps manual rules and adds optional discovery suggestions from Steam, Epic Games, GOG and Xbox installations.
- Password reveal is hidden for servers imported from `ss://` links or online configuration sources; manual server entries retain the option.
- Logs toolbar is always visible; the Font command and Show toolbar preference were removed.
- Tray colors were refined for clearer/professional state separation: graphite (Disabled), bronze (Local PAC), forest teal (Online PAC), navy (Global), with muted activity accents.

### Known limitation

- DNS policy values exist in configuration/IPC, but transparent DNS interception/routing is not implemented in 5.1.0.

## [5.0.0] - 2026-08-15

### Platform and UI

- Migrated the product to .NET 10, x64 and WinUI 3 / Windows App SDK.
- Retired the legacy WinForms/WPF presentation layer after functional parity was reached.
- Added the Windows 11 Fluent shell, tray-first lifecycle, AppInstance single-instance activation, Mica, navigation, native dialogs/status UI and theme support.
- Migrated localization to `ILocalizationService` backed by the existing seven-column CSV format.

### Traffic and networking

- Replaced Privoxy with the managed HTTP/HTTPS forwarding path.
- Replaced external sysproxy helpers with Windows integration.
- Added User/Admin traffic modes and transparent TCP/UDP Admin capture through WinDivert.
- Added automatic Game Mode and per-application `Proxy`, `Direct`, `Block` routing.
- Added live NetworkService/WinDivert/TCP/UDP capture status in the WinUI Traffic page.
- Preserved PAC/GeoSite, Online Config, SIP003 plugins, QR workflows, hotkeys and UDP relay.

### Storage and deployment

- Moved persistent configuration away from executable-side `gui-config.json`; the current backend is `ISettingsStore` / `JsonFileSettingsStore` under LocalAppData.
- Added file-backed rollback snapshots and one-time migration backups under `%LOCALAPPDATA%\Shadowsocks\Migration`.
- Centralized all normal-mode application-owned writable state under `%LOCALAPPDATA%\Shadowsocks`; Clean Mode redirects the same logical tree to a disposable `%TEMP%\Shadowsocks\Clean\...` session.
- Removed portable-mode storage semantics that wrote mutable data beside the EXE.
- Added global per-user single-instance behavior to protect shared Windows integration and storage state.
- Embedded the self-contained single-file NetworkService helper into the product and materialized it only on Admin activation with SHA-256 validation, extraction serialization, version handshake and cleanup.
- Added unpackaged, self-contained, win-x64, single-file release packaging with a strict final layout of one `Shadowsocks.exe`.
- Added repository/release validators, CI packaging checks, storage/deployment tests and read-only/removable-drive validation rules.

### Known limitation

- DNS policy values exist in configuration/IPC, but transparent DNS interception/routing is not implemented in 5.0.0.
