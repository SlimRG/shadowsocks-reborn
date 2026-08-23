# shadowsocks-reborn for Windows

<img src="Shadowsocks.Windows.WinUI/Shell/TrayAssets/ss32Fill.png" alt="Shadowsocks logo" width="32">

[English](README.md) | [Русский](README.ru.md) | **简体中文** | [中文使用说明](https://github.com/SlimRG/shadowsocks-reborn/wiki/ZH-User-Guide)

`shadowsocks-reborn` 是经典 Shadowsocks for Windows v4 客户端面向现代 Windows 的延续版本。当前构建基于 **.NET 10**、**WinUI 3 / Windows App SDK**，仅支持 **x64**，系统要求为 Windows 10 2004 / build 19041 或更高版本以及 Windows 11。

> 本项目是独立 fork，不是 `shadowsocks/shadowsocks-windows` 上游仓库。

如果没有任何完整配置的 Shadowsocks 服务器，依赖服务器的 DNS、PAC 以及启用 Windows 系统代理的操作会保持不可用；添加有效服务器后才会解锁。新的 DNSCrypt 配置首次使用时除 IPv6 外所有选项默认开启，但磁盘上已经保存的 DNSCrypt 设置在 schema migration 中不会被改写。Start on Boot、Administrator capture/UAC 切换和更新等耗时操作会显示明确的 busy progress。WinUI 导航与权限图标统一使用兼容 Windows 10 的 Segoe MDL2 Assets。

## 当前版本：5.2.31

5.2.31 已在 Administrator Mode 中实现 `System`、`Direct`、`Proxy`、`CustomDoh` 和 `DnsCrypt` 五种 DNS policy 的透明 DNS 拦截/路由，同时包含 DNSCrypt/DoH 管理（ODoH 保持关闭）、自动更新加固、开机启动重复实例保护以及当前 WinUI 3 界面。发布流程还会检查 .NET 安全基线、NuGet 漏洞审计、Windows 10 build 19041 兼容性以及最终 EXE 的版本元数据。

完整版本历史见 [更新日志](https://github.com/SlimRG/shadowsocks-reborn/wiki/ZH-Changelog)。

## 主要特性

- 原生 WinUI 3 桌面界面；产品 UI 不再依赖 WinForms/WPF。
- unpackaged、self-contained、win-x64、single-file 发布。
- 正式发布包中只包含 `Shadowsocks.exe`。
- 用户配置和可写数据统一存放在 `%LOCALAPPDATA%\Shadowsocks`。
- Clean Mode（EXE 文件名在 `.exe` 前以 `p` 结尾）把全部可写状态重定向到一次性的 `%TEMP%\Shadowsocks\Clean\...` 会话。
- 内置 HTTP/1.1 proxy 与 HTTPS `CONNECT`；Privoxy/sysproxy 已移除。
- 按应用设置 `Proxy`、`Direct`、`Block` 路由。
- Administrator Mode 通过 WinDivert 对 TCP/UDP 进行透明捕获。
- 自动 Game Mode 会在配置的游戏/程序运行期间暂停 Admin capture，退出后自动恢复。
- SIP003 插件管理：内置 `xray-plugin`、`v2ray-plugin`、`qtun` 选项，并支持手动导入 ZIP/TAR.GZ；导入需要已知但尚未安装插件的 `ss://` 时会先提示安装；受信任的内置目录插件在未被使用时每天自动检查一次更新，也可手动检查。
- UDP relay、二维码导入/导出、热键以及单一内嵌 CSV 本地化目录。

## 流量模式

只有两种可选流量模式：

- **User Mode** — 不需要提权，也不会解包 NetworkService。路由仅作用于进入本地/系统代理的流量。
- **Admin Mode** — 请求 UAC，将内嵌的 `Shadowsocks.NetworkService.exe` 按需释放到当前存储根目录，校验后提权启动，并通过 WinDivert 启用透明 TCP/UDP 捕获。

**Game Mode 是自动兼容状态，不是第三种流量模式。** 当选择 Admin Mode 且配置的游戏/程序启动时，WinDivert capture 会暂停；程序退出后自动恢复。

Traffic 页面会显示配置模式、运行模式、NetworkService、WinDivert、TCP/UDP capture 状态以及 redirect ports。

## 系统要求

- Windows 10 2004 / build 19041 或更高版本，或 Windows 11；
- x64 Windows；
- 从源码构建需要 .NET SDK 10.0.303 或更高版本（release build 使用 .NET 10.0.11 安全基线）。

正式发布版本为 self-contained，不要求用户另行安装 .NET Runtime。

## Solution 结构

- `Shadowsocks.Core` — 协议、加密、配置模型、PAC/GeoSite、路由模型、本地化和存储抽象。
- `Shadowsocks.Windows` — 文件存储 bootstrap、WinINet/system proxy、开机启动、UAC/Admin capture、WinDivert runtime、热键和其他 Windows 集成。
- `Shadowsocks.Windows.WinUI` — 独立的 WinUI shell/tray/QR/power 集成库，不依赖 Core/Windows ProjectReference。
- `Shadowsocks.WinUI` — WinUI 3 应用 shell 和正式发布项目（`Shadowsocks.exe`）。
- `Shadowsocks.NetworkService` — 内嵌到正式版本中的独立提权 WinDivert helper。
- `Shadowsocks.UnitTests` — 不依赖 presentation layer 的 Core/Windows 测试。

架构说明见 [架构](https://github.com/SlimRG/shadowsocks-reborn/wiki/ZH-Architecture)。

## 构建

`Directory.Build.props` 是唯一的三段式产品版本源；`ApplicationInfo.Version`、manifest 和 release notes 都会与其进行一致性校验。

在 Windows 上使用 .NET SDK 10.0.303 或更高版本：

```powershell
dotnet restore .\shadowsocks-reborn.sln -p:Platform=x64 -r win-x64
dotnet build .\shadowsocks-reborn.sln -c Release -p:Platform=x64 -m:1 --no-restore
dotnet test .\Shadowsocks.UnitTests\Shadowsocks.UnitTests.csproj -c Release -p:Platform=x64 --no-build
```

构建正式 ZIP 与 SHA-256：

```powershell
.\packaging\Validate-Repository.ps1
.\packaging\Build-Release.ps1
```

GitHub Release 的固定资源名为：

```text
Shadowsocks-win-x64.zip
Shadowsocks-win-x64.zip.sha256
```

最终 publish 目录与 ZIP 中只能有：

```text
Shadowsocks.exe
```

## 存储与开机启动

普通模式把持久化状态存放在：

```text
%LOCALAPPDATA%\Shadowsocks
```

配置文件为 `%LOCALAPPDATA%\Shadowsocks\settings.json`，并使用原子备份 `settings.backup.json`。插件安装在 `%LOCALAPPDATA%\Shadowsocks\Plugins`。内置目录插件会记录受信任的 GitHub release 来源，并可在未被使用时每天自动更新；如果当前 tag 与候选 tag 都能解析为版本，则更低的候选版本会被拒绝。手动导入的 ZIP/TAR.GZ 插件不会参与后台网络更新。本地化只使用内嵌在 `Shadowsocks.exe` 中的 `i18n.csv`。

将 EXE 名称改为在 `.exe` 前以 `p` 结尾即可进入 **Clean Mode**，例如 `Shadowsocksp.exe` 或 `Shadowsocks-cleanp.exe`。Clean Mode 会把 settings、cache、PAC、logs、插件、runtime/helper 工作目录统一放入 `%TEMP%\Shadowsocks\Clean\...`，退出时尽力清理。Clean Mode 不允许启用 Start with Windows。

普通模式下，Start with Windows 使用 `%LOCALAPPDATA%\Shadowsocks\Startup\Shadowsocks.exe` 的已校验稳定副本。启动时会迁移旧的 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 项、删除重复项；关闭开机启动时也会清理匹配的旧项。与 EXE identity 无关的进程级 single-instance guard 可阻止 startup copy 与原始 EXE 同时启动两个 controller 并争用同一端口。Windows Restart Manager 与 Start with Windows 保持互斥。两种机制都会持续用 `--start-visible` 或 `--start-hidden` 记录实际 UI 状态，因此重启/登录后会恢复关机前的状态：原先打开的窗口重新打开，原先仅在托盘中的实例仍保持在托盘。`WM_QUERYENDSESSION` 会在 Windows 关闭 HWND 之前保存状态，避免把系统关机误判成用户主动“关闭到托盘”。

详细规则见 [存储策略](https://github.com/SlimRG/shadowsocks-reborn/wiki/ZH-Storage-Policy)。

## 自动更新

下载资源先写入 `*.download`，文件句柄关闭后才在 Windows 上通过 `File.Move` 提升为正式临时文件。`.sha256` 支持两种格式：`<hash>  Shadowsocks-win-x64.zip` 或单独的 64 位十六进制 SHA-256。

应用默认在启动后检查 GitHub Releases，并且只接受**严格高于当前已安装版本的数字版本**（`5.2.22 < 5.2.31`，因此 5.2.22 会被忽略；`5.2.32 > 5.2.31`，因此 5.2.32 可以更新）。同时要求存在准确命名的 ZIP 与 `.sha256`，并校验 SHA-256、ZIP 布局以及 payload FileVersion。新 EXE 在 `%TEMP%\Shadowsocks\Updates` 中 staging；如果当前进程来自 LocalAppData 的旧 startup copy，更新检查会使用“当前进程版本”和“记录的主 EXE FileVersion”两者中的较高版本，因此旧 startup copy 不能把更旧的 release 当成更新。UAC handoff 期间 staged updater 会再次校验自身 SHA-256；在真正替换主 EXE 前还会重新读取目标 FileVersion，并拒绝相同或更旧的 payload，随后才执行 rollback-safe 替换、启动新版本并清理临时 transaction。

## Embedded NetworkService

Release build 会把 self-contained single-file 的 `Shadowsocks.NetworkService` 内嵌进 `Shadowsocks.exe`。

User Mode 不会解包 helper。Admin Mode 才会按需释放到当前 storage root，串行化 extraction，进行 SHA-256 校验，并通过 elevated control pipe 做版本握手。停止 broker 后会尽力删除 helper，旧 runtime 目录随后清理。

## WinDivert 验证

只有提权 capture 进程成功打开 WinDivert 后，Admin Mode 才会报告为 active。日志中会出现类似：

```text
WinDivert capture confirmed (start): Admin capture active.; TCP redirect port=..., UDP redirect port=...
```

功能测试可给 `curl.exe` 配置 `Block`，临时关闭 Windows system proxy，然后执行：

```cmd
curl.exe -4 --noproxy "*" https://example.com
```

在 User Mode 中直连请求应绕过 application routing；在 Admin Mode 中应被阻止。

## PAC、HTTP forwarding 与 DNS

Local GeoSite + EasyList/ABP 路由现在**始终由托管 C# 实现**。Local PAC 文件只包含一个最小的 `FindProxyForURL` funnel，用于把流量送入 `ManagedHttpProxyService`；PAC 本身不再执行过滤规则。先应用 application rules，再由 `Shadowsocks.Routing.FilterEngine` 中不可变的 GeoSite + `user-rule.txt` snapshot 作出权威 `DIRECT`/`PROXY` 决策。历史 `abp.js`、compiled-PAC 兼容后端、后端选择器以及可执行的自定义 `abp.txt` 覆盖均已删除；设置架构升级时，已删除的属性会在规范化迁移过程中自动清理。**Online PAC 是独立模式：**只有用户显式启用时，所选外部 PAC 程序才保持权威，因为任意 PAC JavaScript 无法无损转换为 ABP/EasyList 规则。 在 Local 模式下，**PAC / GeoSite** 页面提供 **Open user-rule.txt**，用户规则优先于 GeoSite 默认规则，并在文件变化后自动应用。启用 Online PAC 时会隐藏 Local managed routing、user-rule 和 GeoSite 控件。

Administrator Mode 会把同一 managed rule set 发送给提权 NetworkService。没有显式 application rule 的普通 TCP 在 SYN 阶段标记为 `Deferred` 并反射到 transparent relay；relay 只读取连接开头的明文路由元数据，提取 HTTP `Host` 或 TLS ClientHello SNI，然后选择直连或 Shadowsocks SOCKS5。**不会解密 TLS、不会安装证书，也不会进行 MITM。** HTTP 可按 path/query 匹配；TLS/SNI 只能按 hostname 匹配。显式 application rule 仍具有最高优先级。UDP、TCP DNS/53 以及无法获得 Host/SNI 的协议继续使用确定性的 fallback；连续规则更新会合并，并仅通过已提权 broker 重启 active capture child。

`ManagedHttpProxyService` 支持 HTTP/1.1 与 HTTPS `CONNECT`。HTTPS 仅进行 byte tunnel，因此 TLS 内协商 HTTP/2 不需要本地 HTTP/2 parser。未实现 FTP gateway。

### DNS policy 合约

5.2.31 的配置/IPC 包含五种 DNS policy。Administrator Mode 已通过 `Shadowsocks.NetworkService` + WinDivert 对 UDP/TCP 53 的经典 DNS 实现透明路由：

| Policy | 当前行为 |
| --- | --- |
| `System` | 保持捕获的经典 DNS 使用原始/系统 destination，不做 policy-specific redirect。 |
| `Direct` | 可保留原始 destination，或把 DNS 重定向到配置的主/备用 IPv4/IPv6 resolver；也可把所选 DNS endpoint 通过本地 Shadowsocks SOCKS5 路由。 |
| `Proxy` | 把捕获的经典 DNS 通过 Shadowsocks 转发。 |
| `CustomDoh` | 将捕获的 DNS wire message 桥接到指定 HTTPS DoH endpoint；DoH upstream 可独立选择是否通过 Shadowsocks。 |
| `DnsCrypt` | 重定向到动态分配的本地 `dnscrypt-proxy` listener；安全 runtime 不可用时采用 fail-closed。 |

因此，**5.2.31 已实现透明的系统级 DNS interception/routing**，但必须使用 **Administrator Mode**。User Mode 仍可使用 Shadowsocks 管理的 DNS/DNSCrypt，但不会拦截任意系统 DNS。Game Mode 暂停 Admin capture 时也会暂停系统级 DNS 拦截。透明拦截范围为 UDP/TCP 53 的经典 DNS；应用内部的 DoH、DoT、DoQ 不会被通用拦截。

DNS 页面可以管理可选且经过签名校验的 `dnscrypt-proxy`。`Automatic` 根据 DNSSEC、no-log、无过滤、IPv4/IPv6 等约束从签名公共目录选择具体 DNSCrypt/DoH resolver 并固定到 `server_names`。解析器目录维护首先通过 **Shadowsocks 上的 Cloudflare DoH** 解析源主机名，并以 **Shadowsocks 上的 Google DoH** 作为备用；目录与 `.minisig` 也通过同一隧道下载，并使用固定的 DNSCrypt Minisign 公钥验证。验证成功后才把本地认证缓存交给 `dnscrypt-proxy`。目录 profile 与活动 runtime 都使用 `bootstrap_resolvers = []` 和 `ignore_system_dns = true`，目录 profile 还使用 `urls = []`，因此 dnscrypt-proxy 无法退回远程 source lookup 或系统/明文 bootstrap。`Manual` 支持 DNSCrypt/DoH resolver 以及协议、国家、地址族和隐私过滤；ODoH 保持关闭。 DNSCrypt 组件自身的 release metadata、ZIP 与 Minisign 下载也使用这条 Cloudflare→Google DoH-over-Shadowsocks 路径，包括 GitHub redirect 主机，因此安装/更新不会有意依赖 Windows resolver 来解析 `api.github.com` 或 release assets。

DNS 页面还提供 `Test DNSCrypt` 与 DNS privacy self-test。loopback listener 端口会确认同一数字端口可同时用于 UDP/TCP。DNSCrypt PID/port 变化会实时同步到 NetworkService。启动、重启、恢复或 runtime 故障期间采用 fail-closed，不会把已拦截 DNS 静默降级到 plaintext system DNS。需要 DNS/proxy rewrite 的分片 datagram 会整体丢弃，防止后续 fragment 绕过策略；direct fragment 仍保持 direct。

WinDivert 2.2.2 只从固定官方 release URL 下载，并在加载前校验 x64 DLL/driver 的固定 SHA-256。read-only FLOW observer 提供 endpoint PID ownership，IP Helper 作为 fallback。

首次启用 DNSCrypt 时，DNS 页面会持续显示带动画的安装/验证操作卡片，而不是只留下被禁用且没有进度提示的设置界面。

DNSCrypt Automatic 优先选择与当前 Shadowsocks server 国家匹配的兼容 resolver，无匹配时选择签名目录中的最佳兼容项。resolver 国家只根据 endpoint IP 的 GeoIP 推导，不使用名称/description。Manual resolver latency 异步测量，活动 resolver 在可用时使用 dnscrypt-proxy 实际 RTT。组件自动更新检查最多每 24 小时一次；首次安装始终需要用户显式操作。如果 DNSCrypt 自身通过 Shadowsocks 路由，则 Shadowsocks/forward-proxy endpoint 必须使用 IP literal，以避免 DNS bootstrap 递归。

## 文档

- [中文使用说明](https://github.com/SlimRG/shadowsocks-reborn/wiki/ZH-User-Guide)
- [User guide (English)](https://github.com/SlimRG/shadowsocks-reborn/wiki/EN-User-Guide)
- [Руководство пользователя (RU)](https://github.com/SlimRG/shadowsocks-reborn/wiki/RU-User-Guide)

三个 release-facing README 文件会镜像保留在主仓库；长篇文档统一维护在 GitHub Wiki。

- [架构](https://github.com/SlimRG/shadowsocks-reborn/wiki/ZH-Architecture)
- [存储策略](https://github.com/SlimRG/shadowsocks-reborn/wiki/ZH-Storage-Policy)
- [Windows 11 / WinUI 指南](https://github.com/SlimRG/shadowsocks-reborn/wiki/ZH-Windows-11-WinUI-Guide)
- [参与开发](https://github.com/SlimRG/shadowsocks-reborn/wiki/ZH-Contributing)
- [发布检查清单](https://github.com/SlimRG/shadowsocks-reborn/wiki/ZH-Release-Checklist)
- [安全](https://github.com/SlimRG/shadowsocks-reborn/wiki/ZH-Security)
- [更新日志](https://github.com/SlimRG/shadowsocks-reborn/wiki/ZH-Changelog)

## 许可证

`shadowsocks-reborn` 使用 **GPL-3.0-or-later**。见 [许可证](LICENSE.txt) 与主仓库中的 [LICENSE.txt](https://github.com/SlimRG/shadowsocks-reborn/blob/main/LICENSE.txt)。第三方组件保留各自许可证，详情见 [第三方组件声明](THIRD-PARTY-NOTICES.txt)。
