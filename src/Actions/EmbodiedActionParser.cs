using System;
using System.Text.RegularExpressions;
using StardewValley;
using StardewModdingAPI;

namespace ValleytalkReborn
{
    public static class EmbodiedActionParser
    {
        // 🔧 FIX: EmoteRegex 和 FaceRegex 的方括号必须用 \[ \] 转义，
        //         否则 [...] 会被当作正则字符类，永远匹配不到完整标签。
        private static readonly Regex EmoteRegex   = new(@"\[ACTION:EMOTE:([A-Za-z]+)\]",  RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex FaceRegex    = new(@"\[ACTION:FACE:([A-Za-z]+)\]",   RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex MoveRegex    = new(@"\[ACTION:(STEP:FORWARD|STEP:BACKWARD|STEP:LEFT|STEP:RIGHT|STEP:UP|STEP:DOWN)\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex FollowRegex  = new(@"\[ACTION:FOLLOW\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex GotoRegex    = new(@"\[ACTION:GOTO:(-?\d+),(-?\d+)\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex InviteRegex  = new(@"\[ACTION:INVITE:([A-Za-z]+)\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex EndDateRegex = new(@"\[ACTION:END_DATE\]",            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex StayHomeRegex    = new(@"\[ACTION:STAY_HOME\]",    RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex AllDayFollowRegex = new(@"\[ACTION:ALL_DAY_FOLLOW\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // ─────────────────────────────────────────────────────────────────────
        // Public entry points
        // ─────────────────────────────────────────────────────────────────────

        public static void ParseAndExecute(
            NPC npc,
            string[] lines,
            string originalPlayerInput = null,
            bool allowFallbackEmotes = true)
        {
            if (npc == null || lines == null || lines.Length == 0) return;

            if (MovementManager.Instance.IsMoving)
            {
                StripMoveTags(lines);
                ModEntry.SMonitor?.Log("[EmbodiedActionParser] Action mutex active — all action tags stripped.", LogLevel.Debug);
                return;
            }

            string faceDirection  = null;
            bool   moveDispatched = false;

            for (int i = 0; i < lines.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i])) continue;

                lines[i] = EmoteRegex.Replace(lines[i], match =>
                {
                    DispatchExplicitEmote(npc, match.Groups[1].Value.ToUpperInvariant());
                    return string.Empty;
                });

                lines[i] = FaceRegex.Replace(lines[i], match =>
                {
                    faceDirection = match.Groups[1].Value.ToUpperInvariant();
                    ModEntry.SMonitor?.Log($"[EmbodiedActionParser] Face direction queued: {faceDirection} for {npc.Name}", LogLevel.Debug);
                    return string.Empty;
                });

                lines[i] = MoveRegex.Replace(lines[i], match =>
                {
                    if (!moveDispatched)
                    {
                        var actionStr = match.Groups[1].Value.ToUpperInvariant();
                        moveDispatched = true;
                        ModEntry.SMonitor?.Log($"[EmbodiedActionParser] Move action queued: {actionStr} for {npc.Name}", LogLevel.Debug);
                        MovementManager.Instance.QueueMovement(npc, actionStr);
                    }
                    else
                    {
                        ModEntry.SMonitor?.Log($"[EmbodiedActionParser] Duplicate move tag '{match.Value}' discarded for {npc.Name}.", LogLevel.Debug);
                    }
                    return string.Empty;
                });
                lines[i] = GotoRegex.Replace(lines[i], match =>
                {
                    if (!moveDispatched &&
                        int.TryParse(match.Groups[1].Value, out int tx) &&
                        int.TryParse(match.Groups[2].Value, out int ty))
                    {
                        moveDispatched = true;
                        var targetTile    = new Microsoft.Xna.Framework.Vector2(tx, ty);
                        var capturedInput = originalPlayerInput;
                        ModEntry.SMonitor?.Log($"[EmbodiedActionParser] GOTO dispatched: ({tx},{ty}) for {npc.Name}", LogLevel.Debug);
                        MovementManager.Instance.MoveToTile(npc, targetTile, onComplete: () =>
                        {
                            GotoCompletionInjector.InjectCompletion(npc, capturedInput);
                        });
                    }
                    else if (moveDispatched)
                    {
                        ModEntry.SMonitor?.Log($"[EmbodiedActionParser] Duplicate GOTO tag '{match.Value}' discarded for {npc.Name}.", LogLevel.Debug);
                    }
                    return string.Empty;
                });

                lines[i] = FollowRegex.Replace(lines[i], match =>
                {
                    ModEntry.SMonitor?.Log($"[EmbodiedActionParser] FOLLOW dispatched for {npc.Name}", LogLevel.Debug);
                    StardewValley.DelayedAction.functionAfterDelay(() =>
                    {
                        try
                        {
                            bool ok = DateManager.Instance.TryStartFollow(npc);
                            ModEntry.SMonitor?.Log(
                                ok ? $"[EmbodiedActionParser] Follow started: {npc.Name}" : $"[EmbodiedActionParser] Follow start failed: {npc.Name}",
                                ok ? LogLevel.Info : LogLevel.Warn);
                        }
                        catch (Exception ex)
                        {
                            ModEntry.SMonitor?.Log($"[EmbodiedActionParser] FOLLOW error: {ex.Message}", LogLevel.Error);
                        }
                    }, 200);
                    return string.Empty;
                });

                lines[i] = StayHomeRegex.Replace(lines[i], match =>
                {
                    ModEntry.SMonitor?.Log($"[EmbodiedActionParser] STAY_HOME dispatched for {npc.Name}", LogLevel.Debug);
                    StardewValley.DelayedAction.functionAfterDelay(() =>
                    {
                        try
                        {
                            CompanionScheduleManager.Instance.SetStayHomeMode(npc.Name);
                            ModEntry.SMonitor?.Log($"[EmbodiedActionParser] StayHome set: {npc.Name}", LogLevel.Info);
                        }
                        catch (Exception ex)
                        {
                            ModEntry.SMonitor?.Log($"[EmbodiedActionParser] STAY_HOME error: {ex.Message}", LogLevel.Error);
                        }
                    }, 200);
                    return string.Empty;
                });

                lines[i] = AllDayFollowRegex.Replace(lines[i], match =>
                {
                    ModEntry.SMonitor?.Log($"[EmbodiedActionParser] ALL_DAY_FOLLOW dispatched for {npc.Name}", LogLevel.Debug);
                    StardewValley.DelayedAction.functionAfterDelay(() =>
                    {
                        try
                        {
                            CompanionScheduleManager.Instance.SetAllDayFollow(npc.Name);
                            ModEntry.SMonitor?.Log($"[EmbodiedActionParser] AllDayFollow set: {npc.Name}", LogLevel.Info);
                        }
                        catch (Exception ex)
                        {
                            ModEntry.SMonitor?.Log($"[EmbodiedActionParser] ALL_DAY_FOLLOW error: {ex.Message}", LogLevel.Error);
                        }
                    }, 200);
                    return string.Empty;
                });

                lines[i] = InviteRegex.Replace(lines[i], match =>
                {
                    string locationId = match.Groups[1].Value;
                    ModEntry.SMonitor?.Log($"[EmbodiedActionParser] INVITE dispatched: {npc.Name} → {locationId}", LogLevel.Debug);
                    StardewValley.DelayedAction.functionAfterDelay(() =>
                    {
                        try
                        {
                            bool ok = DateManager.Instance.TryScheduleDate(npc, locationId);
                            ModEntry.SMonitor?.Log(
                                ok  ? $"[EmbodiedActionParser] Date scheduled: {npc.Name} -> {locationId}" : $"[EmbodiedActionParser] Date scheduling failed: {npc.Name} -> {locationId}",
                                ok  ? LogLevel.Info : LogLevel.Warn);
                        }
                        catch (Exception ex)
                        {
                            ModEntry.SMonitor?.Log($"[EmbodiedActionParser] INVITE error: {ex.Message}", LogLevel.Error);
                        }
                    }, 200);
                    return string.Empty;
                });

                lines[i] = EndDateRegex.Replace(lines[i], match =>
                {
                    ModEntry.SMonitor?.Log($"[EmbodiedActionParser] END_DATE dispatched for {npc.Name}", LogLevel.Debug);
                    StardewValley.DelayedAction.functionAfterDelay(() =>
                    {
                        try
                        {
                            DateManager.Instance.EndDateGracefully(npc.Name);
                            ModEntry.SMonitor?.Log($"[EmbodiedActionParser] Date ended gracefully: {npc.Name}", LogLevel.Info);
                        }
                        catch (Exception ex)
                        {
                            ModEntry.SMonitor?.Log($"[EmbodiedActionParser] END_DATE error: {ex.Message}", LogLevel.Error);
                        }
                    }, 500);
                    return string.Empty;
                });

                if (allowFallbackEmotes) DispatchFallbackEmote(npc, lines[i]);
                lines[i] = lines[i].Trim();
            }

            if (faceDirection != null)
                DispatchFaceDirection(npc, faceDirection);
        }

        public static void ParseEmotesAndFaceOnly(
            NPC npc,
            string[] lines,
            bool allowFallbackEmotes = true)
        {
            if (npc == null || lines == null || lines.Length == 0) return;

            string faceDirection = null;

            for (int i = 0; i < lines.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i])) continue;

                lines[i] = EmoteRegex.Replace(lines[i], match =>
                {
                    DispatchExplicitEmote(npc, match.Groups[1].Value.ToUpperInvariant());
                    return string.Empty;
                });

                lines[i] = FaceRegex.Replace(lines[i], match =>
                {
                    faceDirection = match.Groups[1].Value.ToUpperInvariant();
                    return string.Empty;
                });

                lines[i] = MoveRegex.Replace(lines[i], string.Empty);
                lines[i] = GotoRegex.Replace(lines[i], string.Empty);
                lines[i] = FollowRegex.Replace(lines[i], string.Empty);
                lines[i] = StayHomeRegex.Replace(lines[i], string.Empty);
                lines[i] = AllDayFollowRegex.Replace(lines[i], string.Empty);

                if (allowFallbackEmotes)
                    DispatchFallbackEmote(npc, lines[i]);

                lines[i] = lines[i].Trim();
            }

            if (faceDirection != null)
                DispatchFaceDirection(npc, faceDirection);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Private helpers
        // ─────────────────────────────────────────────────────────────────────

        private static int ResolveEmoteId(string emoteType) => emoteType switch
        {
            "QUESTION" => 8,
            "ANGRY"    => 12,
            "SURPRISE" => 16,
            "HEART"    => 20,
            "SLEEP"    => 24,
            "SAD"      => 28,
            "HAPPY"    => 32,
            "X"        => 36,
            "BLUSH"    => 60,
            _          => -1
        };

        private static void DispatchExplicitEmote(NPC npc, string emoteType)
        {
            int emoteId = ResolveEmoteId(emoteType);
            if (emoteId == -1)
            {
                ModEntry.SMonitor?.Log($"[EmbodiedActionParser] Unknown emote type '{emoteType}', ignoring.", LogLevel.Warn);
                return;
            }

            ModEntry.SMonitor?.Log($"[EmbodiedActionParser] Emote dispatched: {emoteType}({emoteId}) for {npc.Name}", LogLevel.Debug);

            StardewValley.DelayedAction.functionAfterDelay(() =>
            {
                try   { npc.doEmote(emoteId); }
                catch (Exception ex) { ModEntry.SMonitor?.Log($"[EmbodiedActionParser] Emote error: {ex.Message}", LogLevel.Warn); }
            }, 500);
        }

        private static void DispatchFallbackEmote(NPC npc, string line)
        {
            int emoteId = -1;
            if      (line.Contains("$h")) emoteId = 32;
            else if (line.Contains("$l")) emoteId = 20;
            else if (line.Contains("$a")) emoteId = 12;
            else if (line.Contains("$s")) emoteId = 28;

            if (emoteId == -1) return;

            StardewValley.DelayedAction.functionAfterDelay(() =>
            {
                try   { npc.doEmote(emoteId); }
                catch (Exception ex) { ModEntry.SMonitor?.Log($"[EmbodiedActionParser] Fallback emote error: {ex.Message}", LogLevel.Warn); }
            }, 600);
        }

        private static void DispatchFaceDirection(NPC npc, string direction)
        {
            StardewValley.DelayedAction.functionAfterDelay(() =>
            {
                try
                {
                    switch (direction)
                    {
                        case "FARMER": npc.faceGeneralDirection(Game1.player.getStandingPosition(), 0, false, false); break;
                        case "UP":     npc.faceDirection(0); break;
                        case "RIGHT":  npc.faceDirection(1); break;
                        case "DOWN":   npc.faceDirection(2); break;
                        case "LEFT":   npc.faceDirection(3); break;
                    }
                }
                catch (Exception ex)
                {
                    ModEntry.SMonitor?.Log($"[EmbodiedActionParser] Face error: {ex.Message}", LogLevel.Warn);
                }
            }, 100);
        }

        private static void StripMoveTags(string[] lines)
        {
            for (int i = 0; i < lines.Length; i++)
            {
                lines[i] = MoveRegex.Replace(lines[i], string.Empty);
                lines[i] = GotoRegex.Replace(lines[i], string.Empty);
                lines[i] = FollowRegex.Replace(lines[i], string.Empty);
                lines[i] = StayHomeRegex.Replace(lines[i], string.Empty);
                lines[i] = AllDayFollowRegex.Replace(lines[i], string.Empty);
                lines[i] = lines[i].Trim();
            }
        }
    }
}