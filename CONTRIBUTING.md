# Contributing

Keep pull requests focused. Avoid unrelated protocol, configuration or UI changes in the same patch.

## Requirements

- Windows
- .NET 10 SDK
- Target framework: `net10.0-windows10.0.19041.0`
- Application/test architecture: x86

The application must remain x86 until the bundled in-process `libsscrypto.dll` is replaced with a compatible build.

## Build and validate

```cmd
dotnet restore .\shadowsocks-windows.sln
dotnet build .\shadowsocks-windows.sln -c Release -p:Platform=x86
dotnet test .\test\ShadowsocksTest.csproj -c Release -p:Platform=x86
dotnet publish .\shadowsocks-csharp\shadowsocks-csharp.csproj -c Release -p:Platform=x86 -p:PublishProfile=FolderProfile
```

Delete stale `bin`/`obj` before diagnosing problems after TFM, WPF dependency or publish-profile changes.

A successful build is not enough for runtime-sensitive changes. Exercise the code path you changed.

## Compatibility rules

- Preserve existing v4 protocol and configuration behavior unless the change explicitly requires otherwise.
- Keep application settings in `gui-config.json`; do not reintroduce `ApplicationSettingsBase`, `app.config` or `Shadowsocks.dll.config`.
- Keep system-proxy integration in-process; do not reintroduce `sysproxy.exe` / `sysproxy64.exe`.
- Do not reintroduce `System.Windows.Forms.DataVisualization` / `System.Data.SqlClient` solely for the traffic chart.
- Do not globally suppress `Win32Exception`. Filter an expected native error only at the call site where it is known to be harmless.

## Runtime checks

For system-proxy changes, test saved PAC and Global modes, mode switching, internal reload, full exit/restoration and restart/reapplication. Tray/menu state must match the effective Windows proxy state.

For listener/Privoxy changes, run repeated reloads and distinguish expected shutdown cancellation from genuine failures.

For WPF/ReactiveUI changes, open every affected view at runtime.

## Localization

User-visible text must be localizable:

- WinForms/tray/menu strings: `shadowsocks-csharp/Data/i18n.csv`
- WPF strings: `shadowsocks-csharp/Localization/Strings*.resx`

Do not add a hard-coded English `MessageBox`, window title, label, tooltip or validation message when it belongs in the UI.

## Bug reports

Remove passwords, server addresses, subscription URLs, PAC secrets and other sensitive data.

Include the client version, Windows version, .NET runtime/SDK version, build/publish command, reproduction steps and relevant logs. For native failures, keep `NativeErrorCode`, HRESULT and the full stack trace.
