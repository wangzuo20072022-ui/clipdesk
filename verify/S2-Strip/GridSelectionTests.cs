using System;

namespace S2Strip;

/// <summary>
/// GridSelection 的自测 —— 八个方位 + 死区 + 边界。
///
/// 为什么值得单独写：方向判定是九宫格里最容易出错的纯逻辑，
/// 出错的表现是"往右上拖却选了左边"，靠肉眼调试极难定位。
/// 这些用例全部跑通再去看窗口。
///
/// ★ 这套用例同时是**回归护栏**：它钉死的是用户唯一认可的那一版手感
///   （方位采样 + 死区取消）。前三轮改手感时如果先跑这套，
///   就能立刻发现"拖回中心不再返回 -1"——那正是用户否掉的方向。
/// </summary>
internal static class GridSelectionTests
{
    private static int _pass;
    private static int _fail;

    public static void Run()
    {
        _pass = 0;
        _fail = 0;

        Console.WriteLine();
        Console.WriteLine("── GridSelection 自测 ────────────────────────────");

        // ① 八个方位各来一发（距离 100px，远离死区）
        //    参数顺序：Check(名字, 期望宫格号, dx, dy)
        Check("正上 (0,-100)", 0, 0, -100);
        Check("右上 (100,-100)", 1, 100, -100);
        Check("正右 (100,0)", 2, 100, 0);
        Check("右下 (100,100)", 3, 100, 100);
        Check("正下 (0,100)", 4, 0, 100);
        Check("左下 (-100,100)", 5, -100, 100);
        Check("正左 (-100,0)", 6, -100, 0);
        Check("左上 (-100,-100)", 7, -100, -100);

        // ② 中心死区 —— 没动 / 只挪一点点，都算取消
        Check("原地不动 (0,0)", -1, 0, 0);
        Check("微动 (5,5) 死区内", -1, 5, 5);
        Check("微动 (20,0) 死区内", -1, 20, 0);

        // ②b ★ 本次回退的**核心行为**：选中的格拖回中心，必须能取消。
        //
        //   前三轮的"锁定"版本在这里会返回 21 而不是 -1 —— 用户明确否决：
        //     「为什么还是能移动到最中心的取消？？？」（反话，意思是必须能）
        //     「算了我感觉还是不好，还是改为能选中取消那一版吧」
        //
        //   这条断言就是那一版的守门人。谁再往里加锁定逻辑，这里立刻红。
        Check("★ 选中 21 后拖回死区 → 取消（不是保持 21）", -1, 0, 18);
        Check("★ 选中 01 后拖回死区 → 取消", -1, 3, -20);
        Check("死区边界内 23px → 取消", -1, 0, 23);
        Check("死区边界外 25px → 仍是 21", 4, 0, 25);

        // ③ 扇区边界：正上扇区的边界在 ±22.5°
        //    22° 偏右 → 仍在"正上"扇区
        Check("偏右 22° (38,-94)", 0, 38, -94);
        //    23° 偏右 → 进"右上"扇区
        Check("偏右 23° (39,-92)", 1, 39, -92);

        // ④ 距离不影响方位（只要出了死区）
        Check("极远正下 (0,9999)", 4, 0, 9999);
        Check("刚出死区正下 (0,25)", 4, 0, 25);

        // ⑤ 坐标映射自洽
        CheckCell("0 → 01", 0, 0, 1);
        CheckCell("4 → 21", 4, 2, 1);
        CheckCell("7 → 00", 7, 0, 0);
        CheckRoundTrip();

        // ⑥ 中心格 (1,1) 不应该映射到任何宫格
        int center = GridSelection.FromCell(1, 1);
        Report("中心格 (1,1) → -1", center == -1, $"实际 {center}");

        Console.WriteLine($"  小计：{_pass} 通过 / {_fail} 失败");
        Console.WriteLine("──────────────────────────────────────────────────");
    }

    private static void Check(string name, int expected, double dx, double dy)
    {
        int actual = GridSelection.Resolve(dx, dy);
        Report($"{name} → {(expected < 0 ? "取消" : GridSelection.Label(expected))}",
               actual == expected,
               actual == expected ? "" : $"实际得到 {(actual < 0 ? "取消" : GridSelection.Label(actual))}");
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
