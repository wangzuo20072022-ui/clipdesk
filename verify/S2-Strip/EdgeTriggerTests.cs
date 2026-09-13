using System;

namespace S2Strip;

/// <summary>
/// 状态机自测。在窗口打开**之前**跑 —— 和 S0 的套路一样：
/// 手感不对时能立刻分清是"状态机算错了"还是"窗口没照做"。
///
/// 时钟是注入的，所以这里"快进时间"不用真的 Thread.Sleep(350)。
/// </summary>
internal static class EdgeTriggerTests
{
    public static void Run()
    {
        Console.WriteLine("── EdgeTriggerStateMachine 自测 ───────────────────");
        int passed = 0, failed = 0;

        // ── 1. 鼠标进来但没停够 → 不展开（路过不误触）──
        {
            var (sm, clock) = Make();
            Check("初始状态是收起", sm.Current == EdgeTriggerStateMachine.Phase.Idle, ref passed, ref failed);

            sm.Tick(cursorInside: true);
            Check("鼠标进来 → 进入等待",
                  sm.Current == EdgeTriggerStateMachine.Phase.HoverPending, ref passed, ref failed);

            clock.Advance(200);                       // 只停了 200ms
            var a = sm.Tick(cursorInside: true);
            Check("只停 200ms → 还不展开", a == EdgeTriggerStateMachine.Action.None, ref passed, ref failed);

            var a2 = sm.Tick(cursorInside: false);    // 走了
            Check("没停够就走 → 不展开", a2 == EdgeTriggerStateMachine.Action.None, ref passed, ref failed);
            Check("没停够就走 → 回到收起态",
                  sm.Current == EdgeTriggerStateMachine.Phase.Idle, ref passed, ref failed);
        }

        // ── 2. 停够 350ms → 展开 ──
        {
            var (sm, clock) = Make();
            sm.Tick(cursorInside: true);
            clock.Advance(349);
            var a = sm.Tick(cursorInside: true);
            Check("349ms 时仍不展开", a == EdgeTriggerStateMachine.Action.None, ref passed, ref failed);

            clock.Advance(1);                         // 累计 350ms
            var a2 = sm.Tick(cursorInside: true);
            Check("满 350ms → 触发展开", a2 == EdgeTriggerStateMachine.Action.Expand, ref passed, ref failed);
            Check("展开后状态正确",
                  sm.Current == EdgeTriggerStateMachine.Phase.Expanded, ref passed, ref failed);
        }

        // ── 3. 刚展开就离开 → 最小展开时长兜住，不立刻收 ──
        {
            var (sm, clock) = Make();
            Expand(sm, clock);

            clock.Advance(50);                        // 才过了 50ms（< MinExpandedMs 200）
            var a = sm.Tick(cursorInside: false);
            Check("刚展开 50ms 就离开 → 不立刻收",
                  a == EdgeTriggerStateMachine.Action.None, ref passed, ref failed);
            Check("此时仍是展开态",
                  sm.Current == EdgeTriggerStateMachine.Phase.Expanded, ref passed, ref failed);
        }

        // ── 4. 正常离开 → 150ms 迟滞后收起 ──
        {
            var (sm, clock) = Make();
            Expand(sm, clock);
            clock.Advance(300);                       // 越过最小展开时长

            sm.Tick(cursorInside: false);
            Check("离开 → 进入待收起",
                  sm.Current == EdgeTriggerStateMachine.Phase.CollapsePending, ref passed, ref failed);

            clock.Advance(149);
            var a = sm.Tick(cursorInside: false);
            Check("149ms 时还没收", a == EdgeTriggerStateMachine.Action.None, ref passed, ref failed);

            clock.Advance(1);                         // 累计 150ms
            var a2 = sm.Tick(cursorInside: false);
            Check("满 150ms → 收起", a2 == EdgeTriggerStateMachine.Action.Collapse, ref passed, ref failed);
            Check("收起后回到 Idle",
                  sm.Current == EdgeTriggerStateMachine.Phase.Idle, ref passed, ref failed);
        }

        // ── 5. 离开后 150ms 内又回来 → 取消收起（防手抖）──
        {
            var (sm, clock) = Make();
            Expand(sm, clock);
            clock.Advance(300);

            sm.Tick(cursorInside: false);
            clock.Advance(100);                       // 才走开 100ms
            var a = sm.Tick(cursorInside: true);      // 回来了

            Check("走开 100ms 又回来 → 不收起", a == EdgeTriggerStateMachine.Action.None, ref passed, ref failed);
            Check("回来后仍是展开态",
                  sm.Current == EdgeTriggerStateMachine.Phase.Expanded, ref passed, ref failed);

            // 再确认它不会"攒着"那 50ms 余量偷偷收掉
            clock.Advance(1000);
            var a2 = sm.Tick(cursorInside: true);
            Check("回来后再待很久也不会自己收",
                  a2 == EdgeTriggerStateMachine.Action.None, ref passed, ref failed);
        }

        // ── 6. 点了一条 → 强制收起 ──
        {
            var (sm, clock) = Make();
            Expand(sm, clock);

            sm.CommitClicked();
            Check("点完条目 → 立刻回到收起态",
                  sm.Current == EdgeTriggerStateMachine.Phase.Idle, ref passed, ref failed);
        }

        // ── 7. 展开 → 收起 → 再展开，能正常工作（不能只灵一次）──
        {
            var (sm, clock) = Make();
            Expand(sm, clock);
            clock.Advance(300);
            sm.Tick(cursorInside: false);
            clock.Advance(150);
            sm.Tick(cursorInside: false);
            Check("收起后回到 Idle",
                  sm.Current == EdgeTriggerStateMachine.Phase.Idle, ref passed, ref failed);

            clock.Advance(1000);
            var a = Expand(sm, clock);
            Check("第二次也能正常展开", a, ref passed, ref failed);
        }

        // ── 8. 鼠标一直在里面不动 → 不该反复触发 Expand ──
        {
            var (sm, clock) = Make();
            Expand(sm, clock);

            int extraExpands = 0;
            for (int i = 0; i < 20; i++)
            {
                clock.Advance(50);
                if (sm.Tick(cursorInside: true) == EdgeTriggerStateMachine.Action.Expand) extraExpands++;
            }
            Check("鼠标停在面板里 → 不重复触发展开", extraExpands == 0, ref passed, ref failed);
        }

        Console.WriteLine($"  小计：{passed} 通过 / {failed} 失败");
        Console.WriteLine("──────────────────────────────────────────────────");
        Console.WriteLine();
    }

    /// <summary>造一个状态机 + 可控时钟</summary>
    private static (EdgeTriggerStateMachine Sm, FakeClock Clock) Make()
    {
        var clock = new FakeClock();
        return (new EdgeTriggerStateMachine(() => clock.Now), clock);
    }

    /// <summary>快进到"已展开"状态，返回是否成功</summary>
    private static bool Expand(EdgeTriggerStateMachine sm, FakeClock clock)
    {
        sm.Tick(cursorInside: true);
        clock.Advance(EdgeTriggerStateMachine.HoverDwellMs);
        var a = sm.Tick(cursorInside: true);
        return a == EdgeTriggerStateMachine.Action.Expand
            && sm.Current == EdgeTriggerStateMachine.Phase.Expanded;
    }

    private static void Check(string what, bool ok, ref int passed, ref int failed)
    {
        Console.WriteLine($"  {(ok ? "✅" : "★❌")} {what}");
        if (ok) passed++; else failed++;
    }

    /// <summary>可以随便快进的假时钟 —— 单测不睡真时间</summary>
    private sealed class FakeClock
    {
        private DateTime _t = new(2026, 1, 1, 0, 0, 0);
        public DateTime Now => _t;
        public void Advance(int ms) => _t = _t.AddMilliseconds(ms);
    }
}
