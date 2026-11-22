# 开发指南

## 环境

- Windows 10 1809 / Windows 11，x64
- .NET 10 SDK：`winget install Microsoft.DotNet.SDK.10`（`global.json` 要求 10.0.100 或更新的 10.0.x）。脚本也认仓库内的便携 SDK `.tools\dotnet`（已 gitignore）。
- Windows App Runtime 2.5.1 以上，直接运行构建目录里的程序时需要（通知依赖它；装过一次 WeChat Notifier 就有了；缺失时后台程序会说明原因，控制台宿主只输出到控制台）

NuGet 源在仓库根目录的 `nuget.config` 中声明（只用 nuget.org），包缓存放在 `.tools\nuget-packages`。

## 构建、测试、打包

```powershell
.\scripts\build.ps1                                   # 构建整个解决方案（Release）
dotnet test --project tests\WeChatNotifier.Tests -c Release
.\scripts\package.ps1                                 # → artifacts\WeChatNotifierSetup-<版本>.exe
```

- `build.ps1` 会先让构建目录里正在运行的 `WeChatNotifier.exe` 退出（否则文件被占用），构建成功后再启动它（`-NoRestart` 不启动）。
- `package.ps1` 发布后台程序和设置窗口（win-x64，自带 .NET 运行时，不裁剪）并压成 zip，连同 Windows App Runtime 的 MSIX 包（取自 NuGet 包 `Microsoft.WindowsAppSDK.Runtime`，版本和 `WeChatNotifier.Notifications.csproj` 引用的一致）内嵌进安装程序 `WeChatNotifierSetup.exe`，并打印大小和 SHA-256。
- 安装程序以 .NET Framework 4.7.2 为目标（Windows 10 1809 起自带），所以任何电脑都能直接运行。它和后台程序共用的几个源文件（`InstallLayout`、`ControlProtocol`、`StartupCommand`、`StartupRegistration`）以链接方式编进安装程序，改它们时要保证在 .NET Framework 上也能编译。
- 版本号在随安装程序发布的三个项目里：`src/WeChatNotifier.App`、`src/WeChatNotifier.Settings`、`src/WeChatNotifier.Setup` 各自的 `.csproj`（`<Version>`）和 `app.manifest`（`assemblyIdentity` 的 `version`，多一位 `.0`），六处一起改。`scripts\release-notes.ps1` 会检查它们是否一致。
- 图标由 `scripts\make-icons.ps1` 生成（Windows PowerShell + System.Drawing），结果已提交：`src/WeChatNotifier.App/Assets/AppIcon.ico` 和 `src/WeChatNotifier.Notifications/Assets/HeaderIcon-*.png`。

推送到 GitHub 后，`.github/workflows/build.yml` 在每次推送时构建、跑测试、打包，并用 `scripts\release-notes.ps1` 写发布说明：在 `scripts\release-notes.template.md` 里填上安装程序的文件名、大小、SHA-256，以及 `CHANGELOG.md` 里这个版本的一节（相对链接换成指向该标签的绝对链接）。六处版本号不一致，或者 `CHANGELOG.md` 里没有这个版本时，构建失败。推送 `v*` 标签（例如 `v0.8.1`）时，把安装程序连同这份说明发布到 GitHub Releases。

发布新版本：

1. 改六处版本号；在 `CHANGELOG.md` 顶部写这个版本的一节，标题形如 `## 0.8.2（2026-12-01）`
2. 本地预览：`.\scripts\package.ps1`，再 `.\scripts\release-notes.ps1`
3. 提交、推送 main，等 Actions 通过
4. `git tag -a v0.8.2 -m "WeChat Notifier 0.8.2"`，`git push origin v0.8.2`：Actions 发布 Release

## 从构建目录运行

```powershell
.\src\WeChatNotifier.App\bin\Release\net10.0-windows10.0.19041.0\WeChatNotifier.exe
```

和安装版一样：托盘图标、开机启动、通知。命令行：`--exit`（让运行中的程序退出）、`--unregister`（退出并删除开机启动、通知注册、通知、图片缓存和错误报告排除项）。不要以管理员身份运行：Windows 不显示来自提升权限进程的通知。

## 控制台宿主（诊断）

`src/WeChatNotifier.ConsoleHost`：同一个 Listener 和通知，但把每个检测到的事件打印到控制台。和后台程序共用单实例锁，不能同时运行（先在托盘菜单里退出）。不随安装程序发布。

```powershell
.\src\WeChatNotifier.ConsoleHost\bin\Release\net10.0-windows10.0.19041.0\WeChatNotifier.ConsoleHost.exe
```

| 参数 | 作用 |
|---|---|
| `--test-toast` | 启动时弹一条测试通知 |
| `--no-toast` | 只输出到控制台 |
| `--no-preview` | 通知只显示会话名，不显示消息内容（也不显示图片） |
| `--no-avatar` / `--no-image-preview` | 通知不带头像 / 图片消息只显示「[图片]」 |
| `--no-image-cache` | 不把复制来的头像、图片写到磁盘（只用生成的首字头像） |
| `--silent` / `--long` | 通知不发声 / 横幅停留约 25 秒 |
| `--no-open-chat` | 点击通知只把微信带到前台，不打开会话 |
| `--unregister` | 从 Windows 删除本程序的注册和全部通知，然后退出 |
| `--redact` | 控制台只显示文字形状（`□`/`x`/`◊`），可配 `--reveal <正则>`；不影响通知 |
| `--verbose` / `--debug-log` | 在控制台显示 / 只在日志文件里写诊断细节（都不含聊天内容） |
| `--notify-muted` | 也报告免打扰会话 |
| `--self-test` | 附着后模拟一次闪烁（不会对微信做任何操作），跑一遍读取流程后退出 |
| `--seconds <n>` / `--stop-event <名称>` | 自动结束 |
| `--no-log` | 不写 `%LOCALAPPDATA%\WeChatNotifier\logs\listener.log` |

## WeChatInspector（研究工具）

`tools/WeChatInspector`：PHASE 0 的研究工具，和 Listener 共用识别规则（`src/WeChatNotifier.Listener/Profile/`）。**微信或 Windows 升级后，先用它检查规则是否还对得上。**

```powershell
$wi = ".\tools\WeChatInspector\bin\Release\net10.0-windows\WeChatInspector.exe"
```

| 命令 | 作用 |
|---|---|
| `& $wi locate` | 用 `WeChatUiProfile` 规则定位并解析会话列表、当前聊天和消息列表。**微信升级后先跑这个** |
| `& $wi processes` / `windows` | 识别微信进程、主窗口和顶层窗口 |
| `& $wi dump [--bulk]` | 导出 UIA 树（AutomationId、Name、ControlType、ClassName、矩形、RuntimeId、Pattern） |
| `& $wi events --seconds 300` | 同时录制 UIA 事件、WinEvent 和 Shell hook |
| `& $wi readprobe --seconds 600` | 每次触发后测试当前窗口状态下能读到什么 |
| `& $wi trayprobe` | 按「点击通知」的同一段代码查找微信的托盘图标（必要时展开再收起「显示隐藏的图标」，不点击微信）。**Windows 更新后先跑这个** |
| `& $wi captureprobe` | 按通知的同一段代码复制可见会话的头像并检查（只保存系统会话的标准图标）。**微信改版后先跑这个** |

输出默认脱敏，写入 `inspector-output\`（已 gitignore）。`--show-text` 关闭脱敏，只在本机调试时使用。

## 文档

性能微基准：`dotnet run --project tools/WeChatNotifier.Performance -c Release`。只处理合成数据，测量会话缓存和 PNG 的耗时/分配量，并检查通知突发合并；不连接微信、不弹通知。前后对比、环境设置和测量边界见 [performance.md](performance.md)。

| 文件 | 内容 |
|---|---|
| [wechat-ui-analysis.md](wechat-ui-analysis.md) | PHASE 0：微信 4.1 的 UI Automation 结构、事件、可读到什么 |
| [architecture.md](architecture.md) | 线程、检测流程、通知组装、头像与图片、点击打开会话、部署 |
| [limitations.md](limitations.md) | 已知限制、原因和应对 |
| [privacy.md](privacy.md) / [privacy-review.md](privacy-review.md) | 程序读写哪些数据、保存在哪里、多久；隐私复查 |
| [performance.md](performance.md) | 延迟、CPU、内存的测量 |
| [testing.md](testing.md) | 手动测试清单（需要真实的微信消息） |

这些文档是开发过程中的工程笔记，按开发阶段（PHASE 0–6）组织。
