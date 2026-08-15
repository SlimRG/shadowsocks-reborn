# Release Checklist

Use this checklist for a public `shadowsocks-reborn` release.

## Before tagging

- [ ] Working tree contains only intentional release changes.
- [ ] `UpdateChecker.Version`, project `<Version>` values, `AssemblyInformationalVersion` and `CHANGELOG.md` agree. The current release pipeline accepts stable `vMAJOR.MINOR.PATCH` tags.
- [ ] `.\packaging\Validate-Repository.ps1` succeeds; every `ResXFileRef` target is present in the checkout.
- [ ] `dotnet restore .\shadowsocks-windows.sln -p:Platform=x64` succeeds.
- [ ] `dotnet build .\shadowsocks-windows.sln -c Release -p:Platform=x64 -m:1 --no-restore` succeeds.
- [ ] `dotnet test .\test\ShadowsocksTest.csproj -c Release -p:Platform=x64 --no-build` succeeds.
- [ ] `git diff --check` is clean.
- [ ] User Mode works without WinDivert files installed.
- [ ] Admin Mode reaches `WinDivert: active` and logs `WinDivert capture confirmed`.
- [ ] A configured Game Mode application pauses WinDivert automatically and Admin Mode returns after it exits.
- [ ] `Proxy`, `Direct` and `Block` application rules were smoke-tested in Admin Mode.
- [ ] Disabled / PAC / Global system-proxy modes were smoke-tested.
- [ ] Local PAC and cached Online PAC were smoke-tested.
- [ ] Public logs/screenshots/config snippets contain no credentials, server addresses, tokens or private URLs.

## Package

Run:

```powershell
.\packaging\Build-Release.ps1 -Version v5.0.0
```

The script must produce:

- `artifacts/release/Shadowsocks-v5.0.0-win-x64.zip`;
- `artifacts/release/Shadowsocks-v5.0.0-win-x64.zip.sha256`.

Open the ZIP and confirm that `Shadowsocks.exe` and `Shadowsocks.NetworkService.exe` are both present. The package must not contain helper `.dll`, `.deps.json`, `.runtimeconfig.json`, Privoxy, sysproxy or legacy native-crypto binaries.

## GitHub

- [ ] Push the release commit and wait for the `CI` workflow to pass.
- [ ] Create and push the matching tag, for example `v5.0.0`.
- [ ] Wait for the `Release` workflow to build the package and create a **draft** GitHub Release with the ZIP plus SHA-256 file.
- [ ] Review generated release notes, attached files and documented known limitations, then publish the draft release.
