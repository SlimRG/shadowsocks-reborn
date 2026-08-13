# Shadowsocks для Windows

<img src="shadowsocks-csharp/Resources/ssw128.png" alt="Логотип Shadowsocks" width="64">

[English](README.md) | **Русский** | [中文说明](https://github.com/shadowsocks/shadowsocks-windows/wiki/Shadowsocks-Windows-%E4%BD%BF%E7%94%A8%E8%AF%B4%E6%98%8E)

Этот репозиторий содержит клиент Shadowsocks for Windows ветки v4, перенесённый на **.NET 10** с максимально возможным сохранением исходной архитектуры и поведения.

## Возможности

- Настройка системного прокси Windows.
- PAC-режим и глобальный режим проксирования.
- GeoSite и пользовательские PAC-правила.
- Локальные SOCKS5- и HTTP-прокси.
- Автоматическое переключение серверов.
- UDP relay.
- SIP003-плагины.
- Глобальные горячие клавиши.
- Импорт и экспорт серверов через QR-коды.
- Онлайн-конфигурации.

## Требования

- Windows, поддерживаемая .NET 10 Desktop.
- .NET 10 Desktop Runtime (x86) для framework-dependent публикации.
- Для встроенных native-компонентов может потребоваться Microsoft Visual C++ Redistributable (x86).

> Приложение намеренно собирается как **x86**, потому что встроенная `libsscrypto.dll` является 32-битной. Privoxy также поставляется как отдельный x86-процесс, а `sysproxy` присутствует сразу в x86- и x64-вариантах.

## Сборка и публикация

Release публикуется как framework-dependent x86 single-file приложение:

```powershell
dotnet publish .\shadowsocks-csharp\shadowsocks-csharp.csproj `
  -c Release `
  -p:Platform=x86 `
  -p:PublishProfile=FolderProfile
```

Результат появляется в каталоге:

```text
shadowsocks-csharp\bin\x86\Release\net10.0-windows\win-x86\publish\
```

## Быстрый старт

1. Запустите Shadowsocks и найдите его значок в области уведомлений.
2. Добавьте один или несколько серверов через меню **Servers**.
3. Включите **Enable System Proxy**, если приложения должны использовать системный прокси Windows.
4. Либо настройте приложение вручную на локальный SOCKS5/HTTP-прокси `127.0.0.1:1080`. Порт можно изменить в настройках.

## PAC

PAC-правила строятся по базе GeoSite из проекта [v2fly/domain-list-community](https://github.com/v2fly/domain-list-community).

Поддерживаются два режима:

- **Whitelist** (`geositePreferDirect = false`, по умолчанию): домены из direct-групп идут напрямую, остальные — через прокси.
- **Blacklist** (`geositePreferDirect = true`): домены из proxied-групп идут через прокси, direct-группы являются исключениями, остальные соединения идут напрямую.

Основные параметры в `gui-config.json`:

- `geositeDirectGroups` — по умолчанию содержит `cn` и `geolocation-!cn@cn`.
- `geositeProxiedGroups` — по умолчанию содержит `geolocation-!cn`.
- `geositePreferDirect` — выбирает режим whitelist/blacklist.

### Пользовательские PAC-правила

Собственные правила рекомендуется добавлять в `user-rule.txt`. Изменения непосредственно в сгенерированном `pac.txt` могут быть потеряны после обновления GeoSite.

Для Microsoft Store/UWP-приложений иногда требуется импортировать системный прокси в WinHTTP из консоли администратора:

```cmd
netsh winhttp import proxy source=ie
```

## Автоматическое переключение серверов

Доступны, в частности, следующие стратегии:

1. Load balancing — случайный выбор сервера.
2. High availability — предпочтение сервера с меньшей задержкой и потерями.
3. Total package loss — выбор по статистике доступности.

Собственную стратегию можно реализовать через интерфейс `IStrategy`.

## UDP

Если приложение не умеет напрямую использовать SOCKS5 UDP, для маршрутизации UDP-трафика через Shadowsocks может понадобиться SocksCap, ProxyCap или аналогичный инструмент.

## Несколько экземпляров

Для независимого запуска нескольких экземпляров разместите каждую копию Shadowsocks в отдельном каталоге и назначьте разные локальные порты. Идентификатор экземпляра вычисляется детерминированно по пути к исполняемому файлу, поэтому IPC и single-instance логика стабильны на современном .NET.

## Плагины

Путь к исполняемому файлу плагина задаётся в редакторе сервера и может быть относительным или абсолютным. При активном SIP003-плагине настройки Forward Proxy не используются.

Документация upstream по [плагинам, не соответствующим SIP003](https://github.com/shadowsocks/shadowsocks-windows/wiki/Working-with-non-SIP003-standard-Plugin).

## Глобальные горячие клавиши

Горячие клавиши могут регистрироваться автоматически при запуске. Для нескольких одновременно запущенных экземпляров Shadowsocks необходимо назначать разные сочетания.

- Установите фокус в поле и нажмите нужное сочетание клавиш.
- **Backspace** очищает назначение.
- Зелёный цвет означает успешную регистрацию.
- Жёлтый означает конфликт с другой программой.

## Разработка

Solution нацелен на `net10.0-windows`; приложение и тесты сохраняют x86-архитектуру.

```powershell
dotnet restore .\shadowsocks-windows.sln
dotnet build .\shadowsocks-windows.sln -c Release -p:Platform=x86
dotnet test .\test\ShadowsocksTest.csproj -c Release -p:Platform=x86
```

Подробности переноса находятся в [NET10-MIGRATION.md](NET10-MIGRATION.md).

## Основные managed-зависимости

| Компонент | Назначение |
| --- | --- |
| ReactiveUI / ReactiveUI.WPF | MVVM и WPF bindings |
| WPFLocalizeExtension | Локализация WPF |
| MdXaml / AvalonEdit | Отображение Markdown |
| Newtonsoft.Json | JSON-конфигурации и API |
| NLog | Логирование |
| Google.Protobuf | Модель данных GeoSite |
| ZXing.Net | QR-коды |
| GlobalHotKeyCore | Глобальные горячие клавиши |
| Caseless.Fody / Fody | IL-weaving для регистронезависимых сравнений строк |

## Встроенные native-компоненты

| Компонент | Архитектура | Назначение |
| --- | --- | --- |
| `libsscrypto.dll` | x86 | Native-криптография Shadowsocks |
| `privoxy.exe` | x86 | Локальный HTTP-to-SOCKS bridge |
| `sysproxy.exe` | x86 | Управление системным прокси Windows |
| `sysproxy64.exe` | x64 | Управление системным прокси на 64-битной Windows |

## Лицензия

Shadowsocks for Windows распространяется по лицензии [GNU General Public License v3.0](LICENSE.txt).

В репозитории также присутствуют сторонние компоненты, распространяемые по собственным лицензиям. Подробности смотрите в соответствующих upstream-проектах.
