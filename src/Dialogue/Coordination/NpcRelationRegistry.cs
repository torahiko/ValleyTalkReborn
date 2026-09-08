using System;
using System.Collections.Generic;
using System.Linq;
using StardewModdingAPI;
using StardewValley;

namespace ValleytalkReborn;

internal sealed class NpcRelationRegistry
{
    public static NpcRelationRegistry Instance { get; } = new();

    // ★ 必须与 content.json 中的 Target 完全一致
    private const string RELATIONS_ASSET_KEY = "ValleytalkReborn/NpcRelations";

    private readonly Dictionary<string, NpcRelationEntry> _relations
        = new(StringComparer.OrdinalIgnoreCase);

    private readonly object _lock = new();
    private bool _loaded = false;

    private NpcRelationRegistry() { }

    /// <summary>
    /// 通过 SMAPI 内容管道加载（CP 自动接管）。
    /// </summary>
    public void LoadAll(IModHelper helper, IMonitor monitor)
    {
        if (_loaded) return;

        lock (_lock)
        {
            if (_loaded) return;

            try
            {
                if (helper == null)
                {
                    monitor?.Log("[NpcRelationRegistry] ✗ IModHelper is null, cannot load asset.", LogLevel.Warn);
                    return;
                }

                var wrapper = helper.GameContent.Load<NpcRelationsFile>(RELATIONS_ASSET_KEY);
                if (wrapper?.Relations == null)
                {
                    monitor?.Log(
                        $"[NpcRelationRegistry] ✗ Asset '{RELATIONS_ASSET_KEY}' returned null or empty.",
                        LogLevel.Warn);
                    return;
                }

                _relations.Clear();
                int count = 0;
                foreach (var entry in wrapper.Relations)
                {
                    if (string.IsNullOrWhiteSpace(entry.NpcA) || string.IsNullOrWhiteSpace(entry.NpcB))
                        continue;

                    string key = MakeKey(entry.NpcA, entry.NpcB);
                    if (entry.Disabled)
                        _relations.Remove(key);
                    else
                        _relations[key] = entry;

                    count++;
                }

                _loaded = true;
                monitor?.Log(
                    $"[NpcRelationRegistry] ✓ Loaded via CP — {count} relations from '{RELATIONS_ASSET_KEY}'.",
                    LogLevel.Info);
            }
            catch (Exception ex)
            {
                monitor?.Log(
                    $"[NpcRelationRegistry] ✗ Failed to load via CP: {ex.Message}\n{ex}",
                    LogLevel.Error);
            }
        }
    }

    /// <summary>
    /// 支持 CP 热重载：当其他 Mod 或 CP 重新编译/EditData 使资源失效时刷新。
    /// </summary>
    public void Reload(IModHelper helper, IMonitor monitor)
    {
        lock (_lock)
        {
            _loaded = false;
            _relations.Clear();
            LoadAll(helper, monitor);
            monitor?.Log("[NpcRelationRegistry] ↻ Hot-reload completed.", LogLevel.Debug);
        }
    }

    /// <summary>
    /// 清理并重置状态（如退回标题界面时调用）。
    /// </summary>
    public void Cleanup()
    {
        lock (_lock)
        {
            _loaded = false;
            _relations.Clear();
        }
    }

    public string GetRelationships(IEnumerable<NPC> participants, bool isChinese = false)
    {
        if (participants == null) return null;

        // ★ 惰性按需加载保险：如果调用时尚未完成加载，自动尝试加载一次
        if (!_loaded)
        {
            LoadAll(ModEntry.SHelper, ModEntry.SMonitor);
        }

        if (!_loaded || _relations.Count == 0) return null;

        string langCode = LocalizedContentManager.CurrentLanguageCode.ToString().ToLowerInvariant();

        var names = new HashSet<string>(
            participants.Where(n => n != null).Select(n => n.Name),
            StringComparer.OrdinalIgnoreCase);

        var lines = new List<string>();
        lock (_lock)
        {
            foreach (var entry in _relations.Values)
            {
                if (!names.Contains(entry.NpcA) || !names.Contains(entry.NpcB))
                    continue;

                // 优先匹配当前语言，回退英语
                string desc = null;
                if (entry.Descriptions != null)
                {
                    if (!entry.Descriptions.TryGetValue(langCode, out desc) || string.IsNullOrWhiteSpace(desc))
                    {
                        entry.Descriptions.TryGetValue("en", out desc);
                    }
                }

                if (!string.IsNullOrWhiteSpace(desc))
                    lines.Add($"- {desc}");
            }
        }

        return lines.Count > 0 ? string.Join("\n", lines) : null;
    }

    private static string MakeKey(string a, string b) =>
        string.Compare(a, b, StringComparison.OrdinalIgnoreCase) <= 0
            ? $"{a}|{b}"
            : $"{b}|{a}";
}