using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using StardewValley;
using StardewModdingAPI;

namespace ValleyTalk
{
    public class MemoryEntry
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string NpcName { get; set; } = "";
        public string Content { get; set; } = "";
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public string Source { get; set; } = "Manual";
    }

    internal class MemoryManager
    {
        public static readonly MemoryManager Instance = new MemoryManager();
        private const int MaxMemoryLengthChinese = 30;
        private const int MaxMemoryLengthEnglish = 30;
        public const int MaxMemoriesPerNpc = 10;
        public const int MaxMemoriesInPrompt = 5;

        private Dictionary<string, List<MemoryEntry>> _memories = new();

        private MemoryManager()
        {
            if (ModEntry.SHelper != null)
            {
                ModEntry.SHelper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
            }
        }

        public void Cleanup()
        {
            try
            {
                if (ModEntry.SHelper != null)
                {
                    ModEntry.SHelper.Events.GameLoop.SaveLoaded -= OnSaveLoaded;
                }
                _memories?.Clear();
                _memories = new Dictionary<string, List<MemoryEntry>>();
                ModEntry.SMonitor?.Log("[MemoryManager] Cleaned up successfully.", LogLevel.Debug);
            }
            catch (Exception ex)
            {
                ModEntry.SMonitor?.Log($"[MemoryManager] Error during cleanup: {ex.Message}", LogLevel.Warn);
            }
        }

        public int GetMaxMemoryLength()
        {
            var lang = LocalizedContentManager.CurrentLanguageCode;
            return lang == LocalizedContentManager.LanguageCode.zh
                ? MaxMemoryLengthChinese
                : MaxMemoryLengthEnglish;
        }

        private void OnSaveLoaded(object sender, StardewModdingAPI.Events.SaveLoadedEventArgs e)
        {
            Load();
        }

        /// <summary>
        /// 功能3：文件 I/O 的细粒度拆分（加载阶段）
        /// 扫描当前存档专属文件夹下所有的 memory_{NpcName}.json 文件
        /// </summary>
        public void Load()
        {
            _memories.Clear();
            if (string.IsNullOrWhiteSpace(Constants.SaveFolderName) || ModEntry.SHelper == null) return;

            string saveDir = Path.Combine(ModEntry.SHelper.DirectoryPath, "data", Constants.SaveFolderName);
            if (!Directory.Exists(saveDir)) return;

            try
            {
                var files = Directory.GetFiles(saveDir, "memory_*.json");
                foreach (var file in files)
                {
                    // Use exact prefix/suffix stripping instead of Replace to avoid
                    // corrupting NPC names that might contain "memory" or ".json" substrings.
                    string fileName = Path.GetFileName(file);
                    const string prefix = "memory_";
                    const string suffix = ".json";
                    string npcName = fileName;
                    if (fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        npcName = fileName.Substring(prefix.Length);
                    if (npcName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                        npcName = npcName.Substring(0, npcName.Length - suffix.Length);
                    
                    var list = ModEntry.SHelper.Data.ReadJsonFile<List<MemoryEntry>>($"data/{Constants.SaveFolderName}/{fileName}");
                    if (list != null)
                    {
                        _memories[npcName] = list;
                    }
                }
            }
            catch (Exception ex)
            {
                ModEntry.SMonitor?.Log($"[MemoryManager] 加载记忆失败: {ex.Message}", LogLevel.Debug);
            }
        }

        /// <summary>
        /// 功能3：文件 I/O 的细粒度拆分（保存阶段）
        /// 只覆写指定 NPC 的独立文件
        /// </summary>
        public void Save(string npcName)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(Constants.SaveFolderName) || ModEntry.SHelper == null) return;
                
                string path = $"data/{Constants.SaveFolderName}/memory_{npcName}.json";
                
                if (!_memories.TryGetValue(npcName, out var list) || list.Count == 0)
                {
                    // 如果记忆被清空了，直接删除文件以节省空间
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

            Save(npcName); // 仅保存当前 NPC
            return 1;
        }

        /// <summary>
        /// 功能5底层：编辑已有记忆
        /// </summary>
        public int? EditMemory(string npcName, string id, string newContent)
        {
            if (!_memories.TryGetValue(npcName, out var list)) return 0;
            
            var entry = list.FirstOrDefault(m => m.Id == id);
            if (entry == null) return 0;

            var trimmed = newContent.Trim();
            if (trimmed.Length > GetMaxMemoryLength()) return -1;

            // 查重时忽略自身
            if (list.Any(m => m.Id != id && string.Equals(m.Content, trimmed, StringComparison.OrdinalIgnoreCase)))
                return null;

            entry.Content = trimmed;
            Save(npcName); // 仅保存当前 NPC
            return 1;
        }

        public bool RemoveMemory(string npcName, string id)
        {
            if (!_memories.TryGetValue(npcName, out var list)) return false;

            var entry = list.FirstOrDefault(m => m.Id == id);
            if (entry == null) return false;

            list.Remove(entry);
            if (list.Count == 0) _memories.Remove(npcName);

            Save(npcName); // 仅保存当前 NPC
            return true;
        }

        public List<MemoryEntry> GetMemories(string npcName)
        {
            if (!_memories.TryGetValue(npcName, out var list)) return new List<MemoryEntry>();
            return list.OrderByDescending(m => m.CreatedAt).ToList();
        }

        public int GetMemoryCount(string npcName)
        {
            return _memories.TryGetValue(npcName, out var list) ? list.Count : 0;
        }

        /// <summary>
        /// 功能4：摆脱硬编码，纯 LLM 意图判断提示词
        /// </summary>
        public string GetSmartMemoryContext(string npcName, string playerInput, int maxCount = MaxMemoriesInPrompt)
        {
            var entries = GetMemories(npcName);
            if (!entries.Any()) return "";

            var selected = entries.Take(maxCount).ToList();
            var sb = new System.Text.StringBuilder();

            sb.AppendLine("=== !!! HIGHEST PRIORITY - PLAYER MEMORIES !!! ===");
            sb.AppendLine("The following are facts or rules the player has established. THESE OVERRIDE YOUR DEFAULT KNOWLEDGE:");
            sb.AppendLine();
            sb.AppendLine("INSTRUCTIONS ON HOW TO USE THESE MEMORIES:");
            sb.AppendLine("1. Evaluate the current context and the player's input dynamically.");
            sb.AppendLine("2. If a memory dictates how to address the player, do so unconditionally in every response.");
            sb.AppendLine("3. If a memory contains a situational condition (e.g., 'when X happens', 'if I do Y'), determine if the current conversation matches the condition before applying it.");
            sb.AppendLine("4. If a memory is a fact about the player, use it ONLY if it naturally fits the current topic or if the player is explicitly asking about themselves. NEVER force unrelated memories into casual topics.");
            sb.AppendLine();
            
            sb.AppendLine("MEMORIES TO EVALUATE:");
            foreach (var e in selected)
            {
                sb.AppendLine($"- {e.Content}");
            }
            sb.AppendLine();
            sb.AppendLine($"=== END OF {npcName}'S PLAYER MEMORIES ===");

            return sb.ToString();
        }
    }
}