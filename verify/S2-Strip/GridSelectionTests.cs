using System;

namespace S2Strip;

/// <summary>
/// GridSelection 的自测 —— 八个方位 + 死区 + 边界。
///
/// 为什么值得单独写：方向判定是九宫格里最容易出错的纯逻辑，
/// 出错的表现是"往右上拖却选了左边"，靠肉眼调试极难定位。
/// 这些用例全部跑通再去看窗口。
/// </summary>
internal static class GridSelectionTests
{
    private static int _pass;
    private static int _fail;

    public static void Run()
    {
        Console.WriteLine();
        Console.WriteLine("── GridSelection 自测 ────────────────────────────");

        // ① 八个方位各来一发（位移 100px）
        //    参数顺序：Check(名字, 期望宫格号, 位移 dx, 位移 dy)
        Check("往上划 (0,-100)", 0, 0, -100);
        Check("往右上划 (100,-100)", 1, 100, -100);
        Check("往右划 (100,0)", 2, 100, 0);
        Check("往右下划 (100,100)", 3, 100, 100);
        Check("往下划 (0,100)", 4, 0, 100);
        Check("往左下划 (-100,100)", 5, -100, 100);
        Check("往左划 (-100,0)", 6, -100, 0);
        Check("往左上划 (-100,-100)", 7, -100, -100);

        // ② 没动 / 动得不够 → 取消；动够了 → 有效
        Check("原地不动 (0,0)", -1, 0, 0);
        Check("微动 (6,6) 不够阈值", -1, 6, 6);
        Check("刚够阈值往下 (0,10)", 4, 0, 10);

        // ③ 扇区边界：往上扇区的边界在 ±22.5°
        Check("偏右 22° (38,-94)", 0, 38, -94);
        Check("偏右 23° (39,-92)", 1, 39, -92);

        // ④ 距离不影响方向
        Check("极远往下 (0,9999)", 4, 0, 9999);
        Check("刚好往下 (0,25)", 4, 0, 25);

        // ⑤ 中心格 (1,1) 不应该映射到任何宫格
        int center = GridSelection.FromCell(1, 1);
        Report("中心格 (1,1) → -1", center == -1, $"实际 {center}");

        // ══ ⑥ 第四轮核心：这是一台"拨盘"，看的是**正在往哪边划** ══
        //
        // 用户的原话与考题：
        //   「你往上拖选择了 01，往下拖一点就选择了 21，
        //     但是如果**再往上拖，它应该立马回到 01**，因为往上拖动了。」
        //   「当用户选择了 01，此时鼠标往右移动了一小段，此时选择的应该是哪个区块？」

        CheckMove("往下划一点（从 01）→ 21", 4, 0, 30, last: 0);
        CheckMove("再往上划一点 → 立刻回到 01", 0, 0, -30, last: 4);
        CheckMove("★ 用户考题：从 01 往右划一点 → 12", 2, 30, 0, last: 0);
        CheckMove("从 01 往左划一点 → 10", 6, -30, 0, last: 0);
        CheckMove("从 21 往右划一点 → 12", 2, 30, 0, last: 4);
        CheckMove("从 12 往上划一点 → 01", 0, 0, -30, last: 2);

        // ⑦ 动得不够阈值 → 保持上一次，不换也不取消
        CheckMove("位移不够阈值 → 保持 21", 4, 0, 5, last: 4);
        CheckMove("位移不够且没选过 → 仍是取消", -1, 0, 5, last: -1);
        CheckMove("★ 来回抖动净位移小 → 保持原选（不会疯跳）", 4, 6, 6, last: 4);

        // ⑧ 坐标映射自洽
        CheckCell("0 → 01", 0, 0, 1);
        CheckCell("4 → 21", 4, 2, 1);
        CheckCell("7 → 00", 7, 0, 0);
        CheckRoundTrip();

        Console.WriteLine($"  小计：{_pass} 通过 / {_fail} 失败");
        Console.WriteLine("──────────────────────────────────────────────────");
    }

    /// <summary>不带"上次方位"的检查（等价于"还没动过"）</summary>
    private static void Check(string name, int expected, double moveX, double moveY)
    {
        var r = GridSelection.Resolve(moveX, moveY, -1);
        Report($"{name} → {(expected < 0 ? "取消" : GridSelection.Label(expected))}",
               r.Index == expected,
               r.Index == expected ? "" : $"实际得到 {(r.Index < 0 ? "取消" : GridSelection.Label(r.Index))}");
    }

    /// <summary>带"上次方位"的检查 —— 拨盘的核心规则全靠它</summary>
    private static void CheckMove(string name, int expected,
                                  double moveX, double moveY, int last)
    {
        var r = GridSelection.Resolve(moveX, moveY, last);
        Report($"{name} → {(expected < 0 ? "取消" : GridSelection.Label(expected))}",
               r.Index == expected,
               r.Index == expected ? "" : $"实际得到 {(r.Index < 0 ? "取消" : GridSelection.Label(r.Index))}");
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
