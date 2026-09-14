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

    /// <summary>
    /// 中心死区半径（物理像素）。小于它 = 没选，松手就取消。
    ///
    /// ★ 第三轮从 24 缩到 **12**（约 0.8mm）。
    ///   实测反馈：24px 太大，从 01 往回划时还落在死区里，于是变成取消。
    /// </summary>
    public const double DeadZoneRadius = 12;

    /// <summary>
    /// 触发一次"按运动方向重判"所需的累计移动量（物理像素）。
    ///
    /// 刻意取成和死区一样大：**这样停在中心附近手抖几像素不会被判成选中**，
    /// 而真要换方向时只需划 12px（约 0.8mm），几乎无感。
    /// </summary>
    public const double MoveThreshold = 12;

    /// <summary>
    /// 鼠标位移 → 宫格号（0..7，对应 FillOrder 下标）。-1 = 取消。
    ///
    /// ══ 第三轮的核心改动 ══════════════════════════════════════════
    ///
    /// 用户原话：
    ///   「鼠标只要移动过了，你就不能选中最中心的取消方块了。
    ///     你现在是往上拖很远选中 01 后，往下移动一点就回到中心取消。
    ///     我想要的是**往下移动一点就直接选中 21**」
    ///
    /// 我第一版方案在这里想错了：我以为"往下移动一点"指的是
    /// **鼠标回到了起点下方**，于是打算继续用"相对起点的方位"判，
    /// 只是把死区从 24 缩到 12。但那是错的 ——
    /// 鼠标从上方往下移动 12px 时，它**仍然在起点的上方**，
    /// 按方位判出来还是 01，根本不会变 21。
    ///
    /// 用户后来把口径说清楚了：**「看鼠标正在往哪个方向划」**。
    /// 所以这里必须用**运动方向**，不是方位。
    ///
    /// ── 规则 ────────────────────────────────────────────────────
    ///
    ///   ① 累计移动量 ≥ <see cref="MoveThreshold"/>
    ///        → 按**累计移动的方向**判            ← 这就是"划向哪儿选哪儿"
    ///
    ///   ② 没怎么动，且在中心死区内
    ///        → 选过方向就保持（**锁定，回不到取消**）
    ///        → 没选过才是真取消（-1）
    ///
    ///   ③ 没怎么动，但在死区外
    ///        → 保持上一次的选中（不要按方位重算）
    ///
    /// ★ 第 ③ 条很关键，是第一版没想通的地方：
    ///   如果这里改成"按方位重算"，那么用户往下划一下选中 21、
    ///   手一停，方位又变回"上方"，会**啪地跳回 01** —— 更糟。
    ///   停住不动就该保持，这是"只换不丢"的另一半。
    ///
    /// ── 为什么用"累计"而不是"这一帧的位移" ──────────────────────
    ///
    /// 慢速移动时每帧只走 1~2px，单帧位移永远够不到阈值。
    /// 累计起来就能触发，而且顺带是个天然滤波器：
    /// 手停着时的随机抖动方向不固定，累计起来会互相抵消，攒不到阈值。
    /// </summary>
    /// <param name="dx">鼠标相对**起点**的横向位移（物理像素）</param>
    /// <param name="dy">鼠标相对**起点**的纵向位移（物理像素，向下为正）</param>
    /// <param name="accX">自上次判定以来**累计**的移动量（横向）</param>
    /// <param name="accY">自上次判定以来**累计**的移动量（纵向，向下为正）</param>
    /// <param name="lastDirection">上一次判定出的宫格号；-1 = 还没选过任何方向</param>
    public static int Resolve(double dx, double dy, double accX, double accY, int lastDirection)
    {
        double moved = Math.Sqrt(accX * accX + accY * accY);

        // ① 有明确动向 → 按运动方向
        if (moved >= MoveThreshold)
        {
            return Sector(accX, accY);
        }

        // ② 在中心死区：选过就锁定，没选过才是取消
        double distance = Math.Sqrt(dx * dx + dy * dy);
        if (distance < DeadZoneRadius)
        {
            return lastDirection >= 0 ? lastDirection : -1;
        }

        // ③ 静止且不在中心 → 保持现状（不按方位重算，否则会跳回去）
        return lastDirection;
    }

    /// <summary>
    /// 给定位移，算它落在哪个 45° 扇区。
    ///
    /// 算法：把 360° 切成 8 个 45° 扇区，每个扇区中心对准一个方位。
    /// 正上方 01 占 [-22.5°, +22.5°)，顺时针递增。
    /// </summary>
    private static int Sector(double dx, double dy)
    {
        // atan2 用 (dx, -dy)：屏幕坐标 y 朝下，取负之后
        // 0° 指向正上、顺时针递增，和 FillOrder 的顺序对齐。
        double angle = Math.Atan2(dx, -dy) * 180.0 / Math.PI;   // (-180, 180]
        if (angle < 0) angle += 360;                             // [0, 360)

        // 每个扇区 45°，01 的扇区是 [-22.5, 22.5) → 加半个扇区再整除
        return (int)Math.Floor((angle + 22.5) / 45.0) % 8;
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
