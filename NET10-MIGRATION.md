# .NET 10 migration notes

This branch keeps the Shadowsocks Windows v4 architecture and retargets the existing client to `net10.0-windows` while preserving the x86 process architecture required by the bundled native cryptography library.

## Scope

The migration intentionally avoids the large architectural changes found in later Shadowsocks Windows generations. Compatibility changes are limited to the parts required for modern .NET and tooling.

Notable changes include:

- SDK-style application and test projects with `PackageReference`.
- `net10.0-windows` target framework.
- x86 `PlatformTarget` retained for `libsscrypto.dll` compatibility.
- `GlobalHotKeyCore` replacing the .NET Framework-only hotkey package.
- ToolStrip-based WinForms menus replacing legacy menu controls.
- Compatibility implementation for the original `HttpServerUtility.UrlTokenEncode` format used by PAC URLs.
- Deterministic cross-process instance and IPC identifiers instead of randomized `string.GetHashCode()` values.
- Modern `NotifyIcon.Text` and shell URL launching.
- ReactiveUI properties implemented without `ReactiveUI.Fody` IL weaving.
- Native .NET single-file publishing; Costura is no longer used.
- Modern WPF-compatible MdXaml/AvalonEdit/localization package versions.
- Modern MSTest project and assertions.

## Build

Use a .NET 10 SDK on Windows:

```powershell
dotnet restore .\shadowsocks-windows.sln
dotnet build .\shadowsocks-windows.sln -c Release -p:Platform=x86
dotnet test .\test\ShadowsocksTest.csproj -c Release -p:Platform=x86
```

## Publish

The supplied profile creates a framework-dependent x86 single-file application:

```powershell
dotnet publish .\shadowsocks-csharp\shadowsocks-csharp.csproj `
  -c Release `
  -p:Platform=x86 `
  -p:PublishProfile=FolderProfile
```

`RuntimeIdentifier=win-x86` is scoped to the publish profile rather than normal Debug/Release builds.

## WPF markup compiler

The WPF project uses current package assets compatible with modern Windows Desktop builds. In particular, MdXaml and its AvalonEdit dependency were updated together to prevent NuGet downgrade and legacy WPF markup-compiler failures.

When changing WPF dependencies, remove stale `bin`/`obj` directories before diagnosing `MarkupCompilePass1` failures.

## Native constraints

The process remains x86 because `Data/libsscrypto.dll.gz` contains a 32-bit native DLL loaded in-process. `privoxy.exe` is a separate x86 helper process. Both x86 and x64 variants of `sysproxy` are bundled.

Moving the main application to x64 therefore requires replacing or rebuilding `libsscrypto.dll` first.

## Validation history

The port has been iterated against actual .NET 10 restore/build/publish diagnostics. The current project configuration has successfully reached `dotnet publish` under .NET 10; subsequent cleanup changes intentionally avoid behavioral modifications to networking, encryption, configuration, and server-selection logic.

### WinForms chart compatibility

The legacy `System.Windows.Forms.DataVisualization` traffic chart was replaced with a small built-in WinForms/GDI+ control. This removes the chart package's hidden runtime dependency on `System.Data.SqlClient` and prevents the unrelated native `sni.dll` SQL transport from being included in the publish output. Shadowsocks itself does not use SQL Server.

### Socket listener shutdown on .NET 7+

.NET 7 changed the legacy `Socket.Begin*/End*` cancellation behavior: closing a socket with a pending asynchronous operation can surface as `SocketException` with `SocketError.OperationAborted` instead of `ObjectDisposedException`. `Listener.AcceptCallback` now treats `OperationAborted`/`Interrupted` during listener shutdown as expected and does not re-arm a stopped or replaced listener. Real socket errors are still logged.
