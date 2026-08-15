# shadowsocks-reborn для Windows

<img src="Shadowsocks.UI/Resources/ssw128.png" alt="Shadowsocks logo" width="64">

[English](README.md) | **Русский**

`shadowsocks-reborn` — Windows-ориентированное продолжение классического Shadowsocks for Windows v4. Текущая кодовая база переведена на **.NET 10**, поддерживает **Windows 10 2004 (сборка 19041) и новее** и собирается **только под x64**, сохраняя классическую модель протокола и конфигурации Shadowsocks.

> Текущая ветка релизов: **5.0**. Это отдельный fork, а не upstream-репозиторий `shadowsocks/shadowsocks-windows`.

## Основные возможности

- .NET 10, TFM `net10.0-windows10.0.19041.0`.
- Только x64 для приложения, тестов и elevated helper.
- AEAD через `System.Security.Cryptography`; старые native crypto wrappers больше не используются.
- Управление системным proxy через WinINet внутри процесса; `sysproxy.exe` удалён.
- Managed HTTP/1.1 и HTTPS `CONNECT` proxy; Privoxy удалён.
- Local PAC с настраиваемыми GeoSite-источниками и кэшируемый Online PAC.
- Маршрутизация приложений `Proxy` / `Direct` / `Block`.
- Опциональный прозрачный TCP/UDP-перехват в Admin Mode через WinDivert.
- Автоматический Game Mode, временно полностью отключающий WinDivert при запуске выбранных приложений.
- SIP003 plugins, классический UDP relay Shadowsocks, QR import/export, hotkeys и локализованный WinForms/WPF UI.

## Режимы трафика

В tray есть только два выбираемых режима:

- **User Mode** — без UAC и драйвера. Правила приложений применяются к трафику, который реально проходит через системный/локальный HTTP proxy. Прямые сокеты приложений, произвольный UDP и QUIC прозрачно не перехватываются.
- **Admin Mode** — запрашивает UAC, при необходимости загружает официальный x64 WinDivert и запускает elevated broker `Shadowsocks.NetworkService.exe`. TCP/UDP классифицируются по приложению и получают действие `Proxy`, `Direct` или `Block`.

**Game Mode не является третьим режимом трафика.** Это автоматическое состояние совместимости. Если выбран Admin Mode и запускается приложение из настроенного списка, capture-child WinDivert останавливается, а служба драйвера WinDivert удаляется. После закрытия приложения Admin Mode восстанавливается автоматически.

В меню отображается фактический runtime-статус: `WinDivert: активен`, `приостановлен (игра запущена)` или `неактивен`.

## Как проверить WinDivert

Admin Mode считается успешно запущенным только после того, как elevated capture-child выполнил `WinDivertOpen`. В логе должна появиться строка примерно такого вида:

```text
WinDivert capture confirmed (start): Admin capture active.; TCP redirect port=..., UDP redirect port=...
```

Для функционального A/B-теста добавь правило `curl.exe -> Block`, на время теста отключи системный proxy и сравни прямой запрос в User Mode и Admin Mode:

```cmd
curl.exe -4 --noproxy "*" https://example.com
```

В User Mode запрос должен пройти мимо application routing, а в Admin Mode — блокироваться.

## Требования

Для запуска release-сборки:

- Windows 10 версии 2004 / сборка 19041 или новее, либо Windows 11;
- x64 OS;
- .NET 10 Desktop Runtime x64 для основного приложения.

`Shadowsocks.NetworkService.exe` публикуется self-contained и отдельного .NET Runtime не требует. WinDivert опционален и загружается только при включении Admin Mode.

## Структура solution

- `Shadowsocks.Engine` — движок и controller layer без зависимостей WinForms/WPF.
- `Shadowsocks.UI` — текущий переходный WinForms/WPF UI; собирает `shadowsocks-reborn.exe`.
- `Shadowsocks.NetworkService` — elevated helper для WinDivert.
- `Shadowsocks.UnitTests` — тесты.

Граница UI описана в `ARCHITECTURE.md`; она подготовлена для следующего этапа миграции на WinUI 3.

## Сборка

На Windows с .NET 10 SDK:

```cmd
dotnet restore .\shadowsocks-reborn.sln -p:Platform=x64 -r win-x64
dotnet build .\shadowsocks-reborn.sln -c Release -p:Platform=x64 -m:1
dotnet test .\Shadowsocks.UnitTests\Shadowsocks.UnitTests.csproj -c Release -p:Platform=x64 --no-build
```

Публикация:

```cmd
dotnet publish .\Shadowsocks.UI\Shadowsocks.UI.csproj -c Release -p:Platform=x64 -p:PublishProfile=FolderProfile -r win-x64 --no-self-contained
```

Полная подготовка release ZIP и SHA-256:

```powershell
.\packaging\Build-Release.ps1 -Version v5.0.0
```

Продуктовая публикация содержит основной framework-dependent single-file EXE и отдельный self-contained single-file elevated helper. Поэтому в release-архиве `shadowsocks-reborn.exe` и `Shadowsocks.NetworkService.exe` должны лежать рядом.

## Конфигурация и runtime-данные

- Основной конфиг: `gui-config.json`.
- Пользовательские PAC-правила: `user-rule.txt`.
- GeoSite и Online PAC загружаются/кэшируются во время работы и не вшиваются в EXE.
- В portable mode WinDivert хранится в локальном каталоге `runtime`; иначе — в `%LOCALAPPDATA%\Shadowsocks\runtime`.

Проект явно исключает из build/publish устаревшие `ApplicationSettingsBase`, Privoxy, sysproxy и старые native crypto artifacts, чтобы они не вернулись при распаковке новой версии поверх старого checkout.

## PAC и HTTP forwarding

Local PAC загружает настроенные GeoSite-источники через активное соединение Shadowsocks и кэширует их отдельно. Online PAC также загружается через Shadowsocks и отдаётся WinINet через локальный `/pac`, поэтому Windows не требуется прямой доступ к удалённому PAC-хосту.

`ManagedHttpProxyService` обрабатывает HTTP/1.1 и HTTPS `CONNECT` в managed-коде. HTTPS остаётся байтовым tunnel, поэтому HTTP/2 внутри TLS работает без собственного HTTP/2 parser. FTP gateway не реализован.

## DNS

Контракт конфигурации/IPC уже содержит режимы `System`, `Direct`, `Proxy` и `CustomDoh`, но прозрачный DNS interception/routing **не реализован в 5.0.0**. Пока эти настройки нельзя считать механизмом принудительной DNS-маршрутизации.

## Состояние UI

Текущий presentation layer пока остаётся **смешанным WinForms/WPF**. `Shadowsocks.Engine` уже не зависит от UI framework, поэтому shell можно переносить на **WinUI 3 / Windows App SDK** без повторного переноса сетевой логики.

## Разработка

Перед PR см. [CONTRIBUTING.md](CONTRIBUTING.md). История текущей ветки находится в [CHANGELOG.md](CHANGELOG.md), исходная история upstream сохранена в `CHANGES`.

## Безопасность и приватность

Не публикуй в Issues пароли, адреса серверов, subscription URL, PAC secrets и полные приватные конфиги. Рекомендации по отчётам — в [SECURITY.md](SECURITY.md).

## Лицензия

`shadowsocks-reborn` распространяется по [GNU General Public License v3.0](LICENSE.txt). Сторонние компоненты сохраняют собственные лицензии.
