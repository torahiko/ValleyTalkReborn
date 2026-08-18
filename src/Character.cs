using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using ValleytalkReborn;
using StardewValley;
using System.Threading.Tasks;
using Microsoft.Xna.Framework.Content;
using StardewModdingAPI.Events;
using System.Threading;

namespace ValleytalkReborn;

public class Character : IDisposable
{
    private BioData _bioData;

    // 🌟 #1: 删除死代码 filterTimes（从未被使用）

    private DialogueFile dialogueData;
    private Season? _sampleCacheSeason;
    private int? _sampleCacheDay;
    private int? _sampleCacheHeartLevel;
    private DialogueValue[] _sampleCache;

    // 🌟 #9: 提取对话样本数量为常量
    private const int DialogueSampleSize = 20;

    // 🌟 #11: 提取星露谷一年天数为常量（4 季 × 28 天）
    private const int StardewYearInDays = 112;

    public NPC StardewNpc { get; internal set; }
    public List<string> ValidPortraits { get; internal set; }

    // 🌟 #2: 改为 static readonly，避免每个实例重复创建字典
    private static readonly Dictionary<string, string> HistoryEvents = new()
    {
        { "wonIceFishing", Util.GetString("wonIceFishing") },
        { "wonGrange", Util.GetString("wonGrange") },
        { "wonEggHunt", Util.GetString("wonEggHunt") }
    };

    public Character(string name, NPC stardewNpc)
    {
        Name = name;
        BioFilePath = $"{VtConstants.BiosPath}/{DialogueCleaner.RemoveDotSuffixes(Name)}";
        StardewNpc = stardewNpc;
        ModEntry.SHelper.Events.Content.AssetRequested += OnAssetRequested;
        ModEntry.SHelper.Events.Content.AssetsInvalidated += OnAssetsInvalidated;
    }

    private void OnAssetRequested(object sender, AssetRequestedEventArgs e)
    {
        if (e.Name.IsEquivalentTo(BioFilePath))
        {
            e.LoadFrom(() => new BioData(), AssetLoadPriority.High);
        }
    }

    private void OnAssetsInvalidated(object sender, AssetsInvalidatedEventArgs e)
    {
        if (e.NamesWithoutLocale.Any(an => an.IsEquivalentTo(BioFilePath)))
        {
            _bioData = null;
            // 🌟 #3: 重置衍生缓存，防止 Bio 热重载后数据不一致
            ValidPortraits = null;
            PossiblePreoccupations = null;
        }
    }

    public void Dispose()
    {
        ModEntry.SHelper.Events.Content.AssetRequested -= OnAssetRequested;
        ModEntry.SHelper.Events.Content.AssetsInvalidated -= OnAssetsInvalidated;
        // Clean up the cancellation token if it belongs to this character
        if (CurrentDialogueCts != null)
        {
            try { CurrentDialogueCts.Cancel(); } catch { }
            CurrentDialogueCts.Dispose();
            CurrentDialogueCts = null;
        }
    }

    private IEnumerable<string> GetLovedAndHatedGiftNames()
    {
        if (!Game1.NPCGiftTastes.TryGetValue(Name, out var npcGiftTastes))
        {
            return Array.Empty<string>();
        }

        string[] tasteLevels = npcGiftTastes.Split('/');
        List<string> returnList = new();

        // 安全检查：最喜爱物品在索引 1
        if (tasteLevels.Length > 1)
        {
            var lovedGifts = ArgUtility.SplitBySpace(tasteLevels[1]);
            foreach (var gift in lovedGifts)
            {
                if (Game1.objectData.TryGetValue(gift, out var data) && data != null)
                {
                    returnList.Add(data.DisplayName);
                }
            }
        }

        // 安全检查：最讨厌物品在索引 7
        if (tasteLevels.Length > 7)
        {
            var hatedGifts = ArgUtility.SplitBySpace(tasteLevels[7]);
            foreach (var gift in hatedGifts)
            {
                if (Game1.objectData.TryGetValue(gift, out var data) && data != null)
                {
                    returnList.Add(data.DisplayName);
                }
            }
        }

        return returnList;
    }

    private void LoadDialogue()
    {
        Dictionary<string, string> canonDialogue = new();

        if (ModEntry.BlockModdedContent && !Bio.UsePatchedDialogue)
        {
            using var manager = new ContentManager(Game1.content.ServiceProvider, Game1.content.RootDirectory);
            try
            {
                string assetName = $"Characters\\Dialogue\\{Name}";
                foreach (var langSuffix in ModEntry.LanguageFileSuffixes)
                {
                    var path = $"{assetName}{langSuffix}";
                    var unmarriedDialogue = manager.Load<Dictionary<string, string>>(path);
                    if (unmarriedDialogue != null)
                    {
                        canonDialogue = unmarriedDialogue;
                        break;
                    }
                }
            }
            catch (Exception)
            {
                // If it fails, just continue
            }

            try
            {
                string assetName = $"Characters\\Dialogue\\MarriageDialogue{Name}";
                foreach (var langSuffix in ModEntry.LanguageFileSuffixes)
                {
                    var path = $"{assetName}{langSuffix}";
                    var marriedDialogue = manager.Load<Dictionary<string, string>>(path);
                    if (marriedDialogue != null)
                    {
                        foreach (var dialogue in marriedDialogue)
                        {
                            // 🌟 #4: Add → 索引器赋值，防止重复 key 抛 ArgumentException
                            canonDialogue[$"M_{dialogue.Key}"] = dialogue.Value;
                        }
                        break;
                    }
                }
            }
            catch (Exception)
            {
                // If it fails, just continue
            }
        }
        else
        {
            // 🌟 #5: StardewNpc.Dialogue 可能为 null（无对话数据的 NPC）
            canonDialogue = StardewNpc.Dialogue ?? new Dictionary<string, string>();
        }

        if (Bio.Dialogue != null)
        {
            foreach (var dialogue in Bio.Dialogue)
            {
                canonDialogue[dialogue.Key] = dialogue.Value;
            }
        }

        DialogueData = new();
        foreach (var dialogue in canonDialogue)
        {
            var context = DialogueContext.SafeParse(dialogue.Key);
            var value = new DialogueValue(dialogue.Value);
            // 🌟 #6: 删除死代码 if (value is DialogueValue)，直接 Add
            DialogueData.Add("Base", context, value);
        }
    }

    private void CheckBio()
    {
        // 🌟 #7: Biography 可能为 null，使用 string.IsNullOrEmpty 防御
        if (_bioData != null && (!string.IsNullOrEmpty(_bioData.Biography) || _bioData.Missing))
        {
            return;
        }

        BioData bioData;
        try
        {
            bioData = Game1.content.LoadLocalized<BioData>(BioFilePath);
        }
        catch (Exception)
        {
            _bioData = new BioData();
            _bioData.Name = Name;
            _bioData.Missing = true;
            // 🌟 #8: SMonitor.Log → SMonitor?.Log，防止 SMonitor 为 null 时 NRE
            ModEntry.SMonitor?.Log($"No bio file found for {Name}.", StardewModdingAPI.LogLevel.Warn);
            return;
        }

        bioData.Name = Name;
        _bioData = bioData;
        _bioData.Missing = false;

        ValidPortraits = new List<string>() { "h", "s", "l", "a" };
        ValidPortraits.AddRange(_bioData.ExtraPortraits.Keys);

        PossiblePreoccupations = new List<string>(_bioData.Preoccupations);
        PossiblePreoccupations.AddRange(GetLovedAndHatedGiftNames());
    }

    internal IEnumerable<DialogueValue> SelectDialogueSample(DialogueContext context)
    {
        if (_sampleCacheSeason == context.Season &&
            _sampleCacheHeartLevel == context.Hearts &&
            _sampleCacheDay == context.DayOfSeason)
        {
            return _sampleCache;
        }

        _sampleCacheSeason = context.Season;
        _sampleCacheDay = context.DayOfSeason;
        _sampleCacheHeartLevel = context.Hearts;

        // Pick the most relevant dialogue entries
        var orderedDialogue = DialogueData
                    ?.AllEntries
                    .OrderBy(x => context.CompareTo(x.Key));

        var firstStep = orderedDialogue
                    ?.Where(x => x.Value != null);

        if (firstStep == null || !firstStep.Any())
        {
            _sampleCache = Array.Empty<DialogueValue>();
            return _sampleCache;
        }

        // 🌟 #9: Take(20) → Take(DialogueSampleSize)
        _sampleCache = firstStep
                    .SelectMany(x => x.Value.AllValues)
                    .Take(DialogueSampleSize)
                    .ToArray()
                    ?? Array.Empty<DialogueValue>();

        return _sampleCache;
    }

    internal IEnumerable<Tuple<StardewTime, IHistory>> EventHistorySample()
    {
        // 🌟 #10: .First() → .FirstOrDefault()，防止空集合抛 InvalidOperationException
        var allPreviousActivities = Game1.getPlayerOrEventFarmer()
            .previousActiveDialogueEvents.FirstOrDefault();

        List<KeyValuePair<string, int>> previousActivites;
        if (allPreviousActivities != null)
        {
            // 🌟 #11: 魔法数字 112 → StardewYearInDays 常量
            previousActivites = allPreviousActivities
                .Where(x => HistoryEvents.ContainsKey(x.Key)
                    && (x.Value < StardewYearInDays || x.Value % StardewYearInDays == 0))
                .ToList();
        }
        else
        {
            previousActivites = new List<KeyValuePair<string, int>>();
        }

        int limit = ModEntry.Config?.MemoryRecentCount > 0 ? ModEntry.Config.MemoryRecentCount : 20;

        // 🌟 #12: DialogueHistoryManager.Instance null 保护
        var historyManager = DialogueHistoryManager.Instance;
        IEnumerable<Tuple<StardewTime, IHistory>> newHistory;
        if (historyManager != null)
        {
            newHistory = historyManager.GetHistory(Name)
                .Where(e => !e.IsConsumed)
                .Where(e => e.DialogueType != "eavesdrop")
                .TakeLast(limit)
                .Select(e => new Tuple<StardewTime, IHistory>(e.Timestamp, new DialogueHistoryAdapter(e)));
        }
        else
        {
            newHistory = Enumerable.Empty<Tuple<StardewTime, IHistory>>();
        }

        var fullHistory = newHistory.Concat(previousActivites.Select(x => MakeActivityHistory(x)));
        return fullHistory.OrderBy(x => x.Item1);
    }

    private Tuple<StardewTime, IHistory> MakeActivityHistory(KeyValuePair<string, int> x)
    {
        var timeNow = new StardewTime(Game1.year, (Season)Game1.season, Game1.dayOfMonth, Game1.timeOfDay);
        var targetDate = timeNow.AddDays(-x.Value);
        return new(targetDate, new ActivityHistory(x.Key));
    }

    internal bool SpokeJustNow()
    {
        // 🌟 #12: DialogueHistoryManager.Instance null 保护
        var historyManager = DialogueHistoryManager.Instance;
        if (historyManager == null) return false;

        var history = historyManager.GetHistory(Name);
        if (!history.Any())
        {
            return false;
        }

        var lastEntry = history.Last();
        return lastEntry.Timestamp.IsJustNow()
            && lastEntry.SpeakerType != SpeakerType.System;
    }

    internal void ClearConversationHistory()
    {
        // 🌟 #12: DialogueHistoryManager.Instance null 保护
        DialogueHistoryManager.Instance?.ClearHistory(Name);
    }

    public string Name { get; }

    // Expose current dialogue cancellation token for plugin access
    public CancellationTokenSource CurrentDialogueCts { get; internal set; }

    /// <summary>
    /// Set to true when the user explicitly cancels via the cancel button.
    /// Checked by retry logic to prevent auto-retry after user cancellation.
    /// </summary>
    public bool IsUserCancelled { get; set; }

    public string DialogueFilePath { get; }
    public string BioFilePath { get; }

    public DialogueFile DialogueData
    {
        get
        {
            if (dialogueData == null)
            {
                LoadDialogue();
            }
            return dialogueData;
        }
        private set => dialogueData = value;
    }

    public ConcurrentBag<Tuple<DialogueContext, DialogueValue>> CreatedDialogue { get; private set; } = new();

    internal BioData Bio
    {
        get
        {
            CheckBio();
            return _bioData;
        }
    }

    public List<string> PossiblePreoccupations { get; internal set; }
    public string Preoccupation { get; internal set; }
    public WorldDate PreoccupationDate { get; internal set; }
}