# shadowsocks-reborn для Windows

<img src="Shadowsocks.Windows.WinUI/Shell/TrayAssets/ss32Fill.png" alt="Shadowsocks logo" width="32">

[English](README.md) | **Русский** | [简体中文](README.zh-CN.md) | [中文使用说明](https://github.com/SlimRG/shadowsocks-reborn/wiki/ZH-User-Guide)

`shadowsocks-reborn` — Windows-ориентированное продолжение классического Shadowsocks for Windows v4. Актуальные сборки используют **.NET 10**, **WinUI 3 / Windows App SDK**, только **x64** и поддерживают Windows 10 build 19041 или новее.

> Это независимый fork, не upstream `shadowsocks/shadowsocks-windows`.

Если нет ни одного полностью настроенного Shadowsocks-сервера, зависимые от сервера DNS, PAC и включение системного proxy Windows недоступны; после добавления валидного сервера они разблокируются. Для новой конфигурации DNSCrypt при первом использовании включены все параметры, кроме IPv6, но уже сохранённые DNSCrypt-настройки при миграциях схемы не изменяются. Длительные операции оболочки — Start on Boot, переключение Administrator capture/UAC и обновления — показывают явный индикатор выполнения. Навигационные и privilege-значки WinUI используют совместимый с Windows 10 набор Segoe MDL2 Assets.

## Текущий релиз: 5.2.31

В 5.2.31 уже реализован прозрачный перехват и DNS-routing в Administrator Mode для политик `System`, `Direct`, `Proxy`, `CustomDoh` и `DnsCrypt`. Релиз также включает управление DNSCrypt/DoH при отключённом ODoH, защищённое автоматическое обновление, защиту от двойного запуска при автозагрузке и актуальную WinUI 3 оболочку. CI/release gates дополнительно контролируют .NET servicing baseline, NuGet vulnerability audit, совместимость с Windows 10 build 19041 и версию опубликованного EXE.

Полная история изменений — в [История изменений](https://github.com/SlimRG/shadowsocks-reborn/wiki/RU-Changelog).

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
- Менеджер SIP003-плагинов со встроенным выбором `xray-plugin`, `v2ray-plugin`, `qtun` и ручным импортом ZIP/TAR.GZ; при импорте `ss://` с известным отсутствующим plugin приложение предлагает установить его до сохранения сервера; доверенные catalog-пакеты автоматически проверяются на обновления раз в сутки, когда не используются, и поддерживают ручную проверку.
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
- `Shadowsocks.Windows.WinUI` — самостоятельная WinUI-specific библиотека shell/tray/QR/power integration без ProjectReference на Core/Windows.
- `Shadowsocks.WinUI` — WinUI 3 shell и проект, публикующий `Shadowsocks.exe`.
- `Shadowsocks.NetworkService` — изолированный elevated WinDivert helper, embedded в release build.
- `Shadowsocks.UnitTests` — тесты Core/Windows без зависимости от UI.

Подробнее: [Архитектура](https://github.com/SlimRG/shadowsocks-reborn/wiki/RU-Architecture).

## Сборка

Единственный источник трёхчастной product version — `Directory.Build.props`. `ApplicationInfo.Version`, manifests и release notes проверяются относительно него.

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
.\packaging\Validate-Repository.ps1
.\packaging\Build-Release.ps1
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

Основной backend конфигурации — `%LOCALAPPDATA%\Shadowsocks\settings.json`, резервный документ — `settings.backup.json`. SIP003-плагины со страницы «Плагины» устанавливаются в `%LOCALAPPDATA%\Shadowsocks\Plugins` и выбираются для сервера по идентификатору. Пакеты из встроенного каталога сохраняют проверенное происхождение GitHub release и могут автоматически обновляться раз в сутки, пока плагин не используется; если оба release tag распознаются как версии, более старая candidate-версия отклоняется. Ручные ZIP/TAR.GZ-пакеты никогда не включаются в фоновое сетевое обновление. Старые значения `HKCU\Software\Shadowsocks Reborn\Settings` игнорируются. Локализация использует только `i18n.csv`, встроенный внутрь `Shadowsocks.exe`; второй файл больше не распаковывается.

В Settings показывается активный путь хранилища и одна кнопка **Открыть**, которая открывает этот каталог как в обычном режиме, так и в Clean Mode.

Если имя EXE заканчивается на `p` перед `.exe`, например `Shadowsocksp.exe` или `Shadowsocks-cleanp.exe`, включается **Clean Mode**. В нём настройки, кэши, PAC, логи, установленные плагины, runtime, helper и component-update/working-файлы пишутся в уникальный `%TEMP%\Shadowsocks\Clean\...` сеанс и удаляются best-effort при Quit. Автозагрузка в Clean Mode недоступна.

В обычном режиме Start with Windows копирует проверенный EXE в `%LOCALAPPDATA%\Shadowsocks\Startup\Shadowsocks.exe`; Windows Run integration указывает только на эту стабильную копию. Если осталась только legacy-запись `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, она мигрируется в каноническую запись; дубли удаляются, а при отключении Start with Windows удаляются и совпадающие legacy-записи. Независимый от app-identity process gate не позволяет startup-copy и исходному EXE одновременно запустить два controller и столкнуться за один локальный порт. Windows Restart Manager взаимоисключён со Start with Windows. Оба механизма постоянно фиксируют фактическое состояние UI через `--start-visible` или `--start-hidden`, поэтому после перезагрузки/входа восстанавливается состояние до выключения: открытое окно снова открывается, а приложение из трея остаётся в трее. `WM_QUERYENDSESSION` фиксирует состояние до системного закрытия HWND, поэтому завершение Windows не ошибочно трактуется как пользовательское «закрыть в трей».

Полная схема — в [Политика хранения](https://github.com/SlimRG/shadowsocks-reborn/wiki/RU-Storage-Policy).

## Обновление приложения

Загрузки обновлений сначала сохраняются как `*.download`; файловый дескриптор закрывается до атомарного переименования временного файла в Windows. SHA-256 sidecar может содержать либо `<hash>  Shadowsocks-win-x64.zip`, либо только 64-символьный шестнадцатеричный хэш.

Обновление приложения по умолчанию автоматическое. После запуска клиент проверяет GitHub Releases и выбирает только **строго более новую числовую версию**, чем установленная (`5.2.22 < 5.2.31`, поэтому 5.2.22 игнорируется; `5.2.32 > 5.2.31`, поэтому 5.2.32 подходит для обновления), требует точные `Shadowsocks-win-x64.zip` и `.sha256`, проверяет SHA-256, структуру ZIP и версию EXE, затем размещает новый single-file EXE в `%TEMP%\Shadowsocks\Updates`. Перед запуском staged updater получает собственный SHA-256; исходный процесс удерживает файл открытым без разрешения записи/удаления на время `Process.Start`/UAC и передаёт digest через внутренний update-handoff. Уже запущенный updater повторно проверяет собственный staged image до замены установленного EXE. Затем он ждёт завершения текущего процесса, сохраняет rollback-копию, заменяет основной EXE и запускает установленную новую копию. Новая копия удаляет временный updater и transaction. Если приложение было запущено из копии автозагрузки в LocalAppData, при выборе обновления используется большая из двух версий: версия запущенной startup-copy и FileVersion записанного основного EXE. Поэтому устаревшая startup-copy не может принять старый релиз за новый. Непосредственно перед заменой updater повторно сверяет FileVersion целевого EXE и отказывается от payload той же или более старой версии. Обновляется записанный основной EXE, а не только startup-copy.

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

Маршрутизация Local GeoSite + EasyList/ABP теперь **всегда выполняется managed-кодом C#**. Файл Local PAC содержит только минимальный `FindProxyForURL`, направляющий трафик в `ManagedHttpProxyService`; правила фильтрации внутри PAC не исполняются. Сначала применяются application rules, затем неизменяемый snapshot GeoSite + `user-rule.txt` в `Shadowsocks.Routing.FilterEngine` принимает авторитетное решение `DIRECT`/`PROXY`. Исторический `abp.js`, compiled-PAC backend, переключатель backend и исполняемый пользовательский `abp.txt` полностью удалены. При изменении схемы настроек удалённые свойства автоматически вычищаются канонической миграцией. **Online PAC — отдельный режим:** если его явно включить, выбранный пользователем внешний PAC остаётся источником решения, поскольку произвольный PAC JavaScript нельзя без потерь преобразовать в правила ABP/EasyList. На странице **PAC / GeoSite** в Local mode доступна кнопка **Открыть user-rule.txt**: пользовательские правила имеют приоритет над GeoSite defaults и применяются автоматически при изменении файла. При включённом Online PAC карточки Local managed routing, user-rule и GeoSite скрываются.

В Administrator Mode тот же managed rule set передаётся elevated NetworkService. Обычный TCP без явного application rule на SYN помечается `Deferred` и отражается в transparent relay; relay читает только начальные открытые routing-метаданные, извлекает HTTP `Host` или TLS ClientHello SNI и после этого открывает либо прямое соединение, либо SOCKS5 через Shadowsocks. **TLS не расшифровывается, сертификаты не устанавливаются, MITM отсутствует.** Для HTTP доступны path/query, для TLS/SNI — только hostname. Явные application rules сохраняют приоритет. UDP, TCP DNS/53 и протоколы без пригодного Host/SNI используют детерминированный fallback; быстрые изменения rule-файлов коалесцируются и перезапускают только capture child через уже elevated broker.

`ManagedHttpProxyService` поддерживает HTTP/1.1 и HTTPS `CONNECT`. HTTPS идёт как byte tunnel, поэтому HTTP/2 внутри TLS не требует отдельного HTTP/2 parser в локальном proxy. FTP gateway не реализован.

### Контракт DNS policy

В актуальном 5.2.31 конфигурация/IPC содержит пять значений DNS policy, и в Administrator Mode прозрачная маршрутизация классического DNS по UDP/TCP порту 53 уже реализована через `Shadowsocks.NetworkService` + WinDivert:

| Policy | Текущее поведение |
| --- | --- |
| `System` | Оставляет перехваченный классический DNS на исходном/системном destination без policy-specific redirect. |
| `Direct` | Может сохранить исходный destination либо перенаправить DNS на заданные основной/резервный IPv4/IPv6 resolver. Выбранный DNS endpoint при необходимости маршрутизируется через локальный SOCKS5 Shadowsocks. |
| `Proxy` | Отправляет перехваченный классический DNS через Shadowsocks. |
| `CustomDoh` | Преобразует перехваченные DNS wire messages в запросы к заданному HTTPS DoH endpoint; DoH upstream можно независимо отправлять через Shadowsocks. |
| `DnsCrypt` | Перенаправляет DNS в динамический локальный listener `dnscrypt-proxy` и работает fail-closed, если защищённый runtime недоступен. |

То есть прозрачный системный DNS interception/routing в **5.2.31 реализован**, но для него требуется **Administrator Mode**. В User Mode остаётся DNS, которым управляет сам Shadowsocks/DNSCrypt, однако произвольный системный DNS не перехватывается. Автоматический Game Mode приостанавливает системный перехват вместе с Admin capture. Прозрачный перехват охватывает классический DNS по UDP/TCP 53; внутренний DoH/DoT/DoQ приложений универсально не перехватывается.

Страница DNS управляет опциональным подписанным `dnscrypt-proxy`. `Automatic` выбирает конкретный DNSCrypt/DoH resolver из подписанного публичного каталога с учётом DNSSEC, no-log, отсутствия фильтрации и IPv4/IPv6, после чего фиксирует его в `server_names`. При обслуживании resolver-каталога имена источников разрешаются через **Cloudflare DoH по Shadowsocks**, с резервным **Google DoH по Shadowsocks**; сам каталог и `.minisig` скачиваются через тот же туннель и проверяются закреплённым Minisign-ключом DNSCrypt. Только после проверки локальная пара передаётся `dnscrypt-proxy`. И catalog-профиль, и активный runtime используют `bootstrap_resolvers = []` и `ignore_system_dns = true`, а catalog-профиль дополнительно содержит `urls = []`, поэтому dnscrypt-proxy не может откатиться к удалённому source lookup или системному/plaintext bootstrap. `Manual` показывает подписанные DNSCrypt/DoH resolver с фильтрами по протоколу, стране, address family и privacy-параметрам; ODoH остаётся отключён. Получение release metadata, ZIP и Minisign самого компонента DNSCrypt также использует этот транспорт Cloudflare→Google DoH поверх Shadowsocks, включая GitHub redirect-hosts, поэтому установка/обновление намеренно не зависит от Windows resolver для `api.github.com` и release assets.

Есть `Test DNSCrypt` и отдельный DNS privacy self-test. Аллокатор локального listener проверяет, что один loopback-порт одновременно доступен по UDP и TCP. Изменения PID/порта DNSCrypt передаются NetworkService во время работы. При запуске, рестарте, восстановлении или сбое DNSCrypt действует fail-closed вместо скрытого отката перехваченного трафика на plaintext system DNS. Фрагментированные datagram, которым нужен DNS/proxy rewrite, отбрасываются целиком; direct-фрагменты остаются direct.

WinDivert 2.2.2 скачивается только с закреплённого официального release URL; x64 DLL/driver должны совпасть с release-pinned SHA-256 до загрузки. Read-only FLOW observer передаёт NETWORK-router PID владельца endpoint, а IP Helper остаётся fallback.

При первой активации DNSCrypt страница DNS оставляет видимой анимированную карточку установки/проверки, поэтому пользователь видит текущий этап вместо просто заблокированных настроек без индикации.

Automatic DNSCrypt предпочитает совместимый resolver в стране активного Shadowsocks-сервера и при отсутствии совпадения выбирает лучший совместимый resolver подписанного каталога. Страна определяется только по GeoIP фактического endpoint IP; имя/description resolver не используются как географические признаки. В Manual задержка измеряется асинхронно, а для активного resolver при наличии используется RTT dnscrypt-proxy. Автоматическая проверка обновлений компонента выполняется не чаще раза в 24 часа; первичная установка остаётся явным действием пользователя. Если сам DNSCrypt маршрутизируется через Shadowsocks, адреса Shadowsocks/forward-proxy должны быть IP literals, чтобы исключить рекурсивный DNS bootstrap.

## Документация

- [Руководство пользователя (RU)](https://github.com/SlimRG/shadowsocks-reborn/wiki/RU-User-Guide)
- [User guide (English)](https://github.com/SlimRG/shadowsocks-reborn/wiki/EN-User-Guide)
- [中文使用说明](https://github.com/SlimRG/shadowsocks-reborn/wiki/ZH-User-Guide)

Длинная Markdown-документация хранится только в отдельном GitHub Wiki и больше не дублируется в основном репозитории. `.github/*.md` остаются в основном репозитории, поскольку это функциональные issue/PR templates GitHub.


- [Архитектура](https://github.com/SlimRG/shadowsocks-reborn/wiki/RU-Architecture) — архитектура проектов и runtime flow.
- [Политика хранения](https://github.com/SlimRG/shadowsocks-reborn/wiki/RU-Storage-Policy) — правила LocalAppData, Clean Mode, staging компонентов и self-update приложения.
- [Руководство Windows 11 / WinUI](https://github.com/SlimRG/shadowsocks-reborn/wiki/RU-Windows-11-WinUI-Guide) — актуальные правила WinUI.
- [Участие в разработке](https://github.com/SlimRG/shadowsocks-reborn/wiki/RU-Contributing) — правила разработки.
- [Проверка перед релизом](https://github.com/SlimRG/shadowsocks-reborn/wiki/RU-Release-Checklist) — проверки перед релизом.
- [Безопасность](https://github.com/SlimRG/shadowsocks-reborn/wiki/RU-Security) — security reporting и чувствительные компоненты.
- [История изменений](https://github.com/SlimRG/shadowsocks-reborn/wiki/RU-Changelog) — изменения fork; история upstream остаётся в [`CHANGES`](https://github.com/SlimRG/shadowsocks-reborn/blob/main/CHANGES).

## Лицензия

`shadowsocks-reborn` распространяется по **GPL-3.0-or-later**. См. [страницу лицензии](LICENSE.txt) и юридически значимый [LICENSE.txt](https://github.com/SlimRG/shadowsocks-reborn/blob/main/LICENSE.txt). Сторонние компоненты сохраняют собственные лицензии; см. [уведомления сторонних компонентов](THIRD-PARTY-NOTICES.txt).
