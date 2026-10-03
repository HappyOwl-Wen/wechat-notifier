# 隐私复查（PHASE 6，2026-10-01，版本 0.6.0）

逐条对照规格 §14（隐私要求）和 §2（不注入、不改微信、不联网）复查代码和运行中的程序。数据清单和保存期限见 [privacy.md](privacy.md)。

## 结论

| 规格要求 | 结果 | 依据 |
|---|---|---|
| 不注入 DLL、不修改/打补丁微信、不 hook 微信内部函数 | ✅ | 只用公开 API：Shell hook（`RegisterShellHookWindow`）、WinEvent（进程外，只收窗口事件）、UI Automation 客户端、`PrintWindow`。没有 `CreateRemoteThread`、`WriteProcessMemory`、`SetWindowsHookEx` 全局钩子 |
| 不读微信网络流量、不破解 TLS | ✅ | 程序没有任何网络代码（见下） |
| 不读微信本地数据库或文件（Level 4） | ✅ | 不访问微信的数据目录；图片只从屏幕上复制（Level 3） |
| 不上传、无遥测/分析/崩溃上报/远程日志/云存储/外部 API | ✅ | 见「网络」「崩溃报告」 |
| 不持久保存聊天内容 | ✅ | 消息只在内存里经过；交给 Windows 的通知由 Windows 保存，最长 24 小时或到重启（见 privacy.md） |
| 临时图片在 LocalAppData，自动清理 | ✅ | `%LOCALAPPDATA%\WeChatNotifier\cache`，随机文件名；替换/撤回时删除，启动时清理过期的，最多 300 个；可在设置里关闭并立即清空 |
| 日志默认只有事件类型、时间、延迟、组件状态 | ✅ | 见「日志审计」 |
| 详细日志只在用户开启调试模式时 | ✅ | 「调试日志」开关；它只增加读取过程的计数和耗时，仍然不含内容 |
| 不需要管理员权限 | ✅ | 安装、运行、开机启动、通知注册、错误报告排除都只写当前用户（HKCU、LocalAppData）；清单是 `asInvoker` |

## 日志审计

逐个检查了源代码里所有写日志的地方（`DiagnosticLog.Debug/Info/Warn/Error`，共约 70 处）。插值进日志的只有：

- 固定的原因/状态字符串（如 `unread-increase`、`chat-window`、`taskbar`、`hidden icons`、`already open`）；
- 计数、尺寸（图片 `WxH`）、毫秒数、pid、窗口句柄、微信版本号；
- 异常类型名和 HRESULT；
- Windows 通知设置（`Enabled` 等）；
- 0.6.1 起，图片消息没有预览时：消息列表末尾的状态（「不在底部」「还在排版」「最新一条是 Text/Image」）和微信界面元素的类名（如 `mmui::ChatBubbleReferItemView`，只在遇到未知类型时）——界面结构，不是内容。

没有任何一处写入会话名、发送者、消息正文、窗口标题或图片。点击通知的日志只写「with conversation key」，不写 key 本身。

## 网络

| 检查 | 结果 |
|---|---|
| 源代码 | 没有 `HttpClient`、`Socket`、`WebRequest`、WinHTTP/WinINet 调用 |
| 运行中的后台程序（10 分钟空闲后） | 0 个 TCP 连接、0 个 UDP 端点；未加载 `ws2_32`、`winhttp`、`wininet`、`webio`、`mswsock`、`System.Net.*`（见 performance.md） |
| 设置窗口 | 同上；它和后台程序之间只用本机窗口消息（`WM_COPYDATA`），没有管道或端口。「Windows 通知设置」按钮打开的是本机的 `ms-settings:` 页面 |
| 安装程序 | 程序文件和 Windows App Runtime 的安装包都在 exe 里面，不下载任何东西；缺少 Windows App Runtime 时，用 Windows 自带的 PowerShell（`Add-AppxPackage`）把 exe 里的安装包为当前用户装上 |
| 依赖 | Windows App SDK 的使用数据走 Windows 自己的诊断数据通道，只含长度、不含内容（源码核对，见 privacy.md）；是否离开电脑由 Windows 的「诊断和反馈」设置决定。WinUI 包附带的 WebView2 程序集没有被使用 |
| 构建 | `scripts\package.ps1` 关闭了 .NET SDK 自己的使用数据（`DOTNET_CLI_TELEMETRY_OPTOUT=1`）；构建只在还原 NuGet 包时联网 |

## 崩溃报告

程序每次启动都把 `WeChatNotifier.exe` 加入当前用户的 Windows 错误报告排除列表：崩溃时进程内存（可能含刚处理过的消息）不会被打包上传。程序自己不写崩溃转储；未处理的异常只记录类型。

## 内存中的数据

| 数据 | 上限 | 何时清除 |
|---|---|---|
| 每个会话的状态（名称、预览、未读数） | 500 个会话 | 微信退出、重新附着 |
| 头像（复制来的像素） | 128 个；0.6.2 起也在 `avatars` 文件夹 | 内存：微信退出；磁盘：30 天没再复制到就删除；关闭头像或图片缓存、清除图片缓存、`--unregister`、卸载时立即 |
| 群聊判断（会话 key → 第一个发送者名字的哈希） | 500 个 | 微信退出 |
| 去重指纹（SHA-256 截断） | 256 条 | 进程结束 |
| 显示过通知的会话 tag（哈希） | 512 个 | 进程结束 |
| 等待缩略图的一条图片消息 | 1 条，最多 1.5 秒 | 加载完或超时就交给 Windows |

## 留在电脑上的东西和删除方法

| 东西 | 位置 | 删除 |
|---|---|---|
| 程序文件 | `%LOCALAPPDATA%\Programs\WeChatNotifier` | 卸载 |
| 开始菜单项 | `%APPDATA%\Microsoft\Windows\Start Menu\Programs\WeChat Notifier.lnk` | 卸载 |
| 卸载项 | `HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\WeChatNotifier` | 卸载 |
| 开机启动 | `HKCU\…\CurrentVersion\Run` 的 `WeChatNotifier` | 设置里关闭，或卸载 |
| 通知注册 | `HKCU\Software\Classes\AppUserModelId\WeChatNotifier` + `CLSID\{…}` | 卸载（`--unregister`） |
| 错误报告排除 | `HKCU\Software\Microsoft\Windows\Windows Error Reporting\ExcludedApplications` | 卸载 |
| 设置、日志、通知图标、图片缓存 | `%LOCALAPPDATA%\WeChatNotifier` | 卸载时可选；也可以直接删除整个目录 |
| 保留的头像（0.6.2） | `%LOCALAPPDATA%\WeChatNotifier\avatars`，文件名是会话 key（哈希） | 卸载时总是删除；`--unregister`、清除图片缓存、关闭头像或图片缓存时删除；30 天没再复制到就自动删除 |
| 通知中心里的通知 | Windows 的通知数据库 | 卸载时全部撤回；否则最长 24 小时或到重启 |

## 复查时发现并修正的问题

- 设置文件缺字段时，开关会被读成「关」（例如消息预览被关掉）：source-generated JSON 对 `init` 属性的行为。已改为普通属性，并有单元测试。
- 通知图标文件名固定，Windows 会继续显示缓存的旧图标：文件名改为带内容哈希。
- PHASE 3 的头像裁剪比实际头像大约 12%，通知里的圆形带进了列表背景：改为按像素找头像，并自己画平滑的圆。
