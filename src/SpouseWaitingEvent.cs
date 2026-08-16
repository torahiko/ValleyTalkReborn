using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Locations;
using ValleytalkReborn;

/// <summary>
/// Spouse Late-Night Waiting Event (伴侣深夜等待)
///
/// Five-phase execution:
///   Phase 1 — Farm door proximity detection (≥01:00, single 50% roll per night)
///              Snapshot player status → fire async LLM bark request
///   Phase 2 — Player.Warped into FarmHouse → intercept with fade + DelayedAction
///   Phase 3 — Fade clears → warp spouse facing player → non-blocking LLM/fallback check
///   Phase 4 — Player talks to spouse → read PorchContext → inject into dialogue
///   Phase 5 — DayStarted → if player ignored spouse → inject penalty perception
/// </summary>
internal static class SpouseWaitingEvent
{
    // ── State ────────────────────────────────────────────────────
    private static bool _initialized;
    private static bool _hasRolledToday;     // 当天是否已进行过触发判定
    private static bool _waitingActive;      // 伴侣正在等待（Phase 3 完成后置 true）
    private static bool _playerResponded;    // 玩家已与伴侣交互

    private static string _porchContext;     // Phase 1 快照，供 Phase 4 使用
    private static string _spouseName;

    // 异步 LLM 请求的 Task 引用
    private static Task<string> _barkTask;
    private static CancellationTokenSource _barkCts;

    // Phase 2 防重入标志
    private static bool _fadeInProgress;

    // 常量配置
    private const int    FADE_DELAY_MS     = 600;    // 黑屏延迟：0.6 秒悬疑感
    private const int    TRIGGER_TIME      = 2500;   // 凌晨 01:00 (25:00 = 01:00)
    private const float  TRIGGER_CHANCE    = 0.5f;   // 50% 概率
    private const int    DOOR_RANGE_TILES  = 3;      // 农场门范围（格）
    private const int    DOOR_RANGE_SQ     = DOOR_RANGE_TILES * DOOR_RANGE_TILES;

    // 兜底气泡文本
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
        ModEntry.SHelper.Events.Player.Warped         += OnPlayerWarped;
        ModEntry.SHelper.Events.GameLoop.DayStarted   += OnDayStarted;

        _initialized = true;
        ModEntry.SMonitor?.Log("[SpouseWaitingEvent] Initialized.", LogLevel.Debug);
    }

    public static void Cleanup()
    {
        if (!_initialized || ModEntry.SHelper == null) return;

        ModEntry.SHelper.Events.GameLoop.UpdateTicked -= OnUpdateTicked;
        ModEntry.SHelper.Events.Player.Warped         -= OnPlayerWarped;
        ModEntry.SHelper.Events.GameLoop.DayStarted   -= OnDayStarted;

        Reset();
        _initialized = false;
        ModEntry.SMonitor?.Log("[SpouseWaitingEvent] Cleaned up.", LogLevel.Debug);
    }

    // ── Phase 1: Farm Door Proximity Detection ───────────────────

    private static void OnUpdateTicked(object sender, UpdateTickedEventArgs e)
    {
        if (!e.IsMultipleOf(30)) return; // 每 0.5 秒检查一次
        if (!ConfigIsValid()) return;
        if (_hasRolledToday) return;     // 今天已经判定过，不再重复判断

        // 时间检查：凌晨 01:00 之后
        if (Game1.timeOfDay < TRIGGER_TIME) return;

        // 玩家必须在 Farm 地图上
        if (!(Game1.currentLocation is Farm farm)) return;

        // 查找 Farm 上的 FarmHouse 门
        Vector2? doorTile = FindFarmHouseDoor(farm);
        if (!doorTile.HasValue) return;

        // 玩家必须在门附近
        if (!IsPlayerNearDoor(doorTile.Value)) return;

        // 标记今天已判定，避免玩家在门口徘徊导致多次 Roll 点
        _hasRolledToday = true;

        // 概率触发
        if (Game1.random.NextDouble() > TRIGGER_CHANCE) return;

        // 获取所有合法配偶（多配偶兼容）
        var spouses = GetAllSpouses();
        if (spouses.Count == 0) return;

        // 多配偶时随机选一个等门
        NPC spouse = spouses[Game1.random.Next(spouses.Count)];

        // 触发！
        TriggerEvent(spouse);
    }

    private static void TriggerEvent(NPC spouse)
    {
        _waitingActive = true;
        _spouseName    = spouse.Name;

        // 快照玩家状态
        _porchContext = EvaluatePlayerStatus();

        // 启动异步 LLM 请求（进门前提前预热生成）
        _barkCts?.Dispose();
        _barkCts  = new CancellationTokenSource();
        _barkTask = RequestSpouseBarkAsync(_porchContext, _barkCts.Token);

        ModEntry.SMonitor?.Log(
            $"[SpouseWaitingEvent] Phase 1 triggered for spouse={spouse.Name}, context={_porchContext}",
            LogLevel.Debug);
    }

    /// <summary>评估玩家当前状态，返回快照字符串</summary>
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

    // ── Phase 2: Warp Interception (Player.Warped) ──────────────

    private static void OnPlayerWarped(object sender, WarpedEventArgs e)
    {
        if (!_waitingActive) return;
        if (!e.IsLocalPlayer) return;
        if (_fadeInProgress) return;

        if (!(e.NewLocation is FarmHouse)) return;

        // 暂时隐藏配偶并停止 AI，防止黑屏期间乱走
        NPC spouseInFarm = GetSpouseInLocation(Game1.currentLocation);
        if (spouseInFarm != null)
        {
            spouseInFarm.Halt();
            spouseInFarm.IsInvisible = true;
        }

        _fadeInProgress = true;

        // 黑屏过渡
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

    private static void ExecutePhase3()
    {
        if (!_waitingActive) return;

        var farmHouse = Game1.currentLocation as FarmHouse;
        if (farmHouse == null)
        {
            _fadeInProgress = false;
            return;
        }

        // 寻找或传送配偶至门前合理位置
        NPC spouse = GetSpouseInLocation(farmHouse);
        if (spouse == null)
        {
            var allSpouses = GetAllSpouses();
            NPC spouseOnFarm = allSpouses.Find(s => string.Equals(s.Name, _spouseName, StringComparison.OrdinalIgnoreCase));
            if (spouseOnFarm != null)
            {
                Vector2 targetTile = GetWaitingTileInFrontOfPlayer(farmHouse);
                Game1.warpCharacter(spouseOnFarm, farmHouse, targetTile);
                spouse = spouseOnFarm;
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

        // 显形并面朝玩家
        spouse.IsInvisible = false;
        spouse.Halt();
        spouse.facePlayer(Game1.player);

        // 非阻塞方式获取 Bark 文本
        string barkText = GetBarkNonBlocking();

        // 弹出气泡
        ShowSpouseBark(barkText, spouse);

        _fadeInProgress = false;

        ModEntry.SMonitor?.Log(
            $"[SpouseWaitingEvent] Phase 3 complete. Bark: \"{barkText}\"",
            LogLevel.Debug);
    }

    /// <summary>
    /// 获取进门玩家前方的合法站立地块，防止卡进墙里或家具中
    /// </summary>
    private static Vector2 GetWaitingTileInFrontOfPlayer(FarmHouse farmHouse)
    {
        Vector2 playerTile = Game1.player.Tile;
        
        // 优先放在玩家上方 2 格或 1 格（玄关空旷区域）
        Vector2 target = new Vector2(playerTile.X, playerTile.Y - 2);
        if (farmHouse.isTileLocationOpen(new xTile.Dimensions.Location((int)target.X, (int)target.Y)))
            return target;

        target = new Vector2(playerTile.X, playerTile.Y - 1);
        if (farmHouse.isTileLocationOpen(new xTile.Dimensions.Location((int)target.X, (int)target.Y)))
            return target;

        // 默认兜底
        return new Vector2(playerTile.X, Math.Max(0, playerTile.Y - 1));
    }

    /// <summary>
    /// 完全非阻塞检查：若 LLM 已完成且成功则读取，否则立即回退兜底文本
    /// </summary>
    private static string GetBarkNonBlocking()
    {
        if (_barkTask != null && _barkTask.IsCompletedSuccessfully && !string.IsNullOrWhiteSpace(_barkTask.Result))
        {
            return _barkTask.Result;
        }

        return GetFallbackBark(_porchContext);
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

    // ── Phase 4: Dialogue Context Injection ─────────────────────

    public static bool TryConsumeSpouseDialogue(string npcName)
    {
        if (!_waitingActive) return false;
        if (!string.Equals(npcName, _spouseName, StringComparison.OrdinalIgnoreCase))
            return false;

        _playerResponded = true;
        return true;
    }

    public static string GetPorchContext() => _porchContext ?? string.Empty;
    public static string GetWaitingSpouseName() => _spouseName;

    public static string BuildStatusPrompt(string status) => status switch
    {
        "green_rain_event" =>
            "[FORCED CONTEXT: You were waiting by the door late at night. " +
            "Today was the bizarre Green Rain event. The valley was covered in strange moss and eerie green mist. " +
            "You stayed inside and worried about the farmer all day. " +
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

    // ── Phase 5: Day Penalty ────────────────────────────────────

    private static void OnDayStarted(object sender, DayStartedEventArgs e)
    {
        // 如果前一天触发了等待事件且玩家没有对话回应，注入惩罚记忆
        if (_waitingActive && !_playerResponded && !string.IsNullOrEmpty(_spouseName))
        {
            string farmerName = Game1.player?.Name ?? "the farmer";

            string penaltyMsg = _porchContext switch
            {
                "green_rain_event" =>
                    $"You waited up all night worried about {farmerName} during the Green Rain, " +
                    "but they never came to talk to you. You're heartbroken.",
                "badly_injured" =>
                    $"You waited up all night terrified {farmerName} was hurt, " +
                    "but they ignored you and went to sleep. You're devastated.",
                _ =>
                    $"You waited up all night for {farmerName}, but they completely ignored you. " +
                    "You're deeply hurt and angry."
            };

            PerceptionManager.Instance.Record(
                key: "spouse_ignored_overnight",
                template: penaltyMsg,
                npcName: _spouseName,
                lifetimeHours: 24);

            ModEntry.SMonitor?.Log(
                $"[SpouseWaitingEvent] Phase 5: Penalty injected for ignored spouse {_spouseName}.",
                LogLevel.Debug);
        }

        // 每天起床重置所有状态
        Reset();
    }

    // ── Async LLM Request ───────────────────────────────────────

    private static async Task<string> RequestSpouseBarkAsync(string status, CancellationToken ct)
    {
        try
        {
            string prompt = BuildBarkPrompt(status);

            var result = await Llm.Instance.RunInference(
                "You are a worried spouse in Stardew Valley. Generate a short exclamation (10-20 words) when your partner comes home very late.",
                prompt,
                string.Empty,
                "[Generate a brief, emotional exclamation of surprise/relief when you see your partner.]");

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

    private static System.Collections.Generic.List<NPC> GetAllSpouses()
    {
        var result = new System.Collections.Generic.List<NPC>();
        if (Game1.player?.friendshipData == null) return result;

        foreach (var pair in Game1.player.friendshipData.Pairs)
        {
            if (!pair.Value.IsMarried()) continue;
            if (!CompanionScheduleManager.IsLegalSpouse(pair.Key)) continue;
            var npc = Game1.getCharacterFromName(pair.Key);
            if (npc != null) result.Add(npc);
        }
        return result;
    }

    private static NPC GetSpouseInLocation(GameLocation location)
    {
        if (location == null || string.IsNullOrEmpty(_spouseName)) return null;
        foreach (var npc in location.characters)
        {
            if (string.Equals(npc.Name, _spouseName, StringComparison.OrdinalIgnoreCase))
                return npc;
        }
        return null;
    }

    private static void Reset()
    {
        _hasRolledToday  = false;
        _waitingActive   = false;
        _playerResponded = false;
        _porchContext    = null;
        _spouseName      = null;
        _fadeInProgress  = false;

        try { _barkCts?.Cancel(); } catch { }
        _barkCts?.Dispose();
        _barkCts  = null;
        _barkTask = null;
    }
}