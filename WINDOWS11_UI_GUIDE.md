# Windows 11 UI guide

This document defines the current WinUI 3 presentation rules. It is not a migration log.

## Shell

- `Shadowsocks.WinUI` is the only desktop presentation project.
- Use the existing left `NavigationView` shell and built-in Settings entry.
- Primary window uses Mica Base where supported; page backgrounds remain transparent unless a dedicated surface is required.
- Closing the window hides it; tray state remains active until explicit Quit.
- Keep persistent application preferences in the main Settings page. The tray is for quick runtime actions, not logging/update preference switches.
- Settings exposes the current data root with an explicit LocalAppData/Clean Mode folder action. In Clean Mode the Start with Windows control is disabled and the displayed root points to the Temp session.
- New profiles check for updates at startup by default. Update preferences live under About & updates; verbose logging and plugin-output preferences live on the Logs page.
- Command labels do not use trailing ellipses; reserve ellipses for transient progress/status text.
- Tray integration belongs to `Shadowsocks.Windows.WinUI`; base `Shadowsocks.Windows` stays WinUI-free.

## Layout and typography

- Standard page gutter: 24 epx; compact/minimal navigation: 12 epx.
- Prefer measurements in multiples of 4.
- Use WinUI type-ramp resources such as `TitleTextBlockStyle`, `BodyTextBlockStyle`, `BodyStrongTextBlockStyle` and `CaptionTextBlockStyle`.
- Use sentence case for UI copy.
- Prefer theme resources (`CardBackgroundFillColorDefaultBrush`, etc.) over custom RGB surfaces.
- Do not encode state solely by color; combine color with text, glyph or selection state.

## Controls

Prefer native Fluent controls:

- `NavigationView` for app navigation;
- `ContentDialog` for modal confirmation/details;
- `InfoBar` for non-blocking state/errors;
- `TeachingTip` for contextual education;
- `NumberBox` for ports/timeouts;
- `ToggleSwitch` for binary settings;
- `ComboBox` for typed choices.

Code-only pages are constructed explicitly and hosted in the shell. Avoid introducing another desktop UI framework for missing controls.

## Traffic and Game Mode

- Traffic exposes only User/Admin as selectable modes.
- Administrator mode uses the UAC/shield affordance and applies immediately when selected.
- Traffic reports configured/runtime mode, NetworkService, WinDivert, TCP/UDP capture and redirect ports.
- Game Mode is displayed as automatic state, never as a third selectable traffic mode.
- Game Mode keeps manual executable/path/wildcard rules and may offer best-effort game suggestions from Steam, Epic Games, GOG and Xbox.
- Discovery never adds or enables a game without explicit user action.

## Servers, plugins and secrets

- Server Name appears before Server IP in the editor.
- The server plugin field is a `ComboBox`: `None`, installed managed plugins, and any existing legacy value needed to preserve configuration compatibility.
- Plugin Options and optional Plugin Arguments remain per-server settings.
- The Plugins page owns installation/removal. Built-in entries are `xray-plugin`, `v2ray-plugin` and `qtun`; manual import accepts ZIP and TAR.GZ packages.
- Plugin packages are stored below the active storage root so Clean Mode automatically uses its Temp session.
- Validate the current server before changing selection when edits could be lost.
- Keep an explicit discard path for an unconfigured/new entry.
- Password reveal is available for manually configured servers.
- Password reveal is not shown for servers imported from `ss://` links or online configuration sources.

## Logs

- The Logs toolbar is always visible.
- Do not restore the removed Font or Show toolbar commands.
- Keep the viewer bounded and scrollable, with compact timestamp/severity/message presentation.
- WARN/ERROR/FATAL state must remain visually distinguishable.
- Top Most uses the WinUI/AppWindow presenter path, not WinForms/WPF APIs.

## PAC and GeoSite

PAC/GeoSite page controls and tray commands share the same state matrix. Hide actions that do not apply to the selected PAC scenario rather than presenting misleading enabled controls.

Long-running network/update operations must use async controller APIs and keep the shell responsive.

## Localization

- WinUI depends on `ILocalizationService`, not direct legacy static UI calls.
- One localization service instance flows through app, shell, pages and tray integration.
- Static control text is localized after control-tree construction; dynamic/formatted text uses page-context localization helpers.
- Do not use translated labels as persisted enum/configuration values.
- CSV column order is `en,ru-RU,zh-CN,zh-TW,ja,ko,fr`.
- Localization has one source of truth: the embedded `i18n.csv`; do not add extracted/user override catalogs.

## App resource lifecycle

- `App.xaml` owns `XamlControlsResources`.
- `App.InitializeComponent()` loads Fluent resources before the first window/control is created.
- Custom `Program.Main` performs AppInstance redirection, creates startup services, initializes C#/WinRT COM wrappers and starts WinUI through public `Application.Start(...)`.
- Do not call generated `XamlGeneratedProgram` entry points directly.
- Keep `App` parameterless for generated XAML compatibility; pass startup context through the existing configuration path.

## Package consistency

`Shadowsocks.WinUI` and `Shadowsocks.Windows.WinUI` must keep compatible Windows App SDK/WinUI package versions. The base Windows project remains free of those dependencies. After any Windows App SDK update, perform a clean restore/build and smoke-test `NavigationView`, dialogs, `InfoBar`, `TeachingTip`, `NumberBox`, `ToggleSwitch` and tray integration.

## Design references

- Windows App SDK application structure
- WinUI custom title bar
- NavigationView
- Windows design guidelines
- Typography
- Alignment, margin and padding
- Mica material
- Windows app settings guidelines

Use current Microsoft documentation when changing these primitives rather than preserving obsolete migration-era workarounds.

- Main pages use a fixed 16-DIP content gutter in every `NavigationView` display mode; page-specific max-width centering must not shift the left edge.

## Tooltips and accessibility

- Add localized tooltips to non-obvious interactive controls and mirror the same text through `AutomationProperties.HelpText` for accessibility.
