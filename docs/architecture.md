# 架构（截至 PHASE 5）

依据：[wechat-ui-analysis.md](wechat-ui-analysis.md) 的实测结论（§9 的架构决定）。

## 1. 项目结构

```
src/
  WeChatNotifier.Core           消息模型 MessageEvent / MessageKind、指纹、去重 LRU；
    Notifications/              NotificationComposer：消息 → 通知文字（纯逻辑，无 Windows 依赖）
    Imaging/                    Bitmap32、PNG 编码、在复制的区域里找头像和图片、圆形头像（AvatarImage）（纯逻辑）
    Configuration/              AppSettings + SettingsStore（settings.json）、StartupCommand（开机启动命令行）、
                                ControlProtocol + StatusSnapshot（设置窗口 ↔ 后台程序）
  WeChatNotifier.Diagnostics    隐私安全日志（有大小上限、轮转）、ListenerMetrics、NotificationMetrics、TextRedactor
  WeChatNotifier.Listener       微信发现、Hook 线程、UIA 定点读取、会话差分、调度、点击后打开微信
    Profile/                    WeChatUiProfile + PreviewText：所有微信相关规则只在这里；
                                ShellTrayProfile：Windows 11 任务栏托盘区的结构（点击微信托盘图标用）
  WeChatNotifier.Notifications  Windows App SDK AppNotificationManager：注册、显示、撤回、点击、清除图片缓存
  WeChatNotifier.App            后台程序 WeChatNotifier.exe（PHASE 4）：托盘图标和菜单、开机启动、单实例、
                                设置、监听自动重启；NotifierHost 连接 Listener 与通知，TrayApp 是纯 Win32 界面
  WeChatNotifier.Settings       设置窗口 WeChatNotifier.Settings.exe（PHASE 5，WinUI 3）：单独的进程，只在窗口打开时存在
  WeChatNotifier.ConsoleHost    控制台宿主：打印 + 原生通知，保留作诊断工具（与 App 共用单实例锁）
tools/WeChatInspector           研究工具，与 Listener 共用 WeChatUiProfile 和脱敏器
tests/WeChatNotifier.Tests      xUnit v3：解析、差分、调度、去重、脱敏、日志上限、通知文字、图片、设置文件、开机启动命令
```

## 2. 线程模型

| 线程 | 职责 | 空闲时 |
|---|---|---|
| **Hook 线程** | 隐藏顶层窗口：`RegisterShellHookWindow`（`HSHELL_FLASH`、`HSHELL_WINDOWCREATED`）、`WM_POWERBROADCAST`、`TaskbarCreated`；WinEvent hook（全局 `SYSTEM_FOREGROUND`，以及只针对微信进程的窗口创建/显示/隐藏/销毁、最小化/恢复、移动缩放结束）。回调只投递一个工作项，不做任何 UIA 调用 | 阻塞在 `GetMessage` |
| **Listener 线程**（MTA） | 唯一的 UIA 客户端；处理工作项；按调度器安排的时间读取；处理通知点击 | 阻塞在 `BlockingCollection.TryTake(timeout)`。只有 burst 进行中、或正在处理一次点击（最多 4 s）时才有超时，平时无限等待 |
| **通知线程** | 调用 `AppNotificationManager.Show` / `RemoveByTagAndGroupAsync`，Listener 从不等待通知平台 | 阻塞在 `BlockingCollection` |
| COM 回调线程 | Windows 在用户点击通知时调用 `NotificationInvoked`；只取出参数 key 投递给 Listener | 不存在 |
| 进程退出等待 | `Process.Exited`：线程池在微信进程句柄上做内核等待 | 无工作 |
| **UI 线程**（App 主线程） | 隐藏顶层窗口：托盘图标和菜单、会话通知（锁屏/解锁）、`WM_ENDSESSION`、`TaskbarCreated`、显示/DPI 变化；另一次启动或 `--exit` 通过命名事件（线程池内核等待）投递消息过来 | 阻塞在 `GetMessage` |
| 对话框线程 | 诊断信息和提示用的 MessageBox，各自一个短命线程，托盘菜单在对话框打开时照常可用 | 不存在 |
| 设置窗口（另一个进程） | WinUI 3 UI 线程；通过 WM_COPYDATA 向后台程序的隐藏窗口发请求；用 FileSystemWatcher 跟随 settings.json；「高级」页显示时每 2 s 取一次状态 | 窗口关闭即进程退出 |

整个程序没有周期性定时器，也不做轮询。只有两种一次性定时器：「暂停通知」到期，以及 Listener 线程意外退出后的重启（退避）。

## 3. 消息检测流程

```
HSHELL_FLASH（任意进程）
  └─ 不是已附着的微信？→ 用版本资源确认是否为微信 UI 进程（结果按 pid+创建时间缓存）
       ├─ 不是 → 忽略（计数 ForeignFlash）
       └─ 是   → 附着（微信直接启动到托盘时，第一次闪烁就能发现它）
  └─ 微信在前台且未开启「前台也通知」→ 丢弃（微信前台时本来就不闪烁）
  └─ ReadScheduler.OnFlash：
       新 burst：+60/150/300/600/1000 ms 读取；每次后续闪烁 +250 ms 追加一次；最后一次闪烁后 +1500 ms 收尾
读取（Listener 线程）：
  1. 本次 burst 中闪烁过的独立聊天窗口 → 读该窗口最新一条消息，与基线比较
  2. 主窗口会话列表：
       可见 / 最小化 → 对持有的 session_list 做一次 FindAll(children)，一次跨进程调用，得到带预览的最新 Name
       隐藏（托盘） → 逐个读取持有的会话单元引用（Qt 导航会跳过不可见控件，但已持有的引用仍可读）
  3. SessionTracker.Apply：按会话名比较
       未读数增加                   → unread-increase
       未读数变小但不是 0            → unread-after-read（0.6.4：中间被读过——手机上、或微信去托盘前——程序没
                                       看到那个 0，之后又来了新消息；可见时预览是撤回提示的除外）
       打开中的会话预览/时间变化     → open-chat-preview（排除草稿、撤回提示）
       有未读的会话进入可见行         → appeared-with-unread
       未读数回到 0                  → 「已读」，用于撤回该会话的通知（不是新消息）；该会话的「已读次数」+1
       行序变化 / 隐藏导致预览消失    → 不是新消息
  4. 打开中的会话在窗口可见时：从消息列表读取完整文本（预览会按列宽截断）
  5. 免打扰会话默认不报告；去重 LRU（256 条，只存哈希）。指纹 = 会话 + 已读次数 + 未读数 + 预览 + 分钟
     （+ 打开中的会话的上一条预览）：读过之后未读数从 0 重新数，「已读次数」让读前读后的同样预览分得开（0.6.3）
  6. burst 开始 1000 ms 后仍无任何变化 → 报告通用「微信 · 新消息」
     （完全没有可读内容时立即报告）
延迟 = 检测时刻 − 最近一次闪烁。微信每条消息都会重新闪烁，所以连续消息中每条都按自己的闪烁计时
（PHASE 1 按 burst 的第一次闪烁计时，长 burst 后面的消息看起来晚了好几秒，实际并没有）。
```

## 4. 通知（PHASE 2）

```
MessageEvent（Listener 线程）
  └─ NotificationComposer.Compose（Core，纯逻辑，有单元测试）
       标题   = 会话名（通用事件为「微信」）
       正文   = 消息文字 / 微信占位符（[图片] 等）/ 托盘状态「新消息」「n 条新消息」；被 @ 时前缀 [有人@我]
       较早的行（0.7.0，ConversationLines，只在内存里）= 同一会话最近的消息，每条一行、旧的在上、最新的在最下，
                最多 3 行，第 4 条来时最旧的移出（FIFO）；读不到内容的（托盘、一次读到好几条）合成一行
                「[n 条新消息]」；比未读数还旧的行（在手机上读过）丢掉；会话读过（ConversationRead）或关闭
                「显示消息内容」时清空。暂停期间也记录，暂停后的第一条通知能看到期间的消息
       署名行 = 「n 条未读」（微信自己的计数）；通用事件为「可能来自「…」」
       Tag    = "c" + SHA-256(会话名) 前 12 字节 → 每个会话一条通知：新消息替换旧通知，
                连发 10 条只更新一个横幅，通知中心每个会话一项
       清理   = 文字去掉 XML 不能承载的字符，按字素截断（不切断 emoji），正文最多 512 字符
  └─ WindowsNotifier.Show → 队列 → 通知线程
       AppNotificationBuilder：参数只有 action=open 与 tag；声音 = Windows「即时消息」事件；
       ExpiresOnReboot = true，Expiration = 24 h
       文字：标题 + 较早的行（一个文本元素，换行分隔，每行由 ToastText 按估计宽度截成一行加「…」）+ 最新一条
       （单独一个元素，用剩下的行，超出加「…」）。Windows 的正文合计最多 4 行，超出的部分直接丢掉、不加标记，
       而且不理会 hint-maxLines（2026-10-02 实测：前面一条长消息换行后，最新一条整个不见了）。宽度估计：
       正文每个汉字 14 有效像素，头像旁一行实测 19 个汉字（266 epx），估计按 256 epx；Windows「文字大小」设置
       （HKCU\Software\Microsoft\Accessibility\TextScaleFactor）按比例缩小
       AppNotificationManager.Show
Listener 读到某会话未读数回到 0 → ConversationRead → RemoveByTagAndGroupAsync（只撤回本次运行显示过的）
```

外观（浅色/深色、强调色、辅助功能）、勿扰/专注模式、每个应用的通知设置都由 Windows 处理（规格 §13、§16）；代码里没有任何颜色或绕过机制。

### 头像、群消息、图片预览（PHASE 3）

UIA 不提供头像和图片的像素（会话单元、图片消息单元都没有子元素），所以这两样走规格的 **Level 3：只复制对应区域**。

```
复制（WindowCapture）：一次 PrintWindow(PW_RENDERFULLCONTENT)，DWM 交出窗口内容（被别的窗口挡住也行），
                      只保留需要的区域；最小化、托盘、其他虚拟桌面（cloaked）时不复制。从不定时复制。
                      Listener 线程按 Per-Monitor-V2 DPI 工作，UIA 矩形、窗口矩形、像素都是物理像素。
头像位置：复制会话单元左侧的整个正方形（SessionAvatarArea），再按像素找出头像（ImageAnalysis.FindAvatar）：
          与单元背景（取区域边框的中位色，悬停/选中色也适用）不同、覆盖超过一半的行和列组成的正方形块；
          未读角标（压在头像右上角并伸出去）和标题文字的开头覆盖不到一半，自然被排除。找不到（头像边缘和背景
          同色）→ 用上一个找到的头像相对单元高度的位置；从没找到过 → 估计值（单元高度 × 0.56，居中）。
          裁剪时去掉最外一圈像素（与列表背景混合的抗锯齿边）。
          PHASE 3 曾直接用 5/8 的固定比例：实际头像小约 12%，通知里的圆形因此带进了背景，看起来是带平边的多边形
头像何时复制：
  - 收到消息、读取时窗口在屏幕上 → 顺带复制该会话头像（与图片共用一次 PrintWindow）
  - 用户在微信里停留 1.5 s 后 → 复制所有可见会话中还没有头像的（最多每 30 s 一次）
  - 存在内存（AvatarCache，最多 128 个，6 h 后才重新复制），微信退出时清空；所以托盘状态的通知也有真头像。
    0.6.2 起同时写到 %LOCALAPPDATA%\WeChatNotifier\avatars\<会话 key>[.badge].png（PngEncoder；读回用 PngDecoder），
    程序重启后按需读回——以前每次重启（更新、开机、「重新启动监听」）都丢掉全部头像，微信最小化或在托盘时只能用
    首字头像，直到微信窗口再出现。30 天没再复制到的文件、超过 128 个时最旧的文件在第一次使用时删除；关闭头像
    或图片缓存、清除图片缓存（ClearAvatarsItem）、--unregister、卸载时删除整个文件夹
  - 复制时会话有未读角标 → 先用着，角标消失后（你读过之后）换成干净的；干净的从不被带角标的替换
  - 复制结果是纯色（未绘制的黑块）→ 丢弃；中间一半几乎都是列表背景色（ImageAnalysis.LooksLikeAvatar，0.7.1）→
    丢弃：复制的区域没对准这一行（2026-10-02 唤醒后 7 个里 6 个是这样，截到的是头像下面的空白和头像下沿）。
    已保存的副本读回时同样检查（背景取副本的四角：微信的头像是圆角的）。唤醒/解锁后 5 s 内和点击通知正在
    打开会话时不复制；批量复制后再读一次会话列表，有一行移动过就整批丢弃
  - 没有真头像 → 通知用会话名首字生成彩色头像（InitialAvatar，GDI 绘制，颜色由会话 key 决定）
圆形（AvatarImage.Round）：通知要用时，把头像用 Catmull-Rom 滤波缩放到通知实际画它的尺寸——48 有效像素 ×
          主显示器 DPI（ToastScale；125 % 下 60×60）——再按每个像素在圆内的面积（边缘 8×8 采样，64 级）设透明度，
          Windows 1:1 画出，边缘平滑。0.6.6 及以前做成 160×160 交给 Windows 缩小：它用简单滤镜，圆边成了台阶。
          不用 hint-crop="circle"：Windows 对约 50 px 的小图先裁圆再放大，边缘同样是锯齿。生成的首字头像先画成
          160×160（AvatarImage.SourceSize），再同样处理
图片预览：消息是图片、主窗口在屏幕上 → 先重新抓一次引用（一次 Subtree 缓存，约 20–35 ms；微信换过聊天页后，
          旧的消息列表引用什么也读不到——0.6.0 及以前图片预览常常失败的原因），再看该会话是否正打开；没打开时
          看有没有这个会话的独立聊天窗口。MessageTail 判断消息列表的末尾：可见区里最低的单元必须正好在列表底部
          （±4 px），否则列表往上翻着或还在滚动——这时最下面看得到的是旧消息，不能拿它的图片；最低的是时间分隔或
          还没有名字 → 还在排版。末尾是图片单元 → 复制「单元 ∩ 消息列表可见区」，去掉左侧头像列（72 逻辑像素），
          ImageAnalysis.FindPicture 找出与聊天背景不同的最大密集块（去掉昵称行）。纯色块和「加载中」占位（一种
          中性灰占 85 % 以上，可带小图标；白色文档截图不算）不算图片。
          还没有图片 → 先不通知：每 200 ms 重新读一次列表末尾、复制一次，图片出现就带图通知，1500 ms 后仍没有就只写
          「[图片]」；窗口离开屏幕、换了会话、聊天窗口关闭时立即放弃（PendingPicture；只在这段时间里有定时，其他
          消息照常立即通知；同一会话的新消息到来时，等待中的这条被新的取代，别的会话的消息到来时，它先不带图发出，
          保证顺序）。文字消息读全文时同样用 MessageTail；通过旧引用读到的末尾和预览对不上时，重新抓一次再读。
          找到后（PictureImage.ForNotification）：用 Catmull-Rom 一次缩放到正好是通知画它的宽度（0.7.3 以前先缩到
          ≤ 720×480，竖图被压到 480 高后又放大，变模糊；现在 4–12 ms）
          （364 有效像素 × 主显示器 DPI；小的放大、大的缩小，高度最多 1.5 倍宽），免得 Windows 用简单滤镜拉伸或缩小
          （缩小会出锯齿）；四角圆弧以外、和聊天背景同色的
          像素设为透明（微信的圆角气泡不再留灰角）。日志为每条图片消息写一行结果（有预览 / 等了多久 / 为什么没有）
群消息：标题 = 群名，正文 = 微信预览自带的「发送者: 内容」（规格 §6）；会话打开时用消息列表的全文并保留发送者前缀。
        「群聊消息」开关（PHASE 5）：UIA 没有群聊标志，所以 MessageEvent.IsGroup = 这条预览带「发送者: 」，或这个
        会话在本次运行中出现过两个不同的发送者（GroupEvidence：只在内存里，发送者只存哈希）。后者也适用于托盘状态
文件：通知图片只能是文件 → ImageStore 写 PNG（自带的小 PNG 编码器）到 %LOCALAPPDATA%\WeChatNotifier\cache，
      随机文件名；同一会话的新通知替换旧通知时删除旧图片，会话已读撤回时删除；启动时删除已过期（24 h）或
      上次重启前的文件；最多 300 个；--unregister 删除整个目录
```

### 部署方式

- 包：`Microsoft.WindowsAppSDK.Foundation` 2.3.12 + `InteractiveExperiences` 2.1.9 + `Runtime` 2.5.1，即 Windows App SDK 2.5.1 稳定版的组成部分。后台程序只用 Foundation；设置窗口另加 `Microsoft.WindowsAppSDK.WinUI` 2.3.9（它把 WebView2 的程序集带进输出目录，但设置窗口不用它）。不带 AI、ML。
- **非打包、依赖 Windows App Runtime 框架包**：使用系统里的 Windows App Runtime 2.5.1+（`Microsoft.WindowsAppRuntime.2` 框架包，加上 Main、Singleton 和 DDLM 包；`AppNotificationManager` 依赖 Singleton 包，所以自包含部署也省不掉它）。安装程序自带这四个包，缺少时为当前用户装上；.NET 运行时则随程序文件自带（自包含发布）。
- SDK 自带的自动初始化（缺运行时就弹窗并退出进程）被关闭；`WindowsAppRuntime.Initialize` 自己调用 Bootstrapper，失败时只关闭通知，控制台输出照常工作。
- 注册（只写 HKCU，不需要管理员）：
  - `HKCU\Software\Classes\AppUserModelId\WeChatNotifier`：DisplayName「微信」（设置「以『微信』的名义显示」，0.7.2 起默认打开；关掉后是「WeChat Notifier」）、IconUri 相应是从 `Weixin.exe` 读出的微信图标（`assets\WeixinIcon-<内容哈希>.png`，按标题栏的实际像素读取；微信没在运行时用上次读出的）或本程序的图标（`%LOCALAPPDATA%\WeChatNotifier\assets\AppIcon-<内容哈希>.png`，由内嵌资源写出；文件名随图标内容变化，因为 Windows 按路径缓存图标）、CustomActivator。图标（`scripts\make-icons.ps1` 生成）：通知标题栏把图标画成 16 有效像素宽，所以内嵌 16、20、24、28、32、36、40、48 px 八个尺寸（100–300 % 缩放，各自按像素网格绘制、不带细节的简洁气泡），启动注册时按主显示器的缩放选正好的那个（`ToastScale.HeaderIconSize`），Windows 1:1 画出——0.6.6 及以前只有一个 64 px 的，Windows 缩到 20 px 时圆边成了锯齿。Windows 在一次登录期间只读一次 IconUri、之后一直用那个路径，所以新路径从下次登录起生效，旧文件不能删（0.6.7 删了，标题栏图标就空了）：保留最近注册的 16 个（每个不到 2 KB）；exe/托盘的 ICO 每个尺寸单独按像素网格绘制，40 px 以上才有三个点。
  - `HKCU\Software\Classes\CLSID\{…}\LocalServer32` → 宿主 exe + `----AppNotificationActivated:`。
- 显式 AppUserModelID `WeChatNotifier`：exe 换目录后仍是同一个应用；激活服务器指向旧路径时自动重新注册。
- `--unregister`：删除注册和全部通知（App 还会先让运行中的实例退出，并删除开机启动和错误报告排除项）。
- 每个用户会话只允许一个宿主（命名 Mutex `Local\WeChatNotifier.Host`，App 和控制台宿主共用），否则每条消息会出现两次。

### 点击通知（规格 §17）

```
NotificationInvoked（COM 线程）→ 参数 key（= tag，会话名 SHA-256 的前 12 字节）→ listener.OpenWeChat(key)
  （不保存 key → 会话名的映射：Listener 对可见会话单元的名字做同样的哈希来找到会话）
Listener 线程：
  1. 微信没有公开文档的「打开某会话」协议（weixin:// 已注册，但路径含义未公开）→ 不用
  2. 把微信带到前台：
       可见 / 最小化 → (SW_RESTORE) + SetForegroundWindow（点击给了本进程前台权限）
       托盘中 → 通过 Windows 外壳的 UI Automation 点击微信的托盘图标，和用户（或读屏软件）点击完全一样；
                微信随后自己显示主窗口。图标在任务栏上就直接点；被收在「显示隐藏的图标」里时，
                先展开弹出框、点图标、再确认弹出框已收起（ShellTray + ShellTrayProfile，约 90 ms）。
                两个「微信」图标（多开）→ 不点，避免打开另一个账号
       微信已退出（且没有任何微信进程）→ 启动 Weixin.exe
     不会在微信运行时再次启动 Weixin.exe：微信 4 支持多开，再次启动会出现第二个登录窗口（PHASE 2 实测）；
     也不会从外部对隐藏的 Qt 窗口调用 ShowWindow（Qt 不知道窗口已显示，状态会错乱）
  3. 已知会话时：等会话列表出现（每 200 ms 重试，最多 5 s），找到唯一同名的会话单元
       → 0.6.3 起先像用户一样**单击这一行**（微信 4.1 对会话单元的 UIA Invoke / Select 都没有反应：
         2026-10-01 的 5 次跳转全部失败）。只在这些条件都满足时点击：微信是前台窗口；这一行中间一半在
         会话列表的可见区里（WeChatUiProfile.SessionClickPoint：行宽 60 % 处、行高中间，避开头像和时间）；
         那个点上没有别的窗口（WindowFromPoint 的根窗口是微信主窗口）。SetCursorPos + SendInput（左键按下/抬起），
         120 ms 后把光标放回原处（用户这期间动过鼠标就不动它）
       → 350 ms 后读当前聊天标题核实；不对或不能安全点击 → UIA Invoke → SelectionItem.Select（以后的微信版本可能支持）
  4. 会话不在可见行、重名或核实失败 → 放弃跳转，微信已在前台（规格 §27：稳定性优先）
```

这是 Listener 唯一会对微信界面（以及任务栏托盘区）执行操作的地方，而且只在用户点击通知之后；单击是一次普通的鼠标输入（不是微信的内部命令，规格 §17）。`WeChatInspector trayprobe` 用同一段代码检查托盘图标能否被找到（不点击微信），Windows 更新后可以先跑它。

## 5. 引用管理（托盘模式的前提）

只有主窗口**可见**时才能捕获引用：一次 `BuildUpdatedCache(Subtree)`，约 15–40 ms。捕获时机：

- 附着时、主窗口显示/创建时（`HSHELL_WINDOWCREATED`、`OBJECT_SHOW`）、从最小化恢复、移动/缩放结束；
- 用户离开微信（前台切到其他程序）且窗口仍可见时：这是隐藏到托盘前最后的可靠时机；
- 可见状态下的每次读取都会刷新引用。

burst 之外的捕获只更新基线，不产生事件（用户正在看或刚看过微信）；burst 之中的捕获同样参与检测。

失效处理：元素报 `UIA_E_ELEMENTNOTAVAILABLE` 等错误时丢弃引用；主窗口销毁或重建（HWND 变化）时重新解析；从睡眠恢复后丢弃所有引用（规格 §22）。

## 6. 后台程序（PHASE 4）

`WeChatNotifier.exe` 是 WinExe：没有控制台，也没有主窗口（规格 §11）。

```
启动
  ├─ --exit / --unregister → 命令行功能（见下），不启动
  ├─ 单实例锁已被占用 → 用户手动启动：打开设置窗口后退出（刚被用户启动的进程可以把窗口带到前台）；
  │                     设置程序缺失时改为通知运行中的实例（命名事件 Show）；开机自启或点击通知启动：直接退出
  ├─ 读取 settings.json（第一次运行 = 文件不存在）
  ├─ Windows 错误报告排除本 exe（WerAddExcludedApplication，按用户，不需要管理员）
  ├─ 开机启动：第一次运行按设置写入（默认开）；之后以 Windows 为准（任务管理器里被禁用就跟着关），
  │           登记的 exe 不存在了（程序被移动）才改指向本 exe
  ├─ NotifierHost.Start：通知注册 → Listener 启动 → 若是点击通知启动的，处理这次点击
  ├─ TrayApp：隐藏顶层窗口 + 托盘图标（NOTIFYICON_VERSION_4）+ 会话通知
  ├─ 手动启动时发一条安静的通知「已在后台运行…」（开机自启时不发；Windows 不显示本程序通知时改为对话框并说明原因）
  └─ 消息循环
```

托盘图标：**单击**打开设置窗口；**右键**菜单（规格 §11）：状态行、设置…、暂停通知（30 分钟 / 1 小时 / 直到手动
恢复，恢复时告诉你暂停期间收到几条）、发送测试通知、重新启动监听、诊断信息（规格 §24，无聊天内容，可 Ctrl+C
复制）、退出。PHASE 4 菜单里的各项开关都移到了设置窗口。

设置改动立即生效，**不重启 Listener**（重启会丢掉托盘状态下读会话列表所需的引用）：Listener 在两个工作项之间换上
新选项，关闭头像复制时同时清空内存里的头像；关闭图片缓存时立即删除缓存目录。托盘图标隐藏后，再运行一次 exe
就会打开设置窗口。

### 设置窗口（PHASE 5，规格 §18）

```
WeChatNotifier.Settings.exe（WinUI 3，非打包，依赖同一个 Windows App Runtime；单独的进程）
  打开方式：单击托盘图标 / 菜单「设置…」/ 再运行一次 WeChatNotifier.exe；已经打开时只把它带到前台
            （命名 Mutex Local\WeChatNotifier.Settings + 按窗口标题查找）
  页面：常规、通知、隐私、高级（NavigationView，Mica 背景，主题画刷：深色/浅色/高对比度跟随 Windows；无动画）
  改设置：写 settings.json（原子替换）→ WM_COPYDATA ReloadSettings → 后台程序读文件、应用（开机启动项也由它写，
          它没在运行时由设置窗口按 WeChatNotifier.exe 的路径写）
  托盘菜单改了设置：FileSystemWatcher 看到 settings.json 变化 → 设置窗口刷新（事件驱动）
  高级页：显示时每 2 s 发 StatusRequest，后台程序用 WM_COPYDATA 回一个 StatusSnapshot（JSON，只有状态、时间、
          计数和延迟）；测试通知、重新启动监听、复制诊断信息；后台程序没运行 → 提示 + 「启动」按钮
```

为什么是单独的进程：WinUI 3 加载后进程工作集约 190 MB。放在后台程序里，打开一次设置之后就一直占着，超出规格
§10 的 100 MB；单独进程关窗即退出，后台程序始终约 60 MB，也不会因为界面出错而停止通知。

通信只用本机窗口消息（ControlProtocol）：`WM_COPYDATA` 发到类名为 `WeChatNotifier.Tray` 的隐藏窗口，命令在
`dwData`；状态请求在 `wParam` 里带上应答窗口（设置进程里的一个 message-only 窗口），后台程序在处理请求的同时
`SendMessageTimeout` 回去。没有管道、端口或文件交换。

命令行：

| 参数 | 作用 |
|---|---|
| `--autostart` | 由 Run 键在登录时传入：不发启动通知 |
| `--exit` | 让运行中的实例退出并等待（最多 10 s）；安装/升级用 |
| `--unregister` | 先 `--exit`，再删除开机启动、通知注册、全部通知和图片、错误报告排除项；保留设置和日志 |

### 安装（PHASE 6）

```
scripts\package.ps1
  dotnet publish App + Settings → artifacts\publish\WeChatNotifier（同一个目录，win-x64，自包含 .NET，不裁剪，无 pdb）
  → payload.zip（约 44 MB）
  + Windows App Runtime 的 4 个 MSIX（NuGet 包 Microsoft.WindowsAppSDK.Runtime 的 tools\MSIX\win10-x64，约 47 MB）
  → 嵌入 WeChatNotifier.Setup（.NET Framework 4.7.2，Windows 自带）→ artifacts\WeChatNotifierSetup-<版本>.exe（约 91 MB）
WeChatNotifierSetup.exe（asInvoker；以管理员身份运行时拒绝：否则启动的通知程序也是提升权限的，Windows 不显示它的通知）
  检查：Windows 10 1809+；内嵌的每个 MSIX（读它的 AppxManifest）是否已为当前用户安装了同一版本或更新的
        （GetPackagesByPackageFamily，同一架构）——和微软的 WindowsAppRuntimeInstall.exe 的判断相同
  确认（--quiet 跳过；缺运行时就在确认里说明）→ 进度窗口（WinForms，工作在单独的 STA 线程上）
  → 缺的运行时包解到临时目录，框架包在前，powershell.exe Add-AppxPackage 逐个安装（为当前用户，不需要管理员；
    只有 Singleton 加 -ForceApplicationShutdown，和微软的安装程序一样）→ 再检查一遍，还缺就报错并指向微软下载页
  → 让运行中的通知程序退出（命名事件 Exit + 等单实例锁）、关闭设置窗口
  → 删除旧程序目录，解压到 %LOCALAPPDATA%\Programs\WeChatNotifier
  → 开始菜单快捷方式（IShellLinkW）→ HKCU 卸载项（UninstallString = WeChatNotifier.exe --uninstall）
  → 已有的开机启动项改指向安装后的 exe（不改它在 Windows 里的开/关状态）→ 启动 WeChatNotifier.exe
WeChatNotifier.exe --uninstall：--unregister 的全部内容 + 开始菜单项 + 卸载项；设置和日志经同意后删除；
  只有从安装目录运行时才删除程序目录（进程退出 2 秒后由 cmd 删除），开发构建目录永远不删
```

下载即用（0.8.0 起）：安装程序本身只依赖 Windows 自带的 .NET Framework 4.7.2，程序文件自带 .NET 运行时，
Windows App Runtime 也在安装程序里，所以电脑上不需要事先装任何东西，也不联网。代价是安装包从约 10 MB 变成约 91 MB。
Windows App Runtime 仍用系统里的包而不是自包含 Windows App SDK：`AppNotificationManager` 无论如何都需要系统里的
Singleton 包，自包含也省不掉它。安装程序不用 .NET 10：依赖框架的话它自己就需要 .NET 运行时，自包含又要再带一份运行时
（约多 70 MB），NativeAOT 需要 C++ 生成工具。

## 7. 生命周期

| 事件 | 处理 |
|---|---|
| 登录 | Run 键启动 `WeChatNotifier.exe --autostart`；任务栏还没准备好时，托盘图标等 `TaskbarCreated` 再加 |
| 启动时微信未运行 | 等待：`HSHELL_WINDOWCREATED` / `HSHELL_FLASH` 会带来微信的窗口，不轮询 |
| 微信启动 | 首个任务栏窗口或首次闪烁时附着 |
| 微信退出 | `Process.Exited` → 解除附着、清空状态 |
| 主窗口重建 | 按 UIA 类名 `mmui::MainWindow` 重新解析 |
| 睡眠 → 唤醒 | `WM_POWERBROADCAST` → 丢弃引用、重新检查进程和窗口（规格 §22） |
| 锁屏 → 解锁 / 远程重新连接 | `WM_WTSSESSION_CHANGE` → Listener 重新检查微信（不丢引用；锁屏时照常通知，锁屏上是否显示由 Windows 设置决定） |
| Explorer 重启 | `TaskbarCreated` → Listener 重新注册 Shell hook；托盘图标重新添加 |
| 显示器 / DPI 变化 | 按新 DPI 重新加载托盘图标；捕获本来就按每个窗口的 DPI 计算 |
| Listener 线程意外退出 | 5 s 后重启，再失败则 10 s、20 s …… 最长 10 min；稳定运行 10 min 后退避归零（规格 §21：不疯狂重试） |
| 注销 / 关机 / 安装程序的 Restart Manager | `WM_ENDSESSION` → 立即停止 Listener、注销通知激活，然后退出 |
| 程序未运行时点击通知 | Windows 按 `LocalServer32` 启动 `WeChatNotifier.exe`（没有窗口）；注册后取得这次点击并打开微信 |
| 程序退出 | `Unregister()`：此后的点击会启动新的进程 |

## 8. 隐私边界

- `MessageEvent` 和通知文字只在内存中，交给 Windows 后即不再保留。去重缓存只存 SHA-256 截断指纹。通知的 tag 和点击参数也是会话名的截断哈希；不保存哈希 → 会话名的映射。
- `settings.json` 只有开关，没有聊天数据。
- 通知内容由 Windows 保存在它的通知数据库中，直到被处理、撤回、过期（24 h）或重启；详见 [privacy.md](privacy.md)。
- `DiagnosticLog` 的调用约定：只记录组件、状态、计数、pid/HWND 和延迟，**从不记录**会话名、正文或窗口标题。文件在 `%LOCALAPPDATA%\WeChatNotifier\logs`，单文件 1 MB，最多 3 个。
- 没有任何网络代码。运行时核查（加载 Windows App SDK 之后）：0 个 TCP/UDP 端点，未加载 `ws2_32`/`winhttp`/`wininet`。
