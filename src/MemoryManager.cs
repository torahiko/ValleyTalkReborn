using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using StardewValley;
using StardewModdingAPI;

namespace ValleytalkReborn;

// ─────────────────────────────────────────────────────────
// MemoryEntry
// ─────────────────────────────────────────────────────────

public class MemoryEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string NpcName { get; set; } = "";
    public string Content { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public string Source { get; set; } = "Manual";
}

// ─────────────────────────────────────────────────────────
// MemoryManager（实现 IMemoryProvider）
// ─────────────────────────────────────────────────────────

internal class MemoryManager : IMemoryProvider
{
    public static readonly MemoryManager Instance = new MemoryManager();
    public const int MaxMemoryLength = 60;
    public const int MaxMemoriesPerNpc = 10;
    public const int MaxMemoriesInPrompt = 5;

    private Dictionary<string, List<MemoryEntry>> _memories = new();
    private bool _isLoaded = false;

    private MemoryManager()
    {
        if (ModEntry.SHelper != null)
        {
            ModEntry.SHelper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
            // Fallback: load on first day start in case SaveLoaded already fired
            // before this singleton was accessed (e.g. after hot reload)
            ModEntry.SHelper.Events.GameLoop.DayStarted += OnDayStarted;
        }
    }

    private void OnDayStarted(object sender, StardewModdingAPI.Events.DayStartedEventArgs e)
    {
        if (!_isLoaded) Load();
    }

    /// <summary>
    /// Loads memory data if it has not been loaded yet.
    /// Safe to call multiple times — no-ops if already loaded.
    /// </summary>
    public void EnsureLoaded()
    {
        if (!_isLoaded) Load();
    }

    /// <summary>是否已成功加载过数据。</summary>
    public bool IsLoaded => _isLoaded;

    public void Cleanup()
    {
        try
        {
            _memories?.Clear();
            _memories = new Dictionary<string, List<MemoryEntry>>();
            _isLoaded = false;
            ModEntry.SMonitor?.Log("[MemoryManager] Cleaned up successfully.", LogLevel.Debug);
        }
        catch (Exception ex)
        {
            ModEntry.SMonitor?.Log($"[MemoryManager] Error during cleanup: {ex.Message}", LogLevel.Warn);
        }
    }

    public int GetMaxMemoryLength() => MaxMemoryLength;

    private void OnSaveLoaded(object sender, StardewModdingAPI.Events.SaveLoadedEventArgs e)
    {
        Load();
    }

    public void Load()
    {
        _memories.Clear();
        _isLoaded = false;

        if (string.IsNullOrWhiteSpace(Constants.SaveFolderName) || ModEntry.SHelper == null) return;

        string saveDir = Path.Combine(ModEntry.SHelper.DirectoryPath, "data", Constants.SaveFolderName);
        if (!Directory.Exists(saveDir))
        {
            _isLoaded = true;
            return;
        }

        try
        {
            var files = Directory.GetFiles(saveDir, "memory_*.json");
            foreach (var file in files)
            {
                string fileName = Path.GetFileName(file);
                const string prefix = "memory_";
                const string suffix = ".json";
                string npcName = fileName;
                if (fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    npcName = fileName.Substring(prefix.Length);
                if (npcName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    npcName = npcName.Substring(0, npcName.Length - suffix.Length);

                var list = ModEntry.SHelper.Data.ReadJsonFile<List<MemoryEntry>>(
                    $"data/{Constants.SaveFolderName}/{fileName}");
                if (list != null && list.Count > 0)
                {
                    _memories[npcName] = list;
                }
            }
            _isLoaded = true;
        }
        catch (Exception ex)
        {
            ModEntry.SMonitor?.Log($"[MemoryManager] 加载记忆失败: {ex.Message}", LogLevel.Warn);
            // _isLoaded 保持 false，表示加载不完整
        }
    }

    public void Save(string npcName)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(Constants.SaveFolderName) || ModEntry.SHelper == null) return;

            string path = $"data/{Constants.SaveFolderName}/memory_{npcName}.json";

            if (!_memories.TryGetValue(npcName, out var list) || list.Count == 0)
            {
                string fullPath = Path.Combine(ModEntry.SHelper.DirectoryPath, path);
                if (File.Exists(fullPath)) File.Delete(fullPath);
            }
            else
            {
                ModEntry.SHelper.Data.WriteJsonFile(path, list);
            }
        }
        catch (Exception ex)
        {
            ModEntry.SMonitor?.Log($"[MemoryManager] 保存 {npcName} 的记忆失败: {ex.Message}", LogLevel.Warn);
        }
    }

    /// <summary>保存所有 NPC 的记忆数据。</summary>
    public void SaveAll()
    {
        foreach (var npcName in _memories.Keys.ToList())
        {
            Save(npcName);
        }
    }

    public int? AddMemory(string npcName, string content)
    {
        if (string.IsNullOrWhiteSpace(npcName) || string.IsNullOrWhiteSpace(content)) return 0;

        if (!_memories.TryGetValue(npcName, out var list))
        {
            list = new List<MemoryEntry>();
            _memories[npcName] = list;
        }

        if (list.Count >= MaxMemoriesPerNpc) return 0;

        var trimmedContent = content.Trim();
        if (trimmedContent.Length > GetMaxMemoryLength()) return -1;

        if (list.Any(m => string.Equals(m.Content, content, StringComparison.OrdinalIgnoreCase)))
            return null;

        list.Insert(0, new MemoryEntry
        {
            NpcName = npcName,
            Content = trimmedContent,
            CreatedAt = DateTime.Now,
            Source = "Manual"
        });

        Save(npcName);
        return 1;
    }

    public int? EditMemory(string npcName, string id, string newContent)
    {
        if (!_memories.TryGetValue(npcName, out var list)) return 0;

        var entry = list.FirstOrDefault(m => m.Id == id);
        if (entry == null) return 0;

        var trimmed = newContent.Trim();
        if (trimmed.Length > GetMaxMemoryLength()) return -1;

        if (list.Any(m => m.Id != id && string.Equals(m.Content, trimmed, StringComparison.OrdinalIgnoreCase)))
            return null;

        entry.Content = trimmed;
        Save(npcName);
        return 1;
    }

    public bool RemoveMemory(string npcName, string id)
    {
        if (!_memories.TryGetValue(npcName, out var list)) return false;

        var entry = list.FirstOrDefault(m => m.Id == id);
        if (entry == null) return false;

        list.Remove(entry);
        if (list.Count == 0) _memories.Remove(npcName);

        Save(npcName);
        return true;
    }

    public List<MemoryEntry> GetMemories(string npcName)
    {
        if (!_memories.TryGetValue(npcName, out var list)) return new List<MemoryEntry>();
        return list.OrderByDescending(m => m.CreatedAt).ToList();
    }

    // ── IMemoryProvider 实现 ──

    public int GetMemoryCount(string npcName)
    {
        return _memories.TryGetValue(npcName, out var list) ? list.Count : 0;
    }

    // ── Prompt 生成 ──

    /// <summary>
    /// 生成注入 System Prompt 的记忆上下文。
    /// 重写指令，明确区分三类规则，消除 LLM 逃避执行的歧义。
    /// </summary>
    public string GetSmartMemoryContext(string npcName, int maxCount = MaxMemoriesInPrompt)
    {
        var entries = GetMemories(npcName);
        if (entries.Count == 0) return "";

        var selected = entries.Take(maxCount).ToList();
        var sb = new System.Text.StringBuilder();

        sb.AppendLine("=== USER-DEFINED HIGH-PRIORITY RULES ===");
        sb.AppendLine("Override persona defaults and story knowledge. Act naturally without meta-explanations.");
        sb.AppendLine();
        sb.AppendLine("RULE TYPES & EXECUTION:");
        sb.AppendLine("• [ADDRESS] (Name/Title): Apply to EVERY response without exception. Self-correct immediately if violated.");
        sb.AppendLine("• [BEHAVIOR] (Actions/Tone): Trigger strictly when matching context/situation occurs.");
        sb.AppendLine("• [FACT] (Background/Lore): Bring up naturally only when relevant; do not force into conversation.");
        sb.AppendLine();
        sb.AppendLine("ACTIVE RULES:");
        foreach (var e in selected)
        {
            sb.AppendLine($"- {e.Content}");
        }
        sb.AppendLine("=========================================");
        sb.AppendLine();
        sb.AppendLine($"=== END OF {npcName.ToUpperInvariant()}'S RULES — FULL COMPLIANCE REQUIRED ===");

        return sb.ToString();
    }
}