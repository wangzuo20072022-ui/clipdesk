using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using static S2Strip.NativeMethods;

namespace S2Strip;

/// <summary>
/// S2 · 条子 + 面板探针
///
/// 一个完整闭环：
///   复制 → 右上角细线 → 鼠标靠近展开成面板 → 点一条 → 写进剪贴板 → 用户自己按 Ctrl+V
///
/// 一次答五个问题（见 docs\技术验证报告.md）：
///   Q1 条子的真实像素高度对不对？窗口最小尺寸地板有没有挡住它？
///   Q2 悬停能不能可靠展开，且**全程不抢焦点**
///   Q3 点条目之后剪贴板里真的变成那一条了吗
///   Q4 收起态的 1cm² 能不能做到鼠标穿透
///   Q5 面板上的滚轮能不能滚
///
/// 时间盒：一次会话。
/// </summary>
internal static class Program
{
    private const int ExitHotKeyId = 0x0C21;
    private const int AltHotKeyId = 0x0C22;

    /// <summary>
    /// 剪贴板事件去抖窗口（毫秒）。
    ///
    /// ★ 第一轮是 **100ms，这是错的，而且正是"复制的东西不显示"的共犯之一。**
    ///
    ///   写入方（记事本 / 浏览器）写剪贴板是**分两步**的：
    ///   先 EmptyClipboard，再 SetClipboardData。
    ///   中间那个瞬间会触发一次 WM_CLIPBOARDUPDATE ——
    ///   而**第一个事件恰恰是唯一一次"格式已就位、内容已填好"的通知**。
    ///   我们把它整个丢掉，剩下的全是半成品状态。
    ///
    ///   实测（独立探针抓的现场）：
    ///       事件#1 序列号=1546 → ✅ 读到内容 尝试1次     ← 被 100ms 去抖丢了
    ///       事件#2 序列号=1549 → ★ 格式不在            ← 只有这个被处理
    ///       事件#3 第1次尝试：OpenClipboard 失败 err=5
    ///       事件#4 序列号=1556 → ✅ 读到内容 尝试2次
    ///
    ///   改成 20ms —— 够挡住真正的重复通知，又不会把第一次通知丢掉。
    /// </summary>
    private const int ClipboardDebounceMs = 20;

    /// <summary>收起态轮询频率：20Hz。只在等鼠标靠近，不用太勤。</summary>
    private const int PollIdleMs = 50;

    /// <summary>展开态轮询频率：60Hz。要跟手判断鼠标有没有离开。</summary>
    private const int PollActiveMs = 16;

    private static readonly Verdict V = new();
    private static readonly ClipboardHistory History = new(20);
    private static readonly Stopwatch _bootClock = new();

    private static HwndSource? _msgWindow;
    private static IntPtr _msgHwnd;
    private static StripPanelWindow? _panel;
    private static Application? _app;

    private static uint _lastSequence;
    private static DateTime _lastClipboardAt = DateTime.MinValue;
    private static int _clipboardEventCount;
    private static int _clipboardOpenAttemptsMax;
    private static bool _selfWriting;

    // Q2 的采样点
    private static IntPtr _foregroundAtStartup;
    private static IntPtr _foregroundAtExpand;
    private static int _focusDriftCount;
    private static int _expandCount;
    private static int _collapseCount;

    // 九宫格
    private static GridWindow? _grid;
    private static bool _altHotkeyRegistered;
    private static volatile bool _altDown;
    private static DateTime _altDownAt;
    private static bool _gridVisible;
    private static int _gridShowCount;

    private static readonly EdgeTriggerStateMachine Trigger = new();

    [STAThread]
    private static void Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        _bootClock.Start();

        WarnIfPreviousInstanceRunning();

        // 纯逻辑先测通过再看窗口 —— 出了问题能立刻分清是"算错了"还是"没画对"
        EdgeTriggerTests.Run();
        HistoryPromoteTests.Run();
        GridSelectionTests.Run();

        PrintHeader();

        if (args.Length > 0 && int.TryParse(args[0], out int seconds) && seconds > 0)
        {
            _autoExitSeconds = seconds;
            Console.WriteLine($"[启动] 自动退出已设置：{seconds} 秒后打印判定表并退出");
        }

        _app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            _app?.Dispatcher.Invoke(() => _app.Shutdown());
        };

        _app.Startup += (_, _) =>
        {
            CreateMessageWindow();
            CreatePanel();
            StartWatcher();
            if (_autoExitSeconds > 0) StartAutoExitTimer();
            RunAssertions();
        };

        _app.Run();

        Cleanup();
    }

    private static int _autoExitSeconds;

    /// <summary>
    /// 报告同名的旧进程。**只报告，不结束任何东西。**
    /// 一个进程可能是用户正在用的，程序自作主张杀掉它是不可接受的。
    /// </summary>
    private static void WarnIfPreviousInstanceRunning()
    {
        int me = Environment.ProcessId;
        var others = new List<int>();
        foreach (var p in Process.GetProcessesByName("S2Strip"))
        {
            if (p.Id != me) others.Add(p.Id);
            p.Dispose();
        }
        if (others.Count == 0) return;

        Console.WriteLine($"⚠️  检测到 {others.Count} 个遗留的探针进程（PID {string.Join(", ", others)}）。");
        Console.WriteLine("   它可能锁着 exe 导致编译失败。停止它：在那个窗口上按 Ctrl+Alt+Q。");
        Console.WriteLine();
    }

    private static void StartAutoExitTimer()
    {
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(_autoExitSeconds),
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Console.WriteLine();
            Console.WriteLine($"[启动] {_autoExitSeconds} 秒到，自动收尾。");
            _app!.Shutdown();
        };
        timer.Start();
    }

    // ── 启动 ────────────────────────────────────────────────────────

    private static void PrintHeader()
    {
        Console.WriteLine();
        Console.WriteLine("╔══════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║  ClipDesk · S2 条子探针                                       ║");
        Console.WriteLine("║  目的：验证「鼠标靠近展开 + 点一条写剪贴板」能不能跑通        ║");
        Console.WriteLine("║  时间盒：一次会话                                              ║");
        Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");
        Console.WriteLine();
    }

    private static void CreateMessageWindow()
    {
        // 一个永不显示的顶层窗口，专门收 WM_CLIPBOARDUPDATE 和逃生热键。
        var p = new HwndSourceParameters("ClipDeskS2MessageWindow")
        {
            WindowStyle = 0x00000000,
            ExtendedWindowStyle = WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
            Width = 1,
            Height = 1,
            PositionX = -32000,
            PositionY = -32000,
        };

        _msgWindow = new HwndSource(p);
        _msgHwnd = _msgWindow.Handle;
        _msgWindow.AddHook(WndProc);

        Console.WriteLine($"[启动] 消息窗口 HWND = 0x{_msgHwnd:X8}");

        bool listenerOk = AddClipboardFormatListener(_msgHwnd);
        V.Log($"  ★ AddClipboardFormatListener = {(listenerOk ? "成功" : "★失败")}");
        _lastSequence = GetClipboardSequenceNumber();

        bool exitHotkey = RegisterHotKey(_msgHwnd, ExitHotKeyId,
                                         MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_Q);
        V.Log($"  ★ 逃生热键 Ctrl+Alt+Q = {(exitHotkey ? "已注册" : "★注册失败")}");

        // ── 九宫格热键 Alt+V ──
        // ★ 这是用户第三轮问「九宫格不见了」的答案。
        //
        //   九宫格本身没坏，代码一直在 verify\S0-Spine\ 里，
        //   但那是**另一个 exe**。这个 S2 探针从建起来就没注册过 Alt+V ——
        //   所以跑 S2 的时候按 Alt+V 当然不会有反应。
        //
        //   顺手在这轮把它并进来，两个功能同跑，省得来回换 exe。
        _altHotkeyRegistered = RegisterHotKey(_msgHwnd, AltHotKeyId,
                                              MOD_ALT | MOD_NOREPEAT, VK_V);
        V.Log($"  ★ 九宫格热键 Alt+V = {(_altHotkeyRegistered ? "已注册" : "★注册失败（被占用？）")}");

        // Q5 的背景数字：系统的滚轮路由设置。
        //   0 = 焦点窗口收滚轮（默认）；1 = 也发给悬停窗口；2 = 只发给悬停窗口
        //   我们是 NOACTIVATE 永不获焦，所以只有非 0 才有可能收到滚轮。
        int routing = -1;
        if (SystemParametersInfo(SPI_GETMOUSEWHEELROUTING, 0, ref routing, 0))
        {
            V.Log($"  ★ SPI_GETMOUSEWHEELROUTING = {routing}"
                  + $"（{(routing == 0 ? "0=只发焦点窗口 → 我们大概率收不到滚轮" : "非 0 → 有机会收到")}）");
        }

        _foregroundAtStartup = GetForegroundWindow();
    }

    private static void CreatePanel()
    {
        _panel = new StripPanelWindow(History, V.Log);
        _panel.ItemActivated += OnItemActivated;

        // 先建好 HWND（这不显示）
        var helper = new WindowInteropHelper(_panel);
        helper.EnsureHandle();

        V.Log($"  ★ 冷启动到 HWND 就绪：{_bootClock.ElapsedMilliseconds} ms");

        var warmClock = Stopwatch.StartNew();
        _panel.Prewarm();
        V.Log($"  ★ 预热耗时：{warmClock.ElapsedMilliseconds} ms");

        // ★ 预热之后必须**真的摆出来**。
        //   Prewarm() 结尾是 SW_HIDE，所以那之后条子是藏着的 ——
        //   不补这一下，启动后屏幕上什么都没有（第一次差点就这么交出去了）。
        //   条子是常驻可见的东西，程序一起来就该在那儿。
        _panel.ShowCollapsed();
        V.Log($"  ★ 条子已摆出：物理 {_panel.BoundsText}");

        // ── 九宫格（Alt+V）也一起预热 ──
        //   预热必须走一遍 WPF Show()，否则视觉树没 Measure/Arrange，
        //   热键一按只会得到一个纯色空框（S0 为这件事白折腾过两轮）。
        _grid = new GridWindow(History, V.Log);
        new WindowInteropHelper(_grid).EnsureHandle();
        _grid.Prewarm();
        V.Log("  ★ 九宫格已预热（按住 Alt+V 弹出）");
    }

    // ── 消息处理 ────────────────────────────────────────────────────

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_HOTKEY when wParam.ToInt32() == ExitHotKeyId:
                V.Log("[逃生热键] Ctrl+Alt+Q → 收尾退出");
                _app?.Shutdown();
                handled = true;
                break;

            case WM_HOTKEY when wParam.ToInt32() == AltHotKeyId:
                OnAltPressed();
                handled = true;
                break;

            case WM_CLIPBOARDUPDATE:
                OnClipboardUpdate();
                handled = true;
                break;
        }

        return IntPtr.Zero;
    }

    private static void OnClipboardUpdate()
    {
        var now = DateTime.Now;
        if ((now - _lastClipboardAt).TotalMilliseconds < ClipboardDebounceMs) return;
        _lastClipboardAt = now;

        if (_selfWriting)
        {
            _selfWriting = false;
            return;
        }

        uint seq = GetClipboardSequenceNumber();
        if (seq == _lastSequence) return;
        _lastSequence = seq;

        var (text, attempts) = ClipboardIo.ReadText();
        _clipboardOpenAttemptsMax = Math.Max(_clipboardOpenAttemptsMax, attempts);

        if (text is null)
        {
            V.Log($"  [剪贴板] 事件#{++_clipboardEventCount} 序列号={seq} → 非文本或读不到（尝试 {attempts} 次）");
            return;
        }

        bool isNew = History.Add(text, "?");
        string preview = text.Replace("\r", " ").Replace("\n", " ");
        if (preview.Length > 40) preview = preview[..40] + "…";
        V.Log($"  [剪贴板] 事件#{++_clipboardEventCount} 序列号={seq} len={text.Length} "
              + $"{(isNew ? "新增" : "顶置")} 尝试{attempts}次 「{preview}」（历史 {History.Items.Count} 条）");

        // 收起态时顺手刷新一下条子（虽然条子不显示内容，但保持数据新鲜）
        if (!Trigger.Current.Equals(EdgeTriggerStateMachine.Phase.Expanded))
        {
            _panel?.Dispatcher.Invoke(() => _panel.FillList());
        }
    }

    // ── 九宫格：按下 Alt+V ──────────────────────────────────────────

    /// <summary>按下 Alt+V —— 在鼠标处弹出九宫格，藏起指针，进入选择模式。</summary>
    private static void OnAltPressed()
    {
        if (_grid is null) return;

        _altDown = true;
        _altDownAt = DateTime.Now;

        if (_gridVisible)
        {
            V.Log("  （九宫格已开着，忽略这次重复触发）");
            return;
        }

        // 条子面板如果正开着，先收起来，免得两个窗口打架
        if (_panel is not null && _panel.IsExpanded)
        {
            Trigger.ForceCollapse();
            _panel.ShowCollapsed();
        }

        _gridShowCount++;
        V.Log("");
        V.Log($"[九宫格] 第 {_gridShowCount} 次弹出");
        V.Log($"  弹出前 前台 = 0x{GetForegroundWindow():X8} "
              + $"「{GetWindowTitle(GetForegroundWindow())}」");

        _grid.ShowAtCursor();
        _gridVisible = true;

        V.Log($"  位置 = {_grid.LastShownPosition}");
    }

    /// <summary>松开 Alt —— 结算选中的那一格。</summary>
    private static void OnAltReleased()
    {
        if (_grid is null) return;

        int index = _grid.CommitSelection();
        string label = _grid.ActiveCellLabel;

        if (index < 0)
        {
            V.Log($"[九宫格] 松开 Alt → {label}，不粘贴");
            _grid.HideGrid();
            _gridVisible = false;
            return;
        }

        var (r, c) = GridSelection.ToCell(index);
        string? text = _grid.GetCellContent(r, c);

        _grid.HideGrid();
        _gridVisible = false;

        if (string.IsNullOrEmpty(text))
        {
            V.Log($"[九宫格] ★ {label} 是空格子，不粘贴");
            return;
        }

        V.Log($"[九宫格] 松开 Alt → 选中 {label}「{(text.Length > 30 ? text[..30] + "…" : text)}」");

        // 粘出去的那条要变成最新的 —— 下次它就该出现在 01（正上方）
        History.Promote(text);

        // 写回剪贴板 → 注入 Ctrl+V
        _selfWriting = true;
        if (!ClipboardIo.WriteText(text))
        {
            _selfWriting = false;
            V.Log("  ★❌ 写剪贴板失败");
            return;
        }
        _lastSequence = GetClipboardSequenceNumber();

        Thread.Sleep(20);
        SendCtrlV();
    }

    /// <summary>
    /// 模拟 Ctrl+V。
    ///
    /// ★ INPUT 那个联合体声明不对的话，SendInput 会**返回 0、一个事件都不送、
    ///   而且不报错** —— S0 在这上面卡过好几轮，注释留在 NativeMethods.cs 里。
    /// </summary>
    private static void SendCtrlV()
    {
        int size = Marshal.SizeOf<INPUT>();
        var inputs = new INPUT[4];

        inputs[0].Type = INPUT_KEYBOARD;
        inputs[0].Keyboard = new KEYBDINPUT { Vk = 0x11 /*Ctrl*/, Flags = 0 };

        inputs[1].Type = INPUT_KEYBOARD;
        inputs[1].Keyboard = new KEYBDINPUT { Vk = (ushort)VK_V, Flags = 0 };

        inputs[2].Type = INPUT_KEYBOARD;
        inputs[2].Keyboard = new KEYBDINPUT { Vk = (ushort)VK_V, Flags = KEYEVENTF_KEYUP };

        inputs[3].Type = INPUT_KEYBOARD;
        inputs[3].Keyboard = new KEYBDINPUT { Vk = 0x11, Flags = KEYEVENTF_KEYUP };

        uint sent = SendInput((uint)inputs.Length, inputs, size);
        V.Log($"  SendInput(Ctrl+V) → 送出 {sent}/4 个（INPUT 结构 {size} 字节）"
              + (sent == 4 ? "" : $"  ★ 失败，错误码={Marshal.GetLastWin32Error()}"));
    }

    // ── 轮询线程 ────────────────────────────────────────────────────

    /// <summary>
    /// 后台线程读光标位置 → 喂给状态机 → 按状态机的指示展开/收起。
    ///
    /// 为什么用轮询而不是让窗口接鼠标消息：
    ///   1. **不需要窗口接住鼠标** —— 命中判定变成一次纯软件矩形比较，
    ///      命中区想做多大就多大，不产生任何窗口面积
    ///   2. **窗口因此可以设成鼠标穿透**，那 1cm² 就不会挡住下面的关闭按钮
    ///   3. **零新技术风险** —— S0 里 Alt 松开检测就是这么做的，实测可靠
    ///
    /// 两条纪律：
    ///   ★ 绝不用 DispatcherTimer —— 用独立后台线程，不唤醒 UI 线程
    ///   ★ 频率分档 —— 收起 20Hz、展开 60Hz
    /// </summary>
    private static void StartWatcher()
    {
        var t = new Thread(() =>
        {
            while (true)
            {
                var phase = Trigger.Current;
                int sleep = phase == EdgeTriggerStateMachine.Phase.Expanded
                            || phase == EdgeTriggerStateMachine.Phase.CollapsePending
                    ? PollActiveMs
                    : PollIdleMs;

                Thread.Sleep(sleep);

                // ── ① 九宫格：按着 Alt 就更新选中的方位，松开就结算 ──
                //    这段和条子面板共用同一个循环，不用再开一个线程。
                {
                    // ★ Esc 取消 —— 第三轮加了"锁定"之后，这是**唯一**的取消手段：
                    //   一旦选中过某个方向就再也回不到"取消"，
                    //   想反悔只能按 Esc（这个交互 CLAUDE.md 里早就写了，之前一直没实现）。
                    if (_gridVisible && (GetAsyncKeyState(VK_ESCAPE) & 0x8000) != 0)
                    {
                        var g = _grid;
                        if (g is not null)
                        {
                            g.Dispatcher.Invoke(() =>
                            {
                                g.CancelSelection();
                                g.HideGrid();
                            });
                        }
                        _gridVisible = false;
                        _altDown = false;
                        V.Log("[九宫格] Esc → 取消，不粘贴");
                        continue;
                    }

                    bool altHeld = (GetAsyncKeyState(VK_LMENU) & 0x8000) != 0
                                || (GetAsyncKeyState(VK_RMENU) & 0x8000) != 0;

                    if (altHeld && _gridVisible && _grid is not null)
                    {
                        var g = _grid;
                        g.Dispatcher.Invoke(() => g.PollSelection());
                    }
                    else if (!altHeld && _altDown)
                    {
                        _altDown = false;
                        var g = _grid;
                        bool heldLongEnough =
                            (DateTime.Now - _altDownAt).TotalMilliseconds >= 120;

                        if (_gridVisible && g is not null && heldLongEnough)
                        {
                            g.Dispatcher.Invoke(OnAltReleased);
                        }
                        else if (_gridVisible && g is not null)
                        {
                            V.Log($"  （Alt 只按了 {(DateTime.Now - _altDownAt).TotalMilliseconds:0}ms，忽略）");
                            g.Dispatcher.Invoke(() => g.HideGrid());
                            _gridVisible = false;
                        }
                    }
                }

                var panel = _panel;
                if (panel is null) continue;

                // 九宫格开着的时候不要去动条子，免得两个窗口抢鼠标
                if (_gridVisible) continue;

                // 顺手把"点击黄框"的过期检查挂在这个心跳上。
                // 反正是已经存在的循环，不额外唤醒任何东西（D9 的规矩：不用 DispatcherTimer）。
                if (panel.IsExpanded)
                {
                    panel.Dispatcher.Invoke(() => panel.TickSelection());
                }

                RectPx hit = panel.HitRect;

                GetCursorPos(out POINT p);
                bool inside = hit.Contains(p.X, p.Y);

                var action = Trigger.Tick(inside);

                if (action == EdgeTriggerStateMachine.Action.Expand)
                {
                    panel.Dispatcher.Invoke(() => OnExpandRequested());
                }
                else if (action == EdgeTriggerStateMachine.Action.Collapse)
                {
                    panel.Dispatcher.Invoke(() => OnCollapseRequested(p.X, p.Y));
                }
            }
        })
        {
            IsBackground = true,
            Name = "EdgeTriggerWatcher",
        };
        t.Start();

        V.Log($"  ★ 轮询线程已开：收起 {PollIdleMs}ms / 展开 {PollActiveMs}ms"
              + $"（停 {EdgeTriggerStateMachine.HoverDwellMs}ms 展开、"
              + $"离开 {EdgeTriggerStateMachine.CollapseDelayMs}ms 收起）");
    }

    private static void OnExpandRequested()
    {
        if (_panel is null) return;

        _expandCount++;
        _foregroundAtExpand = GetForegroundWindow();

        V.Log("");
        V.Log($"[展开] 第 {_expandCount} 次");
        V.Log($"  展开前 前台 = 0x{_foregroundAtExpand:X8} 「{GetWindowTitle(_foregroundAtExpand)}」");

        _panel.ShowExpanded();
        _panel.FillList();

        IntPtr after = GetForegroundWindow();
        V.Log($"  展开后 前台 = 0x{after:X8} 「{GetWindowTitle(after)}」");
        if (after == _foregroundAtExpand)
        {
            V.Log("  ✅ 前台窗口未变（Q2 采样点）");
        }
        else
        {
            _focusDriftCount++;
            V.Log("  ★❌ 前台窗口被抢走了！");
        }
    }

    private static void OnCollapseRequested(int cursorX, int cursorY)
    {
        if (_panel is null) return;

        _collapseCount++;
        _panel.ShowCollapsed();

        V.Log($"  [收起] 第 {_collapseCount} 次（鼠标在 {cursorX},{cursorY}）"
              + $" 实际={_panel.BoundsText}");
    }

    // ── 点条目 → 写剪贴板 ──────────────────────────────────────────

    /// <summary>
    /// 用户点了一条。**只写剪贴板，不模拟按键** —— 用户明确要求自己按 Ctrl+V。
    ///
    /// 这条路径刻意砍掉了 S0 里的 SendInput，于是也顺带砍掉了两个风险：
    ///   · 不用管 UIPI（不注入就无所谓目标是不是管理员程序）
    ///   · 不用管焦点（不注入就无所谓焦点在哪）
    /// </summary>
    private static void OnItemActivated(string text)
    {
        if (_panel is null) return;

        Trigger.CommitClicked();

        V.Log("");
        V.Log($"[点击] → 「{(text.Length > 40 ? text[..40] + "…" : text)}」");

        // Q2 的第三次采样：点击前前台是谁
        IntPtr before = GetForegroundWindow();
        V.Log($"  点击前 前台 = 0x{before:X8} 「{GetWindowTitle(before)}」");
        if (before == _foregroundAtStartup)
        {
            V.Log("  ✅ 从启动到现在，前台窗口全程没变过");
        }
        else if (before == _foregroundAtExpand)
        {
            V.Log("  ✅ 从展开到点击，前台窗口没变过");
        }
        else
        {
            _focusDriftCount++;
            V.Log($"  ★❌ 前台窗口变了（展开时 0x{_foregroundAtExpand:X8} → 现在 0x{before:X8}）");
        }

        // ── 顶置：点过的那条要变成最新的 ──
        //   ★ 注意：**不再刷新列表 UI** —— 一刷新黄框就没了，
        //     而用户要的是"点完之后还看得见我选了哪个"。
        //     列表顺序在下次展开（FillList）时自然会更新。
        V.Log($"  {_panel.DescribeTop(5)}   ← 顶置前（前 5 条）");
        bool promoted = History.Promote(text);
        V.Log($"  {_panel.DescribeTop(5)}   ← 顶置后（前 5 条）");
        V.Log($"  顶置结果：{(promoted ? "已提到最新" : "★ 没找到，可能已被挤出历史")}");

        // 写剪贴板
        _selfWriting = true;
        if (!ClipboardIo.WriteText(text))
        {
            _selfWriting = false;
            V.Log("  ★❌ 写剪贴板失败");
            return;
        }
        _lastSequence = GetClipboardSequenceNumber();
        V.Log($"  已写回剪贴板，序列号={_lastSequence}");

        // ── 自证：读回来比对 ──
        //   这一步把"用户按 Ctrl+V 能不能粘出来"从"我们也说不清"，
        //   缩小到只剩 UIPI 那一件（S0/Q4 的事，不归 S2 管）。
        Thread.Sleep(20);
        var (readBack, attempts) = ClipboardIo.ReadText();
        bool same = readBack == text;
        V.Log($"  [自证] 读回长度={readBack?.Length ?? -1} 与原文一致={(same ? "是" : "★否")}"
              + $"（尝试 {attempts} 次）");
        if (!same)
        {
            string got = readBack is null ? "(null)"
                       : readBack.Length > 30 ? readBack[..30] + "…" : readBack;
            V.Log($"        ★ 实际读到：「{got}」");
        }

        V.Log("  ⬜ 请人工确认：回原窗口按 Ctrl+V，文字出现了吗？");
        V.Log("  ★ 注意：面板**不会**自动关闭 —— 选错了可以再点别的。鼠标离开才收起。");
    }

    // ── 自检 ────────────────────────────────────────────────────────

    private static void RunAssertions()
    {
        V.Log("");
        V.Log("── 尺寸（毫米 / DIP / 物理像素 三个单位一起看）──────────");
        V.Log("  " + Geometry.Describe("条子宽", Geometry.StripWidthMm, 1.5));
        V.Log("  " + Geometry.Describe("条子高", Geometry.StripHeightMm, 1.5));
        V.Log("  " + Geometry.Describe("面板宽", Geometry.PanelWidthMm, 1.5));
        V.Log("  " + Geometry.Describe("面板高", Geometry.PanelHeightMm, 1.5));
        V.Log($"  标定系数 MmCalibration = {Geometry.MmCalibration:0.####}"
              + "（1.0 = 完全相信 Windows 的 DPI）");
        V.Log("  ★ 位置：条子中心在工作区宽度 3/4 处，紧贴顶边，零缝隙");
        V.Log("  ★ 截图辅助：跑 tools/measure.ps1 可以把条子那一块放大 8 倍存成图");

        Console.WriteLine();
        Console.WriteLine("──────────────────────────────────────────────────");
        Console.WriteLine("现在可以开始操作了。建议顺序：");
        Console.WriteLine("  1. 【先看屏幕上方】条子中心应该在屏幕从左数 3/4 的位置、紧贴顶边");
        Console.WriteLine("     拿尺子量一下长度：目标 2cm");
        Console.WriteLine("     量出来不是 2cm 就告诉我实际多少 → 我调 Geometry.MmCalibration");
        Console.WriteLine("  2. 看条子【下面有没有黑色阴影】—— 这一轮专门要确认的");
        Console.WriteLine("  3. 看颜色：在深色壁纸/深色窗口上看得清吗");
        Console.WriteLine("  4. 复制 8 段以上不同文本（记事本 / 浏览器 / 终端各来一次）");
        Console.WriteLine("  5. 开记事本打几个字，把插入符停在文档里（焦点基准）");
        Console.WriteLine("  6. 鼠标【慢慢】推向条子 → 压上去 → 停住");
        Console.WriteLine("     数 ~0.35 秒 → 面板应该展开");
        Console.WriteLine("     ★ 触发范围只有条子那么大，压准一点 —— 这是你要的效果");
        Console.WriteLine("  7. 重点看记事本：标题栏变灰没有？插入符还在闪吗？");
        Console.WriteLine("  8. 在面板上滚滚轮 → 位移看得清吗？（一步 24 DIP）");
        Console.WriteLine("  9. 鼠标移开 → 稍等 → 面板收起，回到细线");
        Console.WriteLine(" 10. 再展开 → 点一条 → 面板收起，看日志的 [自证] 行");
        Console.WriteLine(" 11. 回记事本按 Ctrl+V → 文字应该出现");
        Console.WriteLine(" 12. 再展开一次 → 刚点过的那条应该跑到最上面");
        Console.WriteLine(" 13. 把最大化窗口的关闭按钮移到条子下面 → 还能点中吗？（Q4）");
        Console.WriteLine();
        Console.WriteLine("按 Ctrl+Alt+Q 结束（全局逃生热键，不管焦点在哪都有效）。");
        Console.WriteLine("──────────────────────────────────────────────────");
        Console.WriteLine();
    }

    private static void Cleanup()
    {
        Console.WriteLine();
        Console.WriteLine("── 收尾 ──────────────────────────────────────────");

        UnregisterHotKey(_msgHwnd, ExitHotKeyId);
        if (_altHotkeyRegistered) UnregisterHotKey(_msgHwnd, AltHotKeyId);
        RemoveClipboardFormatListener(_msgHwnd);

        // ★ 光标还原。这一步绝不能省 —— 漏了用户得重启才能看到鼠标。
        CursorHider.Restore();

        V.Log($"  展开次数：{_expandCount}　收起次数：{_collapseCount}");
        V.Log($"  九宫格弹出次数：{_gridShowCount}");
        V.Log($"  剪贴板事件数：{_clipboardEventCount}");
        V.Log($"  剪贴板最多重试：{_clipboardOpenAttemptsMax} 次");
        V.Log($"  前台漂移次数：{_focusDriftCount}（Q2，0 才是好消息）");
        V.Log($"  历史条数：{History.Items.Count}");

        V.Record("Q1", "条子真实像素高度 / 窗口地板有没有挡住",
                 _panel is null ? "⬜ 未采样" : $"收起态实际 {_panel.BoundsText}",
                 null,
                 "必须你肉眼确认右上角那条细线看起来是细的");

        V.Record("Q2", "悬停展开 + 全程不抢焦点",
                 _focusDriftCount == 0
                     ? $"展开 {_expandCount} 次，前台窗口 0 次漂移"
                     : $"★ 漂移 {_focusDriftCount} 次",
                 _expandCount == 0 ? (bool?)null : _focusDriftCount == 0,
                 _expandCount == 0
                     ? "重新跑一次，至少展开 3 次并点几条"
                     : _focusDriftCount == 0
                         ? "D4「不抢焦点」在条子这条链路上同样成立"
                         : "★ 这是致命的，需要重新设计");

        V.Record("Q3", "点条目之后剪贴板真的变成那一条了吗",
                 "看 [自证] 行的「与原文一致」",
                 null,
                 "必须你回原窗口按 Ctrl+V 确认文字出现");

        V.Record("Q4", "收起态的 1cm² 能不能鼠标穿透",
                 "待实测",
                 null,
                 "把最大化窗口的关闭按钮移到条子下面，还能点中吗？");

        V.Record("Q5", "面板上的滚轮能不能滚",
                 "看日志有没有 [滚轮] 行",
                 null,
                 "收不到就用 ▲▼ 按钮（已经画好了）");

        V.PrintTable();

        Console.WriteLine();
        Console.WriteLine("把上面的表格贴进 docs\\技术验证报告.md。");
        Console.WriteLine();
    }
}
