# 微信 Windows 客户端 UI 分析（PHASE 0 结论）

> 数据来源：2026-09-30 在本机用 `tools/WeChatInspector` 实测。三轮真实收消息测试（另一账号发送 WXN-1 … WXN-14 + 1 张图片），外加空闲 CPU 对比测试。
> 文中所有聊天内容均已脱敏：`□` = 中文/其他文字，`x` = 拉丁字母，`◊` = emoji，`#xxxxxxxx` = 每次运行随机生成的 token（不是真实 ID）。测试标记 `WXN-n` 通过 `--reveal` 保留原文。
> 原始日志在本地 `inspector-output/`（已 gitignore，不含聊天原文）。

---

## 0. 结论速览

| 问题 | 结论（实测） |
|---|---|
| 微信能否被可靠监听？ | **能，但不能只靠 UIA 事件。** Weixin 4.1.13 的 UIA 树语义很完整，但 Qt 5.15 的 UIA 桥不发 `StructureChanged`，微信也不对会话/消息单元发 `Name` 变化，所以新消息到来时 **没有任何携带内容的 UIA 事件**。 |
| 哪种事件检测最快？ | 当前打开的会话：消息列表滚动条的 `RangeValue` 变化（UIA PropertyChanged），比任务栏闪烁早 40–70 ms。**所有会话通用**：`HSHELL_FLASH`（Shell hook 收到的任务栏按钮闪烁），可见、最小化、托盘隐藏三种状态都会触发。 |
| 哪种方式 CPU 最低？ | `RegisterShellHookWindow` 收 `HSHELL_FLASH`：空闲时零工作。不注册 UIA 事件处理器时，微信进程也没有额外开销。触发后只做定点读取（每次 1–20 ms）。 |
| 哪种方式最稳定？ | 「闪烁触发 + 按 AutomationId/ClassName 定点读取会话列表」。不依赖树层级和子节点索引，所有定位规则集中在 `WeChatUiProfile`。 |
| 能读到什么？ | 主窗口**可见**或**最小化**：会话名、未读数、最后一条预览（按列宽截断）、时间。**关到托盘（隐藏）**：只有会话名和未读数，**预览文字不可读**。 |
| 哪些版本可能失效？ | 见 §8。 |

**Listener 架构决定：Shell hook 闪烁触发 → 定点 UIA 读取会话列表（持有元素引用）→ 按会话差分 → 产生消息事件。** 详见 §9。

---

## 1. 测试环境

| 项目 | 值 |
|---|---|
| 系统 | Windows 11 Pro 10.0.26300，16 逻辑核，多显示器、缩放 125%，工具以 PerMonitorV2 DPI 感知运行 |
| 微信 | `Weixin.exe` 4.1.13.65（`C:\Program Files\Software\Weixin`）。Qt 5.15.14（Win32 类名前缀 `Qt51514`），UI 框架 `mmui::*` |
| UIA | `CUIAutomation8` / `IUIAutomation6`（NuGet `Interop.UIAutomationClient` 10.19041.0），MTA 线程，`AutoSetFocus=false` |
| 读屏标志 | `SPI_GETSCREENREADER = 0`。不需要打开「讲述人」：Qt 在收到第一个 UIA `WM_GETOBJECT` 时自动激活无障碍 |
| 工具 | .NET 10（SDK 10.0.401），WeChatInspector（本仓库 `tools/`） |

---

## 2. 进程与窗口

### 2.1 进程

| 进程 | 数量 | 角色 | 识别方式 |
|---|---|---|---|
| `Weixin.exe` | 6 | 1 个是 UI 宿主（拥有主窗口），5 个是同一可执行文件的辅助进程 | 版本资源 `CompanyName=Tencent`、`ProductName=Weixin`，再看谁拥有主窗口 |
| `WeChatAppEx.exe` | 10 | 小程序 / 内置网页运行时（RadiumWMPF，`%APPDATA%\Tencent\xwechat\XPlugin\...`），与聊天 UI 无关 | 已知辅助进程名单 |

- 进程名**只作为提示**（`Weixin`、`WeChat`、`WeChatApp`），身份以版本资源确认，`processes --deep` 还能发现被改名的 exe。一次扫描约 30 ms。
- **UI 宿主 = 拥有主窗口的那个进程**。只看进程名无法区分 6 个 `Weixin.exe`。

### 2.2 顶层窗口（UI 宿主进程）

| Win32 类 | 标题 | UIA ClassName | 说明 |
|---|---|---|---|
| `Qt51514QWindowIcon` | `微信` | **`mmui::MainWindow`** | 主窗口。**识别键用 UIA ClassName**：它与 Qt 版本和界面语言无关，而 Win32 类名里嵌着 Qt 版本号 |
| `Qt51514QWindowIcon` | 联系人名 | `mmui::ChatSingleWindow` | 独立聊天窗口。AutomationId = `ChatSingleWindow` + 账号 ID |
| `Qt51514QWindowToolSaveBits` | `Weixin` | `mmui::UnreadMessageHoverWindow` | 鼠标悬停托盘图标时**临时创建**的未读消息浮窗（内含 `List aid='notify_message_list'`），移开即销毁 |
| `Qt51514QWindowToolSaveBits` | `Weixin` | `QLabel` | 应用内提示气泡 |
| `Qt51514WxTrayIconMessageWindowClass` | `WxTrayIconMessageWindow` | – | 托盘图标回调窗口 |
| 子窗口 `MMUIRenderSubWindowHW` | – | Pane（Win32） | 主窗口内的硬件渲染子窗口 |

**主窗口 HWND 会变**：同一个微信进程生命周期内先后观察到 `0x306DC`、`0x4780D28`、`0x24D0A14` 三个主窗口句柄。之后多次托盘隐藏/显示时 HWND 保持不变，但 Listener 绝不能长期缓存 HWND，必须在窗口显示/创建事件时重新解析。

---

## 3. UI Automation 树（实测结构）

### 3.1 关键结构（主窗口可见时，约 150–160 个节点）

```
Window "微信"  cls=mmui::MainWindow  FrameworkId=Qt  hwnd
├─ Pane cls=MMUIRenderSubWindowHW (Win32 子窗口)
└─ Group QWidget › Custom QStackedWidget › Group aid=MainView cls=mmui::MainView
   ├─ ToolBar aid=MainView.main_tabbar cls=mmui::MainTabBar
   │    └─ Button "微信" cls=mmui::XTabBarItem  (+ Group cls=mmui::XBadge)   … 通讯录 / 收藏 / …
   └─ … aid=…main_window_main_splitter_view / …sub_splitter_view (mmui::XSplitterView)
        ├─ Group cls=mmui::ChatMasterView › … › Group cls=mmui::ChatSessionList
        │    └─ List "会话"  aid=session_list  cls=mmui::XTableView            [Selection]
        │         └─ ListItem  aid=session_item_<会话显示名>  cls=mmui::ChatSessionCell   [SelectionItem, Invoke]
        └─ Group cls=mmui::ChatDetailView › … › Group aid=chat_message_page cls=mmui::ChatMessagePage
             ├─ … Text aid=…current_chat_name_label  cls=mmui::XTextView       (当前会话名)
             ├─ Group cls=mmui::MessageView
             │    └─ List "消息"  aid=chat_message_list  cls=mmui::RecyclerListView   [Selection]
             │         ├─ ListItem cls=mmui::ChatTextItemView       Name=完整消息文本（含 emoji）
             │         ├─ ListItem cls=mmui::ChatBubbleReferItemView Name="图片"
             │         └─ ListItem cls=mmui::ChatItemView           Name="19:23"（时间分隔）
             └─ Edit aid=chat_input_field cls=mmui::ChatInputField   [Text, Text2, Value]
```

- 所有 mmui 元素都报告 `Invoke` + `Value`（Qt 默认），列表有 `Selection`，列表项有 `SelectionItem`。**没有** `Grid`/`Table`/`ScrollItem`/`ItemContainer`/`VirtualizedItem`。
- `RuntimeId = [2A, <hwnd>, 4, 0x8000xxxx]`：Qt 为每个 `QAccessibleInterface` 分配的 ID，控件存活期间稳定。
- 会话单元和消息单元都是**叶子节点**：没有子元素，头像、发送者、气泡都不单独暴露。
- `dump --bulk`：一次 `BuildUpdatedCache(TreeScope_Subtree)` 读完整棵树只需 31–34 ms；逐节点 TreeWalker 需 205–211 ms。

### 3.2 规格要求的元素对照

| 规格项 | 实测定位（全部在 `WeChatUiProfile`） | 备注 |
|---|---|---|
| 会话列表 | `AutomationId == "session_list"`（`mmui::XTableView`） | 只含**当前可见的行**，窗口高度决定 6–9 个单元 |
| 当前聊天 | `AutomationId` 以 `current_chat_name_label` 结尾的 `Text` | 也可用 `chat_message_page` 子树 |
| 消息列表 | `AutomationId == "chat_message_list"`（`mmui::RecyclerListView`） | 复用单元，会残留视口外的旧单元，必须按矩形与列表视口求交过滤 |
| 单条消息 | 消息列表的 `ListItem` 子项，类型由 ClassName 决定 | 没有发送者信息，无法区分收/发 |
| 联系人 / 群名称 | 会话单元 `Name` 第一行；`AutomationId = session_item_<显示名>` | 显示名可能重名 |
| 消息正文 | 会话单元的预览行（**会截断**，以 `…` 结尾）；打开的会话中 `ChatTextItemView.Name` 是全文 | |
| 图片消息 | 预览 `[图片]`；消息单元 `mmui::ChatBubbleReferItemView` + `Name="图片"` | 单元有 BoundingRectangle，可用于局部截图 |
| 未读数量 | 会话单元 `Name` 中单独一行 `[n条] ` | 免打扰会话和折叠夹（公众号）的预览本身也带 `[n条] ` 前缀 |

### 3.3 会话单元 `Name` 格式（核心解析规则）

```
<会话名>\n
[已置顶\n]
[[n条] \n]              ← 未读数（n ≥ 1 时出现）
<预览>\n                ← 群聊为 "发送者: 内容"；按列宽截断，以 "…" 结尾
<时间>\n                ← "19:44" / "昨天 15:44" / "星期一" / "09/21"
[消息免打扰\n]
```

实测样例（已脱敏）：

| 状态 | Name |
|---|---|
| 普通、有未读 | `xxxxxxxx⏎[4条] ⏎WXN-14⏎19:44⏎` |
| 置顶、图片 | `文件传输助手⏎□置顶⏎[图片] ⏎09/21⏎` |
| 群聊、免打扰 | `□□□□⏎xxxxx: □□□□⏎13:50⏎消息免打扰⏎` |
| 折叠夹 | `公众号⏎[5条] □□□□□: □□□…⏎19:18⏎` |
| **主窗口隐藏（托盘）时** | `xxxxxxxx⏎[1条] ⏎` ← **预览和时间两行被去掉** |

`WeChatInspector locate` 已按这些规则实现解析（`WeChatUiProfile.ParseSessionCell`）。

### 3.4 Qt 5.15 UIA 桥的两个决定性行为

1. **导航时跳过 `invisible` 子节点。** 主窗口关到托盘后，`FirstChild` 返回空，UIA 树只剩根节点，此时 `FindAll`/`FindFirst`/TreeWalker 都找不到任何内容。
2. **已持有的元素引用在隐藏后仍然有效**：可以直接读属性（不经导航），`SelectionPattern.GetCurrentSelection()` 也不做可见性过滤。但微信在隐藏时会从会话单元的 `Name` 中去掉预览和时间，文本消息单元的 `Name` 变为空字符串。

---

## 4. 事件实测（新消息到来时到底触发什么）

在主窗口子树（`TreeScope_Subtree`）注册了 18 个通用 Automation 事件 + StructureChanged + PropertyChanged（19 个属性）+ Notification，另加全局 FocusChanged、桌面 WindowOpened、进程级 `SetWinEventHook(EVENT_MIN..EVENT_MAX)` 和 `RegisterShellHookWindow`。

| 通道 / 事件 | 新消息时是否触发 | 说明 |
|---|---|---|
| UIA `StructureChanged` | **否** | 只在顶层窗口显示/隐藏时由 UIA core 发出。Qt 5.15 桥没有实现结构变化事件 |
| UIA `PropertyChanged(Name)` | **否** | 会话单元、消息单元都不发；只有输入框在切换会话时发 |
| UIA `PropertyChanged(RangeValue.Value)` | **是（仅限打开中的会话）** | 消息列表的两个滚动条（`QScrollBar` 和 `mmui::VirtualAreaScrollbar`）在该会话收到消息时变化，**可见、最小化、托盘隐藏都会发**。用户手动滚动时也会发 |
| UIA `Text_TextChanged` | 否 | 只有输入框（切换会话时草稿变化） |
| UIA `Notification` / `LiveRegionChanged` | 否 | 从未出现 |
| UIA `SelectionItem_*` / `Selection_Invalidated` | 否 | |
| UIA `Window_WindowOpened/Closed`、`FocusChanged` | 仅窗口操作 | 主窗口激活时也会发 WindowOpened |
| WinEvent `OBJECT_VALUECHANGE/NAMECHANGE/FOCUS` | 与 UIA 事件一一对应 | 这是 UIA→MSAA 桥转发的，idObject 为正数（桥内 ID），`AccessibleObjectFromEvent` 返回 `E_INVALIDARG`。**没有额外信息** |
| WinEvent `OBJECT_SHOW/HIDE/CREATE/DESTROY`、`SYSTEM_FOREGROUND`、`SYSTEM_MINIMIZESTART/END` | 仅窗口操作 | 用于跟踪窗口状态，很便宜 |
| WinEvent `OBJECT_LOCATIONCHANGE` | 与消息无关 | 用户操作时非常多（一轮测试约 5000 条），**Listener 不要订阅这个范围** |
| **Shell hook `HSHELL_FLASH`** | **是，所有会话、所有非前台状态** | 每条消息 4 次：窗口显示/最小化时约 310 ms 一次（中间夹 `HSHELL_REDRAW`），隐藏到托盘时约 625 ms 一次 |

### 4.1 时间线（第 1 轮，节选）

| 消息 | 窗口状态 | UIA 滚动条变化 | `HSHELL_FLASH` | 间隔 |
|---|---|---|---|---|
| WXN-1 | 可见、非前台，**非打开会话** | —（无） | 443.585 s | – |
| WXN-2 | 可见、非前台，打开会话 | 487.000 s | 487.042 s | 42 ms |
| WXN-3a / 3b / 3c | 同上（连续 3 条） | 506.608 / 510.222 / 516.764 | 506.674 / 510.275 / 516.824 | 66 / 53 / 60 ms |
| WXN-4 | **最小化** | 539.881 | 539.947 | 66 ms |
| WXN-5 | **托盘隐藏** | 555.424 | 555.486 | 62 ms |
| 图片 | 托盘隐藏 | 569.253（滚动范围 +416，图片单元更高） | 569.309 | 56 ms |

8 条消息产生 8 组闪烁，没有漏报，也没有重复组。

### 4.2 触发 → 读取（第 2、3 轮，`readprobe`）

| 状态 | 触发后立即读（+7–9 ms） | 触发后 +250 ms 再读 | 可读内容 |
|---|---|---|---|
| 可见、非前台（WXN-14） | 旧数据 | `xxxxxxxx⏎[4条] ⏎WXN-14⏎19:44⏎` | 会话、未读数、预览、时间 ✔ |
| 最小化（WXN-13） | 旧数据 | `xxxxxxxx⏎[3条] ⏎WXN-13⏎19:43⏎` | 会话、未读数、预览、时间 ✔ |
| 托盘隐藏（WXN-9/10/11） | `xxxxxxxx⏎[1条] ⏎` | 同左 | 只有会话名 + 未读数 ✘ 无预览 |
| 前台（WXN-8） | 不闪烁；滚动条事件后 14 ms 读到 `…⏎[1条] ⏎WXN-8⏎19:34⏎` | – | 默认不通知 |

**闪烁比会话列表更新更早，最多约 250 ms。** 所以读取必须带有限次重试（例如 +40/+120/+250/+400/+700/+1200/+2000 ms，读到变化即停）。只读一次会拿到旧数据。

---

## 5. 性能实测

| 项目 | 结果 |
|---|---|
| 微信进程空闲 CPU，没有 UIA 监听者（60 s） | 984 ms = **单核 1.64%**（微信自身开销） |
| 微信进程空闲 CPU，注册全套 UIA 监听（60 s） | 1016 ms = **单核 1.69%**，与上一行的差异在测量噪声内 |
| Inspector 全通道空闲监听（60 s） | 94 ms CPU = 单核 0.16%；峰值工作集 41.6 MB（含日志） |
| Inspector 全通道 + 祖先解析，~10 分钟有人操作 | 1953 ms CPU = 整机 0.021%；峰值工作集 68.7 MB |
| 捕获引用（FindFirst×2 + FindAll） | 13–31 ms |
| 读取 6–9 个已持有的会话单元 `Name` | 1–22 ms（通常 1–8 ms） |
| `locate`：一次 Subtree 缓存 + 内存中搜索 | 14 ms + 8 ms |
| 完整树 dump：Walk / Bulk | 205–211 ms / 31–34 ms |

这些是 Phase 0 的参考数据；正式的 `docs/performance.md` 在 Listener 完成后按规格重新测量。

---

## 6. 各检测方式对比（规格要求的三个问题）

| 方式 | 延迟 | 空闲 CPU | 覆盖 | 稳定性 |
|---|---|---|---|---|
| UIA 事件（Level 1） | 打开中会话最快（比闪烁早 40–70 ms） | ~0 | **只覆盖打开中的会话**；其他会话没有任何事件 | 依赖 Qt 桥的行为，滚动条事件和用户滚动混在一起 |
| WinEventHook（Level 2） | 同 UIA（桥转发） | ~0（要避开 LOCATIONCHANGE） | 同 UIA，没有额外信息 | 适合跟踪窗口生命周期 |
| **Shell hook `HSHELL_FLASH`** | 消息到达即触发，内容 ≤250 ms 后可读 | **0** | **所有会话，可见/最小化/托盘** | 公开 Win32 API（Windows 2000 起就有），依赖微信的默认闪烁行为 |
| 局部截图（Level 3） | – | – | 仅可见且在视口内的图片 | 只用于图片缩略图 |
| 本地数据库（Level 4） | – | – | 全部 | **不采用**：加密，需逆向，且微信升级后容易失效 |

- **最快**：打开中的会话用 UIA 滚动条事件；通用信号是 `HSHELL_FLASH`。实际到手内容的延迟由微信刷新会话列表的时间决定（≤250 ms）。
- **CPU 最低**：只用 Shell hook，不注册 UIA 事件处理器（这样 `UiaClientsAreListening()` 为假，微信的 Qt 不会为我们生成事件），触发后才做定点读。
- **最稳定**：闪烁触发 + 用 AutomationId/ClassName 定位 + 集中维护的 Name 解析器。

---

## 7. 点击通知、焦点与其他发现

- **URI 协议**：`HKCU\Software\Classes\weixin` 注册了 `URL Protocol`，命令为 `Weixin.exe "%1"`，但 Windows 端 `weixin://` 的路径含义没有公开文档，所以只能用于「激活微信」。跳转到指定会话可用 UIA（主窗口可见时对会话单元调用 `SelectionItem.Select()`/`Invoke()`）。PHASE 2 已按此实现（先 Invoke，读当前聊天标题核实，不对再 Select，仍不对就只把微信带到前台），见 [architecture.md](architecture.md) §4。
- **再次启动 `Weixin.exe` 不会激活已运行的微信**（PHASE 2 实测）：微信 4 支持多开，会打开第二个登录窗口。所以托盘状态下显示微信只能点它的托盘图标。Windows 11 外壳把托盘图标暴露为 UIA 按钮（AutomationId `NotifyItemIcon`），微信的按钮 Name 是「 微信」（前面有一个空格）；图标被收起时位于「显示隐藏的图标」弹出框（窗口类 `TopLevelWindowForOverflowXamlIsland`）中。微信的托盘消息窗口标题为 `WxTrayIconMessageWindow`（自有实现，不是 Qt 的 QSystemTrayIcon）。
- **焦点判断**：`SYSTEM_FOREGROUND` WinEvent 或 `GetForegroundWindow()` 与主窗口 HWND 比较。微信在前台时不闪烁，与规格的默认行为（前台不通知）一致。
- **托盘悬停浮窗** `mmui::UnreadMessageHoverWindow`：只在用户悬停托盘图标时存在，不能作为数据源（程序模拟悬停属于未公开的内部交互）。
- **开始菜单 AppID**：`{6D809377-6AF0-444B-8957-A3773F02200E}\Software\Weixin\Weixin.exe`（可用于显示信息，不用于注入或修改）。

---

## 8. 可能失效的场景 / 版本

| 场景 | 影响 | 应对 |
|---|---|---|
| **WeChat 3.x**（`WeChat.exe`，`WeChatMainWndForPC`，DirectUI） | UI 树完全不同，本分析不适用 | 需要单独的 Profile（已预留主窗口类名） |
| WeChat 4.0.x / 以后的 4.x | AutomationId、ClassName、Name 格式可能变 | 升级后运行 `WeChatInspector locate`，只改 `WeChatUiProfile` |
| 迁移到 Qt 6 | Win32 类名变化（不影响，我们用 UIA ClassName）；Qt 6 桥可能补上结构变化事件 | 反而可能出现更好的事件，届时重新运行 `events` |
| 会话单元 Name 的拼接格式变化 | 解析器失效 | 集中在 `ParseSessionCell`；`locate` 可立即暴露 |
| 微信不再闪烁任务栏，或用户在微信设置里关闭新消息提醒 | 主触发器消失 | 降级为打开中会话的 UIA 滚动条事件 + 窗口事件时的一次性读取；在诊断页中提示 |
| 主窗口重建（HWND 变化） | 引用失效 | 在 HSHELL_WINDOWCREATED / OBJECT_SHOW 时重新解析并重新捕获 |
| 窗口高度变化 | 会话单元被销毁/新建（实测 9→8→6 个单元） | 读取时遇到 `ELEMENTNOTAVAILABLE` 就重新捕获（仅在可见时可行） |

---

## 9. Listener 架构决定

```
                ┌──────────── 进程/窗口生命周期（全部事件驱动） ─────────────┐
 Shell hook  ──►│ HSHELL_WINDOWCREATED/DESTROYED/FLASH（任意进程）→ 发现微信  │
 进程句柄等待 ─►│ RegisterWaitForSingleObject(进程) → 微信退出                │
 WinEvent    ──►│ 微信 pid：OBJECT_CREATE..HIDE、FOREGROUND、MINIMIZESTART/END │
                └───────────────┬────────────────────────────────────────────┘
                                │ 主窗口可见/恢复/激活时
                                ▼
                 ReferenceSet：session_list + ChatSessionCell[]（+ chat_message_list）
                 （14–30 ms 捕获一次；隐藏后仍可直接读属性）
                                │
 HSHELL_FLASH（主触发） ────────►│ 定点读取：已持有单元的 Name（1–20 ms）
 （可选：前台模式下的 UIA 滚动条事件）│ 有限重试 +40…+2000 ms，读到变化即停
                                ▼
                 SessionDiff：按会话名比较（未读数↑ / 预览或时间变化）
                 → MessageEvent{conversation, groupSender?, preview?, kind, unreadDelta,
                                contentAvailable, timestamp, fingerprint}
                                ▼
                 Dedup（LRU 256）→ Phase 1: Console  → Phase 2: AppNotificationManager
```

原则：

1. **没有定时轮询。** 空闲时只有一个隐藏窗口在等 Shell hook 消息，外加一个内核等待句柄。
2. **默认不注册任何 UIA 事件处理器**，避免微信侧的事件生成开销。只有用户开启「微信在前台时也通知」时（前台不闪烁），才在前台期间订阅消息列表滚动条的 `RangeValue`。
3. **读取只针对已知元素**：会话单元的 Name，必要时加上打开会话的最后几个可见消息单元。不重扫整棵树；需要重建引用时用一次 `BuildUpdatedCache(Subtree)`，约 15–35 ms。
4. **内容可用性明确标注**：托盘隐藏时事件为 `contentAvailable=false`，通知显示「会话名 · n 条新消息」。
5. **重试和恢复都有退避上限**：UIA 调用设置 `ConnectionTimeout=2 s`、`TransactionTimeout=5 s`；引用失效时只在窗口可见时重建；进程重启走事件，不做忙等。
6. **所有微信相关规则只在 `WeChatUiProfile` 中**：进程提示、主窗口 UIA 类、AutomationId、单元 ClassName、Name 解析。

### 9.1 与规格优先级的对应

- 规格中的 Level 1（UIA 事件）在本版本微信上**不能作为主触发**（§4），改为用 UIA **定点读取**。
- 规格中的 Level 2（WinEventHook）用于窗口生命周期。
- 主触发 `RegisterShellHookWindow` 不在规格列表中，但它是公开的 Win32 API、纯事件驱动、零空闲开销，也不接触微信进程。风险：MSDN 标注它「不适合一般用途，可能在未来版本变化」，不过它自 Windows 2000 起一直保持稳定。
- 没有用到 Level 4、注入、Hook 微信函数或网络监听。

---

## 10. 已知限制与 Phase 1 待验证项

**已确认的限制**

1. **托盘隐藏时读不到消息正文**，只有会话名和未读数（Qt 跳过不可见控件，微信也从 Name 中去掉预览）。
   可选做法：(a) 接受「会话名 · n 条新消息」；(b) 用「—」最小化代替「×」关到托盘，内容可读；(c) 让主窗口开着放在其他窗口后面。
2. 如果微信启动后**从未打开过主窗口**（直接进托盘），Listener 还没有引用，只能发出通用的「微信 · 新消息」，直到用户第一次打开主窗口。
3. 会话预览按列宽**截断**；只有打开中的会话能从消息列表读到全文。
4. 消息单元没有发送者/方向信息；群聊发送者只能从预览中的 `发送者: 内容` 格式推断，需要确认会话确实是群聊。
5. 同一会话的连续多条消息如果在一次读取前都已到达，只能读到最后一条的预览加上未读增量（不会漏会话，但中间消息的文字拿不到）。

**Phase 1 必测**

- 群消息（本次没有共同群，只有已有群会话的预览样本）
- 免打扰会话是否闪烁；免打扰群里的 @ 提及
- 关闭微信「新消息通知」后是否仍闪烁
- 打开中的会话在微信非前台时，未读数是否增加（影响差分规则）
- 手机端发出的消息（出向）不应误报
- 置顶会话数量超过可见行数时，新会话是否落在可见区域
- 语音 / 视频 / 文件 / 链接等消息的预览（已见 `[图片]`、`[动画表情]`、`[视频通话]`）
- 睡眠/唤醒、锁屏、DPI 变化、Explorer 重启（`TaskbarCreated`）后 Shell hook 重新注册

---

## 11. 复现方法

```powershell
# 构建
.\scripts\build.ps1

$wi = ".\tools\WeChatInspector\bin\Release\net10.0-windows\WeChatInspector.exe"
& $wi processes                       # 进程与主窗口识别
& $wi windows                         # 顶层窗口 + UIA 根 + 子节点数
& $wi dump --bulk                     # 完整 UIA 树（脱敏）
& $wi locate                          # 用 WeChatUiProfile 规则定位并解析（升级后首先跑这个）
& $wi events --seconds 300 --ancestry # 录制全部事件通道，期间让别人给你发消息
& $wi readprobe --seconds 600         # 每次闪烁后测试能读到什么
```

输出默认写入 `inspector-output\<命令>-<时间>.log/.jsonl`，文字默认脱敏。`--reveal "WXN-\w+"` 只保留测试标记原文；`--show-text` 关闭脱敏，只应在本机调试时使用。
