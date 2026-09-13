using System;
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
    private const int ClipboardDebounceMs = 100;

    private static readonly Verdict V = new();
    private static readonly ClipboardHistory History = new(5);
    private static readonly Stopwatch _bootClock = new();

    private static HwndSource? _msgWindow;
    private static IntPtr _msgHwnd;
    private static PanelWindow? _panel;
    private static Application? _app;

    private static uint _lastSequence;
    private static DateTime _lastClipboardAt = DateTime.MinValue;
    private static int _clipboardEventCount;
    private static int _clipboardOpenAttemptsMax;
    private static bool _selfWriting;          // 我们自己写剪贴板时置位，防回环

    private static IntPtr _foregroundAtHotkey;
    private static bool _panelVisible;
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
        PrintHeader();

        // 可选的自动退出：S0-Spine.exe 90 → 90 秒后自动收尾并打印判定表。
        // 手动慢跑时不带参数即可。
        if (args.Length > 0 && int.TryParse(args[0], out int seconds) && seconds > 0)
        {
            _autoExitSeconds = seconds;
            Console.WriteLine($"[启动] 自动退出已设置：{seconds} 秒后打印判定表并退出");
        }

        _app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

        // Ctrl+C 要能触发收尾，否则判定表打不出来 —— 那等于白跑一场
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;                       // 拦住默认的强杀
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
    }

    private static void CreatePanel()
    {
        _panel = new PanelWindow(History, V.Log);
        _panel.ItemActivated += OnPanelItemActivated;

        // 先建好 HWND（这不显示），之后的显隐用 ShowWindow —— 避免 WPF 的 Show 抢焦点
        var helper = new WindowInteropHelper(_panel);
        helper.EnsureHandle();

        // S2「首次显示延迟」的第一手数字：WPF 冷启动到 HWND 就绪花了多久。
        V.Log($"  ★ 冷启动到 HWND 就绪：{_bootClock.ElapsedMilliseconds} ms");
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

        if (_panelVisible)
        {
            HidePanel("热键再按一次");
            return;
        }

        // ── Q1 的核心：弹之前先记下前台窗口 ──
        _foregroundAtHotkey = GetForegroundWindow();
        string beforeTitle = GetWindowTitle(_foregroundAtHotkey);
        uint beforePid = GetWindowPid(_foregroundAtHotkey);

        V.Log($"");
        V.Log($"[热键] 第 {_hotkeyFireCount} 次触发（距上次 {gap.TotalMilliseconds:0}ms）");
        V.Log($"  弹出前 前台 = 0x{_foregroundAtHotkey:X8} 「{beforeTitle}」 pid={beforePid}");

        _panel!.ShowNoActivate();
        _panelVisible = true;

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
        _panel?.HidePanel();
        _panelVisible = false;
        V.Log($"[面板] 关闭（{reason}）");
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

    // ── 粘贴 ────────────────────────────────────────────────────────

    private static void OnPanelItemActivated(string text)
    {
        _panelVisible = false;

        V.Log($"");
        V.Log($"[粘贴] 选中 → 「{(text.Length > 40 ? text[..40] + "…" : text)}」");

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
        var inputs = new INPUT[4];

        inputs[0].Type = INPUT_KEYBOARD;
        inputs[0].Keyboard = new KEYBDINPUT { Vk = 0x11 /*Ctrl*/, Flags = 0 };

        inputs[1].Type = INPUT_KEYBOARD;
        inputs[1].Keyboard = new KEYBDINPUT { Vk = (ushort)VK_V, Flags = 0 };

        inputs[2].Type = INPUT_KEYBOARD;
        inputs[2].Keyboard = new KEYBDINPUT { Vk = (ushort)VK_V, Flags = KEYEVENTF_KEYUP };

        inputs[3].Type = INPUT_KEYBOARD;
        inputs[3].Keyboard = new KEYBDINPUT { Vk = 0x11, Flags = KEYEVENTF_KEYUP };

        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        V.Log($"  SendInput(Ctrl+V) 4 个事件 → 实际送出 {sent} 个"
              + (sent == 4 ? "" : $"  ★ 有 {4 - sent} 个被系统丢弃"));
    }

    // ── Alt 松开检测（替代全局钩子）────────────────────────────────

    private static void StartAltReleaseWatcher()
    {
        // 60Hz 轮询。正式项目里九宫格阶段才需要这个频率，
        // S0 只为了"再按一次关闭"能工作。
        var t = new Thread(() =>
        {
            while (true)
            {
                Thread.Sleep(16);

                if (!_altDown) continue;

                bool down = (GetAsyncKeyState(VK_LMENU) & 0x8000) != 0
                         || (GetAsyncKeyState(VK_RMENU) & 0x8000) != 0;

                if (!down)
                {
                    _altDown = false;
                }

                // Esc → 关闭面板（NOACTIVATE 窗口收不到键盘消息，只能这样）
                if ((GetAsyncKeyState(VK_ESCAPE) & 0x8000) != 0 && _panelVisible)
                {
                    V.Log("[面板] Esc 按下");
                    _panel!.Dispatcher.Invoke(() => HidePanel("Esc"));
                }
            }
        })
        {
            IsBackground = true,
            Name = "AltReleaseWatcher",
        };
        t.Start();
    }

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

        // Q2
        V.Record("Q2", "AddClipboardFormatListener 是否挂上",
                 "已挂上（触发次数需人工操作后看日志）",
                 null,
                 "在记事本/Chrome/VS Code/资源管理器 各复制 5 次，看事件计数");

        // Q1 在 Cleanup 里统一判定（需要跑完才有数据），这里不重复记录

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
        Console.WriteLine("  3. 在 Chrome 里也复制一次，看是否照样捕获");
        Console.WriteLine("  4. 按 Alt+V → 面板应该在鼠标处弹出，且记事本仍然是前台");
        Console.WriteLine("  5. 点第 1 条 → 记事本里应该粘出那段文字");
        Console.WriteLine("  6. 用【管理员身份】开一个记事本，重复 1~5，看 Q4");
        Console.WriteLine();
        Console.WriteLine("按 Ctrl+C 结束（在这之前别关控制台，日志是证据）。");
        Console.WriteLine("──────────────────────────────────────────────────");
        Console.WriteLine();
    }

    private static void Cleanup()
    {
        Console.WriteLine();
        Console.WriteLine("── 收尾 ──────────────────────────────────────────");

        if (_hotkeyRegistered) UnregisterHotKey(_msgHwnd, HotKeyId);
        RemoveClipboardFormatListener(_msgHwnd);

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
