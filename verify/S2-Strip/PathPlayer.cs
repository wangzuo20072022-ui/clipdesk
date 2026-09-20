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
    /// ── 这个数字调过三次，记下来免得再来回试 ──────────────────
    ///
    ///   第一版：0（一 tick 全走完）→ 用户「确实扫太快了」，
    ///           60Hz 下几毫秒全过去，中间格根本没露面。
    ///   第二版：60 → 用户「效果很差」，扫得太慢，高亮像粘在鼠标后面。
    ///   第三版：**25** ← 现在这个。
    ///
    /// 25ms 的效果：走完半圈（4 格）约 75ms，比一次眨眼短，
    /// 但明显能看出高亮是一格格挪过去的，不是"啪"地跳。
    ///
    /// 上下界参考：
    ///   · 不能低于 16ms（轮询周期）—— 否则"每 tick 一格"本身就限制了速度，
    ///     再调小也没用，反而会让节拍和采样打架。
    ///   · 超过 ~40ms 就会觉得高亮跟不上手。
    /// </summary>
    public const int StepDwellMs = 25;

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
        return step;
    }

    /// <summary>清空 —— 收起窗口、取消选择、重新弹出时都要调，
    /// 否则上一次的残留路径会在下次弹出时突然冒出来。</summary>
    public void Clear()
    {
        _pending.Clear();
        _nextStepAt = DateTime.MinValue;
        LastStep = -1;
    }
}
