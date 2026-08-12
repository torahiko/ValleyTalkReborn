using System.Collections.Generic;
using System.Linq;
using System.Text;
using StardewValley;
using ValleytalkReborn;
using System;
using StardewModdingAPI.Events;
using StardewModdingAPI;

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
            IncludeSeasons   = true,
            IncludeLocations = flags.IncludeEnvironment,
            IncludeFestivals = festivalRelevant,
            CurrentRegion    = region,
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

        // var translations = Util.GetString("gameSummaryTranslations");
        // if (!string.IsNullOrWhiteSpace(translations))
        //     builder.AppendLine(translations);

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
        // Intro / FarmerBackground / Villagers / Outro 由 JSON SectionOrder 控制，
        // BuildContext 不干预它们（到这里时 JSON 开关已经是 false，不会执行到这里）
        _=> true,
    };

    // ─────────────────────────────────────────────
    // 各节构建方法（从 Build() 里拆出来，保持主流程干净）
    // ─────────────────────────────────────────────

    private static void BuildSeasons(StringBuilder builder, IGameSummarySection sectionObject)
    {
        var seasonsList = sectionObject as IGameSummarySection<SeasonObject>;
        if (seasonsList?.Entries == null) return;

        // 取当前季节名（"spring" / "summer" / "fall" / "winter"），不区分大小写匹配 JSON key
        string currentSeason = Game1.currentSeason; // 返回小写英文字符串

        foreach (var season in seasonsList.Entries)
        {
            // 只注入当前季节
            if (!string.Equals(season.Key, currentSeason, StringComparison.OrdinalIgnoreCase))
                continue;

            var s = season.Value;
            builder.Append($"- **{s.Name}** - {s.Description} ");
            if (s.Crops?.Count > 0)
                // builder.Append($"{Util.GetString("seasonCrops")} {Util.ConcatAnd(s.Crops)}. ");
                builder.Append($"{Util.ConcatAnd(s.Crops)}. ");
            if (s.Forage?.Count > 0)
                // builder.AppendLine($"{Util.GetString("seasonForage")} {Util.ConcatAnd(s.Forage)}.");
                builder.AppendLine($"{Util.ConcatAnd(s.Forage)}.");
            break; // 找到当前季节后直接退出，避免多余遍历
        }
    }

    private void BuildLocations(StringBuilder builder, IGameSummarySection sectionObject, BuildContext ctx)
    {
        var locationsList = sectionObject as IGameSummarySection<LocationObject>;
        if (locationsList?.Entries == null) return;

        foreach (var location in locationsList.Entries.Values)
        {
            // 有 CurrentRegion 限制时：只输出同 Region 的条目，但 The Farm 始终保留
            // （NPC 经常提到农场，无论身在何处）
            if (ctx.CurrentRegion != null
                && location.Region != ctx.CurrentRegion
                && location.Region != "The Farm")
            {
                continue;
            }
            builder.AppendLine($"- **{location.Name}** - {location.Description}");
        }
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
    /// 数据来源为 GameSummary.json 中的 LocationRegions 节。
    /// 返回 null 表示 JSON 中未定义该字段，调用方应自行处理。
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
    /// 数据来源：GameSummary.json 中的 LocationRegions 节。
    /// 用于 ResolveRegion 快速查询。
    /// </summary>
    public Dictionary<string, string> LocationRegions { get; set; }
}