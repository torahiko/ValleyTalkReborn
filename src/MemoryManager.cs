using System;
using System.Collections.Generic;
using System.Linq;
using StardewModdingAPI;

namespace ValleyTalk
{
    /// <summary>
    /// 单条记忆实体。严格绑定 NpcName。
    /// </summary>
    public class MemoryEntry
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string NpcName { get; set; } = "";
        public string Content { get; set; } = "";
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public string Source { get; set; } = "Manual";
    }

    /// <summary>
    /// 记忆管理器（单例）。每个 NPC 最多 10 条记忆，严格按 NPC 隔离。
    /// 持久化通过 SMAPI 的 SaveData 机制实现。
    /// </summary>
    internal static class MemoryManager
    {
        public static readonly MemoryManager Instance = new MemoryManager();

        public const int MaxMemoriesPerNpc = 10;
        public const int MaxMemoriesInPrompt = 5;

        private const string SaveDataKey = "ValleyTalk.Memories";

        // Key = NpcName, Value = 记忆列表（按时间倒序，最新在前）
        private Dictionary<string, List<MemoryEntry>> _memories = new();

        private MemoryManager()
        {
            // 注册存档事件
            ModEntry.SHelper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
            ModEntry.SHelper.Events.GameLoop.Saving += OnSaving;
        }

        /// <summary>
        /// 加载存档时读取记忆数据
        /// </summary>
        private void OnSaveLoaded(object sender, StardewModdingAPI.Events.SaveLoadedEventArgs e)
        {
            Load();
        }

        /// <summary>
        /// 保存存档时写入记忆数据
        /// </summary>
        private void OnSaving(object sender, StardewModdingAPI.Events.SavingEventArgs e)
        {
            Save();
        }

        /// <summary>
        /// 从存档数据加载记忆
        /// </summary>
        public void Load()
        {
            try
            {
                var data = ModEntry.SHelper.Data.ReadSaveData<Dictionary<string, List<MemoryEntry>>>(SaveDataKey);
                _memories = data ?? new Dictionary<string, List<MemoryEntry>>();
            }
            catch (Exception ex)
            {
                ModEntry.SMonitor?.Log($"[MemoryManager] 加载记忆失败: {ex.Message}", LogLevel.Debug);
                _memories = new Dictionary<string, List<MemoryEntry>>();
            }
        }

        /// <summary>
        /// 保存记忆到存档
        /// </summary>
        public void Save()
        {
            try
            {
                ModEntry.SHelper.Data.WriteSaveData(SaveDataKey, _memories);
            }
            catch (Exception ex)
            {
                ModEntry.SMonitor?.Log($"[MemoryManager] 保存记忆失败: {ex.Message}", LogLevel.Warn);
            }
        }

        /// <summary>
        /// 添加记忆。若该 NPC 记忆数量 >= 10，返回 false。
        /// 若内容重复（忽略大小写），返回 null 表示已存在。
        /// </summary>
        public bool? AddMemory(string npcName, string content)
        {
            if (string.IsNullOrWhiteSpace(npcName) || string.IsNullOrWhiteSpace(content))
                return false;

            if (!_memories.TryGetValue(npcName, out var list))
            {
                list = new List<MemoryEntry>();
                _memories[npcName] = list;
            }

            // 上限检查
            if (list.Count >= MaxMemoriesPerNpc)
                return false;

            // 重复检查（忽略大小写）
            if (list.Any(m => string.Equals(m.Content, content, StringComparison.OrdinalIgnoreCase)))
                return null;

            list.Insert(0, new MemoryEntry
            {
                NpcName = npcName,
                Content = content.Trim(),
                CreatedAt = DateTime.Now,
                Source = "Manual"
            });

            return true;
        }

        /// <summary>
        /// 按 ID 删除记忆
        /// </summary>
        public bool RemoveMemory(string npcName, string id)
        {
            if (!_memories.TryGetValue(npcName, out var list))
                return false;

            var entry = list.FirstOrDefault(m => m.Id == id);
            if (entry == null)
                return false;

            list.Remove(entry);

            // 如果该 NPC 没有记忆了，清理字典
            if (list.Count == 0)
                _memories.Remove(npcName);

            return true;
        }

        /// <summary>
        /// 获取指定 NPC 的所有记忆（按时间倒序，最新在前）
        /// </summary>
        public List<MemoryEntry> GetMemories(string npcName)
        {
            if (!_memories.TryGetValue(npcName, out var list))
                return new List<MemoryEntry>();

            return list.OrderByDescending(m => m.CreatedAt).ToList();
        }

        /// <summary>
        /// 获取记忆数量
        /// </summary>
        public int GetMemoryCount(string npcName)
        {
            if (!_memories.TryGetValue(npcName, out var list))
                return 0;
            return list.Count;
        }

        /// <summary>
        /// 构建用于 AI 上下文的记忆提示文本（取最近最多 5 条）
        /// </summary>
        public string GetMemoryPromptContext(string npcName, int maxCount = MaxMemoriesInPrompt)
        {
            var entries = GetMemories(npcName);
            if (!entries.Any())
                return "";

            var selected = entries.Take(maxCount).ToList();
            var lines = selected.Select((e, i) => $"{i + 1}. {e.Content}");
            return "【高优先级记忆，你必须严格遵守】\n" + string.Join("\n", lines);
        }
    }
}
