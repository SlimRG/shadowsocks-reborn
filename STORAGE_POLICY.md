# Storage policy

The release directory is immutable. Product builds contain only `Shadowsocks.exe`; runtime state never uses the executable directory as writable storage.

## Normal mode

The normal per-user root is:

```text
%LOCALAPPDATA%\Shadowsocks
```

Everything owned by the application is stored below that root:

```text
settings.json
settings.backup.json
Cache\PAC\online-pac-cache.pac
Cache\PAC\online-pac-cache.meta.json
Cache\GeoSite\...
Data\PAC\pac.txt
Data\PAC\user-rule.txt
Data\PAC\abp.txt
Logs\shadowsocks.log
Migration\gui-config-<timestamp>.json
Runtime\WinDivert\...
Startup\Shadowsocks.exe
Temp\NetworkService\...
Temp\Updates\...
Temp\StartupLogs\...
Temp\Working\...
```

`settings.json` is the only persistent application-settings backend. It is managed through `ISettingsStore` / `JsonFileSettingsStore`; Shadowsocks configuration is not stored in the Registry.

Writes use a temporary file and atomic replacement. `settings.backup.json` keeps the previous durable settings document, while the configuration document also retains its previous valid `ConfigurationBackup` snapshot.

Old application configuration values under `HKCU\Software\Shadowsocks Reborn\Settings` are ignored.

## Localization

Localization has exactly one source of truth:

```text
Shadowsocks.exe
└── embedded i18n.csv
```

No `i18n.csv` is extracted to LocalAppData or Temp and no external localization override is loaded. Older `%LOCALAPPDATA%\Shadowsocks\Data\i18n.csv` files from development builds are ignored and removed best-effort in normal mode.

## Clean Mode

Clean Mode follows the Rufus-style executable suffix convention: the executable name **without `.exe` must end in `p`** (case-insensitive).

Examples:

```text
Shadowsocksp.exe          -> Clean Mode
Shadowsocks-5.0p.exe      -> Clean Mode
Shadowsocks.exe           -> Normal mode
Shadowsocks-portable.exe  -> Normal mode
```

Each Clean Mode launch creates a unique session root:

```text
%TEMP%\Shadowsocks\Clean\<timestamp>-<pid>-<guid>\
```

The normal storage tree is recreated below that directory, so all writable application state is temporary:

```text
settings.json
settings.backup.json
Cache\...
Data\PAC\...
Logs\...
Runtime\WinDivert\...
Temp\NetworkService\...
Temp\Updates\...
Temp\Working\...
```

Clean Mode deliberately does **not** read or migrate the normal `%LOCALAPPDATA%\Shadowsocks` configuration and does not read the obsolete application Registry settings. It starts from defaults for every session.

A session lock prevents stale cleanup from deleting an active Clean Mode directory. On normal Quit, the controller and helpers stop, NLog is shut down, the lock is released, and the entire session root is deleted best-effort. Stale abandoned Clean Mode sessions are pruned on later launches after the retention window.

### Start with Windows

Start with Windows is unavailable in Clean Mode. The Settings toggle is disabled, the tray item is disabled, and `AutoStartup` rejects the operation at the backend level.

## Start with Windows in normal mode

When enabled, the currently running single-file EXE is copied to:

```text
%LOCALAPPDATA%\Shadowsocks\Startup\Shadowsocks.exe
```

The copy is SHA-256 checked. The Windows `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` integration points only to this stable LocalAppData copy with `--start-hidden`.

This Registry use is Windows shell integration, **not application settings storage**.

## Scratch/runtime data in normal mode

Even disposable application-owned data stays below the normal LocalAppData root:

```text
%LOCALAPPDATA%\Shadowsocks\Temp\
  NetworkService\<app-version>\<hash>\<pid-guid>\Shadowsocks.NetworkService.exe
  Updates\...
  StartupLogs\...
  Working\...
```

The embedded NetworkService helper is materialized only when Admin Mode needs it. Extraction is serialized, SHA-256 validated, version-checked over the control pipe, guarded while active, and removed best-effort when the broker stops. The Windows App SDK/.NET single-file host may still use its own OS-managed Temp extraction area before managed startup; that location is outside the application storage policy.

## Legacy files

Normal mode may read old executable-side files only for one-time migration:

- `gui-config.json`;
- `pac.txt`;
- `user-rule.txt`;
- `abp.txt`;
- online PAC cache files;
- GeoSite cache;
- old temporary log output.

`gui-config.json` is backed up under `%LOCALAPPDATA%\Shadowsocks\Migration` before being imported into `settings.json`. External `i18n.csv` is intentionally **not migrated**.

Clean Mode performs no legacy migration.

## Registry boundary

Application state must not be stored in the Registry. Registry access is limited to Windows-owned integration scenarios that inherently require it, such as:

- Start with Windows (`Run`) in normal mode;
- `ss://` protocol association when explicitly enabled;
- WinINet/system-proxy integration;
- reading launcher/game installation information for discovery.

Do not add application configuration values under a Shadowsocks Registry key.

## Product-directory invariant

Product code must not create or update mutable files beside `Shadowsocks.exe`.

Allowed reads from the executable directory are limited to one-time legacy migration and development-only helper discovery. Release validation must continue to enforce a one-file distribution.
