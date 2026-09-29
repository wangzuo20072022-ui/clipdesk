using System;

namespace ClipDesk;

/// <summary>
/// 「鼠标靠近 → 展开 → 离开 → 收起」的状态机。**纯逻辑，不碰 UI，可单测。**
///
/// 为什么把它单独拿出来：手感调不对的时候，能立刻分清是
/// **"状态机算错了"** 还是 **"窗口没按状态机说的做"**。
/// 这和 S0 里先跑 GridSelectionTests 再开窗口是同一个套路。
///
/// 时钟从外面注入（Func&lt;DateTime&gt;），所以单测能"快进时间"，不用真的等 350ms。
///
/// ── 状态图 ────────────────────────────────────────────────────
///
///   Idle（收起态）
///     ├─ 光标进命中矩形 ────────► HoverPending（记下进圈时刻）
///     └─ 否则 ─────────────────► Idle
///
///   HoverPending（在等 350ms）
///     ├─ 还在圈内且满 350ms ───► ExpandRequested ─► Expanded
///     ├─ 没满就出去了 ─────────► Idle      ←「路过不误触」全靠这条
///     └─ 否则 ─────────────────► HoverPending
///
///   Expanded（面板开着）
///     ├─ 光标离开扩展矩形 ─────► CollapsePending（记下离开时刻）
///     └─ 光标一直在里面 ───────► Expanded
///
///   CollapsePending（在等 150ms 迟滞）
///     ├─ 150ms 内回到矩形 ─────► Expanded   ← 防「手抖一下面板就没了」
///     └─ 150ms 到 ────────────► CollapseRequested ─► Idle
///
/// ── 迟滞为什么分两处 ─────────────────────────────────────────
///   进入侧 350ms 是**需求**（用户要的 300~400ms，防止瞄一眼就弹）。
///   离开侧 150ms 是**防抖**，不是需求 —— 手划过边界时不该反复收放。
/// </summary>
internal sealed class EdgeTriggerStateMachine
{
    public enum Phase
    {
        /// <summary>收起态，等鼠标靠近</summary>
        Idle,

        /// <summary>鼠标进来了，在等够 350ms</summary>
        HoverPending,

        /// <summary>面板开着</summary>
        Expanded,

        /// <summary>鼠标离开了，在等 150ms 迟滞</summary>
        CollapsePending,
    }

    /// <summary>鼠标停多久才算"想用"（毫秒）。用户要的 300~400ms 区间，取中值。</summary>
    public const int HoverDwellMs = 350;

    /// <summary>离开后等多久才收（毫秒）。防抖，不是需求。</summary>
    public const int CollapseDelayMs = 150;

    /// <summary>面板刚展开后至少活这么久（毫秒），期间不因离开而收起</summary>
    public const int MinExpandedMs = 200;

    /// <summary>点太快（毫秒）：展开后立刻点，多半是误触，不结算</summary>
    public const int MinTimeBeforeCommitMs = 0;

    private readonly Func<DateTime> _now;

    public Phase Current { get; private set; } = Phase.Idle;

    /// <summary>进圈的时刻（算 350ms 用）</summary>
    private DateTime _hoverSince = DateTime.MinValue;

    /// <summary>面板展开的时刻（算最小展开时长用）</summary>
    private DateTime _expandedSince = DateTime.MinValue;

    /// <summary>离开的时刻（算 150ms 迟滞用）</summary>
    private DateTime _leftSince = DateTime.MinValue;

    /// <summary>上一次进圈时刻，用于防抖</summary>
    private DateTime _lastExpandAt = DateTime.MinValue;

    public EdgeTriggerStateMachine(Func<DateTime>? clock = null)
    {
        _now = clock ?? (() => DateTime.Now);
    }

    /// <summary>
    /// 每 tick 喂一次"鼠标现在在不在命中矩形里"，返回这一 tick 该做什么。
    ///
    /// 调用方（轮询线程）只负责读鼠标位置 + 算包含关系，
    /// **所有的时间判断都在这里** —— 这样单测才能快进时间。
    /// </summary>
    public Action Tick(bool cursorInside)
    {
        var now = _now();

        switch (Current)
        {
            case Phase.Idle:
                if (cursorInside)
                {
                    Current = Phase.HoverPending;
                    _hoverSince = now;
                }
                return Action.None;

            case Phase.HoverPending:
                if (!cursorInside)
                {
                    // ★ 没等够就走了 —— 路过，不展开。
                    //   这一条是"鼠标扫过右上角不会弹出面板"的全部依据。
                    Current = Phase.Idle;
                    return Action.None;
                }

                if ((now - _hoverSince).TotalMilliseconds >= HoverDwellMs)
                {
                    Current = Phase.Expanded;
                    _expandedSince = now;
                    _lastExpandAt = now;
                    return Action.Expand;
                }
                return Action.None;

            case Phase.Expanded:
                if (cursorInside)
                {
                    // 回到里面了（可能是从 CollapsePending 回来的）
                    _leftSince = DateTime.MinValue;
                    return Action.None;
                }

                // 刚展开就离开 → 给一个最小存活时间，避免"点完还没看清就没了"
                if ((now - _expandedSince).TotalMilliseconds < MinExpandedMs)
                {
                    return Action.None;
                }

                Current = Phase.CollapsePending;
                _leftSince = now;
                return Action.None;

            case Phase.CollapsePending:
                if (cursorInside)
                {
                    // ★ 150ms 内回来了 —— 取消收起。
                    //   这一条是"手抖一下面板不会闪掉"的全部依据。
                    Current = Phase.Expanded;
                    _leftSince = DateTime.MinValue;
                    return Action.None;
                }

                if ((now - _leftSince).TotalMilliseconds >= CollapseDelayMs)
                {
                    Current = Phase.Idle;
                    return Action.Collapse;
                }
                return Action.None;

            default:
                return Action.None;
        }
    }

    /// <summary>
    /// 点了某一条。
    ///
    /// ★ 第三轮改：**不再立刻收起**。
    ///   用户的原话：「点击之后是选中了想粘贴的文字，但窗口依然不关闭，
    ///   等到我的鼠标离开后再关闭」—— 选错了还能改，不用重新悬停一次。
    ///
    ///   所以这里什么都不做：面板继续开着，
    ///   鼠标离开后照常走 CollapsePending → Collapse 那套（带 150ms 迟滞）。
    /// </summary>
    public void CommitClicked()
    {
        // 故意留空。收起的唯一触发条件是"鼠标离开"（或外部 ForceCollapse）。
        //
        // 历史：第一轮这里做的是"立刻回到 Idle"，
        // 结果用户点了发现选错也没法改，窗口已经没了。
    }

    /// <summary>外部强制收起（逃生、异常路径）</summary>
    public void ForceCollapse()
    {
        Current = Phase.Idle;
        _hoverSince = DateTime.MinValue;
        _leftSince = DateTime.MinValue;
    }

    public enum Action
    {
        None,
        Expand,
        Collapse,
    }

    public string Describe() => Current switch
    {
        Phase.Idle => "收起（等靠近）",
        Phase.HoverPending => $"鼠标进来了，等 {HoverDwellMs}ms",
        Phase.Expanded => "面板已展开",
        Phase.CollapsePending => $"鼠标离开了，等 {CollapseDelayMs}ms 迟滞",
        _ => "?",
    };
}
