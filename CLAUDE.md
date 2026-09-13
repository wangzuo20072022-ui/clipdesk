# ClipDesk — 粘贴板工具

一款常驻 Windows 桌面的剪贴板历史工具。平时缩成屏幕右上角一条几乎看不见的小横条，
鼠标顶上去停 0.3 秒展开成亚克力面板，显示最近 20 条复制记录；
按住 `Alt+V` 在鼠标处弹出九宫格，滑动鼠标选一格，松开即粘贴。

> 项目路径固定为 `D:\ClipDesk\`（纯英文，规避 MSIX 打包链路上的中文编码问题）。

---

## 一、产品规格

尺寸以 1920×1080 @100% 缩放为基准，**全部做成可配置**，不写死。

### 三个窗口

所有窗口统一：置顶、永不抢焦点、无边框、不显示在任务栏。

#### A. 收缩条 `StripWindow`

| 项 | 值 |
|---|---|
| 尺寸 | `40 × 4` 物理像素（≈1cm × 1mm） |
| 位置 | 屏幕右上角，距边 8px |
| 行为 | 鼠标**完全穿透**（点到它等于点到下面的窗口） |
| 材质 | 亚克力，**待 V2 验证**；不成立则退回分层窗口 + 半透明圆角色条 |

平时唯一可见的东西。

#### B. 主面板 `PanelWindow`

| 项 | 值 |
|---|---|
| 尺寸 | 面积 = 屏幕的 1/16。取整公式：**宽 = 屏宽 ÷ 8，高 = 屏高 ÷ 2** |
| 实例 | 1920×1080 → `240 × 540`（129,600 = 2,073,600 ÷ 16 ✓） |
| 位置 | 屏幕右侧上方，收缩条正下方 |
| 内容 | 竖向列表，最近 **20 条**历史，**滚轮滚动** |
| 条目 | 正文（多行省略）+ 右下角小字时间；悬停微微亮起 |
| 交互 | 单击某条 → 粘贴回用户原本的窗口 |
| 顶栏 | 细标题栏「粘贴板」+ 清空 / 退出小按钮（透明亚克力、白字） |

> 尺寸建议（可改）：公式忠实执行了「1/16 屏幕」，但 1920×1080 下得到 `240×540`，
> 高是屏幕的 50%，视觉上接近一根竖条。建议高改为屏高的 60%（`240×648`）手感更好。
> 阶段 2 实机看过再定。

#### C. 九宫格 `GridOverlay`

| 项 | 值 |
|---|---|
| 尺寸 | **边长 = √(屏宽 × 屏高 ÷ 9)**，忠实还原「屏幕的 1/9 大小」 |
| 实例 | 1920×1080 → `480 × 480`，单格 `160 × 160` |
| 位置 | 以鼠标当前位置为中心弹出，钳制在显示器工作区内 |
| 内容 | 格子内白色文字，超出省略号 |
| 顺序 | 第 1 格（左上）= 最近一次复制；**顺时针越远越旧** |
| 边线 | 极低透明度白色细线 |
| 交互 | 按住 `Alt+V` 出现 → 滑动鼠标（**指针隐藏**，高亮格按方向跳）→ 松开即粘贴；`Esc` 取消 |

**为什么「20 条」不填满 9 格**：九宫格只有 9 格，历史有 20 条，两者不通用。
**默认只显示最近 9 条**。「最上面是最近一次，顺时针越远越旧」在 9 格内完全成立。

---

## 二、硬约束（不可违反）

1. **不用全局钩子**（`WH_KEYBOARD_LL` / `WH_MOUSE_LL`）—— 避免杀软误报、避免商店审核麻烦
2. **亚克力窗口绝不用 `AllowsTransparency="True"`** —— 它会给 HWND 加 `WS_EX_LAYERED`，
   而 DWM 材质是插在非分层窗口客户区背后的，分层窗口自己提供整张位图 → 材质不渲染
3. **常驻 CPU / GPU 占用要低** —— 空闲时轮询频率必须为 0
4. **代码简洁，注释要够** —— 用户是代码小白，看得懂比写得妙重要
5. **剪贴板历史只在内存，退出即清空** —— 不落盘、不加密存储（因不存储）、不上传

---

## 三、技术方案

### 3.1 窗口统一写法

```xml
WindowStyle="None"       AllowsTransparency="False"   ResizeMode="NoResize"
ShowInTaskbar="False"    ShowActivated="False"        Topmost="True"
Background="{x:Null}"    <!-- 必须留 alpha=0，DWM 材质才有地方透出来 -->
```

配套 DWM 属性（**缺一不可**）：

| 属性 | 值 | 为什么 |
|---|---|---|
| `DWMWA_SYSTEMBACKDROP_TYPE` | `3` = `DWMSBT_TRANSIENTWINDOW` | 亚克力主路径，官方 API，Win11 22621+ |
| `DWMWA_BORDER_COLOR` | `DWMWA_COLOR_NONE` | Win11 默认给窗口画 1px 描边，在 4px 高的条上是灾难 |
| `DWMWA_USE_IMMERSIVE_DARK_MODE` | `1` | 不设则非客户区走浅色分支，与深色亚克力对不上 |
| `DWMWA_WINDOW_CORNER_PREFERENCE` | `DWMWCP_ROUND` | 抗锯齿圆角 |

降级路径：`SetWindowCompositionAttribute` + `ACCENT_ENABLE_ACRYLICBLURBEHIND`（Win10 1803+）。

⛔ **不用 `DesktopAcrylicController`**（WinAppSDK）—— 要在 WPF 手实现 COM 接口、
引入运行时依赖、MSIX 还要加框架依赖，为一个 5 行调用背一个 SDK 不值。

⚠️ **`SetWindowRgn` 与 DWM 圆角二选一，不混用**（region 是 1-bit，圆角会锯齿）。
region 只留给真·非矩形。

### 3.2 焦点与粘贴

三个窗口加 `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW`，且**扩展样式必须在窗口首次显示前
就位**（`SourceInitialized` 事件），显隐用 `ShowWindow(SW_SHOWNOACTIVATE)`。

这样 `GetForegroundWindow()` 全程不变 → 点击条目后直接 `SendInput` 模拟 Ctrl+V，
**不需要 `SetForegroundWindow` / `AttachThreadInput` 那套舞蹈**。

### 3.3 热键与轮询（三段式，替代全局钩子）

| 场景 | 频率 | 线程 |
|---|---|---|
| 空闲 | **0 Hz** | — |
| 常驻顶边停留检测 | **20 Hz** | 后台 `PeriodicTimer` |
| 九宫格激活期间 | **60 Hz** | 后台 `PeriodicTimer` |

- `RegisterHotKey(hwnd, id, MOD_ALT | MOD_NOREPEAT, VK_V)` 拿按下事件
- `GetAsyncKeyState(VK_LMENU / VK_RMENU)` 轮询松开 —— **不用 `VK_MENU`**（泛用键在 Alt 上有历史歧义）
- `GetCursorPos` 轮询方向；`GetAsyncKeyState(VK_ESCAPE)` 取消

⛔ **绝不用 `DispatcherTimer`** —— 每 tick 在 UI 线程跑，会持续唤醒 WPF 渲染管线，
这才是真正费电的部分。只在「高亮格变了」时 `Dispatcher.Invoke` 一次。

热键宿主用**专用的隐藏顶层窗口**（`HwndSource` 建，`WS_EX_TOOLWINDOW`），
不复用主面板 HWND（面板反复显隐，生命周期脆弱）。

### 3.4 三层铁律（架构约束）

- **`Platform\` 是唯一允许出现 `DllImport` 的文件夹** —— UI 层不写一行 P/Invoke
- **`Core\` 不许 `using System.Windows`** —— 状态机和格子映射是最易出 bug 处，必须能脱离 UI 单测
- **三个窗口的「样式组合」是数据不是代码** —— 一张常量表（AllowsTransparency /
  扩展样式 / 材质 API / 形状策略），每窗口从表里取。方便 A/B 实验，也方便将来降级

### 3.5 第三方库

引 `lepoco/wpfui`（WPF-UI，MIT），但**当「控件/主题库」用，不当「窗口库」用**：

- ✅ 用于 `PanelWindow` 的列表项模板、滚动条、按钮、Hover 态、深浅主题
- ❌ **不用它的 `FluentWindow` 作三个窗口的基类** —— 它自带标题栏/导航/Snackbar，
  我们三个窗口都不需要；且它内部的显隐与激活逻辑会和 `WS_EX_NOACTIVATE` 打架

`WindowEffects.cs` 的亚克力降级逻辑**自己写**，不依赖它的自动分支 ——
因为「透明效果被关时切降级主题」是我们的业务逻辑。

---

## 四、目录结构

```
D:\ClipDesk\
├─ CLAUDE.md                        ← 本文件
├─ README.md
├─ .gitignore
├─ global.json                      ← 锁 SDK 版本
├─ docs\
│   └─ 技术验证报告.md               ← S0~S4 的判定表，边跑边写
├─ verify\                          ← 阶段0 一次性验证程序（验完保留，不进主程序）
│   ├─ S0-Spine\                    ← 脊柱：不抢焦点 + 剪贴板 + 粘贴，一次全验
│   ├─ S1-Material\
│   ├─ S2-Strip\
│   ├─ S3-Cost\
│   ├─ S4-Msix\
│   └─ _archive\V1-BackdropMatrix\  ← 上一轮留下的，无定论，归档
├─ reference\                       ← 只读，不编译
│   └─ _probe\Rememory-main\        ← MIT，借用来源（见第五节）
└─ src\ClipDesk\
    ├─ ClipDesk.csproj              ← net8.0-windows, UseWPF, x64
    ├─ app.manifest                 ← PerMonitorV2 DPI
    ├─ App.xaml(.cs)                ← 单实例 Mutex、全局异常兜底、三窗口预热
    ├─ Assets\
    ├─ Platform\                    ← ★ 唯一允许 P/Invoke 的层
    │   ├─ NativeMethods.cs
    │   ├─ WindowEffects.cs         ← ★ 亚克力/圆角/边框/dark mode/扩展样式
    │   ├─ WindowVisibility.cs      ← ★ DWMWA_CLOAK 显隐（借 Rememory，见第五节）
    │   ├─ WindowShape.cs           ← SetWindowRgn + DPI 重算
    │   ├─ FocusGuard.cs            ← NOACTIVATE/TOOLWINDOW/HTTRANSPARENT/ShowNoActivate
    │   ├─ HotKeyService.cs         ← RegisterHotKey + 失败时返回备选键位
    │   ├─ HiddenMessageWindow.cs   ← ★ 热键 + WM_CLIPBOARDUPDATE 的专用宿主
    │   ├─ InputInjector.cs         ← SendInput Ctrl+V + 清理 Alt 菜单模式的收尾按键
    │   ├─ TargetWindowProbe.cs     ← 前台 HWND / 进程完整性级别（UIPI 预判）
    │   └─ MonitorLayout.cs         ← MonitorFromPoint / 工作区 / 物理↔DIP
    ├─ Core\                        ← ★ 纯逻辑，无 UI 依赖
    │   ├─ ClipboardWatcher.cs      ← 监听 + CLIPBRD_E_CANT_OPEN 重试 + 序列号去重
    │   ├─ HistoryStore.cs          ← 20 条环形缓冲、长度截断、去重合并（仅内存）
    │   ├─ Paster.cs                ← 「写回剪贴板 → 注入 Ctrl+V」完整用例
    │   ├─ EdgeTriggerDetector.cs   ← ★ 顶边+右上角停留 0.3s 状态机
    │   ├─ AltHoldTracker.cs        ← ★ Alt 按下/松开/超时/异常中断 状态机
    │   └─ GridSelection.cs         ← ★ 纯函数：鼠标向量 → 0..8 格子号 + 中心死区
    ├─ Views\
    │   ├─ StripWindow.xaml(.cs)
    │   ├─ PanelWindow.xaml(.cs)
    │   ├─ GridOverlay.xaml(.cs)
    │   └─ Controls\HistoryItemView.xaml(.cs)
    ├─ ViewModels\
    │   ├─ HistoryViewModel.cs
    │   └─ GridViewModel.cs
    ├─ Theming\                     ← 含「透明效果被关」时的降级主题
    └─ Services\
        ├─ AppShell.cs              ← ★ 三窗口显隐编排、预热、全屏/锁屏暂停
        └─ Settings.cs              ← 热键、阈值、尺寸（设置持久化，非剪贴板数据）
├─ tests\ClipDesk.Tests\            ← 只测 Core
└─ packaging\
    ├─ Package.appxmanifest         ← runFullTrust + windows.startupTask
    └─ README-打包.md
```

---

## 五、实施阶段

> **顺序的原则**：先做**脊柱**（能跑通完整闭环），再做**有退路的装饰**。
> 唯一的例外是 S0 —— 它是整个交互模型的地基，没有退路，必须先证。

### S0 脊柱 SpineProbe ← **从这里开始，唯一无退路的一步**

一个程序，做完整闭环，**丑不要紧**：

```
RegisterHotKey(Alt+V) → 弹出不透明普通小窗 → 列出剪贴板最近 5 条
   → 鼠标点一条 → SendInput Ctrl+V → 文字落回原来那个窗口
```

材质极简：深色半透明纯色，**不用亚克力**。不做动画，不做收缩条。一次答完五个问题：

| # | 问题 | 通过标准 |
|---|---|---|
| **Q1** | `NOACTIVATE` + `SW_SHOWNOACTIVATE` 下 `GetForegroundWindow()` 是否全程不变？**（唯一没有退路的一条）** | 弹出、点击、粘贴，前台句柄自始至终不变 |
| Q2 | `AddClipboardFormatListener` 能否稳定收到 `WM_CLIPBOARDUPDATE`？ | Chrome / 记事本 / VS Code / 资源管理器 各复制 5 次，全部捕获 |
| Q3 | `SendInput` Ctrl+V 是否落到原窗口？ | 记事本 ✅ / Chrome ✅ / VS Code ✅ |
| Q4 | 管理员权限窗口是否静默失败？ | 记录"失败且不报错"这一事实（S4 风险的依据） |
| Q5 | `RegisterHotKey(MOD_ALT\|MOD_NOREPEAT, V)` 是否成功？是否影响别的程序？ | 成功注册；记事本按 Alt+V 不出现菜单栏闪烁（或记录闪烁） |

**时间盒：一次会话。**

### S1 材质 MaterialProbe（原 V1，砍小）

在脊柱上换背景，三个候选**并排**、同一张彩色条纹底图：① DWM 新版亚克力 ② 旧版纯模糊
`ACCENT_ENABLE_BLURBEHIND` ③ 半透明深色圆角（**退路方案**）。

**一个问题**：三种里哪个最接近你要的"弱一点的模糊"？

**先看旧结论**：`verify\_archive\V1-BackdropMatrix\out\` 已有截图与探针程序，
开跑前先确认它是否已回答这个问题。**答了就不重跑。**

**时间盒：一次会话。** 答不上来 → 直接选方案 ③，记一笔，前进。

### S2 条子 StripProbe（原 V2）

40×4 这么小的窗口 DWM 还给不给材质、有无事实上限？150% 下小数 DIP 会不会让条子发虚？

**退路**：分层窗口 + 半透明圆角色条。**时间盒：一次会话。**

### S3 成本 CostProbe（原 V4）

20/60Hz 轮询下的 CPU 时间与电池掉电速率（**给"费不费电"一个数字答案**）；
快速轻点 Alt+V（50ms）能否完整捕获；Alt 松开会否让记事本/Word 菜单栏闪烁。
**时间盒：一次会话。**

### S4 打包 MsixProbe（原 V6，提前做）

提前的原因：它的失败模式最"意外"（比如 DWM 材质在 MSIX 下失效），
而清单形状已能从 Rememory 照抄。

打包后 DWM 材质 / `RegisterHotKey` / `SendInput` / 剪贴板监听是否照常？
`startupTask` 是否被商店接受？需要哪些 capability？x64-only 是否被接受？
**时间盒：一次会话。**

### 阶段 1~5（S0~S4 之后）

| 阶段 | 内容 |
|---|---|
| 1 剪贴板内核 | `ClipboardWatcher` + `HistoryStore` + `Paster`（无 UI） |
| 2 主面板 | `PanelWindow` 上线：亚克力、竖向列表、滚轮、点击粘贴 |
| 3 收缩条 | `StripWindow` + `EdgeTriggerDetector`。鼠标移开自动收起 |
| 4 九宫格 | `GridOverlay` + `AltHoldTracker` + `GridSelection`。**手感最难调，预留最多迭代时间** |
| 5 打包上架 | 先出 unpackaged exe，再包 MSIX |

---

## 六、验证协议（防止再烧三小时）

每个验证程序，**开跑前**先写下这四行，跑完立刻写判定：

```
问题：      <一句话，一个问题>
通过标准：  <可观察、可二值判断>
时间盒：    <一次会话 / 半小时>
退路：      <不通过时走哪条，为什么也能出货>
判定：      ⬜ 未跑 / ✅ 通过 / ❌ 不通过 → 走退路
证据：      <截图文件名 / 日志片段 / 数字>
```

**硬规矩**：

1. **时间盒到点没有明确答案 → 走退路，写判定，前进。** 不许"再试一个变体"。
2. 一个验证程序**只答一个问题**。想答第二个 → 单开一个。
3. 判定**先写进 `docs\技术验证报告.md`，再开下一个程序**。
4. 判定写完后回填到决策记录表（见第十节）。
5. **每做完一个小部分就汇报**，汇报内容 = 刚才那步的判定，不是过程叙述。
6. **不擅自扩大范围**：脊柱阶段不做动画，S1 阶段不做面板。

---

## 七、从 Rememory 借来的东西

`hpavlo/Rememory`（`reference\_probe\Rememory-main\`），**MIT 协议，已在 Microsoft Store 上架，
借用合法，商用与上架无碍。**

| 借什么 | 它的做法 | 我们怎么改 | 为什么改 |
|---|---|---|---|
| **显隐** | `DWMWA_CLOAK` 遮蔽 → `AppWindow.Hide()` → `Show(false)` 掩盖闪烁 | 保留 cloak，去掉 WinUI 的 `AppWindow` 调用 | `AppWindow` 是 WinUI3 专有；WPF 用 `ShowWindow` P/Invoke |
| **剪贴板** | `AddClipboardFormatListener` + 序列号去重 + 原子自写标志 + **重试 5 次 × 50ms** + 100ms 去抖 | **原样照搬**（C++ 改写为 C#） | 这套已经把 S9/S10 全盖住了 |
| **不抢焦点** | ❌ **它做不到** —— 靠 `SetForegroundWindow` 把焦点抢回来 | **我们比它更好**：`WS_EX_NOACTIVATE` 让前台全程不变，根本不用抢 | 它需要抢，因为它用的是可聚焦窗口 |
| **粘贴** | `SetForegroundWindow` → `Sleep(10)` → `SendInput` Ctrl+V | **只保留 `SendInput` 那半** | 省掉抢焦点，粘贴更干净 |
| **托盘** | `WinUIEx.TrayIcon` + 250ms 延迟区分单击/双击 | 换 `H.NotifyIcon.Wpf`，**逻辑照搬** | 库是 WinUI3 专有 |
| **DPI / 多显示器** | `MonitorFromPoint` + `GetMonitorInfo` + `GetDpiForMonitor` | **原样照搬** → `MonitorLayout.cs` | 纯 P/Invoke |
| **Explorer 重启** | `WM_TASKBARCREATED` 重建托盘图标 | 照搬 | 纯 Win32 |
| **关机重启** | `WM_QUERYENDSESSION` → `RegisterApplicationRestart("", 0x1011)` | 照搬 | 纯 Win32 |
| **热键** | ❌ 全程无 `RegisterHotKey`，只用 `WH_KEYBOARD_LL` | **不借，自己证**（Q5） | 我们禁全局钩子，这条路它没走过 |
| **MSIX 清单** | `runFullTrust` + `desktop:Extension windows.startupTask` | **形状照抄** → `packaging\Package.appxmanifest` | 修正旧稿写成 `uap5:` 的错误 |
| **存储** | SQLite 落盘 | **不借** —— 我们选内存 | 隐私优先 |

> **关于 `DesktopAcrylicController`（旧稿的争议到此关闭）**
> Rememory 用它是**因为它是 WinUI3 应用**，那是它的自然路径 —— 不是 DWM 做不到。
> DWM 的 `DWMSBT_TRANSIENTWINDOW` 产出同样的像素，代价是 5 行 P/Invoke。
> **为一个 5 行调用背一个 SDK，在任何口径下都不划算。**

---

## 八、风险与退路

| # | 级别 | 风险 | 退路 |
|---|---|---|---|
| **S1** | 致命 | **亚克力 × `SetWindowRgn` 是否共存未知**，不兼容则「非矩形 + 模糊」不成立 | ①收缩条放弃模糊，改分层窗口 + 半透明圆角色条（**推荐主方案**）②矩形 + DWM 圆角 |
| **S2** | 致命 | **首次显示延迟**：WPF 冷启动（JIT + 模板解析 + D3D 设备创建）200~400ms，「按住 Alt+V 立即出现」体感崩 | 启动时**预热全部三个窗口**：建 HWND、渲染一帧、`SW_HIDE`。热键路径上只做 `SetWindowPos` |
| **S3** | 高 | **滚轮可能收不到**：`WS_EX_NOACTIVATE` 窗口永不被激活，滚轮路由依赖系统设置「悬停时滚动非活动窗口」，用户可能关掉 | 启动读 `SPI_GETMOUSEWHEELROUTING`：为 2 则正常；否则退回「鼠标靠近面板上下边缘自动滚动」或底部画两个显式滚动按钮 |
| **S4** | 高 | **向管理员权限窗口注入 Ctrl+V 被 UIPI 静默拦截**（不报错、不生效） | 粘贴前检测目标进程完整性级别，高则**明确提示**用户以管理员身份重启。不绕过 UIPI |
| **S5** | 高 | **Alt+V 生命周期**：快速点按、Alt+Tab、热键被占用 | 显式状态机 + `MOD_NOREPEAT` + 注册失败给备用键位 + 超时取消 |
| **S6** | 中高 | **DWM 亚克力降级**：透明效果被关 / 节电 / RDP 时退化成不透明纯色 | 读 `HKCU\...\Personalize\EnableTransparency`，退化时自动切深色半透明纯色主题。**所有排版按「背景可能是不透明深色」设计** |
| **S7** | 中高 | **MSIX 下开机自启不能用注册表 Run 键**，只能用清单里 `desktop:Extension windows.startupTask` | **两阶段**：阶段一出 unpackaged exe（Run 键自启）；阶段二再包 MSIX |
| **S8** | 中 | **多显示器 / 负坐标 / 混合 DPI**：`GetCursorPos` 返回虚拟桌面坐标可能为负；40×4 物理像素在 150% 下是小数 DIP，会发虚 | manifest 声明 PerMonitorV2；尺寸以物理像素换算 DIP；监听 `WM_DPICHANGED` 重算 |
| **S9** | 中 | **`CLIPBRD_E_CANT_OPEN`**：别的程序短暂占用剪贴板时 `Clipboard.GetText()` 直接抛异常 | 重试 3~5 次、每次 20~50ms；失败跳过该条，绝不崩溃 |
| **S10** | 中 | **自写入回环 / 内存膨胀** | `GetClipboardSequenceNumber()` 去重；文本长度上限 128KB 超出截断；只收文本不存图片 |
| **S11** | 中 | **常驻轮询费电** | 三段式频率（0/20/60Hz）；后台线程非 UI 线程；锁屏、全屏、会话切换时完全停表 |
| **S12** | 中 | **单实例 / 崩溃自愈** | 命名 Mutex + `RegisterWindowMessage` 唤起已有实例；`DispatcherUnhandledException` 兜底记日志；处理 Explorer 重启、`SessionSwitch` |
| **S13** | 低 | **独占全屏游戏下 Topmost 失效** | 检测 `SHQueryUserNotificationState`，全屏则隐藏面板、暂停热键 |
| **S14** | 低 | **Alt 未被 `RegisterHotKey` 吞掉** → 记事本/Word 菜单栏闪烁 | 粘贴后补发无害按键取消菜单模式（V4 验证副作用） |
| **S15** | 低 | **`SetWindowRgn` 的 1-bit 锯齿** | 优先 DWM 圆角，region 只用于真·非矩形 |

---

## 九、上架 Microsoft Store

- **打包**：MSIX（Windows Application Packaging Project），full-trust 桌面应用，声明 `runFullTrust`
- **自启**：清单里 `desktop:Extension Category="windows.startupTask"`。**MSIX 下不能用注册表 Run 键。**
  清单形状直接照抄 `reference\_probe\Rememory-main\Rememory\Package.appxmanifest`。
- **账号**：Microsoft Partner Center。个人账号有一次性注册费用。
  ⚠️ **具体金额与是否支持中国大陆个人主体，未联网核实，需自行到 Partner Center 注册页确认**
- **隐私政策**（必填，商店审核会看）：

  > 本应用仅在内存中保留最近 20 条剪贴板文本，用于用户主动粘贴；
  > 数据不落盘、不加密存储（因不存储）、不上传、不共享。关闭应用即全部清除。

- 不得后台静默采集用户输入
- **安装便利性**：MSIX 由商店托管安装与自动更新，用户无需自行装 .NET 运行时

---

## 十、决策记录（唯一的事实来源）

**这张表的作用**：任何时刻你都能看出"哪些是已证的事实，哪些只是假设"。
**⬜ 的条目就是风险所在。** 每跑完一个验证程序，回来把对应行改成 ✅ 或 ❌。

| # | 决策 | 选择 | 理由 | 退路 | 证据 |
|---|---|---|---|---|---|
| D1 | 技术栈 | C# / WPF / .NET 8 | 常驻 40–80MB、空转 CPU≈0、MSIX 约 15MB | 无 | ✅ SDK 8.0.425 已装 |
| D2 | 亚克力路径 | `DWMWA_SYSTEMBACKDROP_TYPE = DWMSBT_TRANSIENTWINDOW` | 与 `DesktopAcrylicController` **产出同样像素，但零依赖、零额外开销** | 半透明深色圆角 | ⬜ S1 |
| D3 | 窗口显隐 | **`DWMWA_CLOAK` 切换**（借 Rememory） | 不销毁 HWND → 显隐瞬时、无重绘闪烁；正面解决 S2 | `ShowWindow(SW_HIDE/SW_SHOWNOACTIVATE)` | ⬜ S1 |
| D4 | **不抢焦点** | `WS_EX_NOACTIVATE` + `WS_EX_TOOLWINDOW`，扩展样式在**首次显示前**就位 | 前台窗口全程不变 → 免去 `SetForegroundWindow` / `AttachThreadInput` 那套舞蹈 | **无退路 —— 唯一的地基** | ⬜ **S0/Q1** |
| D5 | 热键 | `RegisterHotKey(MOD_ALT\|MOD_NOREPEAT, VK_V)` + `GetAsyncKeyState` 轮询松开 | 不用全局钩子（杀软误报 + 商店审核） | `Alt+\`` / `Ctrl+Shift+V` | ⬜ S0/Q5 |
| D6 | 剪贴板监听 | `AddClipboardFormatListener` + 序列号去重 + 重试 | 成熟路径，Rememory 同款 | 无 | ⬜ S0/Q2 |
| D7 | 粘贴 | `SendInput` 模拟 `Ctrl+V` | 唯一可靠路径 | 无 | ⬜ S0/Q3 |
| D8 | 历史存储 | **仅内存**，20 条环形缓冲，退出即清空 | 隐私最干净，政策可写得极短 | 无 | ✅ 已定 |
| D9 | 常驻轮询 | 三段式 0/20/60Hz；**绝不用 `DispatcherTimer`** | 每 tick 在 UI 线程跑会持续唤醒渲染管线，那才是真费电处 | 无 | ⬜ S3 |
| D10 | 托盘 | `H.NotifyIcon.Wpf` | Rememory 用 WinUIEx.TrayIcon，逻辑等价 | 不要托盘，只留热键 | ⬜ 待定 |
| D11 | **不用** | 全局钩子 / 亚克力窗口上开 `AllowsTransparency` / `DesktopAcrylicController` | 见 3.1 与第七节 | — | ✅ 已定 |

---

## 十一、验证方式

每个阶段结束由用户手动验收：

1. **S0 脊柱**：一个丑窗口 + 一份判定表 —— Q1~Q5 全部有明确答案
2. **S1~S4**：四份判定表 —— 每条都写了走哪条路
3. **阶段 1**：复制 25 段不同文本 → 列表只有 20 条、最旧的被挤掉、重复复制置顶不产生重复项
4. **阶段 2**：记事本里复制一段 → 打开面板 → 点该条 → 记事本里出现这段文字
5. **阶段 3**：鼠标从屏幕下方缓推向右上角 → 停住 → 数 0.3 秒后面板展开；移开 → 收起；面板展开时点下面的窗口仍能正常操作
6. **阶段 4**：按住 Alt+V → 九宫格出现 → 按住往左下拖 → 高亮格跟着走 → 松开 → 目标位置粘出对应文字；按 Esc → 取消不粘贴
7. **阶段 5**：本地装 MSIX 包，确认启动、常驻、开机自启

**每个验证程序跑完先写判定再前进**（见第六节）。

---

## 十二、开发环境

| 项 | 状态 |
|---|---|
| OS | Windows 11 Home China 10.0.26200 ✓（满足 `DWMSBT_TRANSIENTWINDOW` 要求的 22621+） |
| .NET SDK | **8.0.425 已装** ✓（`global.json` 已锁）；运行时有 7.0.20 / 8.0.0 / 8.0.31 |
| 编辑器 | VS Code |
| 参考项目 | `reference\_probe\Rememory-main\`（MIT，见第七节）<br>`D:\copy`（旧失败品，Electron + HTML 玻璃实验）—— **只作视觉参考，代码不搬运** |
| 已归档 | `verify\_archive\V1-BackdropMatrix\` —— 上一轮矩阵探针，无书面定论 |
