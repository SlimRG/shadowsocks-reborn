# .NET 10 migration notes

This port keeps the Shadowsocks v4 architecture and behavior and retargets the existing Windows client to `net10.0-windows` (x86).

Key compatibility changes are deliberately narrow: SDK-style projects/PackageReference, `GlobalHotKeyCore` in place of the .NET Framework-only `GlobalHotKey`, ToolStrip-based menus, the original `HttpServerUtility.UrlTokenEncode` token format, deterministic cross-process instance IDs, modern `NotifyIcon.Text`, shell URL launching, and .NET 10 AppVeyor commands.

Build on Windows with a .NET 10 SDK:

```powershell
dotnet restore .\shadowsocks-windows.sln
dotnet build .\shadowsocks-windows.sln -c Release -p:Platform=x86
dotnet test .\test\ShadowsocksTest.csproj -c Release -p:Platform=x86
```

For a framework-dependent x86 single-file publish:

```powershell
dotnet publish .\shadowsocks-csharp\shadowsocks-csharp.csproj -c Release -p:Platform=x86 -p:PublishProfile=FolderProfile
```

The migration was statically validated against the project's historical upstream .NET Core port and current .NET 10 API behavior. The environment used to prepare this archive did not contain a .NET SDK, so a local `dotnet build` could not be executed here.

## WPF markup compiler

- Normal Debug/Release builds no longer carry a RuntimeIdentifier; x86 is enforced by PlatformTarget. win-x86 remains in the publish profile.
- MdXaml was updated to 1.27.0 and the localization markup packages to their .NET Core-capable stable versions to keep MarkupCompilePass1 away from legacy .NET Framework assets.
