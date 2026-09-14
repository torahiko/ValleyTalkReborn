using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace ValleytalkReborn
{
    /// <summary>
    /// 中央对话历史管理器：彻底替代原作者的 EventHistoryReader.cs
    /// 完美支持单人/联机主机（SaveData）与联机客机（Multiplayer Local JSON）模式。
    /// </summary>
    internal class DialogueHistoryManager
    {
        public static DialogueHistoryManager Instance { get; } = new DialogueHistoryManager();

        private readonly Dictionary<string, List<DialogueHistoryEntry>> _history = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DialogueHistoryEntry> _lastEntry = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DialogueHistoryEntry> _pendingGifts = new(StringComparer.OrdinalIgnoreCase);

        private readonly object _historyLock = new();
        private const int MaxEntriesPerNpc = 300;
        private const string SaveKey = "ValleyTalk.DialogueHistory";

        private DialogueHistoryManager()
        {
            if (ModEntry.SHelper != null)
            {
                ModEntry.SHelper.Events.GameLoop.Saving += OnSaving;
                ModEntry.SHelper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
                ModEntry.SHelper.Events.GameLoop.DayEnding += OnDayEnding;
            }
        }

        #region Event Handlers

        private void OnSaving(object sender, SavingEventArgs e)
        {
            SaveSync();
        }

        private void OnSaveLoaded(object sender, SaveLoadedEventArgs e)
        {
            Load();
        }

        private void OnDayEnding(object sender, DayEndingEventArgs e)
        {
            PurgeEavesdropEntries();
        }

        #endregion

        #region Public API

        /// <summary>
        /// 统一清洗对话文本，剔除星露谷控制标签、模板标记和自定义标签，防止脏数据进入历史库。
        /// 所有写入历史的入口（RecordNpcDialogue / RecordPlayerDialogue / RecordGiftReaction）在写入前都必须调用此方法。
        /// </summary>
        internal static string SanitizeForStorage(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;

            string clean = text;
            // ${...} 模板标记
            clean = Regex.Replace(clean, @"\$\{.*?\}", "");
            // #$q、#$r 等带参数的控制标签（含数字、空格、点号路径），匹配到下一个 # 之前的所有内容
            clean = Regex.Replace(clean, @"#\$[^\#]+#?", "");
            // $x 单字符肖像标记
            clean = Regex.Replace(clean, @"\$[a-zA-Z0-9]", "");
            // [ACTION:...] [MOOD:...] 自定义标签
            clean = Regex.Replace(clean, @"\[(?:ACTION|MOOD):.*?\]", "");
            // skip# 前缀
            clean = Regex.Replace(clean, @"^skip#", "");

            return clean.Trim();
        }

        public void RecordNpcDialogue(string npcName, string text, string dialogueType = "dialogue")
        {
            var sanitized = SanitizeForStorage(text);
            if (string.IsNullOrWhiteSpace(sanitized)) return;
            var entry = new DialogueHistoryEntry(npcName, sanitized, SpeakerType.NPC, dialogueType);
            AddEntry(npcName, entry);
        }

        public void RecordPlayerDialogue(string npcName, string text)
        {
            var sanitized = SanitizeForStorage(text);
            if (string.IsNullOrWhiteSpace(sanitized)) return;
            
            string trimmed = sanitized.Trim();
            if (trimmed == "..." || trimmed == "…" || trimmed == "......" || trimmed == "。。。。")
            {
                return;
            }

            var entry = new DialogueHistoryEntry(
                "Player",
                sanitized,
                SpeakerType.Player,
                "conversation"
            );
            AddEntry(npcName, entry);
        }

        public void RecordGiftGiven(string npcName, string giftName, int taste)
        {
            string tasteLabel = taste switch
            {
                0 => "Love",
                2 => "Like",
                4 => "Dislike",
                6 => "Hate",
                _ => "Neutral"
            };

            string text = $"Given gift: {giftName} (Reaction: {tasteLabel})";
            var entry = new DialogueHistoryEntry("System", text, SpeakerType.System, "gift")
            {
                GiftName = giftName,
                GiftTaste = taste
            };

            _pendingGifts[npcName] = entry;
            AddEntry(npcName, entry);
        }

        public void RecordGiftReaction(string npcName, string reactionText)
        {
            var sanitized = SanitizeForStorage(reactionText);
            if (string.IsNullOrWhiteSpace(sanitized)) return;
            var entry = new DialogueHistoryEntry(npcName, sanitized, SpeakerType.NPC, "gift");
            AddEntry(npcName, entry);
            _pendingGifts.Remove(npcName);
        }

        public void RecordSystemEvent(string npcName, string text, string dialogueType = "system")
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            var entry = new DialogueHistoryEntry("System", text, SpeakerType.System, dialogueType);
            AddEntry(npcName, entry);
        }

        public void PurgeEavesdropEntries()
        {
            lock (_historyLock)
            {
                foreach (var list in _history.Values)
                {
                    list.RemoveAll(e => e.DialogueType == "eavesdrop");
                }
            }
        }

        public void ConsumeEavesdropEntries(string npcName)
        {
            lock (_historyLock)
            {
                if (!_history.TryGetValue(npcName, out var list)) return;
                // Directly remove — eavesdrop entries are transient, destroy on use
                list.RemoveAll(e => e.DialogueType == "eavesdrop");
            }
        }

        public void RecordConversationExchange(string npcName, string playerLine, string npcResponse)
        {
            if (!string.IsNullOrWhiteSpace(playerLine))
                RecordPlayerDialogue(npcName, playerLine);

            if (!string.IsNullOrWhiteSpace(npcResponse))
                RecordNpcDialogue(npcName, npcResponse, "conversation");
        }

        public List<DialogueHistoryEntry> GetHistory(string npcName)
        {
            lock (_historyLock)
            {
                if (_history.TryGetValue(npcName, out var entries))
                    return entries.ToList();
                return new List<DialogueHistoryEntry>();
            }
        }

        public List<string> GetFormattedHistory(string npcName)
        {
            var entries = GetHistory(npcName);
            return entries.Select(e => e.Format(npcName)).ToList();
        }

        public List<DialogueHistoryEntry> GetRecentHistory(string npcName, int count)
        {
            var entries = GetHistory(npcName);
            return entries.TakeLast(Math.Min(count, entries.Count)).ToList();
        }

        public void ClearHistory(string npcName)
        {
            lock (_historyLock)
            {
                _history.Remove(npcName);
                _lastEntry.Remove(npcName);
                _pendingGifts.Remove(npcName);
            }
            DialogueMemoryCompressor.ClearCache(npcName);
        }

        public void ClearAllHistory()
        {
            lock (_historyLock)
            {
                _history.Clear();
                _lastEntry.Clear();
                _pendingGifts.Clear();
            }
        }

        public string GetMostRecentNpc()
        {
            lock (_historyLock)
            {
                return _lastEntry.OrderByDescending(x => x.Value.Timestamp.TimeOfDay).FirstOrDefault().Key ?? "";
            }
        }

        public int GetTotalEntryCount()
        {
            lock (_historyLock)
            {
                return _history.Values.Sum(list => list.Count);
            }
        }

        public bool HasHistory(string npcName)
        {
            lock (_historyLock)
            {
                return _history.ContainsKey(npcName) && _history[npcName].Any();
            }
        }

        #endregion

        #region Add Entry & Optimized Deduplication

        private void AddEntry(string npcName, DialogueHistoryEntry entry)
        {
            lock (_historyLock)
            {
                if (!_history.TryGetValue(npcName, out var list))
                {
                    list = new List<DialogueHistoryEntry>();
                    _history[npcName] = list;
                }

                if (_lastEntry.TryGetValue(npcName, out var last))
                {
                    if (entry.IsDuplicateOf(last))
                    {
                        return;
                    }
                }

                list.Add(entry);
                _lastEntry[npcName] = entry;

                if (list.Count > MaxEntriesPerNpc)
                {
                    list.RemoveRange(0, list.Count - MaxEntriesPerNpc);
                }
            }

            TryTriggerCompression(npcName);
        }

        #endregion

        #region Save / Load (Multiplayer Support)

        private string GetMultiplayerFilePath()
        {
            return $"data/multiplayer/{Constants.SaveFolderName}_DialogueHistory.json";
        }

        public void SaveSync()
        {
            lock (_historyLock)
            {
                try
                {
                    var snapshot = CreateDataSnapshotInternal();
                    if (snapshot == null || snapshot.Count == 0) return;

                    if (Context.IsMainPlayer)
                    {
                        ModEntry.SHelper.Data.WriteSaveData(SaveKey, snapshot);
                        ModEntry.SMonitor?.Log($"[DialogueHistoryManager] [Host] Saved history for {snapshot.Count} NPCs.", LogLevel.Debug);
                    }
                    else
                    {
                        ModEntry.SHelper.Data.WriteJsonFile(GetMultiplayerFilePath(), snapshot);
                        ModEntry.SMonitor?.Log($"[DialogueHistoryManager] [Farmhand] Saved local history to {GetMultiplayerFilePath()}.", LogLevel.Debug);
                    }
                }
                catch (Exception ex)
                {
                    ModEntry.SMonitor?.Log($"[DialogueHistoryManager] Save failed: {ex.Message}", LogLevel.Error);
                }
            }
        }

        public void Load()
        {
            lock (_historyLock)
            {
                try
                {
                    Dictionary<string, List<SerializableEntry>> data = null;

                    if (Context.IsMainPlayer)
                    {
                        data = ModEntry.SHelper.Data.ReadSaveData<Dictionary<string, List<SerializableEntry>>>(SaveKey);
                    }
                    else
                    {
                        data = ModEntry.SHelper.Data.ReadJsonFile<Dictionary<string, List<SerializableEntry>>>(GetMultiplayerFilePath());
                    }

                    if (data == null) return;

                    _history.Clear();
                    _lastEntry.Clear();

                    foreach (var (npcName, serializableEntries) in data)
                    {
                        var entries = serializableEntries.Select(se => se.ToEntry()).ToList();
                        if (entries.Count > 0)
                        {
                            _history[npcName] = entries;
                            _lastEntry[npcName] = entries[^1];
                        }
                    }

                    ModEntry.SMonitor?.Log($"[DialogueHistoryManager] Loaded history for {_history.Count} NPCs (IsMainPlayer={Context.IsMainPlayer}).", LogLevel.Debug);
                }
                catch (Exception ex)
                {
                    ModEntry.SMonitor?.Log($"[DialogueHistoryManager] Load failed: {ex.Message}", LogLevel.Error);
                }
            }
        }

        private Dictionary<string, List<SerializableEntry>> CreateDataSnapshotInternal()
        {
            var snapshot = new Dictionary<string, List<SerializableEntry>>();
            foreach (var (npcName, list) in _history)
            {
                if (list != null && list.Count > 0)
                {
                    snapshot[npcName] = list.Select(SerializableEntry.FromEntry).ToList();
                }
            }
            return snapshot;
        }

        #endregion

        #region Cleanup

        public void Cleanup()
        {
            SaveSync();

            // 注意：不在此处注销 Saving/SaveLoaded/DayEnding 事件。
            // 单例构造器只运行一次，注销后无重订阅路径；
            // 首次返回标题后再读档，OnSaving → SaveSync 将永久失效。

            lock (_historyLock)
            {
                _history.Clear();
                _lastEntry.Clear();
                _pendingGifts.Clear();
            }

            ModEntry.SMonitor?.Log("[DialogueHistoryManager] Cleaned up successfully.", LogLevel.Debug);
        }

        #endregion

        #region Private Helpers

        private void TryTriggerCompression(string npcName)
        {
            if (!ModEntry.Config.EnableMemoryCompression) return;

            List<DialogueHistoryEntry> entries;
            lock (_historyLock)
            {
                if (!_history.TryGetValue(npcName, out var list)) return;
                entries = list.ToList();
            }

            int recentCount = ModEntry.Config.MemoryRecentCount;
            if (entries.Count <= recentCount * 2) return;

            var cached = DialogueMemoryCompressor.GetCachedSummary(npcName, entries.Count, recentCount);
            if (string.IsNullOrEmpty(cached))
            {
                _ = System.Threading.Tasks.Task.Run(() =>
                    DialogueMemoryCompressor.GetCompressedSummary(npcName, entries, recentCount));
            }
        }

        #endregion
    }

    // ─────────────────────────────────────────────────────────
    // 用于存档 JSON 序列化的数据包裹类
    // ─────────────────────────────────────────────────────────
    internal class SerializableEntry
    {
        public Guid Id { get; set; }
        public string SpeakerName { get; set; } = "";
        public string Text { get; set; } = "";
        public SpeakerType SpeakerType { get; set; }
        public string DialogueType { get; set; } = "";
        public int Year { get; set; }
        public Season Season { get; set; }
        public int Day { get; set; }
        public int TimeOfDay { get; set; }
        public string GiftName { get; set; }
        public int GiftTaste { get; set; } = -1;

        public static SerializableEntry FromEntry(DialogueHistoryEntry entry)
        {
            return new SerializableEntry
            {
                Id = entry.Id,
                SpeakerName = entry.SpeakerName,
                Text = entry.Text,
                SpeakerType = entry.SpeakerType,
                DialogueType = entry.DialogueType,
                Year = entry.Timestamp.Year,
                Season = entry.Timestamp.Season,
                Day = entry.Timestamp.DayOfMonth,
                TimeOfDay = entry.Timestamp.TimeOfDay,
                GiftName = entry.GiftName,
                GiftTaste = entry.GiftTaste
            };
        }

        public DialogueHistoryEntry ToEntry()
        {
            var time = new StardewTime(Year, Season, Day, TimeOfDay);
            var entry = new DialogueHistoryEntry(SpeakerName, Text, SpeakerType, time, DialogueType)
            {
                GiftName = GiftName,
                GiftTaste = GiftTaste
            };
            return entry;
        }
    }
}