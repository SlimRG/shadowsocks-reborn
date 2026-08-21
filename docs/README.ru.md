# shadowsocks-reborn для Windows

<img src="assets/shadowsocks.png" alt="Shadowsocks logo" width="64">

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
- Менеджер SIP003-плагинов со встроенным выбором `xray-plugin`, `v2ray-plugin`, `qtun` и ручным импортом ZIP/TAR.GZ; установленные пакеты хранятся под активным storage-root.
- UDP relay, QR import/export, hotkeys и единственный embedded CSV-каталог локализации.

## Режимы трафика

Выбираются только два режима:

- **User Mode** — без UAC и без извлечения NetworkService. Правила маршрутизации применяются к трафику, который приходит в локальный/system proxy.
- **Admin Mode** — запрашивает UAC, извлекает embedded `Shadowsocks.NetworkService.exe` под активный storage-root, проверяет его, запускает elevated и включает transparent TCP/UDP capture через WinDivert.

**Game Mode — не третий режим трафика, а автоматическое runtime-состояние.** Если выбран Admin Mode и запускается приложение из списка Game Mode, WinDivert capture временно останавливается. После завершения приложения Admin capture восстанавливается автоматически.

Страница Traffic показывает configured/runtime mode, NetworkService, WinDivert, TCP/UDP capture и redirect ports.

## Требования

- Windows 10 2004 / build 19041 или новее, либо Windows 11;
- x64 Windows;
- .NET 10 SDK 10.0.303 или новее нужен только для сборки из исходников (release build требует security baseline .NET 10.0.11).

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

Нужен .NET SDK 10.0.303 или новее под Windows:

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
.\packaging\Build-Release.ps1 -Version 5.2.22
```

Каноническое имя GitHub Release asset: `Shadowsocks-win-x64.zip` (рядом публикуется `Shadowsocks-win-x64.zip.sha256`).

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

Основной backend конфигурации — `%LOCALAPPDATA%\Shadowsocks\settings.json`, резервный документ — `settings.backup.json`. SIP003-плагины со страницы «Плагины» устанавливаются в `%LOCALAPPDATA%\Shadowsocks\Plugins` и выбираются для сервера по идентификатору. Старые значения `HKCU\Software\Shadowsocks Reborn\Settings` игнорируются. Локализация использует только `i18n.csv`, встроенный внутрь `Shadowsocks.exe`; второй файл больше не распаковывается.

В Settings показывается активный путь хранилища и одна кнопка **Открыть**, которая открывает этот каталог как в обычном режиме, так и в Clean Mode.

Если имя EXE заканчивается на `p` перед `.exe`, например `Shadowsocksp.exe` или `Shadowsocks-5.0p.exe`, включается **Clean Mode**. В нём настройки, кэши, PAC, логи, установленные плагины, runtime, helper и component-update/working-файлы пишутся в уникальный `%TEMP%\Shadowsocks\Clean\...` сеанс и удаляются best-effort при Quit. Автозагрузка в Clean Mode недоступна.

В обычном режиме Start with Windows копирует проверенный EXE в `%LOCALAPPDATA%\Shadowsocks\Startup\Shadowsocks.exe`; Windows Run integration указывает только на эту стабильную копию.

Полная схема — в [STORAGE_POLICY.md](STORAGE_POLICY.md).

## Обновление приложения

Обновление приложения по умолчанию автоматическое. После запуска клиент проверяет GitHub Releases, выбирает подходящую более новую версию, требует точные `Shadowsocks-win-x64.zip` и `.sha256`, проверяет SHA-256, структуру ZIP и версию EXE, затем размещает новый single-file EXE в `%TEMP%\Shadowsocks\Updates`. Новый временный EXE запускается с внутренней командой `--update`, ждёт завершения текущего процесса, с rollback-защитой заменяет основной EXE и запускает уже установленную новую копию. Установленная новая копия удаляет временный updater и transaction. Если приложение было запущено из копии автозагрузки в LocalAppData, обновляется записанный основной EXE, а не только startup-copy.

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

Страница DNS управляет опциональным подписанным компонентом `dnscrypt-proxy`. В режиме `Automatic` приложение выбирает конкретный DNSCrypt/DoH-резолвер из подписанного публичного каталога с учётом настроек DNSSEC, no-log, отсутствия фильтрации и IPv4/IPv6, после чего фиксирует выбранный набор в `server_names`; активный runtime поэтому работает с `bootstrap_resolvers = []` и не откатывается на plaintext системный DNS. В режиме `Manual` доступны подписанные DNSCrypt- и DoH-резолверы с отдельным фильтром по протоколу; явное обновление каталога может однократно использовать bootstrap DNS только до появления подписанного кэша, но активный DNS runtime всегда генерируется с `bootstrap_resolvers = []` и `ignore_system_dns = true`. На странице DNS есть `Test DNSCrypt` для health-check локального listener и отдельная самопроверка конфиденциальности DNS, которая проверяет Admin interception, fail-closed/system-DNS isolation, upstream/transport и bootstrap-конфигурацию активного runtime. Начиная с 5.2.0, Admin Mode прозрачно перехватывает UDP/TCP порт 53 через WinDivert и передаёт запросы в динамический локальный listener DNSCrypt. Изменения PID/порта DNSCrypt применяются во время работы, Game Mode приостанавливает системный перехват, а DNSCrypt всегда работает fail-closed: plaintext DNS блокируется во время запуска, перезапуска, восстановления или сбоя runtime вместо скрытого отката на системный DNS. WinDivert 2.2.2 скачивается только с закреплённого официального release URL, а извлечённые x64 DLL/драйвер обязаны совпасть с закреплёнными SHA-256 до загрузки. Read-only FLOW observer WinDivert даёт NETWORK-маршрутизатору PID владельца endpoint; IP Helper остаётся fallback для существовавших ранее или неоднозначных flow. Фрагментированные datagram, которым потребовался бы transparent DNS/proxy rewrite, отбрасываются целиком, поэтому последующие фрагменты не могут обойти policy; direct-фрагменты остаются direct. `Direct`, `Proxy` и `CustomDoh` доступны как рабочие DNS-policy: Direct может сохранять исходный DNS destination либо прозрачно перенаправлять перехваченный UDP/TCP DNS на основной/резервный IPv4/IPv6-резолвер и при необходимости отправлять выбранные DNS endpoint через локальный SOCKS5 Shadowsocks; Proxy отправляет DNS через Shadowsocks, а Custom DoH передаёт DNS wire messages на заданный HTTPS endpoint со своей независимой опцией маршрутизации через Shadowsocks. В Automatic DNSCrypt сначала предпочитается совместимый резолвер в стране активного Shadowsocks-сервера, а при отсутствии совпадения выбирается лучший совместимый резолвер каталога. Страна резолвера определяется только через GeoIP по фактическому IP его endpoint: имя, город и description не используются как географические подсказки. Anycast не считается страной и не угадывается по текстовому описанию. В Manual можно выбирать DNSCrypt или DoH из подписанного каталога, а ODoH остаётся отключён. В ручном выборе доступны фильтры по протоколу, стране, семейству адресов, DNSSEC, no-log и отсутствию фильтрации; задержка резолверов измеряется асинхронно, а для активного резолвера при наличии используется фактический RTT dnscrypt-proxy. DNSCrypt также работает в User Mode для разрешения имён, которыми управляет Shadowsocks; прозрачный системный перехват DNS по-прежнему требует Administrator Mode. Прозрачный перехват охватывает классический DNS по UDP/TCP порту 53; внутренний DoH/DoT/DoQ приложений универсально не перехватывается. Автоматическая проверка обновлений компонента хранит время последней попытки и выполняется не чаще одного раза в 24 часа, включая восстановление после сильного сдвига системных часов; первичная установка всегда запускается пользователем вручную. При маршрутизации самого DNSCrypt через Shadowsocks адреса Shadowsocks-серверов и forward proxy должны быть IP-адресами, чтобы исключить рекурсивный DNS bootstrap.

## Документация

- [ARCHITECTURE.md](ARCHITECTURE.md) — архитектура проектов и runtime flow.
- [STORAGE_POLICY.md](STORAGE_POLICY.md) — правила LocalAppData, Clean Mode, staging компонентов и self-update приложения.
- [WINDOWS11_UI_GUIDE.md](WINDOWS11_UI_GUIDE.md) — актуальные правила WinUI.
- [CONTRIBUTING.md](CONTRIBUTING.md) — правила разработки.
- [RELEASE_CHECKLIST.md](RELEASE_CHECKLIST.md) — проверки перед релизом.
- [SECURITY.md](SECURITY.md) — security reporting и чувствительные компоненты.
- [CHANGELOG.md](CHANGELOG.md) — изменения fork; история upstream остаётся в [`CHANGES`](https://github.com/SlimRG/shadowsocks-reborn/blob/main/CHANGES).

## Лицензия

`shadowsocks-reborn` распространяется по **GPL-3.0-or-later**. См. [страницу лицензии](LICENSE.md) и юридически значимый [LICENSE.txt](https://github.com/SlimRG/shadowsocks-reborn/blob/main/LICENSE.txt). Сторонние компоненты сохраняют собственные лицензии; см. [уведомления сторонних компонентов](THIRD-PARTY-NOTICES.md).
