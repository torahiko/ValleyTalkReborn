// Tier1SnapshotContext.cs
// VT3-B — Tier 1 块的不可变快照。
// 内部复制外部字典（Ordinal 比较器），此后对原始字典的修改不会污染快照。

using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace ValleytalkReborn;

/// <summary>
/// Tier 1 块的不可变快照视图。构造时做一次防御性深拷贝（Ordinal），
/// 此后快照内容在会话生命周期内保持稳定。
/// </summary>
public sealed class Tier1SnapshotContext
{
    private readonly Dictionary<string, string> _blocks;

    /// <summary>从外部字典构造快照。内部复制，Ordinal 键比较。</summary>
    public Tier1SnapshotContext(Dictionary<string, string> blocks)
    {
        _blocks = new Dictionary<string, string>(blocks ?? new Dictionary<string, string>(),
            System.StringComparer.Ordinal);
    }

    /// <summary>按 blockId 取块内容；缺失返回 string.Empty。</summary>
    public string Get(string blockId)
    {
        return _blocks.TryGetValue(blockId, out var value) ? value : string.Empty;
    }

    /// <summary>是否包含非空指定 blockId。</summary>
    public bool Has(string blockId)
    {
        return _blocks.TryGetValue(blockId, out var value) && value != null;
    }

    /// <summary>全部块（只读视图）。</summary>
    public IReadOnlyDictionary<string, string> All =>
        new ReadOnlyDictionary<string, string>(_blocks);
}
