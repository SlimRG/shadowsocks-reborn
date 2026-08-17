# shadowsocks-reborn для Windows

<img src="docs/assets/shadowsocks.png" alt="Shadowsocks logo" width="64">

[English](README.md) | **Русский**

`shadowsocks-reborn` — Windows-ориентированное продолжение классического Shadowsocks for Windows v4. Ветка 5.x использует **.NET 10**, **WinUI 3 / Windows App SDK**, только **x64** и поддерживает Windows 10 build 19041 или новее.

> Это независимый fork, не upstream `shadowsocks/shadowsocks-windows`.

## Основное

- WinUI 3 — единственный desktop UI; WinForms и WPF больше не входят в продукт.
- Unpackaged, self-contained, win-x64, single-file deployment.
- В release находится только `Shadowsocks.exe`.
- Постоянные пользовательские настройки хранятся в `%LOCALAPPDATA%\Shadowsocks\settings.json`; каталог EXE не используется как изменяемое хранилище.
- В обычном режиме настройки, PAC/GeoSite cache, PAC-данные, logs и startup copy находятся в `%LOCALAPPDATA%\Shadowsocks`.
- Clean Mode по Rufus-схеме (`...p.exe`) переносит все изменяемые данные в одноразовый сеанс `%TEMP%\Shadowsocks\Clean\...`.
- Managed HTTP/1.1 proxy и HTTPS `CONNECT`; Privoxy/sysproxy удалены.
- Маршрутизация приложений: `Proxy`, `Direct`, `Block`.
- Transparent TCP/UDP capture в Admin Mode через WinDivert.
- Автоматический Game Mode временно останавливает Admin capture при запуске заданного приложения.
- Страница игр предлагает найденные Steam, Epic Games, GOG и Xbox игры; ручное добавление правил сохранено.
- SIP003 plugins, UDP relay, QR import/export, hotkeys и единственный embedded CSV-каталог локализации.

## Режимы трафика

Выбираются только два режима:

- **User Mode** — без UAC и без извлечения NetworkService. Правила маршрутизации применяются к трафику, который приходит в локальный/system proxy.
- **Admin Mode** — запрашивает UAC, извлекает embedded `Shadowsocks.NetworkService.exe` под активный storage-root, проверяет его, запускает elevated и включает transparent TCP/UDP capture через WinDivert.

**Game Mode — не третий режим трафика, а автоматическое runtime-состояние.** Если выбран Admin Mode и запускается приложение из списка Game Mode, WinDivert capture временно останавливается. После завершения приложения Admin capture восстанавливается автоматически.

Страница Traffic показывает configured/runtime mode, NetworkService, WinDivert, TCP/UDP capture и redirect ports.

## Требования

- Windows 10 2004 / build 19041 или новее, либо Windows 11;
- x64 Windows;
- .NET 10 SDK нужен только для сборки из исходников.

Опубликованный продукт self-contained и не требует отдельно установленного .NET runtime.

## Структура solution

- `Shadowsocks.Core` — protocol, encryption, configuration model, PAC/GeoSite, routing models, localization и storage abstractions.
- `Shadowsocks.Windows` — file-storage bootstrap, WinINet/system proxy, startup, UAC/Admin capture, WinDivert runtime, hotkeys и Windows integration.
- `Shadowsocks.Windows.WinUI` — WinUI-specific Windows shell/tray integration.
- `Shadowsocks.WinUI` — WinUI 3 shell и проект, публикующий `Shadowsocks.exe`.
- `Shadowsocks.NetworkService` — изолированный elevated WinDivert helper, embedded в release build.
- `Shadowsocks.UnitTests` — тесты Core/Windows без зависимости от UI.

Подробнее: [ARCHITECTURE.md](ARCHITECTURE.md).

## Сборка

Нужен .NET 10 SDK под Windows:

```powershell
dotnet restore .\shadowsocks-reborn.sln -p:Platform=x64 -r win-x64
dotnet build .\shadowsocks-reborn.sln -c Release -p:Platform=x64 -m:1 --no-restore
dotnet test .\Shadowsocks.UnitTests\Shadowsocks.UnitTests.csproj -c Release -p:Platform=x64 --no-build
```

Product publish:

```powershell
dotnet restore .\Shadowsocks.WinUI\Shadowsocks.WinUI.csproj -p:Platform=x64 -p:PublishProfile=FolderProfile -r win-x64
dotnet publish .\Shadowsocks.WinUI\Shadowsocks.WinUI.csproj -c Release -p:Platform=x64 -p:PublishProfile=FolderProfile -r win-x64 --self-contained true --no-restore
```

Либо release ZIP + SHA-256:

```powershell
.\packaging\Build-Release.ps1 -Version v5.0.0
```

Финальный publish directory и release ZIP должны содержать ровно:

```text
Shadowsocks.exe
```

DLL, PDB, runtime JSON, ICO и отдельный `Shadowsocks.NetworkService.exe` запрещены validator-ом.

## Хранение данных и автозагрузка

В обычном режиме всё постоянное состояние приложения хранится под:

```text
%LOCALAPPDATA%\Shadowsocks
```

Основной backend конфигурации — `%LOCALAPPDATA%\Shadowsocks\settings.json`, резервный документ — `settings.backup.json`. Старые значения `HKCU\Software\Shadowsocks Reborn\Settings` игнорируются. Локализация использует только `i18n.csv`, встроенный внутрь `Shadowsocks.exe`; второй файл больше не распаковывается.

В Settings показывается активный путь хранилища и одна кнопка **Открыть**, которая открывает этот каталог как в обычном режиме, так и в Clean Mode.

Если имя EXE заканчивается на `p` перед `.exe`, например `Shadowsocksp.exe` или `Shadowsocks-5.0p.exe`, включается **Clean Mode**. В нём настройки, кэши, PAC, логи, runtime, helper и update/working-файлы пишутся в уникальный `%TEMP%\Shadowsocks\Clean\...` сеанс и удаляются best-effort при Quit. Автозагрузка в Clean Mode недоступна.

В обычном режиме Start with Windows копирует проверенный EXE в `%LOCALAPPDATA%\Shadowsocks\Startup\Shadowsocks.exe`; Windows Run integration указывает только на эту стабильную копию.

Полная схема — в [STORAGE_POLICY.md](STORAGE_POLICY.md).

## Embedded NetworkService

В product publish `Shadowsocks.NetworkService` собирается как self-contained single-file helper и встраивается в `Shadowsocks.exe`.

В User Mode helper не извлекается. При включении Admin Mode он materialize-ится под активный storage-root (`%LOCALAPPDATA%\Shadowsocks\Temp\NetworkService\...` в обычном режиме и внутри Clean Mode session в Clean Mode). Извлечение сериализовано, файл проверяется SHA-256, после UAC выполняется version handshake по control pipe. Пока broker работает, helper защищён от замены/удаления; после остановки каталог удаляется best-effort, а stale runtime очищается при следующих запусках.

Development build может использовать отдельный helper из build output. В release package его нет.

## Проверка WinDivert

Admin Mode считается активным только после успешного `WinDivertOpen` в elevated capture process. Успешный запуск даёт лог примерно такого вида:

```text
WinDivert capture confirmed (start): Admin capture active.; TCP redirect port=..., UDP redirect port=...
```

Для функционального A/B теста добавь `curl.exe -> Block`, временно отключи Windows system proxy и сравни:

```cmd
curl.exe -4 --noproxy "*" https://example.com
```

В User Mode прямой запрос должен пройти мимо application routing, а в Admin Mode — блокироваться.

## PAC, HTTP forwarding и DNS

Local PAC использует заданные GeoSite sources и persistent cache. Online PAC скачивается через Shadowsocks и отдаётся WinINet через локальный `/pac` endpoint.

`ManagedHttpProxyService` поддерживает HTTP/1.1 и HTTPS `CONNECT`. HTTPS идёт как byte tunnel, поэтому HTTP/2 внутри TLS не требует отдельного HTTP/2 parser в локальном proxy. FTP gateway не реализован.

В configuration contract есть DNS policy `System`, `Direct`, `Proxy`, `CustomDoh`, но transparent DNS interception/routing в 5.0.0 пока не реализован.

## Документация

- [ARCHITECTURE.md](ARCHITECTURE.md) — архитектура проектов и runtime flow.
- [STORAGE_POLICY.md](STORAGE_POLICY.md) — LocalAppData/Clean Mode/Temp и migration.
- [WINDOWS11_UI_GUIDE.md](WINDOWS11_UI_GUIDE.md) — актуальные правила WinUI.
- [UI_PARITY_MATRIX.md](UI_PARITY_MATRIX.md) — зафиксированный результат миграции UI.
- [CONTRIBUTING.md](CONTRIBUTING.md) — правила разработки.
- [RELEASE_CHECKLIST.md](RELEASE_CHECKLIST.md) — проверки перед релизом.
- [SECURITY.md](SECURITY.md) — security reporting и чувствительные компоненты.
- [CHANGELOG.md](CHANGELOG.md) — изменения fork; история upstream остаётся в `CHANGES`.

## Лицензия

`shadowsocks-reborn` распространяется по [GNU General Public License v3.0](LICENSE.txt). Сторонние компоненты сохраняют свои лицензии.
