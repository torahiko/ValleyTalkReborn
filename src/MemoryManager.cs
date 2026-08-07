using System;
using System.Collections.Generic;
using System.Linq;
using StardewValley;
using StardewModdingAPI;

namespace ValleyTalk
{
    /// <summary>
    /// 记忆条目实体。关联 NpcName。
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
    /// 记忆管理器。每个 NPC 最多 10 条记忆，关联 NPC 名称。
    /// 持久化通过 SMAPI 的 SaveData 机制实现。
    /// </summary>
    internal class MemoryManager
    {
        public static readonly MemoryManager Instance = new MemoryManager();
        private const int MaxMemoryLengthChinese = 30;
        private const int MaxMemoryLengthEnglish = 30;
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
        /// Returns the max memory length based on the current game language.
        /// Chinese: 30 chars, other languages: 30 chars.
        /// </summary>
        public int GetMaxMemoryLength()
        {
            var lang = LocalizedContentManager.CurrentLanguageCode;
            return lang == LocalizedContentManager.LanguageCode.zh
                ? MaxMemoryLengthChinese
                : MaxMemoryLengthEnglish;
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
        /// 添加记忆。
        /// 返回 1 表示添加成功；
        /// 返回 0 表示参数无效或已达上限；
        /// 返回 -1 表示内容超过当前语言字符上限；
        /// 返回 null 表示内容重复已存在。
        /// </summary>
        public int? AddMemory(string npcName, string content)
        {
            if (string.IsNullOrWhiteSpace(npcName) || string.IsNullOrWhiteSpace(content))
                return 0;

            if (!_memories.TryGetValue(npcName, out var list))
            {
                list = new List<MemoryEntry>();
                _memories[npcName] = list;
            }

            // 上限检查
            if (list.Count >= MaxMemoriesPerNpc)
                return 0;

            // 长度检查（根据当前语言）
            var trimmedContent = content.Trim();
            var maxLen = GetMaxMemoryLength();
            if (trimmedContent.Length > maxLen)
                return -1;

            // 重复检查（不区分大小写）
            if (list.Any(m => string.Equals(m.Content, content, StringComparison.OrdinalIgnoreCase)))
                return null;

            list.Insert(0, new MemoryEntry
            {
                NpcName = npcName,
                Content = trimmedContent,
                CreatedAt = DateTime.Now,
                Source = "Manual"
            });

            ModEntry.SMonitor?.Log($"[MemoryManager] Added memory for {npcName}: {trimmedContent}", LogLevel.Debug);
            return 1;
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

            // 如果该 NPC 没有记忆了，移除键值
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
        /// Enhanced smart memory injection with highest priority enforcement.
        /// This is the recommended method for injecting memories into AI prompts.
        /// </summary>
        public string GetSmartMemoryContext(string npcName, string playerInput, int maxCount = MaxMemoriesInPrompt)
        {
            var entries = GetMemories(npcName);
            if (!entries.Any()) return "";

            var selected = entries.Take(maxCount).ToList();

            // Categorize memories into three types: unconditional addressing rules, conditional rules, and facts
            var addressingRules = selected.Where(e => IsBehavioralRule(e.Content) && !HasCondition(e.Content)).ToList();
            var conditionalRules = selected.Where(e => IsBehavioralRule(e.Content) && HasCondition(e.Content)).ToList();
            var facts = selected.Where(e => !IsBehavioralRule(e.Content)).ToList();

            bool playerAskingAboutSelf = IsPlayerAskingAboutSelf(playerInput);

            var sb = new System.Text.StringBuilder();

            // ========== Part 1: Mandatory declaration (highest priority) ==========
            sb.AppendLine("=== !!! HIGHEST PRIORITY - PLAYER MEMORIES !!! ===");
            sb.AppendLine("The following are facts the player has directly told you.");
            sb.AppendLine("THESE ARE TRUTH. YOUR PERSONALITY COMES SECOND.");
            sb.AppendLine();

            // ========== Part 2: Universal enforcement rules ==========
            sb.AppendLine("YOU MUST FOLLOW THESE RULES:");
            sb.AppendLine("1. When the player asks a question that these memories answer, you MUST use the memory.");
            sb.AppendLine("2. NEVER contradict a memory, even if you think you know better.");
            sb.AppendLine("3. NEVER guess or make up information when a memory provides the answer.");
            sb.AppendLine("4. If a memory says 'call me X' or 'address me as X', you MUST use it in every response.");
            sb.AppendLine("5. These memories OVERRIDE your default knowledge and personality.");
            sb.AppendLine();

            // ========== Part 3: Unconditional behavioral rules ==========
            if (addressingRules.Any())
            {
                sb.AppendLine("ADDRESSING RULES - APPLY TO EVERY RESPONSE:");
                foreach (var e in addressingRules)
                    sb.AppendLine($"- {e.Content}");
                sb.AppendLine();
            }

            // ========== Part 4: Conditional behavioral rules ==========
            if (conditionalRules.Any())
            {
                sb.AppendLine("SITUATIONAL RULES - ONLY APPLY WHEN TOPIC MATCHES:");
                sb.AppendLine("(Check if the current conversation is about the place/event mentioned)");
                foreach (var e in conditionalRules)
                    sb.AppendLine($"- {e.Content}");
                sb.AppendLine();
            }

            // ========== Part 5: Background facts ==========
            if (facts.Any())
            {
                if (playerAskingAboutSelf)
                {
                    sb.AppendLine("PLAYER FACTS - USE THESE TO ANSWER (DO NOT GUESS):");
                    foreach (var e in facts)
                        sb.AppendLine($"- {e.Content}");
                }
                else
                {
                    sb.AppendLine($"PLAYER FACTS ({facts.Count}) - DO NOT MENTION unless player asks about themselves.");
                    // Still list the facts so the AI knows they exist, just not usable right now
                    foreach (var e in facts)
                        sb.AppendLine($"- {e.Content}");
                }
                sb.AppendLine();
            }

            // ========== Part 6: Closing reinforcement ==========
            sb.AppendLine($"=== END OF {npcName}'S PLAYER MEMORIES ===");
            sb.AppendLine("REMEMBER: These memories are TRUTH. Your knowledge is SECONDARY.");

            return sb.ToString();
        }

        /// <summary>
        /// [DEPRECATED] Use GetSmartMemoryContext instead for better memory enforcement.
        /// 生成用于 AI 提示词的记忆上下文文本，最多取 5 条
        /// </summary>
        [Obsolete("Use GetSmartMemoryContext instead for better memory enforcement.")]
        public string GetMemoryPromptContext(string npcName, int maxCount = MaxMemoriesInPrompt)
        {
            var entries = GetMemories(npcName);
            if (!entries.Any())
                return "";

            var selected = entries.Take(maxCount).ToList();
            var behavioral = selected.Where(m => IsBehavioralRule(m.Content)).ToList();
            var background = selected.Where(m => !IsBehavioralRule(m.Content)).ToList();

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("### MEMORY_START ###");

            if (behavioral.Any())
            {
                sb.AppendLine("BEHAVIORAL RULES (must follow):");
                foreach (var e in behavioral)
                    sb.AppendLine($"- {e.Content}");
            }

            if (background.Any())
            {
                sb.AppendLine("BACKGROUND KNOWLEDGE (reference only):");
                foreach (var e in background)
                    sb.AppendLine($"- {e.Content}");
            }

            sb.AppendLine();
            sb.AppendLine("RULES FOR USING MEMORIES:");
            sb.AppendLine("You have learned certain preferences (likes/dislikes) about the player. These are for reference ONLY when the player asks directly or when the topic naturally arises (e.g., gift-giving, cooking, food-related conversations). NEVER actively bring them up in unrelated topics (weather, festivals, quests, etc.) as that would feel forced and awkward. However, if a memory explicitly specifies how you should address or behave towards the player (e.g., \"call me baby\"), you MUST strictly follow that instruction.");
            sb.AppendLine("### MEMORY_END ###");

            return sb.ToString();
        }

        /// <summary>
        /// Determines whether a memory is a behavioral rule (contains address-related keywords).
        /// </summary>
        private static bool IsBehavioralRule(string content)
        {
            if (string.IsNullOrEmpty(content)) return false;

            string[] keywords = { "叫我", "称呼我", "喊我", "call me", "address me", "refer to me" };
            var lower = content.ToLowerInvariant();

            foreach (var k in keywords)
            {
                if (lower.Contains(k)) return true;
            }

            return false;
        }

        /// <summary>
        /// Determines if the player is asking about themselves based on input keywords.
        /// </summary>
        private bool IsPlayerAskingAboutSelf(string input)
        {
            if (string.IsNullOrEmpty(input)) return false;
            var lower = input.ToLowerInvariant();
            string[] keywords = {
                "我喜欢", "我不喜欢", "我的", "我有没有", "我是不是", "关于我", "我最喜欢", "我讨厌",
                "i like", "i dislike", "my favorite", "about me", "do i", "am i", "what do i"
            };
            foreach (var k in keywords) if (lower.Contains(k)) return true;
            return false;
        }

        /// <summary>
        /// Determines if a memory content contains conditional markers.
        /// </summary>
        private bool HasCondition(string content)
        {
            if (string.IsNullOrEmpty(content)) return false;
            string[] markers = { "当", "如果", "在", "去", "到", "记得", "提醒", "when", "if", "at", "to", "remember", "remind" };
            var lower = content.ToLowerInvariant();
            foreach (var m in markers) if (lower.Contains(m)) return true;
            return false;
        }
    }
}
