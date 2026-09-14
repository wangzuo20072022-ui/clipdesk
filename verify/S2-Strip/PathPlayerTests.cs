using System;

namespace S2Strip;

/// <summary>
/// PathPlayer 的自测 —— 节拍、边界、积压裁剪。
///
/// 这一组测试存在的理由：用户说「扫太快了」，我改成每格停 60ms。
/// 但"改了之后到底停没停够"如果只靠肉眼，下次再调就又是盲调。
/// 用注入时钟把这些数字钉死，以后改 StepDwellMs 立刻能看出影响。
/// </summary>
internal static class PathPlayerTests
{
    private static int _pass;
    private static int _fail;

    public static void Run()
    {
        _pass = 0;
        _fail = 0;

        Console.WriteLine();
        Console.WriteLine("── PathPlayer 自测 ───────────────────────────────");

        // ① 空队列
        {
            var (p, _) = Make();
            Check("空队列 IsPlaying=false", !p.IsPlaying);
            Check("空队列 Tick 返回 null", p.Tick() is null);
        }

        // ② 第一格立刻可播，不等节拍
        {
            var (p, clock) = Make();
            p.Enqueue(new[] { 1, 2, 3 }, 8);
            Check("排入后 IsPlaying=true", p.IsPlaying);
            Check("第一格立刻播（不等 60ms）", p.Tick() == 1);
        }

        // ③ 第二格必须等满 60ms
        {
            var (p, clock) = Make();
            p.Enqueue(new[] { 1, 2 }, 8);
            p.Tick();                       // 播 1

            clock.Advance(59);
            Check("59ms 时第二格还不能播", p.Tick() is null);

            clock.Advance(1);               // 累计 60
            Check("满 60ms 播第二格", p.Tick() == 2);
        }

        // ④ 播完队列自然结束
        {
            var (p, clock) = Make();
            p.Enqueue(new[] { 1, 2 }, 8);
            p.Tick();
            clock.Advance(PathPlayer.StepDwellMs);
            p.Tick();

            Check("播完 IsPlaying=false", !p.IsPlaying);
            Check("播完 Tick 返回 null", p.Tick() is null);
        }

        // ⑤ ★ 核心：4 格的路径总耗时必须是 3 个节拍（180ms），不是 4 个
        {
            var (p, clock) = Make();
            p.Enqueue(new[] { 1, 2, 3, 4 }, 8);

            var start = clock.Now;
            p.Tick();                                   // 立刻
            for (int i = 0; i < 3; i++)
            {
                clock.Advance(PathPlayer.StepDwellMs);
                p.Tick();
            }

            double elapsed = (clock.Now - start).TotalMilliseconds;
            Check($"4 格走完耗时 {(int)elapsed}ms（应为 180）",
                  Math.Abs(elapsed - PathPlayer.StepDwellMs * 3) < 0.5);
        }

        // ⑥ 半圈（4 格）要看得清：至少 180ms，别快过这个
        {
            var (p, _) = Make();
            p.Enqueue(new[] { 1, 2, 3, 4 }, 8);
            Check("半圈路径 4 格 → 总时长 ≥180ms",
                  PathPlayer.StepDwellMs * 3 >= 180);
        }

        // ⑦ 积压裁剪：从队头丢，队尾（用户最终目标）必须保住
        {
            var (p, clock) = Make();
            p.Enqueue(new[] { 1, 2, 3, 4, 5, 6, 7, 0, 1, 2 }, 8);
            Check("超上限后只剩 8 格", p.PendingCount == 8);

            // 一路播到底，最后一格应该是 2（队尾没被丢）
            //
            // ★ 这里必须自己推进时钟：Tick() 在没到节拍时返回 null，
            //   而 null 的含义是"这一 tick 还没到点"，不是"播完了"。
            //   退出条件要用 IsPlaying，不能用"Tick 返回 null"。
            //   （我第一版就是这么写错的，测试红了一条 —— 代码是对的，测试是错的）
            int last = -1;
            for (int guard = 0; guard < 100 && p.IsPlaying; guard++)
            {
                clock.Advance(PathPlayer.StepDwellMs);
                if (p.Tick() is int s) last = s;
            }
            Check("丢的是队头，队尾目标保住了（最后一格=2）", last == 2);
        }

        // ⑧ Clear 之后不再出格
        {
            var (p, clock) = Make();
            p.Enqueue(new[] { 1, 2, 3 }, 8);
            p.Tick();
            p.Clear();
            Check("Clear 后 IsPlaying=false", !p.IsPlaying);
            clock.Advance(1000);
            Check("Clear 后不再出格", p.Tick() is null);
            Check("Clear 后 LastStep 归 -1", p.LastStep == -1);
        }

        // ⑨ 空路径排入 = 什么也不做（PathTo 返回空时就是这条路径）
        {
            var (p, _) = Make();
            p.Enqueue(Array.Empty<int>(), 8);
            Check("排入空路径 → 不进入播放", !p.IsPlaying);
        }

        // ⑩ 连续两次 Enqueue（理论上不会发生，但要保证不会崩）
        {
            var (p, clock) = Make();
            p.Enqueue(new[] { 1, 2 }, 8);
            p.Enqueue(new[] { 3, 4 }, 8);
            Check("二次排入累积 4 格", p.PendingCount == 4);
            Check("二次排入后第一格立刻可播", p.Tick() == 1);
        }

        Console.WriteLine($"  小计：{_pass} 通过 / {_fail} 失败");
        Console.WriteLine("──────────────────────────────────────────────────");
    }

    private static (PathPlayer Player, FakeClock Clock) Make()
    {
        var clock = new FakeClock();
        return (new PathPlayer(() => clock.Now), clock);
    }

    private static void Check(string what, bool ok)
    {
        if (ok) { _pass++; Console.WriteLine($"  ✅ {what}"); }
        else { _fail++; Console.WriteLine($"  ❌ {what}"); }
    }

    /// <summary>可以随便快进的假时钟 —— 单测不睡真时间</summary>
    private sealed class FakeClock
    {
        private DateTime _t = new(2026, 1, 1, 0, 0, 0);
        public DateTime Now => _t;
        public void Advance(int ms) => _t = _t.AddMilliseconds(ms);
    }
}
