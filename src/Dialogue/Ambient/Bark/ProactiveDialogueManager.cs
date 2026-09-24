using System;
using System.Collections.Generic;
using StardewModdingAPI;
using StardewValley;

namespace ValleytalkReborn;

internal sealed class ProactiveDialogueDecision
{
    public BarkOutputMode Mode { get; init; }
    public bool SensoryTriggered { get; init; }
    public SensoryEvaluation Sensory { get; init; }
    public string DenyReason { get; init; }
}

internal static class ProactiveDialogueManager
{
    internal const int MainDialogueGateGameMinutes = 120;
    internal const int MaxMicroSocialPerNpcPerDay = 1;

    private static readonly Random _rng = new Random();

    // ── 测试缝线 ──
    internal static Func<int> DayProvider { get; set; } = () => Game1.Date.TotalDays;
    internal static Func<int> NowGameTimeProvider { get; set; } = () => Game1.timeOfDay;
    internal static Func<bool> PlayerMovingProvider { get; set; } = () => Game1.player?.isMoving() == true;
    internal static Func<string, int> HeartsProvider { get; set; } = GetHearts;
    internal static Func<double> MidChanceRollProvider { get; set; } = () => _rng.NextDouble();
    internal static Func<bool> EnabledProvider { get; set; }
        = () => ModEntry.Config?.EnableProactiveMicroSocial == true;
    internal static Func<float> MidChanceProvider { get; set; }
        = () => Math.Clamp(ModEntry.Config?.MicroSocialMidFriendshipChance ?? 0f, 0f, 1f);

    private static readonly Dictionary<string, (int DayNumber, int TimeOfDay)> _lastMainDialogue =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> _microSocialFiredToday = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _lock = new object();

    private static int GetHearts(string npcName)
    {
        try
        {
            var p = Game1.player;
            if (p?.friendshipData != null && p.friendshipData.TryGetValue(npcName, out var fs) && fs != null)
                return fs.Points / 250;
        }
        catch { }
        return 0;
    }

    private static int ToMinutes(int hhmm) => (hhmm / 100) * 60 + hhmm % 100;

    internal static void OnMainDialogueStarted(string npcName)
    {
        if (string.IsNullOrWhiteSpace(npcName)) return;

        lock (_lock)
        {
            _lastMainDialogue[npcName] = (DayProvider(), NowGameTimeProvider());
        }
    }

    internal static ProactiveDialogueDecision Resolve(string npcName)
    {
        if (string.IsNullOrWhiteSpace(npcName))
            return new ProactiveDialogueDecision { Mode = BarkOutputMode.Soliloquy, DenyReason = "invalid-name" };

        try
        {
            if (!EnabledProvider())
                return new ProactiveDialogueDecision { Mode = BarkOutputMode.Soliloquy, DenyReason = "disabled" };

            // 闸门 0：距上次主对话不足 120 分钟
            if (_lastMainDialogue.TryGetValue(npcName, out var last))
            {
                if (last.DayNumber == DayProvider())
                {
                    int diff = ToMinutes(NowGameTimeProvider()) - ToMinutes(last.TimeOfDay);
                    if (diff >= 0 && diff <= MainDialogueGateGameMinutes)
                        return new ProactiveDialogueDecision { Mode = BarkOutputMode.Soliloquy, DenyReason = "gate0-recent-main-dialogue" };
                }
            }

            // 规则 1：感官路径
            var ev = SensoryClassifier.Evaluate(npcName);
            if (ev.Hit)
            {
                if (!SensoryCooldownStore.IsLocked(npcName, ev.Type))
                {
                    if (_microSocialFiredToday.Contains(npcName))
                        return new ProactiveDialogueDecision { Mode = BarkOutputMode.Soliloquy, DenyReason = "daily-cap" };

                    return new ProactiveDialogueDecision
                    {
                        Mode = BarkOutputMode.MicroSocial,
                        SensoryTriggered = true,
                        Sensory = ev,
                    };
                }
                // 已锁定 → 落回关系路径（闸门 2/3）
            }

            // 闸门 2：玩家移动中
            if (PlayerMovingProvider())
                return new ProactiveDialogueDecision { Mode = BarkOutputMode.Soliloquy, DenyReason = "gate2-player-moving" };

            // 闸门 3：心数阶梯
            int hearts;
            try { hearts = HeartsProvider(npcName); }
            catch { hearts = 0; }
            if (hearts >= 7)
            {
                if (_microSocialFiredToday.Contains(npcName))
                    return new ProactiveDialogueDecision { Mode = BarkOutputMode.Soliloquy, DenyReason = "daily-cap" };

                return new ProactiveDialogueDecision { Mode = BarkOutputMode.MicroSocial, SensoryTriggered = false };
            }

            if (hearts >= 3)
            {
                if (MidChanceRollProvider() < MidChanceProvider())
                {
                    if (_microSocialFiredToday.Contains(npcName))
                        return new ProactiveDialogueDecision { Mode = BarkOutputMode.Soliloquy, DenyReason = "daily-cap" };

                    return new ProactiveDialogueDecision { Mode = BarkOutputMode.MicroSocial, SensoryTriggered = false };
                }
                return new ProactiveDialogueDecision { Mode = BarkOutputMode.Soliloquy, DenyReason = "gate3-roll-fail" };
            }

            return new ProactiveDialogueDecision { Mode = BarkOutputMode.Soliloquy, DenyReason = "gate3-low-hearts" };
        }
        catch (Exception ex)
        {
            ModEntry.SMonitor?.Log($"[ProactiveDialogueManager] Resolve error: {ex.Message}", LogLevel.Trace);
            return new ProactiveDialogueDecision { Mode = BarkOutputMode.Soliloquy, DenyReason = "error" };
        }
    }

    internal static bool Commit(string npcName, ProactiveDialogueDecision decision)
    {
        if (decision == null || string.IsNullOrWhiteSpace(npcName) || decision.Mode != BarkOutputMode.MicroSocial)
            return false;

        try
        {
            lock (_lock)
            {
                if (_microSocialFiredToday.Contains(npcName)) return false;

                if (decision.SensoryTriggered && decision.Sensory != null)
                {
                    if (!SensoryCooldownStore.TryClaim(npcName, decision.Sensory.Type, decision.Sensory.Category))
                        return false;
                }

                _microSocialFiredToday.Add(npcName);
                ModEntry.SMonitor?.Log(
                    $"[Proactive] MicroSocial committed: {npcName} (sensory:{(decision.SensoryTriggered ? decision.Sensory.Type.ToString() : "none")})",
                    LogLevel.Debug);
                return true;
            }
        }
        catch
        {
            return false;
        }
    }

    internal static void ClearAll()
    {
        try
        {
            lock (_lock)
            {
                _lastMainDialogue.Clear();
                _microSocialFiredToday.Clear();
            }
        }
        catch { }
    }
}
