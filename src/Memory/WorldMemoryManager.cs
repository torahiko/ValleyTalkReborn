using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace ValleytalkReborn
{
    internal class WorldMemoryManager
    {
        public static readonly WorldMemoryManager Instance = new WorldMemoryManager();

        public const int MaxEntries = 10;
        public const int MaxEntryLength = 60;

        private List<MemoryEntry> _entries = new List<MemoryEntry>();
        private bool _isLoaded = false;

        private static bool IsChineseLanguage =>
            LocalizedContentManager.CurrentLanguageCode == LocalizedContentManager.LanguageCode.zh;

        private WorldMemoryManager()
        {
            // 不在构造函数里注册事件，改由 ModEntry.Entry() 显式调用 Initialize
        }

        public void Initialize(IModHelper helper)
        {
            // 先取消防止重复注册（Cleanup → Entry 的重入场景）
            helper.Events.GameLoop.SaveLoaded -= OnSaveLoaded;
            helper.Events.GameLoop.DayStarted -= OnDayStarted;

            helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
            helper.Events.GameLoop.DayStarted += OnDayStarted;
        }

        public void EnsureLoaded()
        {
            if (!_isLoaded) Load();
        }

        public bool IsLoaded => _isLoaded;

        public void Load()
        {
            _entries.Clear();
            _isLoaded = false;

            if (string.IsNullOrWhiteSpace(Constants.SaveFolderName) || ModEntry.SHelper == null)
            {
                _isLoaded = true;
                return;
            }

            string path = $"data/{Constants.SaveFolderName}/WorldMemory.json";

            try
            {
                var loaded = ModEntry.SHelper.Data.ReadJsonFile<List<MemoryEntry>>(path);

                if (loaded != null)
                {
                    foreach (var entry in loaded)
                    {
                        if (entry == null)
                            continue;

                        if (string.IsNullOrWhiteSpace(entry.Content))
                            continue;

                        if (string.IsNullOrWhiteSpace(entry.Id))
                            entry.Id = Guid.NewGuid().ToString();

                        entry.NpcName = "WORLD";
                        entry.Content = entry.Content.Trim();

                        if (entry.CreatedAt == default(DateTime))
                            entry.CreatedAt = DateTime.Now;

                        _entries.Add(entry);
                    }
                }

                _isLoaded = true;
            }
            catch (Exception ex)
            {
                ModEntry.SMonitor?.Log(
                    $"[WorldMemoryManager] Load failed: {ex.Message}", LogLevel.Warn);
            }
        }

        /// <summary>
        /// [FIX-1] 统一使用 Data API，不再混用 File.Delete
        /// </summary>
        public void Save()
        {
            if (string.IsNullOrWhiteSpace(Constants.SaveFolderName) || ModEntry.SHelper == null)
                return;

            string path = $"data/{Constants.SaveFolderName}/WorldMemory.json";

            try
            {
                ModEntry.SHelper.Data.WriteJsonFile(path, _entries);
            }
            catch (Exception ex)
            {
                ModEntry.SMonitor?.Log(
                    $"[WorldMemoryManager] Save failed: {ex.Message}", LogLevel.Warn);
            }
        }

        public void Cleanup()
        {
            try
            {
                _entries.Clear();
                _isLoaded = false;

                ModEntry.SMonitor?.Log("[WorldMemoryManager] Cleaned up successfully.", LogLevel.Debug);
            }
            catch (Exception ex)
            {
                ModEntry.SMonitor?.Log($"[WorldMemoryManager] Error during cleanup: {ex.Message}", LogLevel.Warn);
            }
        }

        /// <summary>
        /// [FIX-5] 返回明确枚举
        /// </summary>
        public MemoryOperationResult AddEntry(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
                return MemoryOperationResult.NotFound;

            if (_entries.Count >= MaxEntries)
                return MemoryOperationResult.CapacityFull;

            var trimmed = content.Trim();

            if (trimmed.Length > MaxEntryLength)
                return MemoryOperationResult.TooLong;

            if (_entries.Any(e => string.Equals(e.Content, trimmed, StringComparison.OrdinalIgnoreCase)))
                return MemoryOperationResult.Duplicate;

            _entries.Insert(0, new MemoryEntry
            {
                NpcName = "WORLD",
                Content = trimmed,
                CreatedAt = DateTime.Now,
                Source = "Manual"
            });

            Save();
            return MemoryOperationResult.Success;
        }

        /// <summary>
        /// [FIX-5] 返回明确枚举
        /// </summary>
        public MemoryOperationResult EditEntry(string id, string newContent)
        {
            var entry = _entries.FirstOrDefault(e => e.Id == id);
            if (entry == null)
                return MemoryOperationResult.NotFound;

            var trimmed = newContent.Trim();

            if (trimmed.Length > MaxEntryLength)
                return MemoryOperationResult.TooLong;

            if (_entries.Any(e =>
                    e.Id != id &&
                    string.Equals(e.Content, trimmed, StringComparison.OrdinalIgnoreCase)))
                return MemoryOperationResult.Duplicate;

            entry.Content = trimmed;
            Save();
            return MemoryOperationResult.Success;
        }

        public bool RemoveEntry(string id)
        {
            var entry = _entries.FirstOrDefault(e => e.Id == id);
            if (entry == null) return false;

            _entries.Remove(entry);
            Save();
            return true;
        }

        public List<MemoryEntry> GetEntries()
        {
            EnsureLoaded();
            return _entries.OrderByDescending(e => e.CreatedAt).ToList();
        }

        public int Count
        {
            get
            {
                EnsureLoaded();
                return _entries.Count;
            }
        }

        public string GetPromptText()
        {
            EnsureLoaded();

            if (_entries.Count == 0)
                return string.Empty;

            bool isZh = IsChineseLanguage;

            var selected = _entries
                .OrderByDescending(e => e.CreatedAt)
                .Take(MaxEntries)
                .ToList();

            var sb = new StringBuilder();

            sb.AppendLine(isZh
                ? "=== 全局世界观与小镇既定事实 ==="
                : "=== GLOBAL WORLD LORE & FACTS ===");

            sb.AppendLine(isZh
                ? "以下是鹈鹕镇当前公认的既定事实。请将这些事实融入你的日常认知中："
                : "These are established facts about the valley. Treat them as natural, universal knowledge:");

            sb.AppendLine();

            foreach (var e in selected)
                sb.AppendLine($"- [WORLD] {e.Content}");

            sb.AppendLine("=========================================");

            return sb.ToString();
        }

        private void OnSaveLoaded(object sender, SaveLoadedEventArgs e) => Load();

        private void OnDayStarted(object sender, DayStartedEventArgs e)
        {
            if (!_isLoaded) Load();
        }
    }
}