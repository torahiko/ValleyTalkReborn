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
                if (!_helper.ModRegistry.IsLoaded(modId)) continue;
                try
                {
                    _psApi ??= _helper.ModRegistry.GetApi<IPolyamorySweetApi>(modId);
                    if (_psApi != null)
                    {
                        ModEntry.SMonitor?.Log($"[SpouseQuery] PolyamorySweet API connected ({modId}).", LogLevel.Info);
                        break;
                    }
                }
                catch { }
            }

            // 2. 独立解析配偶房间 API
            foreach (var modId in SweetRoomsModIds)
            {
                if (!_helper.ModRegistry.IsLoaded(modId)) continue;
                try
                {
                    _sweetRoomsApi ??= _helper.ModRegistry.GetApi<ISweetRoomsAPI>(modId);
                    if (_sweetRoomsApi != null)
                    {
                        ModEntry.SMonitor?.Log($"[SpouseQuery] SweetRooms API connected ({modId}).", LogLevel.Info);
                        break;
                    }
                }
                catch { }
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
                ModEntry.SMonitor?.Log($"[SpouseQuery] Reflection initialization exception: {ex.Message}", LogLevel.Debug);
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

            // 1. SMAPI API
            if (_psApi != null)
            {
                try
                {
                    var spouses = _psApi.GetSpouses(player, all: true);
                    if (spouses?.ContainsKey(npcName) == true) return true;
                }
                catch { }
            }

            // 2. 反射判定
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

            if (_reflectionAvailable && _currentSpousesField != null)
            {
                try
                {
                    if (_currentSpousesField.GetValue(null) is Dictionary<long, Dictionary<string, NPC>> dict)
                        if (dict.TryGetValue(player.UniqueMultiplayerID, out var spouses))
                            return spouses.ContainsKey(npc.Name);
                }
                catch { }
            }

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

            if (_reflectionAvailable && _unofficialSpousesField != null)
            {
                try
                {
                    if (_unofficialSpousesField.GetValue(null) is Dictionary<long, Dictionary<string, NPC>> dict)
                        if (dict.TryGetValue(player.UniqueMultiplayerID, out var spouses))
                            return spouses.ContainsKey(npc.Name);
                }
                catch { }
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
            if (_psApi != null)
            {
                try
                {
                    var spouses = _psApi.GetSpouses(player, all: true);
                    if (spouses != null)
                        foreach (var kv in spouses)
                            if (kv.Value != null) results.TryAdd(kv.Key, kv.Value);
                }
                catch { }
            }

            // 2. 反射获取
            if (results.Count == 0 && _reflectionAvailable && _currentSpousesField != null)
            {
                try
                {
                    if (_currentSpousesField.GetValue(null) is Dictionary<long, Dictionary<string, NPC>> dict)
                    {
                        if (dict.TryGetValue(player.UniqueMultiplayerID, out var spouses))
                            foreach (var kv in spouses)
                                if (kv.Value != null) results.TryAdd(kv.Key, kv.Value);
                    }
                }
                catch { }
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
        /// </summary>
        public (string MapName, Vector2 Tile) GetHomeDestination(NPC npc)
        {
            if (npc == null) return ("FarmHouse", new Vector2(8, 9));

            // 1. SweetRooms API
            if (_sweetRoomsApi != null)
            {
                try
                {
                    Point cornerTile = _sweetRoomsApi.GetSpouseRoomCornerTile(npc);
                    return ("FarmHouse", new Vector2(cornerTile.X, cornerTile.Y));
                }
                catch (Exception ex)
                {
                    ModEntry.SMonitor?.Log($"[SpouseQuery] SweetRooms API 异常: {ex.Message}", LogLevel.Warn);
                }
            }

            // 2. npc.DefaultMap
            if (!string.IsNullOrWhiteSpace(npc.DefaultMap))
            {
                var homeLoc = Game1.getLocationFromName(npc.DefaultMap);
                if (homeLoc != null)
                {
                    var warpToFarm = homeLoc.warps?.FirstOrDefault(w =>
                        w != null && string.Equals(w.TargetName, "Farm", StringComparison.OrdinalIgnoreCase));
                    if (warpToFarm != null)
                        return (npc.DefaultMap, new Vector2(warpToFarm.X, warpToFarm.Y));

                    return (npc.DefaultMap, new Vector2(8, 9));
                }
            }

            // 3. 原版农舍正门保底
            try
            {
                var entry = Game1.getFarm().GetMainFarmHouseEntry();
                return ("FarmHouse", new Vector2(entry.X, entry.Y));
            }
            catch
            {
                return ("FarmHouse", new Vector2(8, 9));
            }
        }
    }
}