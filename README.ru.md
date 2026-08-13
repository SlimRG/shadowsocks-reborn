# Shadowsocks для Windows

<img src="shadowsocks-csharp/Resources/ssw128.png" alt="Логотип Shadowsocks" width="64">

[English](README.md) | **Русский** | [中文说明](https://github.com/shadowsocks/shadowsocks-windows/wiki/Shadowsocks-Windows-%E4%BD%BF%E7%94%A8%E8%AF%B4%E6%98%8E)

Этот репозиторий сохраняет архитектуру клиента Shadowsocks for Windows **v4** и переносит её на **.NET 10**. Основная цель — совместимость с современными Windows/.NET без ненужного изменения транспорта, шифрования, формата конфигурации, плагинов и логики выбора серверов.

## Возможности

- Системный прокси Windows в режимах PAC и Global.
- Локальные SOCKS5- и HTTP-прокси.
- Генерация PAC по GeoSite и пользовательские правила.
- SIP003-плагины и UDP relay.
- Стратегии переключения серверов.
- Импорт и экспорт через QR-коды и онлайн-конфигурации.
- Глобальные горячие клавиши.
- Локализованный WinForms/WPF-интерфейс.
- Просмотр журнала со встроенным графиком трафика.

## Требования и архитектура

- Target framework: `net10.0-windows10.0.19041.0`.
- Publish RID: `win-x86`.
- Для framework-dependent публикации требуется .NET 10 Desktop Runtime (x86).
- Основной процесс остаётся x86: встроенная `libsscrypto.dll` загружается в процесс и является 32-битной.
- `privoxy.exe` поставляется как отдельный x86-процесс.

Явная Windows-версия в TFM нужна текущему стеку ReactiveUI/System.Reactive, чтобы NuGet выбирал Windows-реализацию dispatcher scheduler.

## Сборка

Используйте .NET 10 SDK под Windows:

```cmd
dotnet restore .\shadowsocks-windows.sln
dotnet build .\shadowsocks-windows.sln -c Release -p:Platform=x86
dotnet test .\test\ShadowsocksTest.csproj -c Release -p:Platform=x86
```

Публикация:

```cmd
dotnet publish .\shadowsocks-csharp\shadowsocks-csharp.csproj -c Release -p:Platform=x86 -p:PublishProfile=FolderProfile
```

Результат:

```text
shadowsocks-csharp\bin\x86\Release\net10.0-windows10.0.19041.0\win-x86\publish\
```

Профиль создаёт framework-dependent, untrimmed, single-file публикацию.

## Конфигурация

Настройки приложения хранятся в `gui-config.json`, включая состояние окна журнала.

`Shadowsocks.dll.config` больше не используется. Старый механизм `ApplicationSettingsBase`/`app.config` удалён.

`Shadowsocks.pdb` для работы программы не обязателен. Во время тестирования его лучше оставлять, чтобы stack trace содержал имена исходных файлов и номера строк.

## Системный прокси

Старый механизм через `sysproxy.exe` / `sysproxy64.exe` удалён. Системный прокси теперь настраивается напрямую через WinINet.

Выбранный режим сохраняется в `gui-config.json`:

- Отключено: `enabled = false`
- Global: `enabled = true`, `global = true`
- PAC: `enabled = true`, `global = false`

При запуске и после внутренних reload Shadowsocks заново применяет сохранённый режим и считывает фактическое состояние Windows. Галочки меню и состояние tray отражают именно фактически применённый режим.

При полном выходе восстанавливается системный прокси, который был активен до запуска Shadowsocks. Выбранный режим Shadowsocks остаётся сохранённым и применяется при следующем запуске.

## PAC

PAC-правила генерируются по базе GeoSite проекта [v2fly/domain-list-community](https://github.com/v2fly/domain-list-community).

Пользовательские правила следует добавлять в `user-rule.txt`: сгенерированный `pac.txt` может быть заменён при обновлении GeoSite.

Основные параметры: `geositeDirectGroups`, `geositeProxiedGroups` и `geositePreferDirect`.

## Плагины и HTTP-прокси

Исполняемый файл плагина задаётся в редакторе сервера; поддерживаются относительные и абсолютные пути.

Запуск Privoxy синхронизирован с локальным HTTP-forwarder: обычный трафик не принимается до того, как Privoxy начнёт слушать порт. При reload активные forwarding handlers закрываются до остановки Privoxy.

Upstream-документация по [плагинам, не соответствующим SIP003](https://github.com/shadowsocks/shadowsocks-windows/wiki/Working-with-non-SIP003-standard-Plugin).

## UDP

Если приложение не умеет напрямую использовать SOCKS5 UDP, может потребоваться SocksCap, ProxyCap или аналогичный инструмент.

Отмена listener-операций при штатном stop/reload не считается ошибкой. Неожиданные socket failures по-прежнему записываются в журнал.

## Локализация

Классический WinForms-интерфейс использует `shadowsocks-csharp/Data/i18n.csv`. WPF-окна используют `shadowsocks-csharp/Localization/Strings*.resx`.

При добавлении пользовательского текста его нужно сразу добавлять в соответствующий источник локализации, а не оставлять жёстко заданной английской строкой.

## Диагностика

Для runtime-логов используется NLog. Реальные `Win32Exception` записываются вместе с native error code, HRESULT и stack trace.

`ERROR_PARTIAL_COPY (299)` игнорируется только в конкретном месте проверки чужого процесса, где такая cross-bitness ошибка ожидаема.

При отчёте об ошибке прикладывайте полный WARN/ERROR и stack trace. `Shadowsocks.pdb` рядом с EXE делает диагностику заметно полезнее.

## Встроенные native-компоненты

| Компонент | Архитектура | Назначение |
| --- | --- | --- |
| `libsscrypto.dll` | x86 | Встроенная криптография Shadowsocks |
| `privoxy.exe` | x86 | Локальный HTTP-to-SOCKS bridge |

Текущий порт не использует `sysproxy.exe`, `sysproxy64.exe`, Costura, `System.Windows.Forms.DataVisualization`, `System.Data.SqlClient` и `sni.dll`.

## Лицензия

Shadowsocks for Windows распространяется по лицензии [GNU General Public License v3.0](LICENSE.txt). Сторонние компоненты сохраняют собственные лицензии.
