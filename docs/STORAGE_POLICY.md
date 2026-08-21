# Storage policy

The release directory is immutable. Product builds contain only `Shadowsocks.exe`; application state, managed components, plugins, caches and logs are never read from or written beside the executable.

## Normal mode

The normal per-user root is:

```text
%LOCALAPPDATA%\Shadowsocks
```

Application-owned persistent/runtime state is stored below that root:

```text
settings.json
settings.backup.json
Cache\PAC\...
Cache\GeoSite\...
Data\PAC\...
Plugins\<plugin-id>\...
Components\DNSCryptProxy\...
Logs\shadowsocks.log
Runtime\WinDivert\...
Runtime\DNSCryptProxy\...
Startup\Shadowsocks.exe
Temp\NetworkService\...
Temp\Updates\DNSCryptProxy\...
Temp\StartupLogs\...
Temp\Working\...
```

`settings.json` is the persistent settings backend through `ISettingsStore` / `JsonFileSettingsStore`. Writes use a temporary file plus replacement and `settings.backup.json` keeps the previous durable document. Old application Registry configuration and executable-side configuration files are not imported.

## SIP003 plugin packages

The Plugins page installs managed packages below `Plugins\<plugin-id>` in the active storage root. Built-in catalog entries download a Windows x64 release archive; manual installation accepts ZIP and TAR.GZ packages. Server configuration stores the managed plugin id and runtime resolution is exclusively through `PluginManager`. Arbitrary executable-directory, absolute-path and PATH fallbacks are not part of the runtime contract.

## Localization

Localization has one source of truth: the `i18n.csv` embedded in `Shadowsocks.exe`. No external localization catalog is extracted, loaded or migrated.

## Clean Mode

Clean Mode uses the Rufus-style executable suffix convention: the executable name without `.exe` must end in `p` (case-insensitive), for example `Shadowsocksp.exe` or `Shadowsocks-5.2p.exe`. Each launch gets a unique root:

```text
%TEMP%\Shadowsocks\Clean\<timestamp>-<pid>-<guid>\
```

The normal logical storage tree is recreated below that directory. Clean Mode does not read normal `%LOCALAPPDATA%\Shadowsocks` settings, does not perform old-file migration and does not enable Start with Windows. On Quit, controller/helpers and logging stop, the session lock is released and the session tree is removed best-effort; stale abandoned sessions are pruned later.

## Start with Windows

Normal mode keeps a SHA-256-verified startup copy at:

```text
%LOCALAPPDATA%\Shadowsocks\Startup\Shadowsocks.exe
```

The HKCU Run entry points to that stable copy with `--start-hidden` and an internal startup-origin argument containing the primary product EXE path. The origin lets automatic self-update replace the primary product EXE even when the current process was launched from the LocalAppData startup copy. After the updated primary copy starts, `AutoStartup` refreshes the stable startup copy. Registry use here is Windows shell integration, not application-settings storage.

## Application self-update transactions

Application self-update is intentionally outside `StorageRoot` and outside a Clean Mode session:

```text
%TEMP%\Shadowsocks\Updates\<transaction>\
  Shadowsocks-win-x64.zip
  Shadowsocks-win-x64.zip.sha256
  Shadowsocks.Update.exe
```

This location must survive shutdown/cleanup of the running application while `Shadowsocks.Update.exe` waits for the old PID and replaces the product EXE. The installed new copy receives internal cleanup arguments, waits for the temporary updater to exit, and deletes the transaction. Stale transactions are pruned best-effort. DNSCrypt/component update staging remains below the active storage root (`Temp\Updates\...`) because it is runtime component state rather than application self-replacement.

## Registry boundary

Application state must not be stored in the Registry. Registry access is limited to Windows-owned integration scenarios that inherently require it, including Start with Windows, explicit `ss://` protocol association, WinINet/system proxy integration, and read-only launcher/game discovery.

## Product-directory invariant

Product code must not create, update or migrate mutable sidecars beside `Shadowsocks.exe`. The executable directory is used only for the running/replacement product binary itself. Release validation enforces a one-file distribution and rejects retired executable-side configuration/migration paths.
