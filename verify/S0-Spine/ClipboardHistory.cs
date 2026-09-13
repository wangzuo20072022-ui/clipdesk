using System;
using System.Collections.Generic;

namespace S0Spine;

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
}
