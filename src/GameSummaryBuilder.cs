using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using ValleytalkReborn;

namespace ValleytalkReborn;

// ─────────────────────────────────────────────────────────
// BuildContext — 运行时过滤条件，由调用方按需构造
// ─────────────────────────────────────────────────────────

public sealed class BuildContext
{
    /// <summary>是否注入季节信息（作物/采集）</summary>
    public bool IncludeSeasons { get; init; } = true;

    /// <summary>是否注入地点百科</summary>
    public bool IncludeLocations { get; init; } = true;

    /// <summary>是否注入节日日历（仅节日当天或前一天有意义）</summary>
    public bool IncludeFestivals { get; init; } = false;

    /// <summary>
    /// 当前 NPC 所在地点的 Region（如 "Pelican Town"、"Mountain"）。
    /// null = 全量输出；非 null = 只输出同 Region + The Farm 的条目。
    /// </summary>
    public string CurrentRegion { get; init; } = null;

    /// <summary>
    /// 当前具体的地图内部名（如 "Town"、"SeedShop"、"Saloon" 等）
    /// </summary>
    public string CurrentLocationName { get; init; } = null;

    /// <summary>
    /// 根据运行时状态自动构造上下文。
    /// 这是唯一应该被外部调用的工厂方法。
    /// </summary>
    public static BuildContext FromGameState(ContextFlags flags, string locationName, Dictionary<string, string> regionMap)
    {
        // 节日当天或前一天才注入节日日历
        bool festivalRelevant = EnvironmentScanner.GetTodayFestivalName() != null
            || IsNextDayFestival();

        // 把地点名映射到 Region
        string region = ResolveRegion(locationName, regionMap);

        return new BuildContext
        {
            IncludeSeasons      = true,
            IncludeLocations    = flags.IncludeEnvironment,
            IncludeFestivals    = festivalRelevant,
            CurrentRegion       = region,
            CurrentLocationName = locationName,
        };
    }

    private static bool IsNextDayFestival()
    {
        int tomorrow = Game1.dayOfMonth + 1;
        if (tomorrow > 28) return false;
        // 借用 Utility 的节日检测，传入明天的日期
        return Utility.isFestivalDay(tomorrow, Game1.season);
    }

    /// <summary>
    /// 把地点名映射到 Region 字符串。
    /// 只查 JSON 配置的 regionMap，匹配不到时返回 null（全量输出，安全降级）。
    /// </summary>
    private static string ResolveRegion(string locationName, Dictionary<string, string> regionMap)
    {
        if (string.IsNullOrWhiteSpace(locationName)) return null;
        if (regionMap == null) return null;
        return regionMap.TryGetValue(locationName, out string region) ? region : null;
    }
}

// ─────────────────────────────────────────────────────────
// GameSummaryBuilder
// ─────────────────────────────────────────────────────────

internal class GameSummaryBuilder
{
    private GameSummary _gameSummaryDict;

    private GameSummary GameSummaryDict
    {
        get
        {
            if (_gameSummaryDict == null)
            {
                _gameSummaryDict = Game1.content.LoadLocalized<GameSummary>(VtConstants.GameSummaryPath);
            }
            return _gameSummaryDict;
        }
    }

    public GameSummaryBuilder()
    {
        ModEntry.SHelper.Events.Content.AssetRequested += (sender, e) =>
        {
            if (e.Name.IsEquivalentTo(VtConstants.GameSummaryPath))
            {
                e.LoadFrom(() => new GameSummary(), AssetLoadPriority.High);
            }
        };
        ModEntry.SHelper.Events.Content.AssetsInvalidated += (sender, e) =>
        {
            if (e.NamesWithoutLocale.Any(an => an.IsEquivalentTo(VtConstants.GameSummaryPath)))
            {
                _gameSummaryDict = null;
            }
        };
    }

    /// <summary>
    /// 构建世界观摘要字符串。
    /// <paramref name="ctx"/> 为 null 时使用全开的默认值（向后兼容）。
    /// </summary>
    internal string Build(BuildContext ctx = null)
    {
        ctx ??= new BuildContext
        {
            IncludeSeasons   = true,
            IncludeLocations = true,
            IncludeFestivals = true,
        };

        var builder = new StringBuilder();
        var sections = GameSummaryDict.SectionOrder;

        if (sections == null)
        {
            ModEntry.SMonitor.Log("GameSummary is missing SectionOrder", LogLevel.Error);
            return string.Empty;
        }

        foreach (var section in sections)
        {
            // JSON 开关先判断 — false 直接跳过，不输出任何内容
            if (!section.Value) continue;

            // 运行时 BuildContext 再过滤
            if (!IsIncludedByContext(section.Key, ctx)) continue;

            var property = GameSummaryDict.GetType().GetProperty(section.Key);
            var sectionObject = property?.GetValue(GameSummaryDict) as IGameSummarySection;
            if (sectionObject == null)
            {
                ModEntry.SMonitor.Log($"GameSummary is missing section {section.Key}", LogLevel.Error);
                continue;
            }

            builder.AppendLine($"### {section.Key} :");

            if (!string.IsNullOrWhiteSpace(sectionObject.Text))
                builder.AppendLine(sectionObject.Text);

            try
            {
                switch (section.Key)
                {
                    case "Seasons":
                        BuildSeasons(builder, sectionObject);
                        break;
                    case "Locations":
                        BuildLocations(builder, sectionObject, ctx);
                        break;
                    default:
                        BuildGeneral(builder, sectionObject);
                        break;
                }
            }
            catch (Exception ex)
            {
                ModEntry.SMonitor.Log($"Error building section {section.Key}: {ex}", LogLevel.Error);
            }
        }

        return builder.ToString();
    }

    // ─────────────────────────────────────────────
    // 运行时过滤映射
    // ─────────────────────────────────────────────

    private static bool IsIncludedByContext(string sectionKey, BuildContext ctx) => sectionKey switch
    {
        "Seasons"   => ctx.IncludeSeasons,
        "Locations" => ctx.IncludeLocations,
        "Festivals" => ctx.IncludeFestivals,
        _           => true,
    };

    // ─────────────────────────────────────────────
    // 各节构建方法
    // ─────────────────────────────────────────────

    private static void BuildSeasons(StringBuilder builder, IGameSummarySection sectionObject)
    {
        var seasonsList = sectionObject as IGameSummarySection<SeasonObject>;
        if (seasonsList?.Entries == null) return;

        string currentSeason = Game1.currentSeason;

        foreach (var season in seasonsList.Entries)
        {
            if (!string.Equals(season.Key, currentSeason, StringComparison.OrdinalIgnoreCase))
                continue;

            var s = season.Value;
            builder.Append($"- **{s.Name}** - {s.Description} ");
            if (s.Crops?.Count > 0)
                builder.Append($"{Util.ConcatAnd(s.Crops)}. ");
            if (s.Forage?.Count > 0)
                builder.AppendLine($"{Util.ConcatAnd(s.Forage)}.");
            break;
        }
    }

    private void BuildLocations(StringBuilder builder, IGameSummarySection sectionObject, BuildContext ctx)
    {
        var locationsList = sectionObject as IGameSummarySection<LocationObject>;
        if (locationsList?.Entries == null) return;

        bool isZh = LocalizedContentManager.CurrentLanguageCode.ToString().StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        string curLoc = ctx.CurrentLocationName ?? "";

        // ─────────────────────────────────────────────────────────────
        // 1. 如果处于室外城镇街道（"Town"）
        //    只输出 The Farm 与一条概括性鹈鹕镇介绍，避免倾倒所有建筑
        // ─────────────────────────────────────────────────────────────
        if (string.Equals(curLoc, "Town", StringComparison.OrdinalIgnoreCase))
        {
            if (locationsList.Entries.TryGetValue("TheFarm", out var farmLoc))
            {
                builder.AppendLine($"- **{farmLoc.Name}** - {farmLoc.Description}");
            }

            string townName = SafeGetTranslation("summaryLocPelicanTownOverviewName", isZh ? "鹈鹕镇" : "Pelican Town");
            string townDesc = SafeGetTranslation("summaryLocPelicanTownOverviewDesc", isZh
                ? "一座民风淳朴的海滨小镇。镇上坐落着皮埃尔杂货店、星之果实酒吧、哈维诊所、铁匠铺、图书馆与博物馆以及社区中心等主要设施。"
                : "A close-knit coastal town featuring local hubs like Pierre's General Store, the Stardrop Saloon, Harvey's Clinic, Blacksmith, Museum, and Community Center.");

            builder.AppendLine($"- **{townName}** - {townDesc}");
            return;
        }

        // ─────────────────────────────────────────────────────────────
        // 2. 正常输出：处于室内建筑，或者处于其他 Region（山脉、森林等）
        // ─────────────────────────────────────────────────────────────
        foreach (var kv in locationsList.Entries)
        {
            string locationKey = kv.Key;
            var location = kv.Value;

            // 若当前在鹈鹕镇具体室内建筑中：仅保留 The Farm 与当前建筑
            if (ctx.CurrentRegion == "Pelican Town")
            {
                if (location.Region == "The Farm")
                {
                    builder.AppendLine($"- **{location.Name}** - {location.Description}");
                    continue;
                }

                if (!IsCurrentBuildingMatch(curLoc, locationKey, location.id))
                {
                    continue;
                }
            }
            else
            {
                // 其他区域过滤逻辑
                if (ctx.CurrentRegion != null
                    && location.Region != ctx.CurrentRegion
                    && location.Region != "The Farm")
                {
                    continue;
                }
            }

            string locName = location.Name;
            string locDesc = location.Description;

            // 🌟 动态适配：社区中心状态
            if (locationKey.Contains("CommunityCenter", StringComparison.OrdinalIgnoreCase) ||
                location.id?.Contains("CommunityCenter", StringComparison.OrdinalIgnoreCase) == true)
            {
                locDesc = GetDynamicCommunityCenterDescription(isZh, ref locName, locDesc);
            }
            // 🌟 动态适配：JojaMart 状态
            else if (locationKey.Contains("JojaMart", StringComparison.OrdinalIgnoreCase) ||
                     location.id?.Contains("JojaMart", StringComparison.OrdinalIgnoreCase) == true)
            {
                locDesc = GetDynamicJojaMartDescription(isZh, ref locName, locDesc);
            }

            builder.AppendLine($"- **{locName}** - {locDesc}");
        }
    }

    /// <summary>
    /// 判定当前所在室内地图名是否与 GameSummary.json 中的条目对应
    /// </summary>
    private static bool IsCurrentBuildingMatch(string curLoc, string entryKey, string entryId)
    {
        if (string.IsNullOrWhiteSpace(curLoc)) return false;

        if (entryKey.EndsWith(curLoc, StringComparison.OrdinalIgnoreCase)) return true;
        if (!string.IsNullOrEmpty(entryId) && entryId.EndsWith(curLoc, StringComparison.OrdinalIgnoreCase)) return true;

        if (curLoc.Equals("Hospital", StringComparison.OrdinalIgnoreCase) && entryKey.Contains("Clinic", StringComparison.OrdinalIgnoreCase))
            return true;
        if (curLoc.Equals("Saloon", StringComparison.OrdinalIgnoreCase) && entryKey.Contains("Saloon", StringComparison.OrdinalIgnoreCase))
            return true;
        if ((curLoc.Equals("LibraryMuseum", StringComparison.OrdinalIgnoreCase) || curLoc.Equals("ArchaeologyHouse", StringComparison.OrdinalIgnoreCase))
            && entryKey.Contains("Museum", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    /// <summary>
    /// 根据游戏全局进度获取社区中心（或改建后仓库/影院）的动态描述
    /// </summary>
    private static string GetDynamicCommunityCenterDescription(bool isZh, ref string name, string fallbackDesc)
    {
        var mail = Game1.MasterPlayer.mailReceived;
        bool isJojaRoute = mail.Contains("JojaMember");
        bool isCCComplete = mail.Contains("ccIsComplete");
        bool hasMovieTheater = mail.Contains("ccMovieTheater");

        if (isJojaRoute)
        {
            if (hasMovieTheater)
            {
                name = isZh ? "电影院" : "Movie Theater";
                return SafeGetTranslation("summaryLocCommunityCenterTheater", isZh
                    ? "经全面翻新改建，这里如今已是小镇热闹的现代化电影院，镇民们闲暇时会来此观影。"
                    : "Formerly the town center / warehouse, now renovated into a modern movie theater for all villagers.");
            }
            name = isZh ? "Joja 仓库" : "Joja Warehouse";
            return SafeGetTranslation("summaryLocCommunityCenterJoja", isZh
                ? "已被 Joja 集团收购改建为仓储设施，彻底断绝了传统社区中心的修复可能。"
                : "Purchased and transformed into a storage warehouse for Joja Corp.");
        }

        if (isCCComplete)
        {
            return SafeGetTranslation("summaryLocCommunityCenterRestored", isZh
                ? "在农夫与神秘精怪祝尼魔的共同努力下已彻底修复，如今重新成为镇民聚会、阅读与交流的活力中心。"
                : "Fully restored to its former glory thanks to the farmer and the Junimos; now a vibrant community hub for town gatherings.");
        }

        return fallbackDesc;
    }

    /// <summary>
    /// 根据游戏全局进度获取 JojaMart 的动态描述
    /// </summary>
    private static string GetDynamicJojaMartDescription(bool isZh, ref string name, string fallbackDesc)
    {
        var mail = Game1.MasterPlayer.mailReceived;
        bool isCCComplete = mail.Contains("ccIsComplete");
        bool hasMovieTheater = mail.Contains("ccMovieTheater") && !mail.Contains("JojaMember");

        if (hasMovieTheater)
        {
            name = isZh ? "电影院" : "Movie Theater";
            return SafeGetTranslation("summaryLocJojaMartTheater", isZh
                ? "原 Joja 超市旧址在倒闭后，经修缮改造为小镇的现代化电影院。"
                : "The former JojaMart structure, successfully renovated into the town's movie theater.");
        }

        if (isCCComplete)
        {
            return SafeGetTranslation("summaryLocJojaMartAbandoned", isZh
                ? "随着社区中心全面复苏，Joja 超市已彻底破产倒闭、大门紧锁，莫里斯及员工均已离开。"
                : "Abandoned and permanently shuttered following the community center's revival; Morris and his team have vacated.");
        }

        return fallbackDesc;
    }

    private static string SafeGetTranslation(string key, string fallback)
    {
        try
        {
            string val = I18n.Get(key);
            if (!string.IsNullOrWhiteSpace(val) && !val.Equals(key))
                return val;
        }
        catch { }
        return fallback;
    }

    private static void BuildGeneral(StringBuilder builder, IGameSummarySection sectionObject)
    {
        var itemsList = sectionObject as IGameSummarySection<GeneralObject>;
        if (itemsList?.Entries == null) return;

        foreach (var item in itemsList.Entries.Values)
            builder.AppendLine($"- **{item.Name}** - {item.Description}");
    }

    /// <summary>
    /// 获取 LocationRegions 映射字典（地图内部名 → Region）。
    /// </summary>
    internal Dictionary<string, string> GetLocationRegions()
    {
        return GameSummaryDict.LocationRegions;
    }
}

// ─────────────────────────────────────────────────────────
// 数据模型
// ─────────────────────────────────────────────────────────

public class GeneralObject
{
    public string id { get; set; }
    public string Name { get; set; }
    public string Description { get; set; }
}

public interface IGameSummarySection
{
    public string Text { get; set; }
}

public interface IGameSummarySection<T> : IGameSummarySection
{
    public Dictionary<string, T> Entries { get; set; }
}

public class GeneralList : IGameSummarySection<GeneralObject>
{
    public string Text { get; set; }
    public Dictionary<string, GeneralObject> Entries { get; set; }
}

public class LocationObject : GeneralObject
{
    public string Region { get; set; }
}

public class LocationList : IGameSummarySection<LocationObject>
{
    public string Text { get; set; }
    public Dictionary<string, LocationObject> Entries { get; set; }
}

public class SeasonObject : GeneralObject
{
    public List<string> Crops { get; set; }
    public List<string> Forage { get; set; }
}

public class SeasonList : IGameSummarySection<SeasonObject>
{
    public string Text { get; set; }
    public Dictionary<string, SeasonObject> Entries { get; set; }
}

internal class GameSummary
{
    public Dictionary<string, bool> SectionOrder { get; set; }
    public GeneralList Intro { get; set; }
    public GeneralList FarmerBackground { get; set; }
    public GeneralList Villagers { get; set; }
    public SeasonList Seasons { get; set; }
    public LocationList Locations { get; set; }
    public GeneralList Festivals { get; set; }
    public GeneralList Outro { get; set; }

    /// <summary>
    /// 地图内部名 → Region 的映射字典。
    /// </summary>
    public Dictionary<string, string> LocationRegions { get; set; }
}