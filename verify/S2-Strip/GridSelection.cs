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
    /// 判定一次"方向"所需的移动量（物理像素）。
    ///
    /// 10px 在 150% 缩放下约 1.8mm —— 手腕几乎不用动。
    /// 用户要的是「往下拖一点就变 21」，所以这个值必须小。
    /// </summary>
    public const double MoveThreshold = 10;

    /// <summary>一次判定的结果</summary>
    public readonly struct DialResult
    {
        /// <summary>选中哪个格子；-1 = 还没动过（松手就是取消）</summary>
        public readonly int Index;

        /// <summary>
        /// 这次是不是**按移动方向**判出来的。
        /// true 表示"这一小段移动已经用掉了"，调用方要把判定基准点挪到当前光标位置。
        /// </summary>
        public readonly bool ConsumedMotion;

        public DialResult(int index, bool consumedMotion)
        {
            Index = index;
            ConsumedMotion = consumedMotion;
        }
    }

    /// <summary>
    /// 方向判定 —— **纯函数**，28+ 条单测覆盖。
    ///
    /// ══ 手感模型（第四轮定稿）════════════════════════════════════
    ///
    /// 一句话：**看鼠标正在往哪个方向划，而不是看鼠标在起点的哪个方位。**
    ///
    /// 用户原话：
    ///   「你往上拖选择了 01，往下拖一点就选择了 21，
    ///     但是如果你再往上拖，它应该**立马回到 01**，因为往上拖动了。」
    ///
    /// 所以：
    ///
    ///     往上划 → 01      往下划 → 21      再往上划 → 又回到 01
    ///     往右划 → 12      （用户的考题，答案就是 12）
    ///
    /// ── 怎么实现：**锚点 + 位移** ────────────────────────────────
    ///
    /// 记一个"锚点" = **上一次判定出方向时的光标位置**。
    /// 每次都看当前光标相对锚点位移了多少：
    ///
    ///     位移 ≥ MoveThreshold  →  按这个位移的方向定格子，然后把锚点挪到当前光标
    ///     位移 &lt; MoveThreshold  →  保持上一次的选中（还没选过就是取消）
    ///
    /// ── 为什么这样是对的 ────────────────────────────────────────
    ///
    ///   ① **"往上划就回 01"自然是成立的** —— 往上划，位移方向就是上。
    ///      不用任何特殊规则。
    ///
    ///   ② **手停着不会疯跳**。这是关键：如果用"每帧位移的累加"，
    ///      手停着时的抖动会一点点攒起来，攒够阈值就随机翻格子。
    ///      而用"相对锚点的位移"，**在同一个点附近抖动的净位移始终很小**，
    ///      永远够不到阈值 —— 天然稳定，不需要任何"稳定 N 毫秒"的计时器。
    ///
    ///   ③ **慢速移动也能触发**。位移是相对锚点量的，划多慢都算数，
    ///      不像"每帧位移"那样慢速时每帧只走 1~2px 永远不够阈值。
    ///
    /// ── 为什么不再需要"死区半径"这个概念 ────────────────────────
    ///
    /// 用户要求「只要移动过了，就不能再选中最中心的取消方块」。
    /// 而"移动过"这件事本身用位移就够了：没动过 → 位移一直是 0 → 判出 -1（取消）；
    /// 动过之后 → 位移方向一定存在 → 永远是某个格子。
    /// **取消只可能发生在"一次都没动"的情况下**，这正是用户要的。
    /// </summary>
    /// <param name="moveX">当前光标相对**锚点**的横向位移（物理像素）</param>
    /// <param name="moveY">当前光标相对**锚点**的纵向位移（物理像素，向下为正）</param>
    /// <param name="lastDirection">上一次判出的格子；-1 = 还没动过</param>
    public static DialResult Resolve(double moveX, double moveY, int lastDirection)
    {
        double moved = Math.Sqrt(moveX * moveX + moveY * moveY);

        if (moved >= MoveThreshold)
        {
            return new DialResult(Sector(moveX, moveY), consumedMotion: true);
        }

        // 没动够 —— 保持上一次的选中。
        // lastDirection = -1 时这里自然就返回 -1（取消），不需要额外判断。
        return new DialResult(lastDirection, consumedMotion: false);
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
