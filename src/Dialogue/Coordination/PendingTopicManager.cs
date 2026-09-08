using System;
using System.Collections.Generic;
using StardewModdingAPI;
using StardewModdingAPI.Events;

namespace ValleytalkReborn
{
    public class PendingTopicEntry
    {
        public string NpcName      { get; set; } = string.Empty;
        public string TopicContent { get; set; } = string.Empty;
        /// <summary>
        /// 实时 topic 用 DateTime 过期（3分钟内）；
        /// 跨天 topic 此字段设为 DateTime.MaxValue，依赖 IsCrossDay 标记。
        /// </summary>
        public DateTime ExpireTime { get; set; }
        /// <summary>
        /// true = 夜间整理写入的跨天记忆 topic，明天第一次见面消费。
        /// false = 传统实时短期 topic（3分钟过期）。
        /// </summary>
        public bool IsCrossDay { get; set; } = false;
    }

    /// <summary>
    /// 轻量级话题/情绪余温管理器。
    /// 支持两种模式：
    ///   - 实时短期 topic（纯内存，3分钟过期）：原有行为不变
    ///   - 跨天记忆 topic（持久化 JSON，次日第一次对话消费）：夜间整理写入
    /// </summary>
    public class PendingTopicManager
    {
        public static readonly PendingTopicManager Instance = new PendingTopicManager();

        private readonly Dictionary<string, PendingTopicEntry> _pendingTopics
            = new(StringComparer.OrdinalIgnoreCase);

        private static string FilePath =>
            $"data/PendingTopics_{Constants.SaveFolderName}.json";

        private PendingTopicManager()
        {
            if (ModEntry.SHelper != null)
            {
                ModEntry.SHelper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
                ModEntry.SHelper.Events.GameLoop.Saving     += OnSaving;
            }
        }

        // ── 事件处理 ───────────────────────────────────────────

        private void OnSaveLoaded(object sender, SaveLoadedEventArgs e)
        {
            try
            {
                var persisted = ModEntry.SHelper.Data
                    .ReadJsonFile<Dictionary<string, PendingTopicEntry>>(FilePath);
                if (persisted == null) return;

                foreach (var kv in persisted)
                {
                    if (!_pendingTopics.ContainsKey(kv.Key))
                        _pendingTopics[kv.Key] = kv.Value;
                }
                ModEntry.SMonitor?.Log(
                    $"[PendingTopicManager] Loaded {persisted.Count} cross-day topic(s).",
                    LogLevel.Debug);
            }
            catch (Exception ex)
            {
                ModEntry.SMonitor?.Log(
                    $"[PendingTopicManager] Load failed: {ex.Message}", LogLevel.Warn);
            }
        }

        private void OnSaving(object sender, SavingEventArgs e)
        {
            try
            {
                var toSave = new Dictionary<string, PendingTopicEntry>();
                foreach (var kv in _pendingTopics)
                {
                    if (kv.Value.IsCrossDay)
                        toSave[kv.Key] = kv.Value;
                }
                ModEntry.SHelper.Data.WriteJsonFile(FilePath, toSave);
            }
            catch (Exception ex)
            {
                ModEntry.SMonitor?.Log(
                    $"[PendingTopicManager] Save failed: {ex.Message}", LogLevel.Warn);
            }
        }

        // ── 公开 API ────────────────────────────────────────────

        /// <summary>
        /// 设置实时短期 topic（默认 3 分钟内有效，原有行为不变）。
        /// </summary>
        public void SetPendingTopic(string npcName, string topic, int validMinutes = 3)
        {
            if (string.IsNullOrWhiteSpace(npcName) || string.IsNullOrWhiteSpace(topic)) return;

            _pendingTopics[npcName] = new PendingTopicEntry
            {
                NpcName      = npcName,
                TopicContent = topic.Trim(),
                ExpireTime   = DateTime.Now.AddMinutes(validMinutes),
                IsCrossDay   = false
            };
        }

        /// <summary>
        /// 写入跨天记忆 topic（夜间整理调用）。
        /// 次日第一次与该 NPC 对话时消费，消费后自动清除。
        /// 若该 NPC 已有跨天 topic，追加到末尾而非覆盖，保留多条记忆的丰富性。
        /// </summary>
        public void SetCrossDayTopic(string npcName, string topic)
        {
            if (string.IsNullOrWhiteSpace(npcName) || string.IsNullOrWhiteSpace(topic)) return;

            if (_pendingTopics.TryGetValue(npcName, out var existing) && existing.IsCrossDay)
            {
                existing.TopicContent += "\n" + topic.Trim();
                ModEntry.SMonitor?.Log(
                    $"[PendingTopicManager] Cross-day topic appended for [{npcName}].",
                    LogLevel.Debug);
                return;
            }

            _pendingTopics[npcName] = new PendingTopicEntry
            {
                NpcName      = npcName,
                TopicContent = topic.Trim(),
                ExpireTime   = DateTime.MaxValue,
                IsCrossDay   = true
            };
            ModEntry.SMonitor?.Log(
                $"[PendingTopicManager] Cross-day topic set for [{npcName}]: {topic}",
                LogLevel.Debug);
        }

        /// <summary>
        /// 获取并消费 topic（消费后立即清除，保证只对下一次直接对话生效一次）。
        /// 实时 topic 检查时间过期；跨天 topic 直接消费。
        /// </summary>
        public string ConsumePendingTopic(string npcName)
        {
            if (string.IsNullOrWhiteSpace(npcName)) return null;

            if (!_pendingTopics.TryGetValue(npcName, out var entry)) return null;

            _pendingTopics.Remove(npcName);

            if (entry.IsCrossDay)
                return entry.TopicContent;

            return DateTime.Now <= entry.ExpireTime ? entry.TopicContent : null;
        }

        /// <summary>
        /// 清理所有内存数据（换日或卸载存档时调用）。
        /// 跨天 topic 已在 OnSaving 持久化，此处安全清除。
        /// </summary>
        public void Cleanup()
        {
            _pendingTopics.Clear();
        }
    }
}
