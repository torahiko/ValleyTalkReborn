// Tier1SnapshotContext.cs
// VT3-B — Tier 1 块的不可变快照。
// 直接持有调用方字典（所有权转移；Ordinal 比较器由调用方 BuildTier1Snapshot 保证）。

using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace ValleytalkReborn;

/// <summary>
/// Tier 1 块的不可变快照视图。直接持有调用方传入的字典（所有权转移，
/// 调用方构造后不得再修改），快照内容在会话生命周期内保持稳定。
/// </summary>
public sealed class Tier1SnapshotContext
{
    private readonly Dictionary<string, string> _blocks;

    /// <summary>从外部字典构造快照。直接持有引用，字典所有权转移给本实例。</summary>
    public Tier1SnapshotContext(Dictionary<string, string> blocks)
    {
        _blocks = blocks ?? new Dictionary<string, string>();
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
