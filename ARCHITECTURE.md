# Architecture

The solution is intentionally split so the networking engine can survive the UI migration from WinForms/WPF to WinUI 3.

- `Shadowsocks.Engine` — proxy/encryption/PAC/GeoSite/routing/configuration/application controller. It must not reference WinForms or WPF.
- `Shadowsocks.UI` — current transitional WinForms + WPF shell. Its executable is `shadowsocks-reborn.exe`.
- `Shadowsocks.NetworkService` — elevated x64 WinDivert helper used by Administrator traffic capture.
- `Shadowsocks.UnitTests` — unit tests for Engine plus the remaining legacy UI-specific hotkey tests.

## UI boundary

Engine code receives UI operations through `IUserInteractionService`. Process paths and arguments are supplied through `RuntimeEnvironment`. This removes direct dependencies on `Program`, `MessageBox`, `Clipboard`, WPF `Window`, and WinForms controls from the engine.

The next UI migration stage can therefore create a WinUI 3 implementation of the same UI boundary without changing proxy, PAC, routing or WinDivert logic.

## Namespace compatibility

Namespaces intentionally remain under `Shadowsocks.*` during this refactor. Project and assembly boundaries changed, but a mass namespace rename was avoided so the architectural split stays reviewable and does not introduce unrelated source churn.
