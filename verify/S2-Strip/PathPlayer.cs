using System;
using System.Collections.Generic;

namespace S2Strip;

/// <summary>
/// 轮盘高亮的"逐格播放器"—— **纯逻辑，不碰 UI，可单测。**
///
/// 为什么单独拿出来：用户反馈「确实扫太快了」。原来是一 tick 就把整条路径
/// 同步 foreach 走完，60Hz 下几毫秒全过去，眼睛根本跟不上，看起来还是"啪地跳"。
///
/// 但"每格停多久"这件事一旦写进窗口类里，就没法验证了 —— 只能靠肉眼看。
/// 抽出来之后，节拍对不对、队列会不会积压、边界情况怎么样，全都能单测钉死。
/// 手感和算法混在一起调试是最费时间的，这里刻意分开。
///
/// 时钟从外面注入，所以单测能"快进时间"，不用真的睡。
/// </summary>
internal sealed class PathPlayer
{
    /// <summary>
    /// 路径上每一格停留多久（毫秒）。
    ///
    /// ── 这个数字调过四次，记下来免得再来回试 ──────────────────
    ///
    ///   第一版：0（一 tick 全走完）→ 用户「确实扫太快了」
    ///   第二版：60 → 用户「效果很差」，太慢
    ///   第三版：25 → 用户「感觉跟没改一样」★
    ///   第四版：**16** ← 现在这个
    ///
    /// ★ 第三版"跟没改一样"不是错觉，也不是常量没生效 ——
    ///   真正的瓶颈在**轮询频率**：当时九宫格开着的时候，
    ///   轮询线程走的是"条子面板收起"那一档（50ms），
    ///   所以实际节拍是 max(25, 50) = 50ms，改常量当然没用。
    ///   修在 Program.cs 的 StartWatcher：九宫格开着时也走 16ms 那一档。
    ///
    /// ── 为什么是 16 ────────────────────────────────────────────
    ///
    ///   16ms 正好等于轮询周期，也就是**"每 tick 一格"** ——
    ///   这是"逐格播放"这个机制能达到的最快速度，再小就没有意义了
    ///   （下一个 tick 本来就要等 16ms）。
    ///
    ///   所以 16 是这套机制的上限，不是保守值。还想更快就得放弃逐格播放，
    ///   退回"一 tick 直接跳到目标格" —— 那就没有扫过去的效果了。
    /// </summary>
    public const int StepDwellMs = 16;

    private readonly Func<DateTime> _now;
    private readonly Queue<int> _pending = new();

    /// <summary>下一格最早什么时候可以播</summary>
    private DateTime _nextStepAt = DateTime.MinValue;

    public PathPlayer(Func<DateTime>? clock = null)
    {
        _now = clock ?? (() => DateTime.Now);
    }

    /// <summary>队列里还有没有没播完的格子</summary>
    public bool IsPlaying => _pending.Count > 0;

    public int PendingCount => _pending.Count;

    /// <summary>最近一次真正播出去的格子；-1 = 还没播过</summary>
    public int LastStep { get; private set; } = -1;

    // ── 实测节拍 ────────────────────────────────────────────────────
    //
    // 为什么要有这个：手感调了三轮都是"我改了个数字，用户说没变化"，
    // 因为**真实节拍并不等于 StepDwellMs** —— 它受轮询频率限制。
    // 与其继续猜，不如把**实际测到的间隔**打出来。
    //
    // 用法：一轮播放结束后（Clear 之前）读一次，日志里就能看到
    // "设的是 16ms，实际跑出来是 XXms"。

    private DateTime _firstStepAt = DateTime.MinValue;
    private DateTime _lastStepAt = DateTime.MinValue;
    private int _stepCount;

    /// <summary>上一轮播放里，相邻两格之间**实际**隔了多少毫秒。0 = 没测到。</summary>
    public double ObservedStepMs { get; private set; }

    /// <summary>上一轮播放一共走了几格</summary>
    public int ObservedStepCount { get; private set; }

    /// <summary>
    /// 排入一条路径（含终点，不含起点）。
    ///
    /// ★ 排完之后**第一格立刻可播**，不等节拍 ——
    ///   否则每次换格都要先卡一个节拍才动，手感会发滞。
    ///
    /// <paramref name="maxPending"/> 是积压上限：超出就从**队头**丢。
    /// 丢队头是刻意的 —— 队尾永远是用户最新的目标，
    /// 保证播放器最后一定停在用户真正想去的那一格。
    /// </summary>
    public void Enqueue(IReadOnlyList<int> path, int maxPending)
    {
        if (path.Count == 0) return;

        foreach (int step in path) _pending.Enqueue(step);

        while (_pending.Count > maxPending) _pending.Dequeue();

        _nextStepAt = DateTime.MinValue;
    }

    /// <summary>
    /// 每 tick 调一次。
    ///
    /// 到节拍了就弹出下一格返回；没到就返回 null（调用方**不要**把 null
    /// 当成"播放结束"，那要看 <see cref="IsPlaying"/>）。
    /// </summary>
    public int? Tick()
    {
        if (_pending.Count == 0) return null;

        var now = _now();
        if (now < _nextStepAt) return null;

        int step = _pending.Dequeue();
        LastStep = step;
        _nextStepAt = now.AddMilliseconds(StepDwellMs);

        // 记录真实间隔（只在同一轮播放里算）
        if (_stepCount == 0) _firstStepAt = now;
        _lastStepAt = now;
        _stepCount++;

        return step;
    }

    /// <summary>清空 —— 收起窗口、取消选择、重新弹出时都要调，
    /// 否则上一次的残留路径会在下次弹出时突然冒出来。</summary>
    public void Clear()
    {
        // 结算这一轮的实测节拍，供日志用（Clear 之后 _stepCount 会归零）
        if (_stepCount > 1)
        {
            ObservedStepCount = _stepCount;
            ObservedStepMs = (_lastStepAt - _firstStepAt).TotalMilliseconds / (_stepCount - 1);
        }
        else
        {
            ObservedStepCount = _stepCount;
            ObservedStepMs = 0;
        }

        _pending.Clear();
        _nextStepAt = DateTime.MinValue;
        LastStep = -1;
        _stepCount = 0;
    }
}
