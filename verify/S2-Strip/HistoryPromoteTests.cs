using System;

namespace S2Strip;

/// <summary>
/// 「粘贴后顶置」的纯逻辑自测。
///
/// 为什么值得单独写：这条需求（粘过的东西变成最新）的判定，靠肉眼看窗口很难确认 ——
/// 字小、格子内容又只显示前 24 个字。用一段自测把它钉死，
/// 之后窗口上看到什么，就只剩下「画没画对」这一个问题了。
///
/// 和 GridSelectionTests 一样，在窗口打开前先跑，失败会直接列出来。
/// </summary>
internal static class HistoryPromoteTests
{
    public static void Run()
    {
        Console.WriteLine("── ClipboardHistory.Promote 自测 ──────────────────");

        int passed = 0, failed = 0;

        // ── 1. 基本：把第 4 条顶到最前 ──
        {
            var h = new ClipboardHistory(20);
            h.Add("A", "?");
            h.Add("B", "?");
            h.Add("C", "?");
            h.Add("D", "?");
            // 现在顺序是 D C B A（新的在前）
            Check("顶置 D 之前顺序是 D,C,B,A",
                  Order(h) == "D,C,B,A", ref passed, ref failed);

            bool moved = h.Promote("C");

            Check("Promote(C) 返回 true",
                  moved, ref passed, ref failed);
            Check("顶置后顺序是 C,D,B,A",
                  Order(h) == "C,D,B,A", ref passed, ref failed);
        }

        // ── 2. 顶置已经在最前的那条 → 顺序不该变 ──
        {
            var h = new ClipboardHistory(20);
            h.Add("A", "?");
            h.Add("B", "?");
            h.Promote("B");
            Check("顶置已是最新的那条，顺序不变（仍是 B,A）",
                  Order(h) == "B,A", ref passed, ref failed);
        }

        // ── 3. 找不到（已被挤出缓冲）→ 返回 false，且不动列表 ──
        {
            var h = new ClipboardHistory(3);
            h.Add("A", "?");
            h.Add("B", "?");
            h.Add("C", "?");
            h.Add("D", "?");   // A 被挤掉了

            bool moved = h.Promote("A");
            Check("顶置已被挤出的条目 → 返回 false",
                  !moved, ref passed, ref failed);
            Check("列表不受影响（仍是 D,C,B）",
                  Order(h) == "D,C,B", ref passed, ref failed);
        }

        // ── 4. 顶置后时间戳应该刷新（列表头部那个时间要更新）──
        {
            var h = new ClipboardHistory(20);
            h.Add("A", "?");
            System.Threading.Thread.Sleep(20);
            h.Add("B", "?");
            var before = h.Items[1].At;      // A 的时间戳
            h.Promote("A");
            Check("顶置后时间戳被刷新",
                  h.Items[0].At > before, ref passed, ref failed);
        }

        // ── 5. 空串 / null 不该炸 ──
        {
            var h = new ClipboardHistory(20);
            h.Add("A", "?");
            Check("Promote(null) 返回 false", !h.Promote(null!), ref passed, ref failed);
            Check("Promote(\"\") 返回 false", !h.Promote(""), ref passed, ref failed);
        }

        Console.WriteLine($"  小计：{passed} 通过 / {failed} 失败");
        Console.WriteLine("──────────────────────────────────────────────────");
        Console.WriteLine();
    }

    private static string Order(ClipboardHistory h)
    {
        var parts = new string[h.Items.Count];
        for (int i = 0; i < h.Items.Count; i++) parts[i] = h.Items[i].Text;
        return string.Join(",", parts);
    }

    private static void Check(string what, bool ok, ref int passed, ref int failed)
    {
        Console.WriteLine($"  {(ok ? "✅" : "★❌")} {what}");
        if (ok) passed++; else failed++;
    }
}
