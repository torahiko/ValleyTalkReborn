using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;

namespace ValleytalkReborn
{
    public class DateLocationInfo
    {
        public string LocationId { get; set; } = string.Empty;
        public string TargetMap { get; set; } = string.Empty;
        public string DisplayNameZh { get; set; } = string.Empty;
        public string DisplayNameEn { get; set; } = string.Empty;
        public string ContextDescriptionZh { get; set; } = string.Empty;
        public string ContextDescriptionEn { get; set; } = string.Empty;
        public int RequiredHearts { get; set; } = 4;
        public bool AllowRainyDays { get; set; } = true;
        public string TimeWindow { get; set; } = "1800-2130";

        // ─── 2.0 新增：定点物理锚点 ───

        /// <summary>
        /// NPC 约定等候地块（Tile 坐标，非像素坐标）。
        /// </summary>
        public Vector2 WaitTile { get; set; } = Vector2.Zero;

        /// <summary>
        /// NPC 等候时的朝向：0=上, 1=右, 2=下, 3=左。
        /// </summary>
        public int DefaultFacingDirection { get; set; } = 2;

        /// <summary>
        /// 定点活动类型标记（供后续动作状态机使用，如 "Talk", "Sit", "Drink"）。
        /// </summary>
        public string StagedActivityType { get; set; } = "Talk";

        public bool IsAvailable(NPC npc, out string reason)
        {
            if (!string.IsNullOrEmpty(TargetMap) && Game1.getLocationFromName(TargetMap) == null)
            {
                bool isZh = LocalizedContentManager.CurrentLanguageCode == LocalizedContentManager.LanguageCode.zh;
                reason = isZh
                    ? "目标区域未开放或不可达"
                    : "Target location unavailable";
                return false;
            }

            var world = new DateWorldSnapshot(
                TimeOfDay: Game1.timeOfDay,
                PlayerLocationName: Game1.player?.currentLocation?.Name ?? "",
                IsFestivalDay: Utility.isFestivalDay(Game1.dayOfMonth, Game1.season),
                IsWorldReady: StardewModdingAPI.Context.IsWorldReady
            );
            return DateRules.CanScheduleNow(npc, this.LocationId, world, out reason);
        }
    }

    public static class DateLocationRegistry
    {
        public const string ASSET_KEY = "ValleytalkReborn/DateLocations";
        public static Dictionary<string, DateLocationInfo> Locations { get; private set; } = new(StringComparer.OrdinalIgnoreCase);
        private static bool _isLoaded = false;

        public static void Initialize(IModHelper helper)
        {
            // 初始化先准备默认列表，防止游戏刚启动、CP 尚未完全拦截 Content 管道前空指针
            FallbackToDefault();
        }

        public static void LoadAssets()
        {
            try
            {
                var loaded = ModEntry.SHelper.GameContent
                    .Load<Dictionary<string, DateLocationInfo>>(ASSET_KEY);

                if (loaded != null && loaded.Count > 0)
                {
                    var result = new Dictionary<string, DateLocationInfo>(StringComparer.OrdinalIgnoreCase);
                    foreach (var (key, value) in loaded)
                    {
                        if (value == null) continue;
                        value.LocationId = key; // 回填 ID
                        result[key] = value;
                    }

                    Locations = result;
                    _isLoaded = true;
                    ModEntry.SMonitor?.Log($"[DateLocationRegistry] 成功载入 {Locations.Count} 个约会地点资产。", LogLevel.Info);
                    return;
                }
            }
            catch (Exception ex)
            {
                ModEntry.SMonitor?.Log($"[DateLocationRegistry] 约会地点资产加载失败: {ex.Message}，降级启用 C# 硬编码兜底。", LogLevel.Error);
            }

            if (!_isLoaded)
            {
                FallbackToDefault();
            }
        }

        public static void ReloadAssets()
        {
            _isLoaded = false;
            LoadAssets();
        }

        private static void FallbackToDefault()
        {
            Locations = new Dictionary<string, DateLocationInfo>(StringComparer.OrdinalIgnoreCase)
            {
                ["Saloon"] = new()
                {
                    LocationId = "Saloon",
                    TargetMap = "Saloon",
                    DisplayNameZh = "星之果实酒吧",
                    DisplayNameEn = "The Stardrop Saloon",
                    ContextDescriptionZh = "酒吧里暖意融融，壁炉柴火噼啪作响，空气中弥漫着麦芽酒与披萨的香气，还有点唱机的轻柔旋律。",
                    ContextDescriptionEn = "The saloon is warm and bustling, with the gentle crackle of the fireplace, aroma of food, and soft music.",
                    RequiredHearts = 4,
                    AllowRainyDays = true,
                    WaitTile = new Vector2(18, 20),
                    DefaultFacingDirection = 2,
                    StagedActivityType = "Drink"
                },
                ["Beach"] = new()
                {
                    LocationId = "Beach",
                    TargetMap = "Beach",
                    DisplayNameZh = "海滩码头",
                    DisplayNameEn = "The Beach Pier",
                    ContextDescriptionZh = "夜晚的海风带着微咸的湿气，海浪一下下拍打着栈桥，头顶是无垠的星空与粼粼的波光。",
                    ContextDescriptionEn = "The evening ocean breeze is gentle and cool, with waves softly lapping against the wooden pier beneath a starry sky.",
                    RequiredHearts = 4,
                    AllowRainyDays = false,
                    WaitTile = new Vector2(28, 34), // 栈桥伸入海面处
                    DefaultFacingDirection = 2,
                    StagedActivityType = "Talk"
                },
                ["Forest"] = new()
                {
                    LocationId = "Forest",
                    TargetMap = "Woods",
                    DisplayNameZh = "秘密森林",
                    DisplayNameEn = "The Secret Forest",
                    ContextDescriptionZh = "幽静的森林深处荧光闪烁，古树环绕，池塘水面倒映着夜色，四周静谧得只有虫鸣与树叶沙沙声。",
                    ContextDescriptionEn = "Secluded deep within ancient trees, with faint glowing mushrooms, still waters, and soft whispers of nature.",
                    RequiredHearts = 6,
                    AllowRainyDays = true,
                    WaitTile = new Vector2(31, 23), // 池塘边荧光处
                    DefaultFacingDirection = 2,
                    StagedActivityType = "Talk"
                },
                ["Mountain"] = new()
                {
                    LocationId = "Mountain",
                    TargetMap = "Mountain",
                    DisplayNameZh = "深山湖畔",
                    DisplayNameEn = "The Mountain Lake",
                    ContextDescriptionZh = "山顶湖畔夜风清凉，远眺能看到小镇的零星灯火，倒映在清澈冰凉的湖面上，格外浪漫开阔。",
                    ContextDescriptionEn = "Cool crisp mountain air overlooking the lake, with distant town lights twinkling on the water.",
                    RequiredHearts = 4,
                    AllowRainyDays = false,
                    WaitTile = new Vector2(31, 10), // 矿井下方湖畔平台
                    DefaultFacingDirection = 2,
                    StagedActivityType = "Talk"
                },
                ["Town"] = new()
                {
                    LocationId = "Town",
                    TargetMap = "Town",
                    DisplayNameZh = "鹈鹕镇广场",
                    DisplayNameEn = "Pelican Town Square",
                    ContextDescriptionZh = "小镇广场的路灯泛着暖黄色的光晕，两人在石板路边散步，气氛悠闲而日常。",
                    ContextDescriptionEn = "Streetlamps cast a warm golden glow across the cobblestone square, creating a peaceful evening stroll.",
                    RequiredHearts = 4,
                    AllowRainyDays = true,
                    WaitTile = new Vector2(55, 67), // 广场喷泉长椅旁
                    DefaultFacingDirection = 2,
                    StagedActivityType = "Talk"
                }
            };
        }
    }
}
