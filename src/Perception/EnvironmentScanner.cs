using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.TerrainFeatures;

namespace ValleytalkReborn
{
    /// <summary>
    /// Scans the player's current environment and returns human-readable descriptions
    /// for LLM context injection. Covers festivals, location type, and nearby objects.
    /// Fully refactored for:
    ///   1. Positive Guidance (Zero negative "No objects" constraints)
    ///   2. Dual-language Support (Chinese Native & English Standard with Fallback)
    ///   3. Unified internal scan pipeline (single source of truth)
    ///   4. Language-agnostic metadata filtering for multi-language resilience
    ///   5. High-performance spatial indexing (O(R²) bounded tile query instead of full-map traversal)
    /// </summary>
    public static class EnvironmentScanner
    {
        private const int SCAN_BUFFER_MULTIPLIER = 2;

        // 按需计算语言，避免类加载时定格导致游戏内切语言失效
        private static bool IsChineseLanguage =>
            LocalizedContentManager.CurrentLanguageCode == LocalizedContentManager.LanguageCode.zh;

        // ── 双语节日日历（包含 1.6 新节日） ──
        private static readonly Dictionary<string, Dictionary<int, (string En, string Zh)>> VanillaFestivals =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["spring"] = new()
                {
                    [13] = ("Egg Festival", "复活节彩蛋猎手大赛"),
                    [15] = ("Desert Festival", "沙漠节"),
                    [16] = ("Desert Festival", "沙漠节"),
                    [17] = ("Desert Festival", "沙漠节"),
                    [24] = ("Flower Dance", "花舞节")
                },
                ["summer"] = new()
                {
                    [11] = ("Luau", "夏日海滩宴会"),
                    [20] = ("Trout Derby", "鳟鱼大赛"),
                    [21] = ("Trout Derby", "鳟鱼大赛"),
                    [28] = ("Dance of the Moonlight Jellies", "月光水母之舞")
                },
                ["fall"] = new()
                {
                    [16] = ("Stardew Valley Fair", "星露谷展览会"),
                    [27] = ("Spirit's Eve", "万灵节")
                },
                ["winter"] = new()
                {
                    [8]  = ("Festival of Ice", "冰雪节冰钓大赛"),
                    [12] = ("SquidFest", "鱿鱼节"),
                    [13] = ("SquidFest", "鱿鱼节"),
                    [15] = ("Night Market", "夜市"),
                    [16] = ("Night Market", "夜市"),
                    [17] = ("Night Market", "夜市"),
                    [25] = ("Feast of the Winter Star", "冬日星盛宴")
                },
            };

        // ── 双语地图友好名映射（全面补全原版 1.6 + SVE 地图，全量别名兼容） ──
        private static readonly Dictionary<string, (string En, string Zh)> LocationFriendlyNames =
            new(StringComparer.OrdinalIgnoreCase)
            {
                // ==========================================
                //  1. 原版：户外公共与自然区域
                // ==========================================
                ["Farm"]                = ("the farm", "农场空地"),
                ["Town"]                = ("Pelican Town", "鹈鹕镇小镇广场"),
                ["Beach"]               = ("the beach", "阳光海滩"),
                ["Mountain"]            = ("the mountain", "山区"),
                ["Forest"]              = ("Cindersap Forest", "煤块森林"),
                ["BusStop"]             = ("the bus stop", "巴士站台"),
                ["Railroad"]            = ("the railroad", "火车站台"),
                ["Desert"]              = ("the Calico Desert", "桑科沙漠"),
                ["Woods"]               = ("the Secret Woods", "秘密森林"),
                ["Backwoods"]           = ("the backwoods", "农场后山小径"),
                ["CommunityCenter"]     = ("the Community Center", "社区中心"),
                ["Summit"]              = ("the Summit", "山顶绝壁"),

                // ==========================================
                //  2. 原版：地下、特殊与水域区域（含 1.6）
                // ==========================================
                ["Mine"]                = ("the mines", "矿洞"),
                ["UndergroundMine"]     = ("the mines", "矿井深处"),
                ["SkullCave"]           = ("the Skull Cavern", "头骨山洞"),
                ["Sewer"]               = ("the sewers", "下水道"),
                ["BugLand"]             = ("the Mutant Bug Lair", "变异虫穴"),
                ["WitchSwamp"]          = ("the Witch's Swamp", "女巫沼泽"),
                ["WitchHut"]            = ("the Witch's Hut", "女巫小屋"),
                ["WitchWarpCave"]       = ("the Witch's Warp Cave", "女巫传送洞穴"),
                ["Submarine"]           = ("the deep-sea submarine", "深海潜艇内部"),
                ["BathHouse_Pool"]      = ("the public bath pool", "公共浴场温水池"),
                ["BathHouse_Entry"]     = ("the bathhouse entrance", "浴室前厅"),
                ["BathHouse_Mens"]      = ("the men's locker room", "浴室男更衣室"),
                ["BathHouse_MensLocker"]= ("the men's locker room", "浴室男更衣室"),
                ["BathHouse_Womens"]    = ("the women's locker room", "浴室女更衣室"),
                ["BathHouse_WomensLocker"] = ("the women's locker room", "浴室女更衣室"),
                ["Club"]                = ("the Oasis Casino", "绿洲赌场"),
                ["MasteryCave"]         = ("the Mastery Cave", "专精洞穴"),

                // ==========================================
                //  3. 原版：农场建筑与室内
                // ==========================================
                ["FarmHouse"]           = ("the farmhouse", "农舍小屋内部"),
                ["Cabin"]               = ("the farm cabin", "农场联机木屋"),
                ["Greenhouse"]          = ("the greenhouse", "温室"),
                ["Cellar"]              = ("the farmhouse cellar", "农舍地窖"),
                ["Barn"]                = ("the barn", "畜棚内部"),
                ["BigBarn"]             = ("the big barn", "大畜棚内部"),
                ["DeluxeBarn"]          = ("the deluxe barn", "高级畜棚内部"),
                ["Coop"]                = ("the coop", "鸡舍内部"),
                ["BigCoop"]             = ("the big coop", "大鸡舍内部"),
                ["DeluxeCoop"]          = ("the deluxe coop", "高级鸡舍内部"),
                ["Shed"]                = ("the shed", "储物木棚"),
                ["BigShed"]             = ("the big shed", "大储物木棚"),
                ["SlimeHutch"]          = ("the slime hutch", "史莱姆屋"),
                ["FarmCave"]            = ("the farm cave", "农场洞穴"),

                // ==========================================
                //  4. 原版：商铺与公共建筑室内
                // ==========================================
                ["Saloon"]              = ("the Stardrop Saloon", "星之果实酒吧"),
                ["Hospital"]            = ("Harvey's Clinic", "哈维的诊所"),
                ["SeedShop"]            = ("Pierre's General Store", "皮埃尔杂货店"),
                ["Sunroom"]             = ("Caroline's Sunroom", "卡洛琳的日光温室"),
                ["Blacksmith"]          = ("Clint's Blacksmith", "克林特铁匠铺"),
                ["ArchaeologyHouse"]    = ("the Museum and Library", "图书馆与博物馆"),
                ["LibraryMuseum"]       = ("the Museum and Library", "图书馆与博物馆"),
                ["ManorHouse"]          = ("the Mayor's Manor", "镇长大宅"),
                ["JojaMart"]            = ("JojaMart", "Joja 超市"),
                ["AbandonedJojaMart"]   = ("the abandoned JojaMart", "废弃的 Joja 超市"),
                ["MovieTheater"]        = ("the Movie Theater", "星露谷电影院"),
                ["FishShop"]            = ("Willy's Fish Shop", "威利的鱼店"),
                ["SandyHouse"]          = ("the Oasis Store", "绿洲商店"),
                ["AdventureGuild"]      = ("the Adventurer's Guild", "探险家公会"),

                // ==========================================
                //  5. 原版：村民住宅
                // ==========================================
                ["AnimalShop"]          = ("Marnie's Ranch", "玛妮的牧场"),
                ["LeahHouse"]           = ("Leah's cottage", "莉亚的小屋"),
                ["SamHouse"]            = ("1 Willow Lane (Sam's house)", "柳巷1号（山姆家）"),
                ["HaleyHouse"]          = ("2 Willow Lane (Haley and Emily's house)", "柳巷2号（海莉与艾米莉家）"),
                ["JoshHouse"]           = ("1 River Road (George and Evelyn's house)", "河滨路1号（乔治与艾芙琳家）"),
                ["ScienceHouse"]        = ("the Carpenter's Shop (Robin's house)", "木匠工坊（罗宾家）"),
                ["SebastianRoom"]       = ("Sebastian's room", "塞巴斯蒂安的地下室房间"),
                ["Trailer"]             = ("Pam's trailer", "潘姆的房车"),
                ["Trailer_Big"]         = ("Pam's restored house", "潘姆修缮后的新房"),
                ["Tent"]                = ("Linus's tent", "莱纳斯的帐篷"),
                ["LinusTent"]           = ("Linus's tent", "莱纳斯的帐篷"),
                ["WizardHouse"]         = ("the Wizard's Tower", "法师塔"),
                ["WizardHouseBasement"] = ("the Wizard's basement", "法师塔地下密室"),
                ["ElliottHouse"]        = ("Elliott's beach cabin", "艾利欧特的海边小屋"),
                ["LeoTreeHouse"]        = ("Leo's treehouse", "雷欧的树屋"),

                // ==========================================
                //  6. 原版：姜岛区域 (Ginger Island)
                // ==========================================
                ["IslandSouth"]         = ("Ginger Island South Beach", "姜岛南部海滩码头"),
                ["IslandNorth"]         = ("Ginger Island North", "姜岛北部山麓"),
                ["IslandWest"]          = ("Ginger Island West", "姜岛西部平原"),
                ["IslandEast"]          = ("Ginger Island East Jungle", "姜岛东部丛林"),
                ["IslandSouthEast"]     = ("Ginger Island Pirate Cove", "姜岛东南海盗湾"),
                ["IslandSouthEastCave"] = ("the Pirate Cove", "海盗秘密洞穴"),
                ["IslandFarmHouse"]     = ("the island farmhouse", "姜岛农舍内部"),
                ["IslandFarmCave"]      = ("the island farm cave", "姜岛农场洞穴"),
                ["IslandFieldOffice"]   = ("the Island Field Office", "姜岛野外考察办事处"),
                ["FieldOffice"]         = ("the Island Field Office", "姜岛野外考察办事处"),
                ["IslandHut"]           = ("Leo's island hut", "雷欧的姜岛小屋"),
                ["IslandResort"]        = ("the Island Resort", "姜岛度假村"),
                ["Caldera"]             = ("the Volcano Caldera", "火山破火山口锻造台"),
                ["VolcanoDungeon"]      = ("the Volcano Dungeon", "姜岛火山地牢"),
                ["Volcano_Entrance"]    = ("the Volcano entrance", "火山地牢入口"),
                ["QiNutRoom"]           = ("Qi's Walnut Room", "齐先生的金色核桃房"),
                ["IslandWestCave1"]     = ("the Gourmand Frog's Cave", "美食家青蛙洞穴"),

                // ==========================================
                //  7. SVE (Stardew Valley Expanded) 拓展地点
                // ==========================================
                // 农场拓展
                ["Custom_GrandpasShed"]             = ("Grandpa's Shed", "爷爷的储物木棚"),
                ["Custom_GrandpasShedGreenhouse"]   = ("Grandpa's Shed Greenhouse", "爷爷的木棚温室"),

                // 蓝月葡萄园 (Sophia)
                ["Custom_BlueMoonVineyard"]         = ("Blue Moon Vineyard", "蓝月葡萄园"),
                ["Custom_SophiaHouse"]              = ("Sophia's Cottage", "苏菲亚的小屋"),
                ["Custom_SophiaCellar"]             = ("Sophia's Wine Cellar", "苏菲亚的藏酒窖"),

                // 费尔黑文农场 (Andy)
                ["Custom_FairhavenFarm"]            = ("Fairhaven Farm", "费尔黑文农场"),
                ["Custom_AndyHouse"]                = ("Andy's House", "安迪的农舍"),

                // 极光葡萄园 (Apples / Junimo)
                ["Custom_AuroraVineyard"]           = ("Aurora Vineyard", "极光葡萄园"),
                ["Custom_AuroraVineyardBasement"]   = ("Aurora Vineyard Cellar", "极光葡萄园地窖"),
                ["Custom_AuroraVineyardCellar"]     = ("Aurora Vineyard Cellar", "极光葡萄园地窖"),
                ["Custom_ApplesRoom"]               = ("Apples' Junimo Room", "小苹果的祝尼魔之室"),

                // 詹金斯洋房 (Olivia & Victor)
                ["Custom_JenkinsHouse"]             = ("the Jenkins Residence", "詹金斯家大宅"),
                ["Custom_JenkinsCellar"]            = ("the Jenkins Wine Cellar", "詹金斯家酒窖"),

                // 高地与前哨站 (Lance / Highlands)
                ["Custom_Highlands"]                = ("the Highlands", "高地山区"),
                ["Custom_HighlandsOutpost"]         = ("the Highlands Outpost", "高地探险哨所"),
                ["Custom_HighlandsCavern"]          = ("the Highlands Cavern", "高地幽深洞窟"),
                ["Custom_LanceHouse"]               = ("Lance's Quarters", "兰斯的居所"),
                ["Custom_AdventurersGuildBarracks"] = ("the Guild Barracks", "探险家公会宿营区"),

                // 剪水大桥与格兰普顿近郊 (Scarlett / Grampleton)
                ["Custom_ShearwaterBridge"]         = ("Shearwater Bridge", "剪水大桥"),
                ["Custom_GrampletonSuburbs"]        = ("Grampleton Suburbs", "格兰普顿城郊"),
                ["Custom_ScarlettHouse"]            = ("Scarlett's House", "斯嘉丽的家"),

                // 深红荒地与冒险家营地 (Crimson Badlands / Camilla / Alesia)
                ["Custom_Badlands"]                 = ("the Crimson Badlands", "深红荒地"),
                ["Custom_BadlandsCave"]             = ("the Badlands Caverns", "荒地危险洞窟"),
                ["Custom_CrimsonBadlands"]          = ("the Crimson Badlands", "深红荒地"),
                ["Custom_CrimsonBadlandsMines"]     = ("the Badlands Caverns", "荒地危险洞窟"),
                ["Custom_CastleVillageOutpost"]     = ("Castle Village Outpost", "城堡村前哨站"),

                // 森林与小镇新增建筑
                ["Custom_ForestWest"]               = ("Western Cindersap Forest", "煤块森林西部荒野"),
                ["Custom_BearCave"]                 = ("the Bear Cave", "巨熊洞穴"),
                ["Custom_BearShrine"]               = ("the Bear Cave", "巨熊洞穴"),
                ["Custom_JunimoWoods"]              = ("the Junimo Woods", "祝尼魔神秘森林"),
                ["Custom_SpriteSpring"]             = ("Sprite Spring", "小精灵泉水"),
                ["Custom_EnchantedGrove"]           = ("the Enchanted Grove", "魔导树丛"),
                ["Custom_SusanHouse"]               = ("Susan's Farmhouse", "苏珊的农舍"),
                ["Custom_EmeraldFarm"]              = ("Emerald Farm", "翡翠农场"),
                ["Custom_ClaireHouse"]              = ("Claire's Apartment", "克莱尔的公寓"),
                ["Custom_MartinHouse"]              = ("Martin's House", "马丁的住处"),
                ["Custom_MorrisHouse"]              = ("Morris's Apartment", "莫里斯的公寓"),
                ["Custom_GuntherHouse"]             = ("Gunther's Quarters", "冈瑟的住处"),
                ["Custom_MarlonFayHouse"]           = ("Marlon and Fay's Homestead", "马龙与费伊的居所"),
                ["Custom_FableReef"]                = ("Fable Reef", "寓言暗礁")
            };

        // ═════════════════════════════════════════════════════════════
        //  三梯队优先级白名单 + 黑名单
        // ═════════════════════════════════════════════════════════════
        private static readonly HashSet<string> Tier1_Creatures = new(StringComparer.OrdinalIgnoreCase)
        {
            "Chicken", "Cow", "Sheep", "Pig", "Goat", "Duck", "Rabbit", "Dinosaur",
            "Horse", "Cat", "Dog", "Pet",
            "Slime", "Bat", "Skeleton", "Ghost", "Serpent", "Mummy",
        };

        private static readonly HashSet<string> Tier2_ActiveDevices = new(StringComparer.OrdinalIgnoreCase)
        {
            "Keg", "Preserves Jar", "Cheese Press", "Mayonnaise Machine", "Oil Maker",
            "Loom", "Crystalarium", "Bee House", "Mushroom Box", "Vinegar Jug",
            "Furnace", "Campfire", "Torch", "Brazier", "Fire Pit", "Heater",
            "Crab Pot", "Fish Pond",
            "Junimo Hut", "Slime Hutch", "Incubator", "Worm Bin",
        };

        private static readonly HashSet<string> Tier3_Landmarks = new(StringComparer.OrdinalIgnoreCase)
        {
            "Statue Of Endless Fortune", "Statue Of Perfection", "Statue Of True Perfection",
            "Gold Clock", "Obelisk", "Totem", "Shipping Bin", "Silo",
            "Jack-O-Lantern", "Rarecrow", "Scarecrow", "Seasonal Decor",
            "Grandfather Clock", "Bookcase", "Chandelier", "Fireplace", "Piano",
        };

        private static readonly HashSet<string> Blacklist = new(StringComparer.OrdinalIgnoreCase)
        {
            "Stone Floor", "Wood Floor", "Straw Floor", "Weathered Floor", "Crystal Floor",
            "Stepping Stone Path", "Gravel Path", "Wood Path", "Cobblestone Path",
            "Chest", "Stone Chest", "Junimo Chest",
            "Weeds", "Stone", "Twig", "Fiber",
            "Workbench", "Recycling Machine", "Mini-Obelisk",
        };

        private static readonly string[] FurnitureNameBlacklist = new[]
        {
            "垂叶", "台灯", "地毯", "窗户", "植物", "小茶几", "灯", "盆栽", "盆景",
            "地板", "墙纸", "挂件", "桌布", "饰品", "窗帘", "百叶", "纱帘", "花瓶", "花盆",
            "壁篮", "挂饰", "标志", "旗帜", "海报", "挂画", "火把", "烛台",
            "Rug", "Lamp", "Plant", "Foliage", "Window", "Curtain", "Wallpaper", "Flooring",
            "Vase", "Pot", "Basket", "Banner", "Sign", "Poster", "Painting", "Sconce", "Garland"
        };

        private static readonly string[] GotoUnreachableBlacklist = new[]
        {
            "画", "窗户", "窗帘", "纱帘", "百叶", "挂件", "海报", "壁纸", "墙纸", "横幅", "挂钟",
            "Painting", "Picture", "Window", "Curtain", "Poster", "Wallpaper", "Banner", "Sconce",
            "垂叶", "吊灯", "吊饰", "Foliage", "Chandelier", "Ceiling",
            "地毯", "地砖", "地板", "垫子", "Rug", "Carpet", "Mat", "Floor", "Path",
            "桌布", "Tablecloth"
        };

        // ─────────────────────────────────────────────
        //  对外 API
        // ─────────────────────────────────────────────
        public static string GetTodayFestivalName()
        {
            try
            {
                if (!Utility.isFestivalDay(Game1.dayOfMonth, Game1.season)) return null;

                if (VanillaFestivals.TryGetValue(Game1.currentSeason, out var days)
                    && days.TryGetValue(Game1.dayOfMonth, out var nameInfo))
                {
                    return IsChineseLanguage ? nameInfo.Zh : nameInfo.En;
                }

                return IsChineseLanguage
                    ? $"{Game1.currentSeason}季节日（第 {Game1.dayOfMonth} 天）"
                    : $"{Game1.currentSeason.ToLower()} festival (day {Game1.dayOfMonth})";
            }
            catch { return null; }
        }

        public static bool IsFestivalCurrentlyActive()
            => Game1.CurrentEvent != null && Game1.CurrentEvent.isFestival;

        public static string GetLocationFriendlyName(string locationName)
        {
            if (string.IsNullOrWhiteSpace(locationName))
                return IsChineseLanguage ? "未知区域" : "an unknown place";

            // 1. 精确匹配
            if (LocationFriendlyNames.TryGetValue(locationName, out var friendlyInfo))
                return IsChineseLanguage ? friendlyInfo.Zh : friendlyInfo.En;

            // 2. 兼容剥离 Custom_ 前缀后再尝试匹配
            if (locationName.StartsWith("Custom_", StringComparison.OrdinalIgnoreCase))
            {
                string stripped = locationName.Substring("Custom_".Length);
                if (LocationFriendlyNames.TryGetValue(stripped, out var strippedInfo))
                    return IsChineseLanguage ? strippedInfo.Zh : strippedInfo.En;
            }
            // 兼容补充 Custom_ 前缀后再尝试匹配
            else
            {
                string added = "Custom_" + locationName;
                if (LocationFriendlyNames.TryGetValue(added, out var addedInfo))
                    return IsChineseLanguage ? addedInfo.Zh : addedInfo.En;
            }

            // 3. 回退为原始名称
            return locationName;
        }

        /// <summary>
        /// 统一使用内部扫描管线，用于构建 Prompt 的近景物件描述。
        /// 过滤掉 NPC，避免与 Prompts 里的 sceneNearbyVillagers 重复；
        /// 寻路管线 ScanNearbyObjectsWithTiles 保持完整不受影响。
        /// </summary>
        public static List<string> ScanNearbyObjects(int radiusTiles = 5, int maxItems = 8)
            => ScanNearbyObjects(null, radiusTiles, maxItems);

        public static List<string> ScanNearbyObjects(NPC npc, int radiusTiles = 5, int maxItems = 8)
        {
            var rawResults = ScanInternal(npc, radiusTiles, maxItems * SCAN_BUFFER_MULTIPLIER);

            // 核心修复：纯展示文本层剔除 NPC，现场村民由 sceneNearbyVillagers 独立感知呈现
            var objectResults = rawResults.Where(item => item.Type != "NPC");

            // 按优先级分桶组装
            var tier1 = new List<string>();
            var tier2 = new List<string>();
            var tier3 = new List<string>();

            foreach (var item in objectResults)
            {
                switch (item.Priority)
                {
                    case 1: tier1.Add(item.Description); break;
                    case 2: tier2.Add(item.Description); break;
                    case 3: tier3.Add(item.Description); break;
                }
            }

            var results = new List<string>();
            results.AddRange(tier1.Distinct(StringComparer.OrdinalIgnoreCase).Take(maxItems));
            int remaining = maxItems - results.Count;
            if (remaining > 0)
            {
                results.AddRange(tier2.Distinct(StringComparer.OrdinalIgnoreCase).Take(remaining));
                remaining = maxItems - results.Count;
                if (remaining > 0)
                    results.AddRange(tier3.Distinct(StringComparer.OrdinalIgnoreCase).Take(remaining));
            }

            return results;
        }

        /// <summary>
        /// 统一使用内部扫描管线
        /// </summary>
        public static List<(string Description, Vector2 Tile)> ScanNearbyObjectsWithTiles(
            NPC npc, int radiusTiles = 15, int maxItems = 20)
        {
            var rawResults = ScanInternal(npc, radiusTiles, maxItems);

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var results = new List<(string, Vector2)>();

            foreach (var item in rawResults)
            {
                string dedupKey = $"{item.Type}_{item.Description}@{(int)item.Tile.X},{(int)item.Tile.Y}";
                if (!seen.Add(dedupKey)) continue;

                results.Add((item.Description, item.Tile));
                if (results.Count >= maxItems) break;
            }

            return results;
        }

        public static string FormatAssetsForGotoPrompt(NPC npc, int radiusTiles = 15)
        {
            var assets = ScanNearbyObjectsWithTiles(npc, radiusTiles);

            if (assets.Count == 0)
            {
                return IsChineseLanguage
                    ? "周围空间开阔，视野良好（坐标按 x,y 标注）："
                    : "Nearby area is clear and accessible (coordinate format x,y):";
            }

            var sb = new System.Text.StringBuilder();
            sb.AppendLine(IsChineseLanguage
                ? "附近可通行的目标环境与物品（坐标格式为 x,y）："
                : "Nearby objects you can walk to (coordinate format x,y):");

            foreach (var (desc, tile) in assets)
                sb.AppendLine($"- {desc} @ ({(int)tile.X},{(int)tile.Y})");

            return sb.ToString().TrimEnd();
        }

        // ─────────────────────────────────────────────
        //  统一内部扫描管线（单一数据源）
        // ─────────────────────────────────────────────
        private readonly struct ScanItem
        {
            public readonly string Description;
            public readonly Vector2 Tile;
            public readonly int Priority;     // 1=Tier1, 2=Tier2, 3=Tier3
            public readonly string Type;      // "NPC", "Terrain", "Object", "Furniture"

            public ScanItem(string description, Vector2 tile, int priority, string type)
            {
                Description = description;
                Tile = tile;
                Priority = priority;
                Type = type;
            }
        }

        /// <summary>
        /// 核心扫描方法：采用定界坐标查询，彻底摆脱大地图全量 Pairs 遍历，运算量常数化
        /// </summary>
        private static List<ScanItem> ScanInternal(NPC npc, int radiusTiles, int maxRawItems)
        {
            var results = new List<ScanItem>();
            var location = npc?.currentLocation ?? Game1.currentLocation;
            if (location == null) return results;

            var centerTile = npc?.Tile ?? Game1.player?.Tile ?? Vector2.Zero;

            try
            {
                // ── 1. 扫描周围 NPC（Tier 1：NPC 列表很小，直接按列表过滤）──
                foreach (var character in location.characters)
                {
                    if (character == null || character == npc) continue;
                    var charTile = character.Tile;
                    if (Math.Abs(charTile.X - centerTile.X) > radiusTiles ||
                        Math.Abs(charTile.Y - centerTile.Y) > radiusTiles) continue;

                    string name = character.displayName ?? character.Name;
                    if (!string.IsNullOrWhiteSpace(name))
                        results.Add(new ScanItem(name, charTile, 1, "NPC"));

                    if (results.Count >= maxRawItems) return results;
                }

                // ── 2. 扫描地形特征与放置物品（局部定界查询，杜绝全图遍历）──
                int minX = (int)centerTile.X - radiusTiles;
                int maxX = (int)centerTile.X + radiusTiles;
                int minY = (int)centerTile.Y - radiusTiles;
                int maxY = (int)centerTile.Y + radiusTiles;

                for (int x = minX; x <= maxX; x++)
                {
                    for (int y = minY; y <= maxY; y++)
                    {
                        var tile = new Vector2(x, y);

                        // 2.1 地形特征 (Crops, Trees, Grass 等)
                        if (location.terrainFeatures.TryGetValue(tile, out var feature) && feature != null)
                        {
                            string desc = DescribeTerrainFeature(feature, location);
                            if (!string.IsNullOrEmpty(desc))
                            {
                                bool isTier1 = desc.Contains("crop", StringComparison.OrdinalIgnoreCase)
                                            || desc.Contains("fruit", StringComparison.OrdinalIgnoreCase)
                                            || desc.Contains("树")
                                            || desc.Contains("作物");

                                results.Add(new ScanItem(desc, tile, isTier1 ? 1 : 3, "Terrain"));
                                if (results.Count >= maxRawItems) return results;
                            }
                        }

                        // 2.2 放置物品 (Machines, Storage, Forageables 等)
                        if (location.Objects.TryGetValue(tile, out var obj) && obj != null)
                        {
                            string name = obj.DisplayName;
                            string internalObjName = obj.Name ?? string.Empty;
                            string itemId = obj.ItemId ?? string.Empty;

                            if (!string.IsNullOrWhiteSpace(name) &&
                                !Blacklist.Contains(name) &&
                                !Blacklist.Contains(internalObjName) &&
                                !Blacklist.Contains(itemId))
                            {
                                string desc = DescribeObject(obj);
                                if (!string.IsNullOrEmpty(desc))
                                {
                                    int priority = 3;
                                    if (Tier1_Creatures.Contains(internalObjName) || Tier1_Creatures.Contains(name))
                                        priority = 1;
                                    else if (Tier2_ActiveDevices.Contains(internalObjName) || Tier2_ActiveDevices.Contains(name))
                                        priority = 2;
                                    else if (Tier3_Landmarks.Contains(internalObjName) || Tier3_Landmarks.Contains(name))
                                        priority = 3;

                                    results.Add(new ScanItem(desc, tile, priority, "Object"));
                                    if (results.Count >= maxRawItems) return results;
                                }
                            }
                        }
                    }
                }

                // ── 3. 扫描家具（通常只在室内出现，家具列表极小）──
                if (location is StardewValley.Locations.DecoratableLocation decoratable)
                {
                    foreach (var furniture in decoratable.furniture)
                    {
                        if (furniture == null) continue;
                        var fTile = furniture.TileLocation;
                        if (Math.Abs(fTile.X - centerTile.X) > radiusTiles ||
                            Math.Abs(fTile.Y - centerTile.Y) > radiusTiles) continue;

                        int fType = furniture.furniture_type.Value;
                        if (fType == StardewValley.Objects.Furniture.painting ||
                            fType == StardewValley.Objects.Furniture.rug ||
                            fType == StardewValley.Objects.Furniture.window)
                        {
                            continue;
                        }

                        string name = SanitizeObjectName(furniture.DisplayName);
                        if (string.IsNullOrWhiteSpace(name)) continue;

                        string internalName = furniture.Name ?? string.Empty;
                        string fItemId = furniture.ItemId ?? string.Empty;

                        bool isNoise = FurnitureNameBlacklist.Any(black =>
                            name.Contains(black, StringComparison.OrdinalIgnoreCase) ||
                            internalName.Contains(black, StringComparison.OrdinalIgnoreCase) ||
                            fItemId.Contains(black, StringComparison.OrdinalIgnoreCase));

                        if (isNoise) continue;

                        int priority = (Tier2_ActiveDevices.Contains(name) || Tier2_ActiveDevices.Contains(internalName)) ? 2 : 3;

                        results.Add(new ScanItem(name, fTile, priority, "Furniture"));
                        if (results.Count >= maxRawItems) return results;
                    }
                }
            }
            catch (Exception ex)
            {
                ModEntry.SMonitor?.Log(
                    $"[EnvironmentScanner] ScanInternal error at {location?.Name ?? "null"} " +
                    $"center=({centerTile.X},{centerTile.Y}) radius={radiusTiles}: {ex.Message}",
                    LogLevel.Debug);
            }

            return results;
        }

        // ─────────────────────────────────────────────
        //  私有：物品与环境描述（双语支持）
        // ─────────────────────────────────────────────
        private static string DescribeObject(StardewValley.Object obj)
        {
            if (obj == null) return null;

            string name = SanitizeObjectName(obj.DisplayName);
            string internalName = obj.Name ?? string.Empty;
            string itemId = obj.ItemId ?? string.Empty;

            if (string.IsNullOrWhiteSpace(name) ||
                Blacklist.Contains(name) ||
                Blacklist.Contains(internalName) ||
                Blacklist.Contains(itemId))
            {
                return null;
            }

            if (obj.bigCraftable.Value)
                return name;

            if (IsChineseLanguage)
            {
                return obj.Category switch
                {
                    StardewValley.Object.SeedsCategory     => $"种子（{name}）",
                    StardewValley.Object.flowersCategory   => $"花卉（{name}）",
                    StardewValley.Object.FruitsCategory    => $"水果（{name}）",
                    StardewValley.Object.VegetableCategory => $"蔬菜（{name}）",
                    StardewValley.Object.GemCategory       => $"宝石（{name}）",
                    StardewValley.Object.mineralsCategory  => $"矿物（{name}）",
                    _ => name
                };
            }
            else
            {
                return obj.Category switch
                {
                    StardewValley.Object.SeedsCategory     => $"seeds ({name})",
                    StardewValley.Object.flowersCategory   => $"flowers ({name})",
                    StardewValley.Object.FruitsCategory    => $"fruit ({name})",
                    StardewValley.Object.VegetableCategory => $"vegetables ({name})",
                    StardewValley.Object.GemCategory       => $"gem ({name})",
                    StardewValley.Object.mineralsCategory  => $"mineral ({name})",
                    _ => name
                };
            }
        }

        // ─────────────────────────────────────────────
        //  显示名清洗：丢弃引擎/模组的错误回退标识
        // ─────────────────────────────────────────────
        private static string SanitizeObjectName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;

            if (name.StartsWith("错误物品", StringComparison.Ordinal)
                || name.StartsWith("Error Item", StringComparison.OrdinalIgnoreCase))
                return null;

            if (name.Contains("(RF.", StringComparison.Ordinal)) return null;

            return name;
        }

        private static string DescribeTerrainFeature(TerrainFeature feature, GameLocation location)
        {
            if (IsChineseLanguage)
            {
                return feature switch
                {
                    Tree tree when tree.growthStage.Value >= Tree.treeStage => "大树",
                    FruitTree fruitTree when fruitTree.fruit != null && fruitTree.fruit.Count > 0
                        => "挂满果实的果树",
                    FruitTree => "果树",
                    Grass => (Game1.currentSeason == "winter" || location?.Name == "Desert")
                        ? "干枯的草丛" : null,
                    HoeDirt dirt when dirt.crop != null && dirt.readyForHarvest() => "成熟可收割的作物",
                    HoeDirt dirt when dirt.crop != null => "生长的作物",
                    Flooring => null,
                    _ => null
                };
            }
            else
            {
                return feature switch
                {
                    Tree tree when tree.growthStage.Value >= Tree.treeStage => "a large tree",
                    FruitTree fruitTree when fruitTree.fruit != null && fruitTree.fruit.Count > 0
                        => "a fruit tree with ripe fruit",
                    FruitTree => "a fruit tree",
                    Grass => (Game1.currentSeason == "winter" || location?.Name == "Desert")
                        ? "patch of dry grass" : null,
                    HoeDirt dirt when dirt.crop != null && dirt.readyForHarvest() => "a ready-to-harvest crop",
                    HoeDirt dirt when dirt.crop != null => "growing crops",
                    Flooring => null,
                    _ => null
                };
            }
        }
    }
}