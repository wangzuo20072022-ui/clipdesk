using System;

namespace S2Strip;

/// <summary>
/// 九宫格方位选择 —— 纯函数，不碰 UI。
///
/// 正式项目里这是 Core\GridSelection.cs，会被单元测试覆盖。
/// 放在这里是刻意的：方向判定是最容易出 bug 的地方（八个扇区的边界、
/// 中心死区、负坐标），必须能脱离窗口单独验。
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
/// </summary>
internal static class GridSelection
{
    /// <summary>
    /// 填充顺序 —— 下标 = 排序号-1，值 = (row, col)。
    /// 顺时针一圈，从正上方 01 开始。
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

    /// <summary>中心死区半径（物理像素）。小于它 = 没选，松手就取消。</summary>
    public const double DeadZoneRadius = 24;

    /// <summary>
    /// 鼠标位移 → 宫格号（0..7，对应 FillOrder 下标）。
    /// 返回 -1 表示**在中心死区内 = 取消**。
    ///
    /// 算法：把 360° 切成 8 个 45° 扇区，每个扇区中心对准一个方位。
    /// 正上方 01 占 [-22.5°, +22.5°)，顺时针递增。
    /// </summary>
    public static int Resolve(double dx, double dy)
    {
        double distance = Math.Sqrt(dx * dx + dy * dy);
        if (distance < DeadZoneRadius) return -1;

        // atan2 用 (dx, -dy)：屏幕坐标 y 朝下，取负之后
        // 0° 指向正上、顺时针递增，和 FillOrder 的顺序对齐。
        double angle = Math.Atan2(dx, -dy) * 180.0 / Math.PI;   // (-180, 180]
        if (angle < 0) angle += 360;                             // [0, 360)

        // 每个扇区 45°，01 的扇区是 [-22.5, 22.5) → 加半个扇区再整除
        int sector = (int)Math.Floor((angle + 22.5) / 45.0) % 8;
        return sector;
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
