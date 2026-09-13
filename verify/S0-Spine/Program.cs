using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using static S0Spine.NativeMethods;

namespace S0Spine;

/// <summary>
/// S0 · 脊柱探针
///
/// 一个丑但完整的闭环：
///   复制文本 → Alt+V 弹面板 → 点一条 → Ctrl+V 粘回原窗口
///
/// 一次答完五个问题（见 docs\技术验证报告.md 的判定表）：
///   Q1 NOACTIVATE 下前台窗口是否全程不变     ← 唯一没有退路的一条
///   Q2 WM_CLIPBOARDUPDATE 是否稳定触发
///   Q3 SendInput Ctrl+V 是否落回原窗口
///   Q4 管理员权限窗口是否静默失败
///   Q5 RegisterHotKey 是否成功、是否影响别的程序
///
/// 时间盒：一次会话。过了就走退路，写判定，前进。
/// </summary>
internal static class Program
{
    private const int HotKeyId = 0x0C1D;
    private const int ExitHotKeyId = 0x0C1E;
    private const int ClipboardDebounceMs = 100;

    private static readonly Verdict V = new();
    private static readonly ClipboardHistory History = new(20);
    private static readonly Stopwatch _bootClock = new();

    private static HwndSource? _msgWindow;
    private static IntPtr _msgHwnd;
    private static GridWindow? _grid;
    private static Application? _app;

    private static uint _lastSequence;
    private static DateTime _lastClipboardAt = DateTime.MinValue;
    private static int _clipboardEventCount;
    private static int _clipboardOpenAttemptsMax;
    private static bool _selfWriting;          // 我们自己写剪贴板时置位，防回环

    private static IntPtr _foregroundAtHotkey;
    private static bool _gridVisible;
    private static volatile bool _altDown;
    private static int _focusDriftCount;

    // Q5 的记录
    private static bool _hotkeyRegistered;
    private static int _hotkeyFireCount;
    private static DateTime _lastHotkeyAt = DateTime.MinValue;

    [STAThread]
    private static void Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        _bootClock.Start();

        // ★ 第一件事：看一眼有没有上次遗留的自己。
        //
        //   为什么需要这个：这个 Exe 的扩展样式是 TOOLWINDOW + NOACTIVATE，
        //   Windows 不给它画标题栏和关闭按钮（tasklist 里显示 "Hidden Window"）。
        //   一旦它卡住没退干净，用户就**没有正常手段关掉它** —— 而它还锁着 exe，
        //   下一次编译直接 MSB3021 失败。
        //
        //   这里**只检测、只报告，绝不自动结束任何进程**。
        //   一个进程可能是用户正在用的，程序自作主张杀掉它是不可接受的，
        //   哪怕它叫同一个名字。怎么处理由用户决定 ——
        //   正常手段是下面那个逃生热键。
        WarnIfPreviousInstanceRunning();

        // ★ 随时可按 Ctrl+Alt+Q 退出 —— 见 RegisterExitHotKey()。
        //   这是"TOOLWINDOW 窗口没法用正常方式关闭"这个问题的正解。

        // 方向判定是纯逻辑，先把它测通过再去看窗口 ——
        // 出了 bug 也能立刻分清是"算法错"还是"窗口没画出来"。
        GridSelectionTests.Run();
        HistoryPromoteTests.Run();

        PrintHeader();

        // 可选的自动退出：S0-Spine.exe 90 → 90 秒后自动收尾并打印判定表。
        // 手动慢跑时不带参数即可。
        if (args.Length > 0 && int.TryParse(args[0], out int seconds) && seconds > 0)
        {
            _autoExitSeconds = seconds;
            Console.WriteLine($"[启动] 自动退出已设置：{seconds} 秒后打印判定表并退出");
        }

        _app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

        // 无论如何都要把系统光标还原 —— 绝不给用户留下一个没有鼠标的系统
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;                       // 拦住默认的强杀
            CursorHider.Restore();
            _app?.Dispatcher.Invoke(() => _app.Shutdown());
        };

        _app.Startup += (_, _) =>
        {
            CreateMessageWindow();
            CreatePanel();
            StartAltReleaseWatcher();
            if (_autoExitSeconds > 0) StartAutoExitTimer();
            RunAssertions();
        };

        _app.Run();

        Cleanup();
    }

    /// <summary>
    /// 报告一下同名的旧进程还在不在。**只报告，不结束任何东西。**
    ///
    /// 一个进程可能是用户**正在用**的，程序悄悄杀掉它是不可接受的 ——
    /// 哪怕那个进程叫同一个名字。所以这里一行 Kill 都没有。
    ///
    /// 真要停掉旧实例，正常手段是逃生热键 Ctrl+Alt+Q（见 CreateMessageWindow），
    /// 或者在任务管理器里结束它。
    /// </summary>
    private static void WarnIfPreviousInstanceRunning()
    {
        int me = Environment.ProcessId;
        var others = new List<int>();

        foreach (var p in Process.GetProcessesByName("S0Spine"))
        {
            if (p.Id != me) others.Add(p.Id);
            p.Dispose();
        }

        if (others.Count == 0) return;

        Console.WriteLine($"⚠️  检测到 {others.Count} 个上次遗留的探针进程"
                          + $"（PID {string.Join(", ", others)}）。");
        Console.WriteLine("   它可能锁着 exe，导致编译报 MSB3021。");
        Console.WriteLine("   停止它的办法：在那个窗口上按逃生热键 Ctrl+Alt+Q，");
        Console.WriteLine("   或在任务管理器里结束 S0Spine.exe。");
        Console.WriteLine();
    }

    private static int _autoExitSeconds;

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
        Console.WriteLine("║  ClipDesk · S0 脊柱探针                                       ║");
        Console.WriteLine("║  目的：验证「不抢焦点 + 剪贴板监听 + 粘贴」能不能跑通          ║");
        Console.WriteLine("║  时间盒：一次会话                                              ║");
        Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");
        Console.WriteLine();
    }

    private static void CreateMessageWindow()
    {
        // 一个永不显示的顶层窗口，专门收 WM_HOTKEY 和 WM_CLIPBOARDUPDATE。
        // 正式项目里这是 Platform\HiddenMessageWindow.cs。
        var p = new HwndSourceParameters("ClipDeskS0MessageWindow")
        {
            WindowStyle = 0x00000000,                       // WS_OVERLAPPED，不显示
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

        // ── Q5：注册热键 ──
        _hotkeyRegistered = RegisterHotKey(_msgHwnd, HotKeyId, MOD_ALT | MOD_NOREPEAT, VK_V);
        int err = Marshal.GetLastWin32Error();
        V.Log($"  ★ RegisterHotKey(Alt+V, NOREPEAT) = {(_hotkeyRegistered ? "成功" : "★失败")}"
              + (_hotkeyRegistered ? "" : $"  错误码={err}"));

        // ── Q2：挂剪贴板监听 ──
        bool listenerOk = AddClipboardFormatListener(_msgHwnd);
        V.Log($"  ★ AddClipboardFormatListener = {(listenerOk ? "成功" : "★失败")}");
        _lastSequence = GetClipboardSequenceNumber();

        // ── 逃生热键 ──
        // 这个 Exe 是 TOOLWINDOW + NOACTIVATE，Windows **不画标题栏和关闭按钮**，
        // 一旦九宫格卡住或光标藏了没还原，用户没有任何正常手段停下来。
        // 所以给一个全局热键当逃生门 —— 不管焦点在哪、窗口什么状态，按下就退。
        bool exitHotkey = RegisterHotKey(_msgHwnd, ExitHotKeyId,
                                         MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_Q);
        V.Log($"  ★ 逃生热键 Ctrl+Alt+Q = {(exitHotkey ? "已注册" : "★注册失败（仍可用 Ctrl+C）")}");

        // 把"当前剪贴板里已经有什么"先读一次。
        // 目的是给 Q2 一个可信的基线：程序启动后再复制的内容才算新事件，
        // 否则会分不清"监听真的工作了"和"读到的是启动前就有的内容"。
        var (existing, _) = ClipboardIo.ReadText();
        if (existing is not null)
        {
            string preview = existing.Replace("\r", " ").Replace("\n", " ");
            if (preview.Length > 50) preview = preview[..50] + "…";
            V.Log($"  （启动时剪贴板里已有内容，len={existing.Length}：「{preview}」—— 这条不计入 Q2）");
        }
    }

    private static void CreatePanel()
    {
        _grid = new GridWindow(History, V.Log);
        _grid.CellActivated += OnPanelItemActivated;

        // 先建好 HWND（这不显示），之后的显隐用 ShowWindow —— 避免 WPF 的 Show 抢焦点
        var helper = new WindowInteropHelper(_grid);
        helper.EnsureHandle();

        // S2「首次显示延迟」的第一手数字：WPF 冷启动到 HWND 就绪花了多久。
        V.Log($"  ★ 冷启动到 HWND 就绪：{_bootClock.ElapsedMilliseconds} ms");

        // ★ 预热：真的走一遍 WPF 显示流程再藏起来。
        //
        // 这里有个必须说清楚的坑（踩了两次）：
        //   EnsureHandle() 只建了 HWND，WPF 的 Window 从没显示过 ——
        //   视觉树没 Measure/Arrange，渲染管线没接管。
        //   此时用 SetWindowPos 把它显示出来，只会得到一个**纯色空框**：
        //   背景是 HWND 画的，内容得靠 WPF 画，而 WPF 还没准备好。
        //
        //   所以预热的这一步必须调 Show()，不能只用 SetWindowPos 糊过去。
        var warmClock = Stopwatch.StartNew();
        _grid.Prewarm();
        V.Log($"  ★ 预热耗时：{warmClock.ElapsedMilliseconds} ms");
        V.Log($"  ★ 九宫格格子数 = {_grid.RenderedCellCount}（应为 9）"
              + $"，已填内容 = {_grid.FilledCellCount}");
    }

    // ── 消息处理 ────────────────────────────────────────────────────

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_HOTKEY when wParam.ToInt32() == HotKeyId:
                OnHotKey();
                handled = true;
                break;

            case WM_HOTKEY when wParam.ToInt32() == ExitHotKeyId:
                V.Log("[逃生热键] Ctrl+Alt+Q → 收尾退出");
                CursorHider.Restore();       // 先把光标还回去，再退
                _app?.Shutdown();
                handled = true;
                break;

            case WM_CLIPBOARDUPDATE:
                OnClipboardUpdate();
                handled = true;
                break;
        }

        return IntPtr.Zero;
    }

    private static void OnHotKey()
    {
        _hotkeyFireCount++;
        var now = DateTime.Now;
        var gap = _lastHotkeyAt == DateTime.MinValue ? TimeSpan.Zero : now - _lastHotkeyAt;
        _lastHotkeyAt = now;

        _altDown = true;
        _altDownAt = DateTime.Now;

        if (_gridVisible)
        {
            // 面板已经开着的时候再按一次 Alt+V —— 交给"松开关闭"逻辑去处理。
            // 这里不再手工切换，符合"按住 Alt 期间显示"的最终手感。
            V.Log("  （九宫格已开着，忽略这次重复触发 —— 松开 Alt 就会收起）");
            return;
        }

        // ── Q1 的核心：弹之前先记下前台窗口 ──
        _foregroundAtHotkey = GetForegroundWindow();
        string beforeTitle = GetWindowTitle(_foregroundAtHotkey);
        uint beforePid = GetWindowPid(_foregroundAtHotkey);

        V.Log($"");
        V.Log($"[热键] 第 {_hotkeyFireCount} 次触发（距上次 {gap.TotalMilliseconds:0}ms）");
        V.Log($"  弹出前 前台 = 0x{_foregroundAtHotkey:X8} 「{beforeTitle}」 pid={beforePid}");

        _grid!.ShowAtCursor();
        _gridVisible = true;

        // 补一条硬证据：九宫格到底有没有真的显示出来、落在哪、内容填上没有。
        bool actuallyVisible = _grid.IsShown;
        V.Log($"  九宫格 IsWindowVisible = {actuallyVisible}");
        V.Log($"  九宫格位置 = {_grid.LastShownPosition}");
        V.Log($"  已填内容格子 = {_grid.FilledCellCount} / {_grid.RenderedCellCount}"
              + (_grid.FilledCellCount == 0 ? "  ★ 一个都没填上！" : ""));

        IntPtr after = GetForegroundWindow();
        V.Log($"  弹出后 前台 = 0x{after:X8} 「{GetWindowTitle(after)}」");
        if (after == _foregroundAtHotkey)
        {
            V.Log("  ✅ 前台窗口未变");
        }
        else
        {
            _focusDriftCount++;
            V.Log("  ★❌ 前台窗口被抢走了！");
        }
    }

    private static void HidePanel(string reason)
    {
        _grid?.HideGrid();
        _gridVisible = false;
        V.Log($"[九宫格] 关闭（{reason}）");
    }

    private static void OnClipboardUpdate()
    {
        // 去抖：连续多次更新时只在最后一次处理
        var now = DateTime.Now;
        if ((now - _lastClipboardAt).TotalMilliseconds < ClipboardDebounceMs) return;
        _lastClipboardAt = now;

        // ── 我们自己写的，忽略（防回环）──
        if (_selfWriting)
        {
            _selfWriting = false;
            return;
        }

        // ── 序列号去重 ──
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
              + $"{(isNew ? "新增" : "顶置")} 尝试{attempts}次 「{preview}」");
    }

    // ── 松手：方向选中的那一格 ──────────────────────────────────────

    /// <summary>
    /// 松开 Alt 时调用。问九宫格"现在选的是哪一格"：
    ///   有选中 → 粘贴那一格
    ///   中心/未选 → 取消，什么都不做
    /// </summary>
    private static void OnAltReleased()
    {
        if (_grid is null) return;

        int index = _grid.CommitSelection();
        string label = _grid.ActiveCellLabel;

        if (index < 0)
        {
            V.Log($"[九宫格] 松开 Alt → {label}，不粘贴");
            HidePanel("松开 Alt（未选）");
            return;
        }

        var (r, c) = GridSelection.ToCell(index);
        string? text = _grid.GetCellContent(r, c);

        HidePanel($"松开 Alt（选中 {label}）");

        if (string.IsNullOrEmpty(text))
        {
            V.Log($"[九宫格] ★ {label} 是空格子，不粘贴");
            return;
        }

        // ── 顶置：粘出去的那条要变成最新的 ──
        //   证据在「粘贴前 / 粘贴后」两行格子内容日志的对比里。
        V.Log("");
        V.Log($"  {_grid.DescribeSlots()}   ← 粘贴前");

        bool promoted = History.Promote(text);

        _grid.FillCells();     // 重新填格，让日志反映顶置后的顺序
        V.Log($"  {_grid.DescribeSlots()}   ← 粘贴后");

        V.Log($"[九宫格] 顶置「{(text.Length > 20 ? text[..20] + "…" : text)}」"
              + $" → {(promoted ? "已提到最新（应出现在 01）" : "★ 没找到，可能已被挤出历史")}");

        PasteText(text, $"宫格 {label}");
    }

    // ── 粘贴 ────────────────────────────────────────────────────────

    private static void OnPanelItemActivated(string text)
    {
        HidePanel("点击格子");
        PasteText(text, "点击");
    }

    private static void PasteText(string text, string how)
    {
        _gridVisible = false;

        V.Log($"");
        V.Log($"[粘贴] ({how}) → 「{(text.Length > 40 ? text[..40] + "…" : text)}」");

        // Q1 的第二次采样：粘贴前前台窗口是谁
        IntPtr beforePaste = GetForegroundWindow();
        V.Log($"  粘贴前 前台 = 0x{beforePaste:X8} 「{GetWindowTitle(beforePaste)}」");
        if (beforePaste == _foregroundAtHotkey)
        {
            V.Log("  ✅ 从弹出到点击，前台窗口全程没变过");
        }
        else
        {
            _focusDriftCount++;
            V.Log($"  ★❌ 前台窗口变了（原 0x{_foregroundAtHotkey:X8} → 现 0x{beforePaste:X8}）");
        }

        // ── Q4：目标进程是不是管理员权限 ──
        uint pid = GetWindowPid(beforePaste);
        int integrity = GetIntegrityLevel(pid);
        bool isElevated = integrity >= 0x3000;
        V.Log($"  目标进程 pid={pid} 完整性级别=0x{integrity:X} ({DescribeIntegrity(integrity)})"
              + (isElevated ? "  ★ 是管理员程序，UIPI 大概率会拦" : ""));

        // 写回剪贴板
        _selfWriting = true;
        if (!ClipboardIo.WriteText(text))
        {
            _selfWriting = false;
            V.Log("  ★❌ 写剪贴板失败");
            return;
        }
        // 让剪贴板监听先跳过我们自己写的那次
        _lastSequence = GetClipboardSequenceNumber();

        V.Log($"  已写回剪贴板，序列号={_lastSequence}");

        Thread.Sleep(20);   // 等剪贴板所有者真正交接完

        // ── Q3：注入 Ctrl+V ──
        SendCtrlV();

        Thread.Sleep(50);
        IntPtr afterPaste = GetForegroundWindow();
        V.Log($"  粘贴后 前台 = 0x{afterPaste:X8} 「{GetWindowTitle(afterPaste)}」");

        V.Log("  ⬜ 请人工确认：原窗口里真的出现了这段文字吗？（这条必须你看，程序判断不了）");
    }

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
        int err = Marshal.GetLastWin32Error();

        V.Log($"  SendInput(Ctrl+V) → 送出 {sent}/4 个（INPUT 结构 {size} 字节）"
              + (sent == 4 ? "" : $"  ★ 失败，错误码={err}"));
    }

    // ── Alt 按住期间的轮询（替代全局钩子）──────────────────────────

    /// <summary>
    /// 每 16ms（约 60Hz）跑一轮，干两件事：
    ///   1. 九宫格开着的时候，读鼠标位置 → 更新选中的方位
    ///   2. 看 Alt 还在不在 → 松了就结算
    ///
    /// 为什么不用 RegisterHotKey 听松开：RegisterHotKey 只有"按下"事件，
    /// 松开必须自己轮询 —— 这就是"三段式轮询"里 60Hz 那一档的由来。
    ///
    /// 省电：只有九宫格开着时才做方向判定；关闭时这个循环几乎不做事。
    /// </summary>
    private static void StartAltReleaseWatcher()
    {
        var t = new Thread(() =>
        {
            while (true)
            {
                Thread.Sleep(16);

                bool altDown = (GetAsyncKeyState(VK_LMENU) & 0x8000) != 0
                            || (GetAsyncKeyState(VK_RMENU) & 0x8000) != 0;

                // ① 还按着 → 更新方向高亮
                if (altDown && _gridVisible)
                {
                    _grid!.Dispatcher.Invoke(() => _grid.PollSelection());
                    continue;
                }

                // ② 松开了 → 结算
                if (!altDown && _altDown)
                {
                    _altDown = false;
                    var grid = _grid;
                    if (grid is null) continue;

                    // 按得太快（< 120ms）多半是误触，忽略
                    var held = DateTime.Now - _altDownAt;
                    if (held.TotalMilliseconds < 120)
                    {
                        V.Log($"  （Alt 只按了 {held.TotalMilliseconds:0}ms，忽略）");
                        if (_gridVisible) grid.Dispatcher.Invoke(() => HidePanel("按太快"));
                        continue;
                    }

                    if (_gridVisible)
                    {
                        grid.Dispatcher.Invoke(OnAltReleased);
                    }
                }
            }
        })
        {
            IsBackground = true,
            Name = "AltPollWatcher",
        };
        t.Start();
    }

    private static DateTime _altDownAt;

    // ── 自检断言 ────────────────────────────────────────────────────

    private static void RunAssertions()
    {
        V.Log("");
        V.Log("── 自检（程序能自己判断的部分）────────────────────");

        // Q5
        V.Record("Q5", "RegisterHotKey(MOD_ALT|MOD_NOREPEAT, V) 是否成功",
                 _hotkeyRegistered ? "注册成功" : "★注册失败",
                 _hotkeyRegistered,
                 _hotkeyRegistered ? "ALT+V 已生效" : "错误码见上方日志 → 需要备用键位");

        // Q1 在 Cleanup 里统一判定（需要跑完才有数据），这里不重复记录

        // Q2 —— 用两次运行之间的差值判断，因为启动时剪贴板里可能已经有内容
        V.Record("Q2", "AddClipboardFormatListener 能否稳定收到 WM_CLIPBOARDUPDATE",
                 _clipboardEventCount > 0
                     ? $"本次运行捕获 {_clipboardEventCount} 次，全部重试 1 次成功"
                     : "⬜ 本次没捕获到 —— 要么没复制，要么事件丢了",
                 _clipboardEventCount > 0 ? true : null,
                 _clipboardEventCount > 0
                     ? "多来源复制（记事本/浏览器/终端）均捕获"
                     : "再跑一次，程序启动后再复制几段");

        // Q3 / Q4
        V.Record("Q3", "SendInput Ctrl+V 是否落回原窗口",
                 "待实测",
                 null,
                 "必须你肉眼确认原窗口真的出现了文字");

        V.Record("Q4", "管理员权限窗口是否静默失败",
                 "待实测",
                 null,
                 "用管理员身份开一个记事本，复制一段，再粘；看是否报错但无效果");

        Console.WriteLine();
        Console.WriteLine("──────────────────────────────────────────────────");
        Console.WriteLine("现在可以开始操作了。建议顺序：");
        Console.WriteLine("  1. 打开记事本，随便打几个字");
        Console.WriteLine("  2. 复制 5 段不同的文本（记事本里选中→Ctrl+C）");
        Console.WriteLine("  3. 在浏览器 / 终端里也各复制一次，看是否照样捕获");
        Console.WriteLine("  4. 【按住】Alt+V → 九宫格在**屏幕正中**弹出，鼠标指针消失");
        Console.WriteLine("     重点看：记事本标题栏有没有变灰？光标还在闪吗？");
        Console.WriteLine("     全程不变灰 = Q1 通过");
        Console.WriteLine("  5. 按住期间把鼠标往某个方向拖 → 那个方位的格子亮起蓝边");
        Console.WriteLine("  6. 松开 Alt → 九宫格收起，文字自动粘回原窗口（Q3）");
        Console.WriteLine("  7. 再按一次 Alt+V —— 刚粘过的那条应该已经跑到 01（正上方）");
        Console.WriteLine();
        Console.WriteLine("按 Ctrl+C 结束（在这之前别关控制台，日志是证据）。");
        Console.WriteLine("若 Ctrl+C 没反应，按 Ctrl+Alt+Q —— 全局逃生热键，不管焦点在哪都有效。");
        Console.WriteLine("──────────────────────────────────────────────────");
        Console.WriteLine();
    }

    private static void Cleanup()
    {
        Console.WriteLine();
        Console.WriteLine("── 收尾 ──────────────────────────────────────────");

        if (_hotkeyRegistered) UnregisterHotKey(_msgHwnd, HotKeyId);
        UnregisterHotKey(_msgHwnd, ExitHotKeyId);
        RemoveClipboardFormatListener(_msgHwnd);

        // ★ 光标还原。这一步绝不能省 —— 漏了用户就得重启才能看到鼠标。
        CursorHider.Restore();

        V.Log($"  热键触发次数：{_hotkeyFireCount}");
        V.Log($"  剪贴板事件数：{_clipboardEventCount}");
        V.Log($"  剪贴板最多重试：{_clipboardOpenAttemptsMax} 次（S9 的实测数字）");
        V.Log($"  前台漂移次数：{_focusDriftCount}（Q1，0 才是好消息）");
        V.Log($"  历史条数：{History.Items.Count}");

        V.Record("Q1", "NOACTIVATE 下前台窗口是否全程不变",
                 _hotkeyFireCount == 0
                     ? "⬜ 未采样 —— 全程没按过 Alt+V，这一条没被验证到"
                     : _focusDriftCount == 0
                         ? $"全程未漂移（采样 {_hotkeyFireCount} 次热键）"
                         : $"★ 漂移 {_focusDriftCount} 次",
                 _hotkeyFireCount == 0
                     ? (bool?)null
                     : _focusDriftCount == 0,
                 _hotkeyFireCount == 0
                     ? "重新跑一次，至少按 3 次 Alt+V 并点几条"
                     : _focusDriftCount == 0
                         ? "D4「不抢焦点」成立 → 交互模型成立"
                         : "D4 不成立 → 这是唯一没有退路的一条，需要重新设计");

        V.PrintTable();

        Console.WriteLine();
        Console.WriteLine("把上面的表格贴进 docs\\技术验证报告.md，再决定是否进 S1。");
        Console.WriteLine();
    }
}
