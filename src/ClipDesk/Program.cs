using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using static ClipDesk.NativeMethods;

namespace ClipDesk;

/// <summary>
/// ClipDesk 的常驻入口：监听剪贴板、显示收缩条和方向网格。
/// 所有正常运行都在后台完成，不依赖控制台窗口。
/// </summary>
internal static class Program
{
    private const int ExitHotKeyId = 0x0C21;
    private const int AltHotKeyId = 0x0C22;
    private const int TuningHotKeyId = 0x0C23;

    /// 剪贴板事件去抖窗口（毫秒）。复制程序可能在短时间内发送多次更新，
    /// 这里保留足够短的窗口以避免错过第一条完整内容。
    private const int ClipboardDebounceMs = 20;

    /// <summary>收起态轮询频率：20Hz。只在等鼠标靠近，不用太勤。</summary>
    private const int PollIdleMs = 50;

    /// <summary>展开态轮询频率：60Hz。要跟手判断鼠标有没有离开。</summary>
    private const int PollActiveMs = 16;

    /// <summary>
    /// 启动期诊断：只写文件，**不弹窗**。
    /// 热键被别的程序占用是常见情况，不该用一堵墙拦住用户。
    /// </summary>
    private static void Log(string message) => LogFile.Write(message);

    /// <summary>
    /// 用户主动操作失败时的提示（写剪贴板、模拟粘贴）。
    /// 这类失败用户必须知道，否则他会以为"点了没反应"。
    /// </summary>
    private static void Notify(string message)
    {
        LogFile.Write(message);
        if (!_uiReady) return;
        System.Windows.MessageBox.Show(message, "ClipDesk", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
    private static bool _uiReady;
    private static readonly ClipboardHistory History = new(20);

    /// <summary>运行时日志只用于向用户提示不可忽略的失败。</summary>
    private static readonly GlassParams Glass = GlassParams.Load();


    private static HwndSource? _msgWindow;
    private static IntPtr _msgHwnd;
    private static StripPanelWindow? _panel;
    private static Application? _app;

    private static uint _lastSequence;
    private static DateTime _lastClipboardAt = DateTime.MinValue;
    private static bool _selfWriting;


    // 九宫格
    private static GridWindow? _grid;
    private static SettingsWindow? _settings;
    private static TrayIcon? _tray;
    private static bool _altHotkeyRegistered;
    private static volatile bool _altDown;
    private static DateTime _altDownAt;
    private static bool _gridVisible;

    private static readonly EdgeTriggerStateMachine Trigger = new();

    [STAThread]
    private static void Main()
    {
        _app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        _app.Startup += (_, _) =>
        {
            CreateMessageWindow();
            CreatePanel();
            StartWatcher();
            CreateTrayIcon();
        };
        _app.Exit += (_, _) => Cleanup();
        _app.Run();
    }

    private static void CreateMessageWindow()
    {
        // 一个永不显示的顶层窗口，专门收 WM_CLIPBOARDUPDATE 和逃生热键。
        var p = new HwndSourceParameters("ClipDeskMessageWindow")
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

        AddClipboardFormatListener(_msgHwnd);
        _lastSequence = GetClipboardSequenceNumber();

        RegisterHotKey(_msgHwnd, ExitHotKeyId,
                       MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_Q);

        // Alt+V 网格使用全局热键，确保按住手势期间不需要先点击窗口。
        _altHotkeyRegistered = RegisterHotKey(_msgHwnd, AltHotKeyId,
                                              MOD_ALT | MOD_NOREPEAT, VK_V);

        // ── 玻璃调参热键 Ctrl+Alt+G ──
        RegisterHotKey(_msgHwnd, TuningHotKeyId,
                       MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_G);

        // ★★ 热键被占用时**醒目警告**。
        //
        if (!_altHotkeyRegistered)
        {
            Log("ClipDesk: Alt+V 热键注册失败，可能已被其他程序占用。");
        }

    }

    private static void CreatePanel()
    {
        _panel = new StripPanelWindow(History, Log, Glass);
        _panel.ItemActivated += OnItemActivated;

        // 先建好 HWND（这不显示）
        var helper = new WindowInteropHelper(_panel);
        helper.EnsureHandle();

        _panel.Prewarm();

        // ★ 预热之后必须**真的摆出来**。
        //   Prewarm() 结尾是 SW_HIDE，所以那之后条子是藏着的 ——
        //   不补这一下，启动后屏幕上什么都没有（第一次差点就这么交出去了）。
        //   条子是常驻可见的东西，程序一起来就该在那儿。
        _panel.ShowCollapsed();

        // ── 九宫格（Alt+V）也一起预热 ──
        //   预热必须走一遍 WPF Show()，否则视觉树没 Measure/Arrange，
        //   热键一按只会得到一个纯色空框（S0 为这件事白折腾过两轮）。
        _grid = new GridWindow(History, Log, Glass);
        new WindowInteropHelper(_grid).EnsureHandle();
        _grid.Prewarm();

        // 到这里三个界面都已就绪，之后的失败才允许弹窗打扰用户。
        _uiReady = true;

    }

    // ── 消息处理 ────────────────────────────────────────────────────

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_HOTKEY when wParam.ToInt32() == ExitHotKeyId:
                        _app?.Shutdown();
                handled = true;
                break;

            case WM_HOTKEY when wParam.ToInt32() == AltHotKeyId:
                OnAltPressed();
                handled = true;
                break;

            case WM_HOTKEY when wParam.ToInt32() == TuningHotKeyId:
                OpenSettings();
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

        var (text, _) = ClipboardIo.ReadText();

        if (text is null)
        {
            return;
        }

        History.Add(text, "?");
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
            return;
        }

        // 条子面板如果正开着，先收起来，免得两个窗口打架
        if (_panel is not null && _panel.IsExpanded)
        {
            Trigger.ForceCollapse();
            _panel.ShowCollapsed();
        }


        _grid.ShowAtCursor();
        _gridVisible = true;

    }

    /// <summary>松开 Alt —— 结算选中的那一格。</summary>
    private static void OnAltReleased()
    {
        if (_grid is null) return;

        int index = _grid.CommitSelection();

        if (index < 0)
        {
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
            return;
        }


        // 粘出去的那条要变成最新的 —— 下次它就该出现在 01（正上方）
        History.Promote(text);

        // 写回剪贴板 → 注入 Ctrl+V
        _selfWriting = true;
        if (!ClipboardIo.WriteText(text))
        {
            _selfWriting = false;
            Notify("写剪贴板失败：请重试一次。");
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
        if (sent != 4) Notify($"粘贴模拟失败：SendInput 仅发送 {sent}/4 个输入事件。");
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
                // ★ 轮询频率取"两件事里更急的那一件"。
                //
                //   ★★ 这里有个把我坑惨的 bug（第四轮才发现）：
                //
                //   九宫格和条子面板**共用这一个线程**，但频率原来只看了
                //   条子面板的状态机 Trigger.Current ——
                //   而九宫格开着的时候，条子面板是收起状态（Idle），
                //   于是线程走的是 50ms 那一档！
                //
                //   结果：我把九宫格逐格播放的节拍从 60ms 调到 25ms，
                //   用户说"感觉跟没改一样" —— 因为实际节拍是 max(25, 50) = 50ms，
                //   被轮询周期卡住了，改常量当然没用。
                //
                //   修法：九宫格开着时也走 16ms 那一档。
                bool stripNeedsFastPoll =
                    Trigger.Current == EdgeTriggerStateMachine.Phase.Expanded
                    || Trigger.Current == EdgeTriggerStateMachine.Phase.CollapsePending;

                int sleep = (_gridVisible || stripNeedsFastPoll)
                    ? PollActiveMs      // 16ms —— 九宫格逐格播放的上限就是它
                    : PollIdleMs;       // 50ms —— 平时等鼠标靠近，不用那么勤

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

    }

    private static void OnExpandRequested()
    {
        if (_panel is null) return;

        _panel.ShowExpanded();
        _panel.FillList();

    }

    private static void OnCollapseRequested(int cursorX, int cursorY)
    {
        if (_panel is null) return;

        _panel.ShowCollapsed();

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

        // ── 顶置：点过的那条要变成最新的 ──
        //   ★ 注意：**不再刷新列表 UI** —— 一刷新选中框就没了，
        //     而用户要的是"点完之后还看得见我选了哪个"。
        //     列表顺序在下次展开（FillList）时自然会更新。
        History.Promote(text);

        // 写剪贴板；用户自己按 Ctrl+V，程序不模拟按键。
        _selfWriting = true;
        if (!ClipboardIo.WriteText(text))
        {
            _selfWriting = false;
            Notify("写剪贴板失败：请重试一次。");
            return;
        }
        _lastSequence = GetClipboardSequenceNumber();
    }

    /// <summary>
    /// 创建托盘图标。**必须在三个界面就绪之后** —— 图标一出现用户就会去点它，
    /// 那时候设置中心要能打开。
    /// </summary>
    private static void CreateTrayIcon()
    {
        try
        {
            _tray = new TrayIcon(
                onOpenSettings: () => _app?.Dispatcher.Invoke(OpenSettings),
                onExit: () => _app?.Dispatcher.Invoke(() => _app.Shutdown()));
        }
        catch (Exception ex)
        {
            // 托盘图标建不起来不该让整个程序挂掉 —— 条子和热键还能用。
            Log($"创建托盘图标失败：{ex.Message}");
        }
    }

    /// <summary>打开设置中心（托盘菜单和 Ctrl+Alt+G 都走这里）。</summary>
    private static void OpenSettings()
    {
        if (_settings is null)
        {
            _settings = new SettingsWindow(Glass, RefreshGlassSurfaces);
            _settings.Closed += (_, _) => _settings = null;
            _settings.Show();
        }
        else if (_settings.IsVisible)
        {
            _settings.Activate();
        }
        else
        {
            _settings.Show();
        }
    }

    /// <summary>
    /// 调参滑块变动后，让三个界面下一次出现时使用新参数。
    ///
    /// ★ 九宫格 / 面板不是常驻的，下一次 Show 时自然会按新参数抓屏。
    ///   条子是常驻的，必须立即重画 —— 但不在调参回调里同步抓屏，
    ///   否则拖动滑块会卡住调参窗口。用 Dispatcher 异步排一次即可。
    /// </summary>
    private static void RefreshGlassSurfaces()
    {
        if (_panel is null || !_panel.IsVisible) return;

        _panel.Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Background,
            new Action(() =>
            {
                // 条子/面板当前是什么尺寸，就按当前 Bounds 重新渲染。
                if (_panel is null) return;

                var b = _panel.HitRect;
                _panel.RefreshGlassForTuning(b);
            }));
    }

    private static void Cleanup()
    {
        if (_msgHwnd != IntPtr.Zero)
        {
            UnregisterHotKey(_msgHwnd, ExitHotKeyId);
            if (_altHotkeyRegistered) UnregisterHotKey(_msgHwnd, AltHotKeyId);
            UnregisterHotKey(_msgHwnd, TuningHotKeyId);
            RemoveClipboardFormatListener(_msgHwnd);
        }
        CursorHider.Restore();

        // ★ 托盘图标必须先 Dispose，否则图标会残留在右下角，
        //   直到用户把鼠标划过去才消失 —— 看起来像没退干净。
        _tray?.Dispose();
        _tray = null;
    }

}
