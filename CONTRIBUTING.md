# Contributing

Contributions are welcome. Keep changes focused and avoid unrelated behavior changes in the same pull request.

## Before opening a pull request

1. Search existing issues and pull requests for related work.
2. Build the solution with the .NET 10 SDK.
3. Run the test project.
4. Add or update tests when behavior changes.
5. Update documentation when user-visible behavior, requirements, or configuration changes.

```powershell
dotnet restore .\shadowsocks-windows.sln
dotnet build .\shadowsocks-windows.sln -c Release -p:Platform=x86
dotnet test .\test\ShadowsocksTest.csproj -c Release -p:Platform=x86
```

The application must remain x86 until the bundled in-process `libsscrypto.dll` is replaced with a compatible 64-bit build.

## Issues

Before opening an issue:

- Search existing issues first.
- For connection problems, review the upstream [Troubleshooting guide](https://github.com/shadowsocks/shadowsocks-windows/wiki/Troubleshooting).
- Remove passwords, server addresses, subscription URLs, and other sensitive data from logs and screenshots.
- Include the client version, Windows version, .NET runtime version, reproduction steps, and relevant log output.
