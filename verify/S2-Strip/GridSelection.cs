using System;
using System.Collections.Generic;

namespace S2Strip;

/// <summary>
/// 九宫格方位选择 —— 纯函数，不碰 UI，40+ 条单测覆盖。
///
/// 正式项目里这是 Core\GridSelection.cs。
/// 放在这里是刻意的：方向判定是最容易出 bug 的地方，必须能脱离窗口单独验。
///
/// 布局（坐标写作「行列」两位数字）：
///
///     ┌─────┬─────┬─────┐
///     │ 00  │ 01  │ 02  │
///     ├─────┼─────┼─────┤
///     │ 10  │  ␀  │ 12  │   中心留空，不可选
///     ├─────┼─────┼─────┤
///     │ 20  │ 21  │ 22  │
///     └─────┴─────┴─────┘
///
/// 由新到旧的填充顺序（顺时针，从正上方开始）：
///
///             ①01
///         ⑧00      ②02        1 = 最新
///             ␀ 中心留空       8 = 最旧
///         ⑦10      ③12
///             ⑥21  ⑤22  ④20
///
/// ══ 手感模型（第四轮定稿，前三轮全错在这上面）══════════════════
///
/// 用户的原话：
///   「我想要的是一个**类似轮盘**的手感。选到最左边的时候，再去选最右边，
///     你必须根据鼠标的移动方向**顺时针到最右边，或者逆时针到最右边**。
///     而你做的是**跳过中间直接到最右边**。」
///
/// 模型 = **方位采样 + 必须路过**，也就是 Blender 馅饼菜单那一套：
///
///   ① **圆心固定**在弹出那一刻的鼠标位置，全程不动。
///      （前三轮我一直在"起点/锚点"上做文章，而正确模型里根本没有起点这回事）
///
///   ② **鼠标在哪个方位就是哪一格** —— 所见即所得。
///      8 个扇区平分 360°，正上方是 01，顺时针排。
///
///   ③ **换格必须"路过"** —— 从旧格子走到新格子时，
///      沿最短旋转方向**逐格经过中间格**，每一格都通知 UI 画一下。
///      高亮会**扫过去**，这就是"轮盘感"。
///      用户说的"往下拖必须经过 00、10、20 或 02、12、22"正是这个。
///
///   ④ **死区**：光标要走完整个死区直径才换格。
///      一次都没动过 → 取消；动过 → 锁定，永远回不到取消。
/// </summary>
internal static class GridSelection
{
    /// <summary>
    /// 填充顺序 —— 下标 = 排序号-1，值 = (row, col)。
    /// 顺时针一圈，从正上方 01 开始。
    ///
    /// ★ 这个顺序同时就是**扇区顺序**：下标 i 的扇区中心在 i × 45°。
    ///   所以"路过"只要在下标上做加减就行，不用碰几何。
    /// </summary>
    public static readonly (int Row, int Col)[] FillOrder =
    {
        (0, 1),   // ① 正上   最新
        (0, 2),   // ② 右上
        (1, 2),   // ③ 正右
        (2, 2),   // ④ 右下
        (2, 1),   // ⑤ 正下
        (2, 0),   // ⑥ 左下
        (1, 0),   // ⑦ 正左
        (0, 0),   // ⑧ 左上   最旧
    };

    /// <summary>可选格数（8 —— 中心那格不算）</summary>
    public const int SelectableCount = 8;

    /// <summary>
    /// 采样的有效半径（物理像素）。
    ///
    /// 用户说「往外拖到屏幕外，也应该按最近的格算」，
    /// 所以**没有上限** —— 拖多远都按方位算，只是最多到 01/21 那两个格。
    /// 这个值只用来判断"够不够远、算不算动过"。
    /// </summary>
    public const double MinRadius = 16;

    /// <summary>
    /// 死区半径（物理像素）。
    ///
    /// ★ 这是"能回到取消"的**唯一**含义：
    ///   光标从中心往外走，**必须走完整个直径**（2 × 半径）才换格。
    ///   所以"转回中心附近"时不会立刻丢掉选中 —— 得真的回到中心附近才行。
    ///
    /// 用户明确要求「只要移动过了就不能选中心」，所以一旦选中过，
    /// 死区里也**保持上一次的格子**，不再回到取消。
    /// </summary>
    public const double DeadZoneRadius = 16;

    // ── 扇形分界 ────────────────────────────────────────────────────
    //
    //   0°       → 01（正上）      扇区 [-22.5°, 22.5°)
    //   45°      → 02（右上）
    //   90°      → 12（正右）
    //   ...
    //
    // 所以最右那个格子的中心在 90°，最左在 270°。

    /// <summary>扇区宽度（度）</summary>
    private const double SectorWidth = 45.0;

    /// <summary>半宽，用来把角度平移成"从 01 扇区中心起算"</summary>
    private const double HalfSector = SectorWidth / 2.0;

    /// <summary>
    /// 把鼠标相对**圆心**的位移换算成格子号（0..7，对应 FillOrder 下标）。
    /// 返回 -1 表示"离圆心太近，还不算动过"。
    ///
    /// 这是纯方位采样 —— 不做任何"方向"或"历史"的考虑。
    /// 路过逻辑在 <see cref="PathTo"/> 里。
    /// </summary>
    public static int Sample(double dx, double dy)
    {
        double distance = Math.Sqrt(dx * dx + dy * dy);
        if (distance < DeadZoneRadius) return -1;

        // atan2 用 (dx, -dy)：屏幕坐标 y 朝下，取负之后
        // 0° 指向正上、顺时针递增，和 FillOrder 的顺序对齐。
        double angle = Math.Atan2(dx, -dy) * 180.0 / Math.PI;   // (-180, 180]
        if (angle < 0) angle += 360;                             // [0, 360)

        // 每个扇区 45°，01 的扇区是 [-22.5, 22.5) → 加半个扇区再整除
        return (int)Math.Floor((angle + HalfSector) / SectorWidth) % SelectableCount;
    }

    /// <summary>
    /// 从 <paramref name="from"/> 换到 <paramref name="to"/> 时，
    /// **依次要经过的格子**（含终点，不含起点）。
    ///
    /// ★ 这是"轮盘感"的全部来源。
    ///
    /// 沿**最短旋转方向**逐格走：
    ///   从 01（下标 0）到 21（下标 4）：
    ///     顺时针 4 步 → 02、12、22、21
    ///     逆时针 4 步 → 00、10、20、21
    ///   刚好半圈，两个方向一样长 —— 这时**固定选一个方向**（顺时针），
    ///   保证同样的输入永远得到同样的结果，不会左右横跳。
    ///
    /// 从 01 到 12（下标 0 → 2）：
    ///   顺时针 2 步 → 02、12      ← 用户要的"经过 02 再到 12"
    ///   逆时针 6 步 → 太远
    ///
    /// 返回空列表表示不用换格（from == to，或 from 无效）。
    /// </summary>
    public static IReadOnlyList<int> PathTo(int from, int to)
    {
        if (from < 0 || to < 0 || from == to) return Array.Empty<int>();

        int n = SelectableCount;

        // 顺时针要走几步（下标递增方向）
        int clockwise = ((to - from) % n + n) % n;
        // 逆时针要走几步
        int counter = n - clockwise;

        // 走短的那条；正好半圈（相等）时固定走顺时针，
        // 保证同样输入永远同样输出 —— 否则会在两个方向之间抖
        int steps = clockwise <= counter ? clockwise : -counter;

        var path = new List<int>(Math.Abs(steps));
        int cur = from;
        for (int i = 0; i < Math.Abs(steps); i++)
        {
            cur = ((cur + Math.Sign(steps)) % n + n) % n;
            path.Add(cur);
        }
        return path;
    }

    /// <summary>宫格号 → 行列坐标</summary>
    public static (int Row, int Col) ToCell(int index)
    {
        if (index < 0 || index >= FillOrder.Length)
            throw new ArgumentOutOfRangeException(nameof(index), index, "宫格号必须在 0..7");

        return FillOrder[index];
    }

    /// <summary>行列坐标 → 宫格号；中心格返回 -1</summary>
    public static int FromCell(int row, int col)
    {
        for (int i = 0; i < FillOrder.Length; i++)
        {
            if (FillOrder[i].Row == row && FillOrder[i].Col == col) return i;
        }
        return -1;   // 中心 (1,1) 或越界
    }

    /// <summary>宫格号的两位数字写法，用于界面显示与日志</summary>
    public static string Label(int index)
    {
        var (row, col) = ToCell(index);
        return $"{row}{col}";
    }

    /// <summary>方位的中文名，日志里好读</summary>
    public static string DirectionName(int index) => index switch
    {
        -1 => "中心（取消）",
        0 => "正上",
        1 => "右上",
        2 => "正右",
        3 => "右下",
        4 => "正下",
        5 => "左下",
        6 => "正左",
        7 => "左上",
        _ => "?",
    };
}
