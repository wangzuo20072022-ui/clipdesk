# ClipDesk — 粘贴板工具

一款常驻 Windows 桌面的剪贴板历史工具。平时缩成屏幕右上角一条几乎看不见的小横条，
鼠标顶上去停 0.3 秒展开成亚克力面板，显示最近 20 条复制记录；
按住 `Alt+V` 在鼠标处弹出九宫格，滑动鼠标选一格，松开即粘贴。

> 项目路径固定为 `D:\ClipDesk\`（纯英文，规避 MSIX 打包链路上的中文编码问题）。

---

## 〇、这份文档怎么用（先读这条）

**这是一份活的文档，不是合同。** 它记录的是"截至目前为止，我们决定怎么做"。

| 规则 | 说明 |
|---|---|
| **需求冲突时，以用户最新的话为准** | 用户提出新需求 → **直接改这份文档**，不要拿文档去驳回用户 |
| **改完要改得干净** | 别只改一处留个矛盾的另一处（本文档就犯过：位置写了三处，只改了两处） |
| **不确定就问，别自己选** | 文档里两份说法打架时，这是"我该问"的信号，不是"我挑一个"的信号 |
| **改了要提交 git** | 每次实质改动都跟着一次 commit，历史就是决策记录 |

**为什么写这条**：本文档曾经把"九宫格在屏幕正中"和"九宫格在鼠标处"两句话同时留在里面，
我擅自按"更权威"的那句执行，结果改错了 —— 用户要的是鼠标处。
**文档自相矛盾的地方，错的是文档，不是用户。**

---

## 一、产品规格

尺寸以 1920×1080 @100% 缩放为基准，**全部做成可配置**，不写死。

### 三个窗口

所有窗口统一：置顶、永不抢焦点、无边框、不显示在任务栏。

#### A. 收缩条 `StripWindow`

| 项 | 值 |
|---|---|
| 尺寸 | **真实 20mm × 2mm**（2cm × 2mm，按 DPI 换算，**不是**固定像素数） |
| 位置 | **中心在屏幕宽度的 3/4 处**（即"右四分之一处"）；**紧贴工作区顶部，零缝隙** |
| 颜色 | **亮色**（近白），必须在深色背景下看得清 |
| 触发 | **触发范围 = 条子本身的尺寸，一点都不外扩，不许做隐形热区** |
| 穿透 | 尽力做到鼠标穿透；做不到也能接受（用户已明确） |

> **尺寸经过一轮实测修正**。最初是 `10mm × 1mm`，用户量出来只有 0.7cm 且太薄，
> 要求**长度和厚度都翻倍** → `20mm × 2mm`。

> **位置也修正过**。最初钉在屏幕右上角（距右边缘 8 DIP），用户反馈"很影响操作"——
> 那个位置正好压着最大化窗口的**关闭按钮**。改为"中心在 3/4 处、紧贴顶部"。

平时唯一可见的东西。

**★ 这里有三条被违反过、现在立为硬性要求的规矩：**

1. **可见元素的宽高一律显式写死**，不许交给对齐方式去推断。
   *这个错误犯了两次*：`TextBlock` 不定宽 → 长文本把布局撑坏、左边一列被挤出窗口；
   `Border` 不定宽 + `HorizontalAlignment=Right` → 期望宽度是 0，实际长度不可控
   （用户量出来 0.7cm 就是这个原因）。
2. **触发区尺寸 = 可见尺寸**。第一轮我把窗口做成 10mm×10mm、可见细线只占顶上一条，
   命中判定用整个窗口 —— 命中区是条子面积的 10 倍，被用户明确否掉。
3. **真实毫米依赖"显示设置里的缩放"与"显示器实际密度"一致**。
   Windows **只知道缩放比例，不知道你的屏有多大**。
   两者对不上时，按 DPI 算出来的毫米就不是真实毫米。
   → 备了 `Geometry.MmCalibration` 标定系数：拿尺子量一次实际长度，
   填 `新系数 = 旧系数 × 目标 ÷ 实测` 即可。**这是"真实毫米"自带的坑。**

#### B. 主面板 `PanelWindow`

| 项 | 值 |
|---|---|
| 尺寸 | **真实 64mm 宽 × 黄金比高**（高 = 宽 ÷ 1.618 = 39.55mm）。竖长方形 |
| 位置 | 与收缩条**同一个横向中心**（屏幕宽度 3/4 处），**紧贴顶边**，向下展开 |
| 内容 | 竖向列表，最近 **20 条**历史，**滚轮滚动** |
| 条目 | **单行紧凑**：左边预览（省略号截断）+ 右边小字「序号 + 时间」；悬停微微亮起 |
| 交互 | 单击某条 → **写进剪贴板**（用户自己按 Ctrl+V，程序不模拟按键） |
| 滚轮 | **一步约 24 DIP**（约 1/3 行高），位移必须看得见 |
| 顶栏 | 细标题栏「粘贴板 · N 条」+ ▲▼ 滚动按钮（滚轮失效时的保底） |

> **尺寸与滚轮都经过一轮实测修正**：
> - 最初是"屏幕 1/16"公式（40mm 宽），用户嫌小 → **宽 ×1.6 → 64mm**，高按黄金比
> - 条目最初是两行（预览两行 + 时间一行），行高 65 DIP，
>   在变矮后的面板里只放得下 1.7 行 → 改成**单行紧凑**，行高约 26 DIP
> - 滚轮最初一步 48 DIP ÷ 65 行高 = **74%** ——
>   用户反馈「往下滚一下下面的文字框正好覆盖掉上面的文字框，
>   视觉上很难辨别是往下滚了还是往上滚了」。
>   这正是"一步跨得太多、没有参照物发生位移"的典型症状。
>   → 一步改成 **24 DIP**，并把行做矮，位移就明显了。

> 面板与条子**同一个中心**，所以展开时是向左右同时长出去、向下长出来，
> 视觉上就是"从那条线长出来的"。

#### C. 九宫格 `GridOverlay`

**这是一套「方向手势菜单」，不是点选菜单。** 核心是按住 Alt 期间不碰鼠标键。

| 项 | 值 |
|---|---|
| 尺寸 | **边长 = √(屏宽 × 屏高 ÷ 9)**，忠实还原「屏幕的 1/9 大小」 |
| 实例 | 1920×1080 → `480 × 480`，单格 `160 × 160` |
| **位置** | ★ **鼠标处** —— 窗口左上角 = 光标位置 − 边长的一半，**光标正好落在中心那一格** |
| 边线 | 极低透明度白色细线 |

> **位置这一条改过两次，已经定案：必须是「鼠标处」。**
> 中间试过"固定在屏幕正中"，除了手感（眼睛得先去找它），还有一个**硬技术原因**：
> `SetSystemCursor` 把系统箭头换成透明的，但**别的程序在光标进入自己窗口时会用自己的光标覆盖它**
> —— 记事本里是 I 形，浏览器里是手形。
> 窗口贴着光标 → 光标底下永远是我们自己的九宫格 → 我们是最后说话的人；
> 窗口跑到屏幕正中 → 光标常落在别的窗口上 → 那个程序说了算 → **指针又冒出来了**。
> 所以"跟着鼠标走"不只是手感，它是**光标能藏住的前提**。

**格子布局：中心留空**

```
┌─────┬─────┬─────┐
│ 00  │ 01  │ 02  │    00 = row 0 col 0
├─────┼─────┼─────┤    01 = row 0 col 1
│ 10  │  ␀  │ 12  │    中心 11 **空白，不可选**
├─────┼─────┼─────┤
│ 20  │ 21  │ 22  │    坐标写作「行列」两位数字
└─────┴─────┴─────┘
```

**由新到旧的填充顺序（顺时针，从正上方开始）**

```
        ①01
    ⑧00      ②02         1 = 最新
        ␀ 中心留空        8 = 最旧
    ⑦10      ③12
        ⑥21  ⑤22  ④20
```

| 顺序 | 格位 | 方位 |
|---|---|---|
| 1 | `01` | 正上 |
| 2 | `02` | 右上 |
| 3 | `12` | 正右 |
| 4 | `22` | 右下 |
| 5 | `21` | 正下 |
| 6 | `20` | 左下 |
| 7 | `10` | 正左 |
| 8 | `00` | 左上 |

**交互流程**

1. **按住 `Alt+V`** → 九宫格在鼠标处弹出，**鼠标指针隐藏**
2. 进入**选择模式**：以「按下 Alt+V 那一刻的鼠标位置」为起点
3. **鼠标往哪个方向划，就选中那个方位的格子** —— 8 个 45° 扇区
4. 选中格**微微亮起 + 高亮边框**，表示当前选中
5. **松开 `Alt+V`** → 立即把选中格的内容粘回原窗口
6. **按 `Esc`** → 取消，不粘贴

**硬性要求**

- **不需要点击**。全程只有「按住 → 移鼠标 → 松开」三个动作
- 高亮用**边框**表示（用户明确要求"亮起边框最好"）
- **粘出去的那条要变成最新的**（用户明确要求）—— 见下

**★ 手感三条（第四轮定稿，改之前先读这段）**

用户的原始诉求（来回澄清了三轮才说准）：

> 「你往上拖选择了 01，往下拖一点就选择了 21，
>   但是如果你**再往上拖，它应该立马回到 01**，因为往上拖动了。」
>
> 「鼠标只要移动过了，就不能再选中最中心的取消方块。」

**模型：这是一台"拨盘" —— 看鼠标正在往哪边划，不看它在起点的哪个方位。**

```
往上划 → 01        往下划 → 21        再往上划 → 又回 01
往右划 → 12        往左划 → 10        往右上划 → 02
```

| 规则 | 说明 |
|---|---|
| **判定依据** | **位移方向**（相对"上一次判出方向时的光标位置"，即**锚点**） |
| **阈值** | 位移 ≥ **10 物理像素**（150% 下约 1.8mm）就判一次 |
| **判完之后** | 锚点挪到当前光标 —— "这一小段移动已经用掉了" |
| **位移不够阈值** | **保持**上一次的选中，不换也不取消 |
| **取消** | 只在**一次都没动过**的时候（没动 → 位移恒为 0 → 判出"取消"） |
| **`Esc`** | 显式取消 |

**★★ 最容易搞错的一条：必须按"位移方向"判，不能按"相对起点的方位"判。**

我在这上面连错两轮：

- 第一轮用**绝对方位**（从起点指向鼠标）→ 往上拖到 01 后往下拖一点，
  鼠标**仍在起点上方**，按方位判还是 01，永远变不成 21。
- 第二轮改成**累计位移**，但把"判定基准点"固定在了起点 →
  拖出去再拖回来时，累计位移的方向和鼠标实际所在的方向不一致，
  右上拖，然后鼠标往左下走，**方向是对的**，但用户再往上拖时
  累计方向已经乱了，回不到 01。

**正解是"锚点跟着判定走"**：每次判出方向后，把锚点挪到当前光标。
于是"往上划 → 01、往下划 → 21、再往上划 → 回 01"**自然成立**，
不需要任何特殊规则。

**为什么不用"每帧位移的累加"**：手停着时人手仍在抖 1~3px，
累加会一点点攒起来，攒够阈值就**随机翻格子**。
而用"相对锚点的位移"，**在同一个点附近抖动的净位移始终很小**，
永远够不到阈值 —— 天然稳定，不需要任何"稳定 N 毫秒"的计时器。

**判断逻辑在 `GridSelection.Resolve(moveX, moveY, lastDirection)`**，
返回 `DialResult { Index, ConsumedMotion }`，纯函数、**29 条单测**覆盖
（含上面每一条手感规则，以及用户那道考题"从 01 往右划 → 12"）。

**「粘过的变成最新」规则**

用九宫格粘出第 N 条之后，**那条要立刻升到最新，其余顺延**。

```
粘之前              粘之后（假设粘的是 12）
01 ← 最新           01 ← 刚粘出去的那条
02  ...             02 ← 原来的 01
12 ← 我粘的这条     03 ⋯ 其余顺延
...                 ...
```

理由：九宫格只有 8 格，常用的是「反复粘同一条」。粘过就顶置，下次它就在 `01`（正上方、
最顺手的位置），不用每次重新找。

| 实现要点 | 说明 |
|---|---|
| 位置 | `Core\HistoryStore.cs` 的 `Promote(text)` |
| **按内容找，不按索引** | 按 Alt+V 到松开之间可能又复制了新东西，列表顺序会变；按索引会移错人 |
| 时机 | 松开 Alt、**写回剪贴板之前**，在 `GridOverlay` 的提交路径里调 |
| 不能靠剪贴板监听自动顶置 | 我们写回剪贴板那一下被防回环逻辑挡掉了，走不到 `Add()`，必须显式调 |
| 找不到就不动 | 那条可能已被挤出环形缓冲；返回 `false`，不报错不崩溃 |

同样的规则也适用于**主面板**（阶段 2）：点一条粘出去，那条也升到最新。

**与「20 条历史」的关系**：九宫格只有 8 个可选格，历史有 20 条，两者不通用。
**默认只显示最近 8 条。**

**手感最难调，预留最多迭代时间。**

---

### 多选批量粘贴（主面板的功能，阶段 2）

**用户需求原文**：「我要的是只要复制就能记录上去，也就是说一次复制很多东西，然后一次性粘贴很多东西」

即：面板里可以**勾选多条**，一次把所有勾中的内容按顺序粘出去（多条之间以换行分隔）。

| 项 | 值 |
|---|---|
| 进入方式 | 面板里每条左侧有勾选框 |
| 粘贴顺序 | 按**历史顺序**（新的在前），与勾选先后无关 |
| 分隔 | 换成多行文本，一次 `SendInput Ctrl+V` 全部粘出 |
| 单条模式 | 不勾任何框 = 原来的「点一条粘一条」，两种模式共存 |
| 落点 | `Core\HistorySelection.cs` —— 拼装逻辑纯函数，可单测 |

**为什么放在阶段 2 而不是现在**：它依赖面板的列表交互；而且它只是**文本拼装 + 一次注入**，
没有新的系统级未知点。真正难的（不抢焦点、时序）在九宫格那边。

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
    │   ├─ HistorySelection.cs      ← ★ 多选 → 一次粘贴多条的拼装逻辑
    │   ├─ Paster.cs                ← 「写回剪贴板 → 注入 Ctrl+V」完整用例
    │   ├─ EdgeTriggerDetector.cs   ← ★ 顶边+右上角停留 0.3s 状态机
    │   ├─ AltHoldTracker.cs        ← ★ Alt 按下/松开/超时/异常中断 状态机
    │   └─ GridSelection.cs         ← ★ 纯函数：鼠标向量 → 宫格号 + 中心死区
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
| 2 主面板 | `PanelWindow` 上线：亚克力、竖向列表、滚轮、点击粘贴<br>**+ 多选批量粘贴**（勾多条 → 一次粘出，见「多选批量粘贴」节） |
| 3 收缩条 | `StripWindow` + `EdgeTriggerDetector`。鼠标移开自动收起 |
| 4 九宫格 | `GridWindow` + `AltHoldTracker` + `GridSelection`。<br>**方向手势菜单：按住 Alt → 移鼠标选方位 → 松开即粘。**<br>方向判定是纯函数，已有 20 条自测用例（`verify\S0-Spine\GridSelectionTests.cs`），<br>阶段 4 时搬进 `tests\ClipDesk.Tests\`。<br>**手感最难调，预留最多迭代时间** |
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
| **S3** | ~~高~~ → **已降级** | ~~滚轮可能收不到~~ **实测收得到**：`SPI_GETMOUSEWHEELROUTING = 2`（滚轮发给悬停窗口），26 次展开中滚轮正常响应 | 已无误；▲▼ 按钮保留作保底 |
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
