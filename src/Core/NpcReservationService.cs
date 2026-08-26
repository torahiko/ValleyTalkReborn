using System;
using System.Collections.Generic;

namespace ValleytalkReborn;

/// <summary>
/// NPC 占用服务：协调 A2A 与 Ambient Bark 对 NPC 的独占访问。
/// 当前游戏逻辑全部在主线程运行，因此第一版不需要 lock。
/// </summary>
internal sealed class NpcReservationService
{
    private readonly Dictionary<string, string> _owners =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 尝试为指定 owner 占用 NPC。
    /// </summary>
    internal bool TryReserve(string npcName, string owner)
    {
        if (string.IsNullOrWhiteSpace(npcName) ||
            string.IsNullOrWhiteSpace(owner))
            return false;

        if (_owners.TryGetValue(npcName, out var existingOwner))
            return string.Equals(existingOwner, owner, StringComparison.OrdinalIgnoreCase);

        _owners[npcName] = owner;
        return true;
    }

    /// <summary>
    /// 检查 NPC 是否被任意 owner 占用。
    /// </summary>
    internal bool IsReserved(string npcName)
    {
        return !string.IsNullOrWhiteSpace(npcName)
            && _owners.ContainsKey(npcName);
    }

    /// <summary>
    /// 检查 NPC 是否被指定 owner 占用。
    /// </summary>
    internal bool IsReservedBy(string npcName, string owner)
    {
        return !string.IsNullOrWhiteSpace(npcName)
            && !string.IsNullOrWhiteSpace(owner)
            && _owners.TryGetValue(npcName, out var existingOwner)
            && string.Equals(existingOwner, owner, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 释放指定 owner 对 NPC 的占用。
    /// </summary>
    internal void Release(string npcName, string owner)
    {
        if (string.IsNullOrWhiteSpace(npcName) ||
            string.IsNullOrWhiteSpace(owner))
            return;

        if (_owners.TryGetValue(npcName, out var existingOwner) &&
            string.Equals(existingOwner, owner, StringComparison.OrdinalIgnoreCase))
        {
            _owners.Remove(npcName);
        }
    }

    /// <summary>
    /// 释放指定 owner 对所有 NPC 的占用。
    /// </summary>
    internal void ReleaseOwner(string owner)
    {
        if (string.IsNullOrWhiteSpace(owner))
            return;

        var names = new List<string>();

        foreach (var pair in _owners)
        {
            if (string.Equals(pair.Value, owner, StringComparison.OrdinalIgnoreCase))
                names.Add(pair.Key);
        }

        foreach (var name in names)
            _owners.Remove(name);
    }

    /// <summary>
    /// 清除所有占用记录。
    /// </summary>
    internal void Clear()
    {
        _owners.Clear();
    }
}
