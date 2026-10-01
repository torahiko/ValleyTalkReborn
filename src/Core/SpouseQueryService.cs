using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;

namespace ValleytalkReborn
{
    /// <summary>
    /// 统一配偶查询单例服务。
    /// 三级降级策略：SMAPI API → PolyamorySweetLove 反射 → 原版 friendshipData / spouse 字段。
    /// 跨 Mod 边界（BOUNDARY）的 API 与反射调用一旦抛出异常，立即记录 Warn 并熔断
    /// （置空引用 / 关闭反射），后续查询确定性走下一级降级，避免主循环内反复跨边界与日志刷屏。
    /// </summary>
    public sealed class SpouseQueryService
    {
        public static readonly SpouseQueryService Instance = new();

        // 恋爱/结婚关系 Mod ID
        private static readonly string[] PolyamoryModIds =
        {
            "ApryllForever.PolyamorySweetLove",
            "ApryllForever.PolyamorySweet",
            "Omegasis.PolyamorySweetLove",
            "PeacefulEnd.PolyamorySweet"
        };

        // 独立的配偶房间拼接 Mod ID（不要与恋爱 Mod 混查，否则引发 Pintail 映射异常）
        private static readonly string[] SweetRoomsModIds =
        {
            "ApryllForever.PolyamorySweetRooms",
            "ApryllForever.SweetRooms",
            "Omegasis.PolyamorySweetRooms",
            "PeacefulEnd.PolyamorySweetRooms"
        };

        private IModHelper _helper;
        private IPolyamorySweetApi _psApi;
        private ISweetRoomsAPI _sweetRoomsApi;
        private bool _apiResolveAttempted = false;

        // 反射支持（兼容无 API 暴露或特定旧版本的 PolyamorySweetLove）
        private bool _reflectionAvailable = false;
        private FieldInfo _currentSpousesField;
        private FieldInfo _unofficialSpousesField;

        /// <summary>_poly API 或反射任一可用即为 true；用于求婚守卫的 poly 感知降级。</summary>
        public bool IsPolyamoryEnvironmentActive => _psApi != null || _reflectionAvailable;

        private SpouseQueryService() { }

        /// <summary>在 ModEntry.Entry 中调用初始化。</summary>
        public void Initialize(IModHelper helper)
        {
            _helper = helper ?? throw new ArgumentNullException(nameof(helper));
            InitReflectionFallback();
        }

        /// <summary>在 GameLaunched 事件触发时调用，解析 SMAPI API 实例。</summary>
        public void ResolveApis()
        {
            if (_apiResolveAttempted || _helper == null) return;
            _apiResolveAttempted = true;

            // 1. 解析多配偶恋爱关系 API
            foreach (var modId in PolyamoryModIds)
            {
                try
                {
                    if (!_helper.ModRegistry.IsLoaded(modId)) continue;
                    _psApi ??= _helper.ModRegistry.GetApi<IPolyamorySweetApi>(modId);
                    if (_psApi != null)
                    {
                        ModEntry.SMonitor?.Log($"[SpouseQuery] PolyamorySweet API connected ({modId}).", LogLevel.Info);
                        break;
                    }
                }
                catch (Exception ex)
                {
                    // BOUNDARY：第三方 Mod 的 Pintail 代理失配等，记录后继续尝试下一个候选 ID。
                    ModEntry.SMonitor?.Log($"[SpouseQuery] Failed to resolve IPolyamorySweetApi from '{modId}': {ex.Message}", LogLevel.Warn);
                }
            }

            // 2. 独立解析配偶房间 API
            foreach (var modId in SweetRoomsModIds)
            {
                try
                {
                    if (!_helper.ModRegistry.IsLoaded(modId)) continue;
                    _sweetRoomsApi ??= _helper.ModRegistry.GetApi<ISweetRoomsAPI>(modId);
                    if (_sweetRoomsApi != null)
                    {
                        ModEntry.SMonitor?.Log($"[SpouseQuery] SweetRooms API connected ({modId}).", LogLevel.Info);
                        break;
                    }
                }
                catch (Exception ex)
                {
                    // BOUNDARY：同上，继续尝试下一个候选 ID。
                    ModEntry.SMonitor?.Log($"[SpouseQuery] Failed to resolve ISweetRoomsAPI from '{modId}': {ex.Message}", LogLevel.Warn);
                }
            }

            // 二次兜底：Entry 阶段第三方程序集可能尚未加载到 AppDomain，
            // GameLaunched 阶段再尝试一次反射降级初始化。
            if (!_reflectionAvailable)
            {
                InitReflectionFallback();
            }
        }

        private void InitReflectionFallback()
        {
            try
            {
                var polyAssembly = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == "PolyamorySweetLove");
                if (polyAssembly == null) return;

                var modEntryType = polyAssembly.GetType("PolyamorySweetLove.ModEntry");
                if (modEntryType == null) return;

                _currentSpousesField = modEntryType.GetField("currentSpouses", BindingFlags.Public | BindingFlags.Static);
                _unofficialSpousesField = modEntryType.GetField("currentUnofficialSpouses", BindingFlags.Public | BindingFlags.Static);

                if (_currentSpousesField != null || _unofficialSpousesField != null)
                {
                    _reflectionAvailable = true;
                    ModEntry.SMonitor?.Log("[SpouseQuery] PolyamorySweetLove reflection fallback is ready.", LogLevel.Debug);
                }
            }
            catch (Exception ex)
            {
                // BOUNDARY：第三方程序集结构变更导致反射探测失败；保持原版降级可用。
                ModEntry.SMonitor?.Log($"[SpouseQuery] Failed to initialize PolyamorySweetLove reflection fallback: {ex.Message}", LogLevel.Warn);
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  边界安全探测器（BOUNDARY probes）
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// 经 SMAPI API 读取配偶字典。API 抛错时记录 Warn 并熔断（置空 _psApi），
        /// 后续调用确定性返回 false，不再跨边界。
        /// </summary>
        internal bool TryGetSpousesFromApi(Farmer player, bool all, out Dictionary<string, NPC> spouses)
        {
            if (_psApi == null || player == null)
            {
                spouses = null;
                return false;
            }

            try
            {
                spouses = _psApi.GetSpouses(player, all: all);
                return spouses != null;
            }
            catch (Exception ex)
            {
                // BOUNDARY：API 实例与 Mod 当前内部状态失配，熔断后走反射 / 原版降级。
                ModEntry.SMonitor?.Log(
                    $"[SpouseQuery] PolyamorySweet API GetSpouses(all={all}) threw {ex.GetType().Name}: {ex.Message}. Disabling API integration.",
                    LogLevel.Warn);
                _psApi = null;
                spouses = null;
                return false;
            }
        }

        /// <summary>
        /// 经反射字段读取配偶字典。读取抛错时记录 Warn 并熔断（关闭 _reflectionAvailable），
        /// 后续调用确定性返回 false，不再跨边界。
        /// </summary>
        internal bool TryGetSpousesFromReflection(FieldInfo field, Farmer player, out Dictionary<string, NPC> spouses)
        {
            if (!_reflectionAvailable || field == null || player == null)
            {
                spouses = null;
                return false;
            }

            try
            {
                if (field.GetValue(null) is Dictionary<long, Dictionary<string, NPC>> dict
                    && dict.TryGetValue(player.UniqueMultiplayerID, out spouses))
                {
                    return true;
                }
                spouses = null;
                return false;
            }
            catch (Exception ex)
            {
                // BOUNDARY：第三方字段结构变更或线程态异常，熔断后走原版降级。
                ModEntry.SMonitor?.Log(
                    $"[SpouseQuery] Failed to read reflection field '{field.Name}': {ex.Message}. Disabling reflection fallback.",
                    LogLevel.Warn);
                _reflectionAvailable = false;
                spouses = null;
                return false;
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  配偶状态判定
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// 判断是否为合法配偶/室友（包含原版与多婚 Mod）。
        /// 替代 CSM.IsLegalSpouse。
        /// </summary>
        public bool IsMarried(string npcName, Farmer player = null)
        {
            if (string.IsNullOrWhiteSpace(npcName)) return false;
            player ??= Game1.player;
            if (player == null) return false;

            // 1. SMAPI API（all: true 覆盖官方与非官方配偶）
            if (TryGetSpousesFromApi(player, all: true, out var spouses)
                && spouses?.ContainsKey(npcName) == true)
            {
                return true;
            }

            // 2. 反射判定（经 IsOfficial / IsUnofficial 的安全探测器）
            var npc = Game1.getCharacterFromName(npcName);
            if (npc != null && (IsOfficialSpouse(npc, player) || IsUnofficialSpouse(npc, player)))
                return true;

            // 3. 原版 friendshipData / spouse
            if (player.friendshipData?.TryGetValue(npcName, out var f) == true && f != null)
            {
                if (f.IsMarried() || f.IsRoommate()) return true;
            }

            return string.Equals(player.spouse, npcName, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 判断是否为官方配偶。
        /// </summary>
        public bool IsOfficialSpouse(NPC npc, Farmer player = null)
        {
            if (npc == null) return false;
            player ??= Game1.player;
            if (player == null) return false;

            // 1. SMAPI API（all: false 即官方配偶语义）
            if (TryGetSpousesFromApi(player, all: false, out var spouses)
                && spouses?.ContainsKey(npc.Name) == true)
            {
                return true;
            }

            // 2. 反射
            if (TryGetSpousesFromReflection(_currentSpousesField, player, out var refSpouses)
                && refSpouses?.ContainsKey(npc.Name) == true)
            {
                return true;
            }

            // 3. 原版
            if (string.Equals(player.spouse, npc.Name, StringComparison.OrdinalIgnoreCase))
                return true;

            return player.friendshipData?.TryGetValue(npc.Name, out var f) == true && f != null && f.IsMarried();
        }

        /// <summary>
        /// 判断是否为非官方配偶。
        /// </summary>
        public bool IsUnofficialSpouse(NPC npc, Farmer player = null)
        {
            if (npc == null) return false;
            player ??= Game1.player;
            if (player == null) return false;

            // 1. SMAPI API：在全部配偶中但不在官方配偶中即为非官方。
            if (TryGetSpousesFromApi(player, all: true, out var allSpouses)
                && allSpouses?.ContainsKey(npc.Name) == true
                && !(TryGetSpousesFromApi(player, all: false, out var official)
                     && official != null && official.ContainsKey(npc.Name)))
            {
                return true;
            }

            // 2. 反射
            if (TryGetSpousesFromReflection(_unofficialSpousesField, player, out var refSpouses)
                && refSpouses?.ContainsKey(npc.Name) == true)
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// 获取所有配偶 NPC 列表。
        /// </summary>
        public List<NPC> GetAllMarriedNpcs(Farmer player = null)
        {
            player ??= Game1.player;
            if (player == null) return new List<NPC>();

            var results = new Dictionary<string, NPC>(StringComparer.OrdinalIgnoreCase);

            // 1. SMAPI API
            if (TryGetSpousesFromApi(player, all: true, out var apiSpouses) && apiSpouses != null)
            {
                foreach (var kv in apiSpouses)
                    if (kv.Value != null) results.TryAdd(kv.Key, kv.Value);
            }

            // 2. 反射获取（官方与非官方字段都读取）
            if (results.Count == 0 && _reflectionAvailable)
            {
                if (TryGetSpousesFromReflection(_currentSpousesField, player, out var currentSpouses) && currentSpouses != null)
                {
                    foreach (var kv in currentSpouses)
                        if (kv.Value != null) results.TryAdd(kv.Key, kv.Value);
                }

                if (TryGetSpousesFromReflection(_unofficialSpousesField, player, out var unofficialSpouses) && unofficialSpouses != null)
                {
                    foreach (var kv in unofficialSpouses)
                        if (kv.Value != null) results.TryAdd(kv.Key, kv.Value);
                }
            }

            // 3. 原版降级
            if (results.Count == 0)
            {
                if (player.friendshipData != null)
                {
                    foreach (var pair in player.friendshipData.Pairs)
                    {
                        if (pair.Value != null && (pair.Value.IsMarried() || pair.Value.IsRoommate()))
                        {
                            var c = Game1.getCharacterFromName(pair.Key);
                            if (c != null) results.TryAdd(pair.Key, c);
                        }
                    }
                }

                if (results.Count == 0 && !string.IsNullOrEmpty(player.spouse))
                {
                    var c = Game1.getCharacterFromName(player.spouse);
                    if (c != null) results[player.spouse] = c;
                }
            }

            return results.Values.ToList();
        }

        /// <summary>
        /// 获取配偶归宿地点与落脚点坐标（支持 SweetRooms 多配偶房间）。
        /// 无 SweetRooms 时优先 npc.DefaultMap 的农场门 warp，其次 npc.DefaultPosition；
        /// 最终保底为 FarmHouse 正门内侧 warp（不再使用 (8, 9) 魔数）。
        /// </summary>
        public (string MapName, Vector2 Tile) GetHomeDestination(NPC npc)
        {
            // 1. SweetRooms API
            if (npc != null && _sweetRoomsApi != null)
            {
                try
                {
                    Point cornerTile = _sweetRoomsApi.GetSpouseRoomCornerTile(npc);
                    return ("FarmHouse", new Vector2(cornerTile.X, cornerTile.Y));
                }
                catch (Exception ex)
                {
                    // BOUNDARY：SweetRooms 内部状态异常，熔断后走 DefaultMap/FarmHouse 确定性降级。
                    ModEntry.SMonitor?.Log(
                        $"[SpouseQuery] SweetRooms API threw {ex.GetType().Name}: {ex.Message}. Disabling SweetRooms API.",
                        LogLevel.Warn);
                    _sweetRoomsApi = null;
                }
            }

            // 2. npc.DefaultMap
            if (npc != null && !string.IsNullOrWhiteSpace(npc.DefaultMap))
            {
                var homeLoc = Game1.getLocationFromName(npc.DefaultMap);
                if (homeLoc != null)
                {
                    var warpToFarm = homeLoc.warps?.FirstOrDefault(w =>
                        w != null && string.Equals(w.TargetName, "Farm", StringComparison.OrdinalIgnoreCase));
                    if (warpToFarm != null)
                        return (npc.DefaultMap, new Vector2(warpToFarm.X, warpToFarm.Y));

                    var defaultTile = new Vector2(npc.DefaultPosition.X / 64f, npc.DefaultPosition.Y / 64f);
                    if (defaultTile.X < 0f || defaultTile.Y < 0f)
                    {
                        // BUG 类：外部 Mod 篡改 DefaultPosition，负坐标立即中止，转入农舍正门保底。
                        ModEntry.SMonitor?.Log(
                            $"[SpouseQuery] {npc.Name} DefaultPosition corrupted: ({npc.DefaultPosition.X},{npc.DefaultPosition.Y}) " +
                            $"→ negative tile ({defaultTile.X},{defaultTile.Y}), aborting to FarmHouse entry.",
                            LogLevel.Error);
                    }
                    else
                    {
                        return (npc.DefaultMap, defaultTile);
                    }
                }
            }

            // 3. 原版农舍正门保底（npc 为空、DefaultMap 不可用或坐标异常时）
            var farmhouseWarp = Game1.getLocationFromName("FarmHouse")?.warps?.FirstOrDefault(w =>
                w != null && string.Equals(w.TargetName, "Farm", StringComparison.OrdinalIgnoreCase));
            if (farmhouseWarp != null)
                return ("FarmHouse", new Vector2(farmhouseWarp.X, farmhouseWarp.Y));

            ModEntry.SMonitor?.Log(
                "[SpouseQuery] FarmHouse front-door warp not found, returning origin tile.",
                LogLevel.Warn);
            return ("FarmHouse", Vector2.Zero);
        }

        // ══════════════════════════════════════════════════════════════
        //  测试缝（仅测试环境使用；生产路径禁止调用）
        // ══════════════════════════════════════════════════════════════

        /// <summary>清空全部状态，用于单元测试隔离（无真实游戏环境）。</summary>
        internal void ResetForTesting()
        {
            _helper = null;
            _psApi = null;
            _sweetRoomsApi = null;
            _apiResolveAttempted = false;
            _reflectionAvailable = false;
            _currentSpousesField = null;
            _unofficialSpousesField = null;
        }

        /// <summary>注入桩 API 实例，跳过 ModRegistry 解析。</summary>
        internal void InjectApisForTesting(IPolyamorySweetApi psApi, ISweetRoomsAPI sweetRoomsApi = null)
        {
            _psApi = psApi;
            _sweetRoomsApi = sweetRoomsApi;
        }

        /// <summary>注入桩反射字段并启用反射降级（任一字段非空即启用）。</summary>
        internal void InjectReflectionForTesting(FieldInfo currentSpousesField, FieldInfo unofficialSpousesField)
        {
            _currentSpousesField = currentSpousesField;
            _unofficialSpousesField = unofficialSpousesField;
            _reflectionAvailable = currentSpousesField != null || unofficialSpousesField != null;
        }
    }
}
