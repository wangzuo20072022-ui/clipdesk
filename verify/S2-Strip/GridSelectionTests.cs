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

        // ② 中心死区（已缩到 12px）—— 没选过任何方向时，在这儿就是取消
        Check("原地不动 (0,0)", -1, 0, 0);
        Check("微动 (8,8) 死区内", -1, 8, 8);
        Check("刚出死区 (0,13) 有效", 4, 0, 13);

        // ③ 扇区边界：正上扇区的边界在 ±22.5°
        //    22° 偏右 → 仍在"正上"扇区
        Check("偏右 22° (38,-94)", 0, 38, -94);
        //    23° 偏右 → 进"右上"扇区
        Check("偏右 23° (39,-92)", 1, 39, -92);

        // ④ 距离不影响方位（只要出了死区）
        Check("极远正下 (0,9999)", 4, 0, 9999);
        Check("刚出死区正下 (0,25)", 4, 0, 25);

        // ⑥ 中心格 (1,1) 不应该映射到任何宫格
        int center = GridSelection.FromCell(1, 1);
        Report("中心格 (1,1) → -1", center == -1, $"实际 {center}");

        // ⑦ 第三轮：锁定 —— 选过方向之后，死区里不再回到"取消"
        CheckMove("移动过 → 死区内保持 21（不取消）", 4, 0, 0, 0, 20, last: 4);
        CheckMove("移动过 → 死区内保持 01（不取消）", 0, 0, 0, 0, -20, last: 0);
        CheckMove("没选过 → 死区内仍是取消", -1, 0, 0, 0, 0, last: -1);

        // ⑧ 第三轮：运动方向 —— 用户的核心诉求
        //
        //   场景：鼠标本来在上方远处选中了 01，
        //         现在**往下划一点点**。
        //   位移累计是往下 15px，于是判出 正下 = 21。
        CheckMove("从 01 往下划一点 → 立刻变 21", 4, 0, -100, 0, 15, last: 0);
        CheckMove("从 01 往右划 → 变 12", 2, -50, -100, 20, 0, last: 0);
        CheckMove("从 21 往上划 → 变 01", 0, 0, 80, 0, -15, last: 4);

        // ⑨ 第三轮：累计不够、但鼠标已离起点很远 → 保持（不按方位跳回去）
        //
        //   这是"往下划一下选中 21、手一停"的场景：
        //   累计<阈值，而鼠标仍在起点上方（dy=-80）。
        //   ★ 如果这里按方位重算就会啪地跳回 01 —— 那更糟。必须保持。
        CheckMove("划完停住不动 → 保持 21（不跳回 01）", 4, 0, -80, 3, 3, last: 4);

        // ⑩ 抖动抵不过阈值：来回抖 5px 不足以换格
        CheckMove("小幅抖动 → 保持原选", 4, 0, -30, 5, 5, last: 4);

        // ⑪ 坐标映射自洽
        CheckCell("0 → 01", 0, 0, 1);
        CheckCell("4 → 21", 4, 2, 1);
        CheckCell("7 → 00", 7, 0, 0);
        CheckRoundTrip();

        Console.WriteLine($"  小计：{_pass} 通过 / {_fail} 失败");
        Console.WriteLine("──────────────────────────────────────────────────");
    }

    private static void Check(string name, int expected, double dx, double dy)
    {
        int actual = GridSelection.Resolve(dx, dy, dx, dy, -1);
        Report($"{name} → {(expected < 0 ? "取消" : GridSelection.Label(expected))}",
               actual == expected,
               actual == expected ? "" : $"实际得到 {(actual < 0 ? "取消" : GridSelection.Label(actual))}");
    }

    /// <summary>带"累计移动量"和"上次方位"的检查 —— 第三轮的手感规则全靠它</summary>
    private static void CheckMove(string name, int expected,
                                  double dx, double dy, double accX, double accY, int last)
    {
        int actual = GridSelection.Resolve(dx, dy, accX, accY, last);
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
