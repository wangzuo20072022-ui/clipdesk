using System;
using System.Collections.Generic;

namespace S2Strip;

/// <summary>
/// 最近 N 条剪贴板文本的环形缓冲。
/// 正式项目里这是 Core\HistoryStore.cs（20 条），验证时只用 5 条 —— 面板上放得下。
/// </summary>
internal sealed class ClipboardHistory
{
    public sealed record Entry(string Text, DateTime At, string Source);

    private readonly List<Entry> _items = new();

    public int Capacity { get; }

    public ClipboardHistory(int capacity) => Capacity = capacity;

    public IReadOnlyList<Entry> Items => _items;

    /// <summary>新增一条，返回它是不是真的进了列表（重复的会被顶到最前，不产生新项）</summary>
    public bool Add(string text, string source)
    {
        if (string.IsNullOrEmpty(text)) return false;

        // 与最新一条重复 → 只更新时间，不新增（就是「重复复制置顶不产生重复项」）
        if (_items.Count > 0 && _items[0].Text == text)
        {
            _items[0] = _items[0] with { At = DateTime.Now };
            return false;
        }

        // 历史里已有 → 移到最前
        int existing = _items.FindIndex(e => e.Text == text);
        if (existing >= 0)
        {
            var moved = _items[existing] with { At = DateTime.Now, Source = source };
            _items.RemoveAt(existing);
            _items.Insert(0, moved);
            return false;
        }

        _items.Insert(0, new Entry(text, DateTime.Now, source));
        while (_items.Count > Capacity) _items.RemoveAt(_items.Count - 1);
        return true;
    }

    /// <summary>
    /// 把某条**按内容**顶到最前（最新），其余顺延 —— 时间戳也刷新成现在。
    ///
    /// 这是「粘贴过的东西应该变成最新的」这条需求的实现：
    ///   用 Alt+V 粘了第 5 条 → 它下次应该出现在 01（正上方），而不是还在老位置。
    ///
    /// 为什么**按内容**找而不是按索引：
    ///   从按下 Alt+V 到松开粘贴之间有几百毫秒，这期间完全可能又复制了新东西，
    ///   列表顺序会变 —— 按索引去移会移错人。按内容找永远移的就是那一条。
    ///
    /// 找不到就不动（比如那条已经被挤出环形缓冲了），返回 false。
    /// </summary>
    public bool Promote(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;

        int existing = _items.FindIndex(e => e.Text == text);
        if (existing < 0) return false;

        var moved = _items[existing] with { At = DateTime.Now };
        _items.RemoveAt(existing);
        _items.Insert(0, moved);
        return true;
    }
}
