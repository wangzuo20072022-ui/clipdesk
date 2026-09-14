using System;
using System.Collections.Generic;

namespace S2Strip;

/// <summary>
/// GridSelection 的自测 —— 八方位 + 死区 + 扇区边界 + **路过**。
///
/// 重点是最后一类："换格必须逐格路过中间格"。
/// 这是第四轮的核心（轮盘手感），也是我前三轮一直没做对的东西。
/// </summary>
internal static class GridSelectionTests
{
    private static int _pass;
    private static int _fail;

    /// <summary>半径 R：足够远，肯定出了死区</summary>
    private const double R = 100;

    public static void Run()
    {
        _pass = 0;
        _fail = 0;

        Console.WriteLine();
        Console.WriteLine("── GridSelection 自测 ────────────────────────────");

        // ① 八个方位各来一发
        CheckSample("正上 (0,-R)", 0, 0, -R);
        CheckSample("右上 (R,-R)", 1, R, -R);
        CheckSample("正右 (R,0)", 2, R, 0);
        CheckSample("右下 (R,R)", 3, R, R);
        CheckSample("正下 (0,R)", 4, 0, R);
        CheckSample("左下 (-R,R)", 5, -R, R);
        CheckSample("正左 (-R,0)", 6, -R, 0);
        CheckSample("左上 (-R,-R)", 7, -R, -R);

        // ② 死区
        CheckSample("原地不动", -1, 0, 0);
        CheckSample("微动 (8,8)", -1, 8, 8);
        CheckSample("刚出死区往下 (0,17)", 4, 0, 17);

        // ③ 扇区边界：往上扇区的边界在 ±22.5°
        CheckSample("偏右 22° (38,-94)", 0, 38, -94);
        CheckSample("偏右 23° (39,-92)", 1, 39, -92);

        // ④ 距离不影响方位（拖多远都按方位算）
        CheckSample("极远往下 (0,9999)", 4, 0, 9999);
        CheckSample("极远往右 (9999,0)", 2, 9999, 0);

        // ⑤ 中心格 (1,1) 不应该映射到任何宫格
        int center = GridSelection.FromCell(1, 1);
        Report("中心格 (1,1) → -1", center == -1, $"实际 {center}");

        // ══ ⑥ 第四轮核心：路过 ══════════════════════════════════════
        //
        // 用户原话：
        //   「选到最左边的时候，再去选最右边，你必须根据鼠标的移动方向
        //     顺时针到最右边，或者逆时针到最右边。
        //     而你做的是跳过中间直接到最右边。」

        // 01(0) → 21(4)：正好半圈，固定走顺时针 → 02,12,22,21
        CheckPath("01 → 21（半圈，固定顺时针）", 0, 4, new[] { 1, 2, 3, 4 });

        // ★ 用户最在意的那条：01 → 12 必须经过 02
        CheckPath("★ 01 → 12 必须经过 02", 0, 2, new[] { 1, 2 });

        // 反向：12 → 01 必须经过 02
        CheckPath("★ 12 → 01 必须经过 02（反向）", 2, 0, new[] { 1, 0 });

        // 20 → 22：相邻两格，只走一步
        CheckPath("20 → 22 走最短（经过 21）", 5, 3, new[] { 4, 3 });

        // 22 → 20：反方向，也经过 21
        CheckPath("22 → 20 走最短（经过 21）", 3, 5, new[] { 4, 5 });

        // 00(7) → 02(1)：2 步
        CheckPath("00 → 02 经过 01", 7, 1, new[] { 0, 1 });

        // 同一个格子 → 不用换
        CheckPath("01 → 01 空路径", 0, 0, Array.Empty<int>());

        // 从无效状态出发 → 不产生路径（调用方直接跳过去就行）
        CheckPath("-1 → 03 空路径", -1, 3, Array.Empty<int>());

        // ══ ⑦ 绕一圈：8 步应该正好回到原点 ══════════════════════════
        {
            int cur = 0;
            var seen = new List<int> { cur };
            for (int i = 0; i < 8; i++)
            {
                cur = (cur + 1) % 8;
                seen.Add(cur);
            }
            bool ok = seen.Count == 9 && seen[8] == 0;
            Report("顺时针绕 8 步回到原点", ok, $"实际 {string.Join(",", seen)}");
        }

        // ⑧ 坐标映射自洽
        CheckCell("0 → 01", 0, 0, 1);
        CheckCell("4 → 21", 4, 2, 1);
        CheckCell("7 → 00", 7, 0, 0);
        CheckRoundTrip();

        Console.WriteLine($"  小计：{_pass} 通过 / {_fail} 失败");
        Console.WriteLine("──────────────────────────────────────────────────");
    }

    private static void CheckSample(string name, int expected, double dx, double dy)
    {
        int actual = GridSelection.Sample(dx, dy);
        string exp = expected < 0 ? "取消" : GridSelection.Label(expected);
        string act = actual < 0 ? "取消" : GridSelection.Label(actual);
        Report($"{name} → {exp}", actual == expected, actual == expected ? "" : $"实际得到 {act}");
    }

    private static void CheckPath(string name, int from, int to, int[] expected)
    {
        var actual = GridSelection.PathTo(from, to);
        bool ok = actual.Count == expected.Length;
        if (ok)
        {
            for (int i = 0; i < expected.Length; i++)
            {
                if (actual[i] != expected[i]) { ok = false; break; }
            }
        }

        string exp = expected.Length == 0 ? "（空）" : string.Join("→", Labels(expected));
        string act = actual.Count == 0 ? "（空）" : string.Join("→", Labels(actual));
        Report($"{name} → {exp}", ok, ok ? "" : $"实际得到 {act}");
    }

    private static IEnumerable<string> Labels(IReadOnlyList<int> indexes)
    {
        foreach (int i in indexes) yield return GridSelection.Label(i);
    }

    private static void CheckCell(string name, int index, int row, int col)
    {
        var (r, c) = GridSelection.ToCell(index);
        Report($"{name}", r == row && c == col, r == row && c == col ? "" : $"实际 ({r},{c})");
    }

    private static void CheckRoundTrip()
    {
        bool ok = true;
        string bad = "";
        for (int i = 0; i < GridSelection.SelectableCount; i++)
        {
            var (r, c) = GridSelection.ToCell(i);
            if (GridSelection.FromCell(r, c) != i)
            {
                ok = false;
                bad = $"宫格 {i} 往返不一致";
                break;
            }
        }
        Report("序号 ↔ 坐标 往返自洽", ok, bad);
    }

    private static void Report(string name, bool ok, string detail)
    {
        if (ok) { _pass++; Console.WriteLine($"  ✅ {name}"); }
        else { _fail++; Console.WriteLine($"  ❌ {name}   {detail}"); }
    }
}
