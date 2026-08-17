# Changelog

Fork-specific changes are tracked here. Historical upstream Shadowsocks for Windows changes remain in `CHANGES`.

## [Unreleased]

### Storage

- Replaced the application Registry settings backend with atomic `settings.json` / `settings.backup.json` under `%LOCALAPPDATA%\Shadowsocks`; old Shadowsocks Registry configuration is intentionally ignored.
- Localization now uses only the embedded `i18n.csv`; external LocalAppData overrides are no longer loaded or migrated.
- Added Rufus-style Clean Mode: an executable stem ending in `p` redirects all writable application state to a disposable `%TEMP%\Shadowsocks\Clean\...` session and cleans it on Quit.
- Added explicit LocalAppData/Clean Mode folder actions to Settings and disabled Start with Windows in Clean Mode at UI, tray, and backend levels.

### Fixed

- Fixed repository validation of MSBuild item metadata so attribute-form `LogicalName`/`Version` values are recognized for embedded `appsettings.json`, embedded NetworkService, and package references under PowerShell StrictMode.
- Fixed `Validate-Repository.ps1` under `Set-StrictMode -Version Latest`: optional MSBuild XML properties such as `UseWPF`/`UseWindowsForms` are now read through strict-safe XPath helpers instead of dynamic property access.
- Replaced the legacy second-launch Win32 MessageBox with a localized Fluent WinUI `ContentDialog` owned by the already-running instance; the existing window is restored/foregrounded first, with an InfoBar fallback if another modal dialog is active.
- Added Pass-3 regression validation for tray localization/focus, ss:// server-list refresh, logging event synchronization and the pure WinUI projection graph.
- Removed the legacy Windows.UI.Core hotkey-state projection from the WinUI frontend; modifier detection now uses Win32 GetKeyState so .NET 10 no longer requires conflicting UWP XAML projections.
- Main-window User/Admin traffic controls now apply immediately like tray commands; Administrator mode carries the UAC shield affordance.
- Fixed the remaining controller reference to removed executable-side `Configuration.ConfigFilePath` after the storage migration.
- Fixed the `RuntimeEnvironment` namespace ambiguity in `AutoStartup` by explicitly binding the application runtime environment type.
- Fixed the same `RuntimeEnvironment` namespace ambiguity in the WinUI custom `Program.Main` bootstrap and added repository validation to prevent the conflict from returning.
- Product single-file publish removes PDB files copied from referenced projects before validating the one-EXE release layout; normal build symbols remain unchanged.
- Restored historical lower-case percent escapes when generating SIP002 `ss://` links for compatibility with existing URL tests/links.

### Changed

- Added localized WinUI tooltips and matching accessibility help text across navigation, server management, traffic, Game Mode, PAC/GeoSite, online configuration, hotkeys, sharing, logs, settings and update controls.
- Completed all six non-English localization columns (`ru-RU`, `zh-CN`, `zh-TW`, `ja`, `ko`, `fr`) for every active embedded UI key and added release validation that rejects missing translations or placeholder mismatches.
- Moved `Verbose Logging` and `Show Plugin Output` to the Logs page, and `Check for Updates at Startup` to About & updates.
- Updated the stable dependency graph to Windows App SDK 2.4.0, Microsoft.WindowsAppSDK.WinUI 2.3.6, NLog 6.1.4, Fody 6.9.3, Google.Protobuf 3.35.1, Newtonsoft.Json 13.0.4, System.Drawing.Common 10.0.10, Microsoft.NET.Test.Sdk 18.9.0 and Windows SDK BuildTools 10.0.28000.2526; WinUIEx 2.9.2, ZXing.Net 0.16.11, MSTest 4.3.3 and System.Management 10.0.10 remain current stable versions.
- Updated NLog file archiving to the NLog 6 `ArchiveSuffixFormat` configuration.
- Start with Windows now uses a SHA-256-verified copy at `%LOCALAPPDATA%\Shadowsocks\Startup\Shadowsocks.exe`; legacy Run entries are migrated and the copy is refreshed when a newer product EXE is launched manually.
- Game Mode keeps manual rules and adds optional discovery suggestions from Steam, Epic Games, GOG and Xbox installations.
- Password reveal is hidden for servers imported from `ss://` links or online configuration sources; manual server entries retain the option.
- Logs toolbar is always visible; the Font command and Show toolbar preference were removed.
- Tray colors were refined for clearer/professional state separation: graphite (Disabled), bronze (Local PAC), forest teal (Online PAC), navy (Global), with muted activity accents.

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
