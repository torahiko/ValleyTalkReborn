using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Locations;

namespace ValleytalkReborn
{
    /// <summary>
    /// Spouse Late-Night Waiting Event (伴侣深夜等待)
    /// Five-phase execution:
    ///   Phase 1 — Farm door proximity detection (≥01:00, single 50% roll per night)
    ///              Randomly selects one spouse who is at home → fire async LLM bark request
    ///   Phase 2 — Player.Warped into FarmHouse → intercept with fade + DelayedAction
    ///   Phase 3 — Fade clears → warp spouse facing player → wait for LLM (1.5s timeout)
    ///   Phase 4 — Player talks to spouse → record quiet reassurance (not obligation)
    ///   Phase 5 — DayStarted → if player ignored spouse → record quiet aftermath (not punishment)
    /// </summary>
    internal static class SpouseWaitingEvent
    {
        // ── State ────────────────────────────────────────────────────
        private static bool _initialized;
        private static bool _hasRolledToday;
        private static bool _waitingActive;
        private static bool _playerResponded;

        private static string _porchContext;
        private static string _spouseName;

        private static Task<string> _barkTask;
        private static CancellationTokenSource _barkCts;

        private static bool _fadeInProgress;

        // ── Constants ──────────────────────────────────────────────
        private const int    FADE_DELAY_MS     = 600;
        private const int    TRIGGER_TIME      = 2500;
        private const float  TRIGGER_CHANCE    = 0.5f;
        private const int    DOOR_RANGE_TILES  = 3;
        private const int    DOOR_RANGE_SQ     = DOOR_RANGE_TILES * DOOR_RANGE_TILES;
        private const int    BARK_TIMEOUT_MS   = 1500;

        // ── Fallback Bark Text ──────────────────────────────────────
        private static readonly string[] GreenRainFallbacks =
        {
            "外面那场绿雨……吓死我了。",
            "你今天去哪了，满身都是苔藓……",
            "That green rain was terrifying. I'm so glad you're safe.",
            "The whole valley turned green today... I was so worried.",
        };

        private static readonly string[] NormalFallbacks =
        {
            "天啊……",
            "你终于回来了。",
            "I was starting to worry.",
            "You're finally home.",
        };

        // ── Lifecycle ────────────────────────────────────────────────

        public static void Initialize()
        {
            if (_initialized || ModEntry.SHelper == null) return;

            ModEntry.SHelper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
            ModEntry.SHelper.Events.GameLoop.DayStarted   += OnDayStarted;
            ModEntry.SHelper.Events.GameLoop.SaveLoaded   += (_, _) => Reset();
            ModEntry.SHelper.Events.Player.Warped         += OnPlayerWarped;

            _initialized = true;
            ModEntry.SMonitor?.Log("[SpouseWaitingEvent] Initialized.", LogLevel.Debug);
        }

        public static void Cleanup()
        {
            if (!_initialized || ModEntry.SHelper == null) return;

            ModEntry.SHelper.Events.GameLoop.UpdateTicked -= OnUpdateTicked;
            ModEntry.SHelper.Events.GameLoop.DayStarted   -= OnDayStarted;
            ModEntry.SHelper.Events.GameLoop.SaveLoaded   -= (_, _) => Reset();
            ModEntry.SHelper.Events.Player.Warped         -= OnPlayerWarped;

            Reset();
            _initialized = false;
            ModEntry.SMonitor?.Log("[SpouseWaitingEvent] Cleaned up.", LogLevel.Debug);
        }

        // ── Phase 1: Farm Door Proximity Detection ───────────────────

        private static void OnUpdateTicked(object sender, UpdateTickedEventArgs e)
        {
            if (!e.IsMultipleOf(30)) return;
            if (!ConfigIsValid()) return;
            if (_hasRolledToday) return;
            if (Game1.timeOfDay < TRIGGER_TIME) return;
            if (!(Game1.currentLocation is Farm farm)) return;

            Vector2? doorTile = FindFarmHouseDoor(farm);
            if (!doorTile.HasValue) return;
            if (!IsPlayerNearDoor(doorTile.Value)) return;

            _hasRolledToday = true;

            if (Game1.random.NextDouble() > TRIGGER_CHANCE) return;

            var spouses = GetAllSpouses();
            if (spouses.Count == 0) return;

            var spouse = spouses[Game1.random.Next(spouses.Count)];

            if (!IsSpouseAtHome(spouse))
            {
                ModEntry.SMonitor?.Log(
                    $"[SpouseWaitingEvent] Randomly selected {spouse.Name} but not at home — skip.",
                    LogLevel.Debug);
                return;
            }

            TriggerEvent(spouse);
        }

        private static void TriggerEvent(NPC spouse)
        {
            _waitingActive = true;
            _spouseName    = spouse.Name;
            _porchContext  = EvaluatePlayerStatus();

            _barkCts?.Dispose();
            _barkCts  = new CancellationTokenSource();
            _barkTask = RequestSpouseBarkAsync(_porchContext, _barkCts.Token);

            ModEntry.SMonitor?.Log(
                $"[SpouseWaitingEvent] Phase 1 triggered for spouse={spouse.Name}, context={_porchContext}",
                LogLevel.Debug);
        }

        private static string EvaluatePlayerStatus()
        {
            var player = Game1.player;
            if (player == null) return "normal_late";

            if (Game1.isGreenRain)
                return "green_rain_event";

            if (player.health > 0 && (float)player.health / player.maxHealth < 0.25f)
                return "badly_injured";

            if (player.stamina <= 0)
                return "exhausted";

            if (player.hasBuff("tipsy") || player.hasBuff("drunk"))
                return "drunk";

            if (Game1.IsRainingHere() || Game1.IsSnowingHere())
                return "bad_weather";

            return "normal_late";
        }

        private static bool IsPlayerNearDoor(Vector2 doorTile)
        {
            if (Game1.player == null) return false;
            return Vector2.DistanceSquared(Game1.player.Tile, doorTile) <= DOOR_RANGE_SQ;
        }

        private static bool IsSpouseAtHome(NPC spouse)
        {
            if (spouse?.currentLocation == null) return false;
            string locName = spouse.currentLocation.Name;
            return string.Equals(locName, "Farm", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(locName, "FarmHouse", StringComparison.OrdinalIgnoreCase);
        }

        // ── Phase 2: Warp Interception ──────────────────────────────

        private static void OnPlayerWarped(object sender, WarpedEventArgs e)
        {
            if (!_waitingActive) return;
            if (!e.IsLocalPlayer) return;
            if (_fadeInProgress) return;
            if (!(e.NewLocation is FarmHouse)) return;

            var spouseInFarm = GetSpouseInLocation(Game1.currentLocation);
            if (spouseInFarm != null)
            {
                spouseInFarm.Halt();
                spouseInFarm.IsInvisible = true;
            }

            _fadeInProgress = true;

            Game1.globalFadeToBlack(
                () =>
                {
                    DelayedAction.functionAfterDelay(
                        () => ExecutePhase3(),
                        FADE_DELAY_MS);
                },
                0.02f);

            ModEntry.SMonitor?.Log("[SpouseWaitingEvent] Phase 2: Fade initiated on FarmHouse warp.", LogLevel.Debug);
        }

        // ── Phase 3: Face-to-Face Reveal ────────────────────────────

        private static async void ExecutePhase3()
        {
            if (!_waitingActive) return;
            if (!(Game1.currentLocation is FarmHouse farmHouse))
            {
                _fadeInProgress = false;
                return;
            }

            NPC spouse = GetSpouseInLocation(farmHouse);
            if (spouse == null)
            {
                var allSpouses = GetAllSpouses();
                spouse = allSpouses.FirstOrDefault(s => string.Equals(s.Name, _spouseName, StringComparison.OrdinalIgnoreCase));
                if (spouse != null)
                {
                    Vector2 targetTile = GetWaitingTileInFrontOfPlayer(farmHouse);
                    Game1.warpCharacter(spouse, farmHouse, targetTile);
                }
            }
            else
            {
                Vector2 targetTile = GetWaitingTileInFrontOfPlayer(farmHouse);
                spouse.setTilePosition((int)targetTile.X, (int)targetTile.Y);
            }

            if (spouse == null)
            {
                ModEntry.SMonitor?.Log("[SpouseWaitingEvent] Phase 3: Spouse not found in FarmHouse.", LogLevel.Warn);
                _fadeInProgress = false;
                return;
            }

            spouse.IsInvisible = false;
            spouse.Halt();
            spouse.facePlayer(Game1.player);

            string barkText;
            try
            {
                barkText = await GetBarkWithTimeoutAsync(_barkCts?.Token ?? CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                barkText = GetFallbackBark(_porchContext);
                ModEntry.SMonitor?.Log("[SpouseWaitingEvent] Bark timeout, using fallback.", LogLevel.Debug);
            }

            ShowSpouseBark(barkText, spouse);
            _fadeInProgress = false;

            ModEntry.SMonitor?.Log(
                $"[SpouseWaitingEvent] Phase 3 complete. Bark: \"{barkText}\"",
                LogLevel.Debug);
        }

        private static async Task<string> GetBarkWithTimeoutAsync(CancellationToken ct)
        {
            if (_barkTask == null)
                return GetFallbackBark(_porchContext);

            try
            {
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(BARK_TIMEOUT_MS));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

                var completedTask = await Task.WhenAny(_barkTask, Task.Delay(BARK_TIMEOUT_MS, linkedCts.Token));

                if (completedTask == _barkTask && _barkTask.IsCompletedSuccessfully)
                {
                    var result = _barkTask.Result;
                    if (!string.IsNullOrWhiteSpace(result))
                        return result;
                }
            }
            catch (OperationCanceledException)
            {
                // 正常超时，忽略
            }
            catch (Exception ex)
            {
                ModEntry.SMonitor?.Log($"[SpouseWaitingEvent] Bark error: {ex.Message}", LogLevel.Debug);
            }

            return GetFallbackBark(_porchContext);
        }

        private static Vector2 GetWaitingTileInFrontOfPlayer(FarmHouse farmHouse)
        {
            Vector2 playerTile = Game1.player.Tile;

            var candidates = new Vector2[]
            {
                new Vector2(playerTile.X, playerTile.Y - 2),
                new Vector2(playerTile.X, playerTile.Y - 1),
                new Vector2(playerTile.X - 1, playerTile.Y - 1),
                new Vector2(playerTile.X + 1, playerTile.Y - 1),
                new Vector2(playerTile.X, playerTile.Y + 1),
            };

            foreach (var pos in candidates)
            {
                if (pos.X < 0 || pos.Y < 0 ||
                    pos.X >= farmHouse.map.Layers[0].LayerWidth ||
                    pos.Y >= farmHouse.map.Layers[0].LayerHeight)
                    continue;

                var rect = new Rectangle((int)pos.X * 64, (int)pos.Y * 64, 64, 64);
                if (!farmHouse.isCollidingPosition(rect, Game1.viewport, false, 0, false, null, true))
                {
                    if (farmHouse.isTileLocationOpen(new xTile.Dimensions.Location((int)pos.X, (int)pos.Y)))
                        return pos;
                }
            }

            return new Vector2(playerTile.X, Math.Max(0, playerTile.Y - 1));
        }

        private static void ShowSpouseBark(string bark, NPC spouse)
        {
            try
            {
                spouse.showTextAboveHead(bark);
            }
            catch (Exception ex)
            {
                ModEntry.SMonitor?.Log($"[SpouseWaitingEvent] showTextAboveHead error: {ex.Message}", LogLevel.Warn);
            }
        }

        // ── Phase 4: Dialogue Context & Quiet Reassurance ──────────

        /// <summary>
        /// 只读探测：是否存在待消费的配偶等待事件。不产生任何副作用。
        /// </summary>
        public static bool HasPendingSpouseDialogue(string npcName)
        {
            if (!_waitingActive) return false;
            return string.Equals(npcName, _spouseName, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 确认消费：记录"被看见"的正向回响记忆，并将等待事件标记为已回应。
        /// 必须只在确认 PendingSpouseWaitingBlock 已经真正进入本轮 Prompt 之后调用，
        /// 否则会出现"状态已消费，但玩家从未看到台词"的静默丢失。
        /// </summary>
        public static void ConfirmSpouseDialogueConsumed(string npcName)
        {
            if (!_waitingActive) return;
            if (!string.Equals(npcName, _spouseName, StringComparison.OrdinalIgnoreCase))
                return;
            if (_playerResponded) return; // 幂等守卫：同一次等待事件只记录一次回响

            _playerResponded = true;

            // ★ 正向回响：对方被看见了，安心了。仅此而已。
            string farmerName = Game1.player?.Name ?? "the farmer";
            string reassuranceMsg = _porchContext switch
            {
                "green_rain_event" =>
                    $"{_spouseName} waited up terrified during the Green Rain. " +
                    $"When {farmerName} came home, they paused and spoke. " +
                    $"{_spouseName} felt seen. The fear eased, replaced by quiet reassurance.",

                "badly_injured" =>
                    $"{_spouseName} waited up terrified {farmerName} was hurt. " +
                    $"When {farmerName} came home injured but still took a moment to check in, " +
                    $"{_spouseName} let out a breath they'd been holding all night.",

                "exhausted" =>
                    $"{_spouseName} waited up, worried and restless. " +
                    $"When {farmerName} came home exhausted but gave a tired nod, " +
                    $"{_spouseName} knew they weren't alone in this.",

                "drunk" =>
                    $"{_spouseName} waited up, annoyed and concerned. " +
                    $"When {farmerName} came home and muttered an apology, " +
                    $"{_spouseName} sighed. It wasn't much, but it was something.",

                "bad_weather" =>
                    $"{_spouseName} waited up, watching the storm. " +
                    $"When {farmerName} came home soaked and shivering, " +
                    $"they stood in the doorway and caught {_spouseName}'s eye. " +
                    $"The storm outside, somehow, felt smaller.",

                _ =>
                    $"{_spouseName} waited up late, unable to sleep. " +
                    $"When {farmerName} came home and gave {_spouseName} a moment of their time, " +
                    $"the knot in {_spouseName}'s chest loosened. Just a little."
            };

            PerceptionManager.Instance.Record(
                key: "spouse_appreciated",
                template: reassuranceMsg,
                npcName: _spouseName,
                lifetimeHours: 12
            );

            ModEntry.SMonitor?.Log(
                $"[SpouseWaitingEvent] Phase 4: {_spouseName} felt seen — quiet reassurance recorded.",
                LogLevel.Debug);
        }

        public static string GetPorchContext() => _porchContext ?? string.Empty;
        public static string GetWaitingSpouseName() => _spouseName;

        public static string BuildStatusPrompt(string status) => status switch
        {
            "green_rain_event" =>
                "[FORCED CONTEXT: You were waiting by the door late at night. " +
                "Today was the bizarre Green Rain event. The valley was covered in strange moss and eerie green mist. " +
                "You stayed inside and worried about the farmer all night. " +
                "The farmer just came home. Express your relief and lingering worry naturally.]",
            "badly_injured" =>
                "[FORCED CONTEXT: You were waiting by the door late at night, worried sick. " +
                "The farmer just came home badly injured and bleeding. React with shock and concern.]",
            "exhausted" =>
                "[FORCED CONTEXT: You were waiting by the door late at night, pacing back and forth. " +
                "The farmer just came home completely exhausted and pale, barely able to stand. React with concern.]",
            "drunk" =>
                "[FORCED CONTEXT: You were waiting by the door late at night, annoyed. " +
                "The farmer just came home smelling strongly of alcohol. Express your displeasure.]",
            "bad_weather" =>
                "[FORCED CONTEXT: You were waiting by the door late at night, watching the window. " +
                "The farmer just came home soaking wet and shivering. React with a mix of worry and annoyance.]",
            _ =>
                "[FORCED CONTEXT: You were waiting by the door late at night, unable to sleep. " +
                "The farmer just came home very late. Express your relief and ask where they've been.]"
        };

        // ── Phase 5: Quiet Aftermath (Not Punishment) ──────────────

        private static void OnDayStarted(object sender, DayStartedEventArgs e)
        {
            if (_waitingActive && !_playerResponded && !string.IsNullOrEmpty(_spouseName))
            {
                string farmerName = Game1.player?.Name ?? "the farmer";

                string aftermathMsg = _porchContext switch
                {
                    "green_rain_event" =>
                        $"{_spouseName} waited up through the Green Rain. " +
                        $"When {farmerName} came home and went straight to bed without a word, " +
                        $"{_spouseName} stayed up a while longer. " +
                        $"The quiet felt heavier than the storm. Nothing was said. Nothing needed to be.",

                    "badly_injured" =>
                        $"{_spouseName} waited up, terrified {farmerName} was hurt. " +
                        $"When {farmerName} came home injured and walked right past, " +
                        $"{_spouseName} sat in the dark and let the silence settle. " +
                        $"The words didn't come. Maybe they didn't need to.",

                    "exhausted" =>
                        $"{_spouseName} waited up, worried and tired. " +
                        $"When {farmerName} stumbled in and collapsed into bed, " +
                        $"{_spouseName} lay awake, staring at the ceiling. " +
                        $"Some nights, that's just how it is.",

                    "drunk" =>
                        $"{_spouseName} waited up, annoyed. " +
                        $"When {farmerName} came home drunk and ignored them, " +
                        $"{_spouseName} turned away. " +
                        $"There was nothing to say that wouldn't make it worse.",

                    "bad_weather" =>
                        $"{_spouseName} waited up, watching the rain. " +
                        $"When {farmerName} came home soaked and walked past, " +
                        $"{_spouseName} watched the door close. " +
                        $"The storm outside was louder than anything they could have said.",

                    _ =>
                        $"{_spouseName} waited up late, worried about {farmerName}. " +
                        $"When {farmerName} came home and went straight to bed, " +
                        $"{_spouseName} closed their eyes and let the silence breathe. " +
                        $"Some things don't need to be said to be real."
                };

                PerceptionManager.Instance.Record(
                    key: "spouse_waiting_aftermath",
                    template: aftermathMsg,
                    npcName: _spouseName,
                    lifetimeHours: 24
                );

                ModEntry.SMonitor?.Log(
                    $"[SpouseWaitingEvent] Phase 5: Quiet aftermath recorded for {_spouseName}.",
                    LogLevel.Debug);
            }

            Reset();
        }

        // ── Async LLM Request ───────────────────────────────────────

        private static async Task<string> RequestSpouseBarkAsync(string status, CancellationToken ct)
        {
            try
            {
                string prompt = BuildBarkPrompt(status);

                var result = await Llm.Instance.RunInference(
                    systemPromptString: "You are a worried spouse in Stardew Valley. Generate a short exclamation (10-20 words) when your partner comes home very late.",
                    gameCacheString: "",
                    npcCacheString: "",
                    promptString: prompt,
                    responseStart: "[Generate a brief, emotional exclamation of surprise/relief when you see your partner.]",
                    n_predict: 128,
                    allowRetry: false);

                if (result != null && result.IsSuccess && !string.IsNullOrWhiteSpace(result.Text))
                {
                    return result.Text.Trim();
                }
            }
            catch (OperationCanceledException)
            {
                // 正常取消
            }
            catch (Exception ex)
            {
                ModEntry.SMonitor?.Log($"[SpouseWaitingEvent] Bark request failed: {ex.Message}", LogLevel.Debug);
            }

            return null;
        }

        private static string BuildBarkPrompt(string status) => status switch
        {
            "green_rain_event" =>
                "[CONTEXT: Today was the terrifying Green Rain event. " +
                "The valley turned green with strange moss and eerie mist. " +
                "Your spouse the farmer stayed out all night. " +
                "You've been waiting by the door, terrified for their safety.]",
            "badly_injured" =>
                "[CONTEXT: Your spouse the farmer came home badly injured, bleeding and bruised.]",
            "exhausted" =>
                "[CONTEXT: Your spouse the farmer came home completely exhausted, barely able to stand.]",
            "drunk" =>
                "[CONTEXT: Your spouse the farmer came home reeking of alcohol.]",
            "bad_weather" =>
                "[CONTEXT: Your spouse the farmer came home soaking wet and shivering from the storm.]",
            _ =>
                "[CONTEXT: Your spouse the farmer came home very late at night after a long day.]"
        };

        private static string GetFallbackBark(string status)
        {
            if (string.IsNullOrEmpty(status)) status = "normal_late";
            var rng = Game1.random;

            if (status == "green_rain_event")
                return GreenRainFallbacks[rng.Next(GreenRainFallbacks.Length)];
            return NormalFallbacks[rng.Next(NormalFallbacks.Length)];
        }

        // ── Helper Methods ──────────────────────────────────────────

        private static bool ConfigIsValid()
        {
            return ModEntry.Config != null
                && ModEntry.Config.EnableMod
                && Game1.player != null
                && Game1.player.friendshipData != null;
        }

        private static Vector2? FindFarmHouseDoor(Farm farm)
        {
            if (farm == null) return null;
            try
            {
                foreach (var door in farm.doors.Pairs)
                {
                    if (string.Equals(door.Value, "FarmHouse", StringComparison.OrdinalIgnoreCase))
                        return new Vector2(door.Key.X, door.Key.Y);
                }
            }
            catch (Exception ex)
            {
                ModEntry.SMonitor?.Log($"[SpouseWaitingEvent] FindFarmHouseDoor error: {ex.Message}", LogLevel.Debug);
            }
            return null;
        }

        private static List<NPC> GetAllSpouses()
        {
            return CompanionScheduleManager.Instance.GetAllMarriedNpcs();
        }

        private static NPC GetSpouseInLocation(GameLocation location)
        {
            if (location == null || string.IsNullOrEmpty(_spouseName)) return null;
            foreach (var npc in location.characters)
            {
                if (string.Equals(npc.Name, _spouseName, StringComparison.OrdinalIgnoreCase) &&
                    CompanionScheduleManager.IsLegalSpouse(npc.Name))
                {
                    return npc;
                }
            }
            return null;
        }

        private static void Reset()
        {
            _hasRolledToday = false;
            _waitingActive = false;
            _playerResponded = false;
            _porchContext = null;
            _spouseName = null;
            _fadeInProgress = false;

            try { _barkCts?.Cancel(); } catch { }
            _barkCts?.Dispose();
            _barkCts = null;
            _barkTask = null;

            ModEntry.SMonitor?.Log("[SpouseWaitingEvent] Reset complete.", LogLevel.Debug);
        }
    }
}