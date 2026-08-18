# Architecture

`shadowsocks-reborn` 5.x is a WinUI 3 desktop application split into platform-neutral core logic, Windows integration, WinUI integration, the product shell, and an isolated elevated capture helper.

## Project graph

```text
Shadowsocks.WinUI
  ├── Shadowsocks.Core
  ├── Shadowsocks.Windows
  └── Shadowsocks.Windows.WinUI

Shadowsocks.Windows
  └── Shadowsocks.Core

Shadowsocks.Windows.WinUI
  ├── Shadowsocks.Core
  └── Shadowsocks.Windows

Shadowsocks.NetworkService
  └── isolated elevated process / named-pipe boundary
```

Projects:

- `Shadowsocks.Core` (`net10.0`) — protocol, encryption, configuration model, PAC/GeoSite logic, localization, routing models, logging configuration and storage abstractions.
- `Shadowsocks.Windows` — file-storage bootstrap, legacy migration, WinINet/system proxy, startup, hotkeys, SIP003 package management/process hosting, UAC/Admin capture, WinDivert runtime and NetworkService coordination.
- `Shadowsocks.Windows.WinUI` — WinUI-specific Windows shell/tray integration.
- `Shadowsocks.WinUI` — unpackaged WinUI 3 application and product publish project. Release assembly name is `Shadowsocks`.
- `Shadowsocks.NetworkService` — isolated elevated x64 helper that owns transparent WinDivert capture/routing.
- `Shadowsocks.UnitTests` — Core/Windows tests without presentation dependencies.

`Shadowsocks.Core` must remain free of WinUI, WinForms, WPF and Windows App SDK dependencies. `Shadowsocks.NetworkService` is deliberately not a normal executable `ProjectReference` of the product.

## Application lifecycle

WinUI 3 is the only desktop presentation layer.

Startup flow:

```text
Program.Main
  ↓
Windows App SDK AppInstance
  ↓ single-instance redirect if required
App
  ↓
WindowsStorageBootstrapper
  ↓
ShadowsocksController
  ↓
MainWindow / tray / pages
```

The application uses one global per-user `AppInstance` key. Launching another copy from a different directory or USB drive redirects activation to the existing instance instead of creating a second settings writer.

Closing the main window hides it; the tray keeps the application alive. Explicit Quit performs controller shutdown and runtime cleanup.

## Presentation boundary

Pages read state and perform operations through controller/services. They do not own a second networking/configuration model; persistence and Registry integration stay behind storage/Windows services except for simple shell actions such as opening the data folder.

WinUI-specific code belongs in `Shadowsocks.WinUI` or `Shadowsocks.Windows.WinUI`. Synchronous Windows integration belongs behind services in `Shadowsocks.Windows`; do not reintroduce WinForms/WPF to solve shell/platform tasks.

## Traffic architecture

```text
WinUI / tray
  ↓
ShadowsocksController
  ↓
GameModeManager / AdminCaptureManager
  ↓ named pipe
Shadowsocks.NetworkService (elevated)
  ↓
WinDivert
```

Only **User** and **Admin** are selectable traffic modes.

- User Mode does not require elevation or NetworkService extraction.
- Admin Mode acquires an on-demand NetworkService runtime lease, launches the broker elevated, validates its protocol version, and enables transparent TCP/UDP capture.
- Game Mode is automatic: a configured running application suspends Admin capture while keeping Admin as the configured mode. Capture restores automatically when the trigger exits.

Application rules support `Proxy`, `Direct` and `Block`.

## Storage architecture

Core uses `ISettingsStore`; product startup configures `JsonFileSettingsStore`.

Normal mode:

```text
%LOCALAPPDATA%\Shadowsocks
  settings.json
  settings.backup.json
  Cache\...
  Data\...
  Plugins\...
  Logs\...
  Runtime\...
```

Clean Mode is selected by an executable stem ending in `p` and redirects the same logical tree to:

```text
%TEMP%\Shadowsocks\Clean\<session>\...
```

The Registry is not an application-configuration backend. Registry access remains only for Windows integrations that require it (Run, protocol association, WinINet/system proxy, discovery reads).

The executable directory is a legacy/development read source only. Product runtime must not use it as mutable storage.

See [STORAGE_POLICY.md](STORAGE_POLICY.md).

## PAC, GeoSite, localization and logging

Default application resources are embedded. Mutable state is externalized:

- PAC/user data → `%LOCALAPPDATA%\Shadowsocks\Data\PAC`;
- Online PAC cache → `%LOCALAPPDATA%\Shadowsocks\Cache\PAC`;
- GeoSite cache/runtime → `%LOCALAPPDATA%\Shadowsocks\Cache\GeoSite` and `Runtime\WinDivert` as applicable;
- SIP003 plugin packages → `%LOCALAPPDATA%\Shadowsocks\Plugins` (or the active Clean Mode storage root);
- localization → embedded `Shadowsocks.Core.Data.i18n.csv` only;
- logs → `%LOCALAPPDATA%\Shadowsocks\Logs`.

NLog is configured programmatically; product deployment does not require `NLog.config`.

## Startup copy

When Start with Windows is enabled, `AutoStartup` copies the current executable to:

```text
%LOCALAPPDATA%\Shadowsocks\Startup\Shadowsocks.exe
```

The copy is SHA-256 checked and the HKCU Run entry points to that stable path with `--start-hidden`. Start with Windows is disabled in Clean Mode.

## Embedded NetworkService

Product publish uses a separate build step for the elevated helper:

1. publish `Shadowsocks.NetworkService` as self-contained, win-x64 and single-file;
2. embed the helper EXE as `Shadowsocks.WinUI.Embedded.Shadowsocks.NetworkService.exe`;
3. bundle that resource into the final `Shadowsocks.exe`;
4. reject every release sidecar.

Admin runtime materialization:

1. hash embedded helper bytes;
2. serialize extraction with a named mutex;
3. create a unique `Temp\NetworkService\<version>\<hash>\<pid-guid>` directory below the active storage root;
4. write and verify the helper SHA-256;
5. launch with UAC;
6. verify helper version over the control-pipe `ping` handshake;
7. keep a guard handle for the broker lifetime;
8. remove the current run directory on shutdown and prune stale directories later.

User Mode does not extract NetworkService.

## Product deployment

Release profile:

```text
unpackaged
self-contained
win-x64
PublishSingleFile
WindowsAppSDKSelfContained
```

Distribution invariant:

```text
Release/
└── Shadowsocks.exe
```

CI, `packaging/Build-Release.ps1` and `packaging/Validate-Repository.ps1` enforce the project boundaries and one-file release layout.
