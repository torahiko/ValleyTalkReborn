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

    private static readonly Dictionary<string,TimeSpan> filterTimes = new() { { "House", TimeSpan.Zero }, { "Action", TimeSpan.Zero }, { "Received Gift", TimeSpan.Zero }, { "Given Gift", TimeSpan.Zero }, { "Editorial", TimeSpan.Zero }, { "Gender", TimeSpan.Zero }, { "Question", TimeSpan.Zero } };
    private DialogueFile dialogueData;
    private Season? _sampleCacheSeason;
    private int? _sampleCacheDay;
    private int? _sampleCacheHeartLevel;
    private DialogueValue[] _sampleCache;

    public NPC StardewNpc { get; internal set; }
    public List<string> ValidPortraits { get; internal set; }
    private readonly Dictionary<string,string> HistoryEvents = new()
    {
        // { "cc_Bus", Util.GetString("cc_Bus_Repaired") },
        // { "cc_Boulder", Util.GetString("cc_Boulder_Removed") },
        // { "cc_Bridge", Util.GetString("cc_Bridge") },
        // { "cc_Complete", Util.GetString("cc_Complete") },
        // { "cc_Greenhouse", Util.GetString("cc_Greenhouse") },
        // { "cc_Minecart", Util.GetString("cc_Minecart") },
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
                foreach(var langSuffix in ModEntry.LanguageFileSuffixes)
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
                foreach(var langSuffix in ModEntry.LanguageFileSuffixes)
                {
                    var path = $"{assetName}{langSuffix}";
                    var marriedDialogue = manager.Load<Dictionary<string, string>>(path);
                    if (marriedDialogue != null)
                    {
                        foreach (var dialogue in marriedDialogue)
                        {
                            canonDialogue.Add($"M_{dialogue.Key}", dialogue.Value);
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
            canonDialogue = StardewNpc.Dialogue;
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
            var context = new DialogueContext(dialogue.Key);
            var value = new DialogueValue(dialogue.Value);
            if (value is DialogueValue)
            {
                DialogueData.Add("Base",context, value);
            }
        }
    }

    private void CheckBio()
    {
        if (_bioData != null && ( _bioData.Biography.Length > 0 || _bioData.Missing))
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
            ModEntry.SMonitor.Log($"No bio file found for {Name}.", StardewModdingAPI.LogLevel.Warn);
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
        // Pick 20 most relevant dialogue entries
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
        _sampleCache = firstStep
                    .SelectMany(x => x.Value.AllValues)
                    .Take(20).ToArray()
                    ?? Array.Empty<DialogueValue>();
        return _sampleCache;
    }

    internal IEnumerable<Tuple<StardewTime, IHistory>> EventHistorySample()
    {
        var allPreviousActivities = Game1.getPlayerOrEventFarmer().previousActiveDialogueEvents.First();
        var previousActivites = allPreviousActivities.Where(x => HistoryEvents.ContainsKey(x.Key) && (x.Value < 112 || x.Value % 112 == 0)).ToList();

        int limit = ModEntry.Config?.MemoryRecentCount > 0 ? ModEntry.Config.MemoryRecentCount : 20;

        var newHistory = DialogueHistoryManager.Instance.GetHistory(Name)
            .Where(e => !e.IsConsumed)
            .TakeLast(limit)
            .Select(e => new Tuple<StardewTime, IHistory>(e.Timestamp, new DialogueHistoryAdapter(e)));

        var fullHistory = newHistory.Concat(previousActivites.Select(x => MakeActivityHistory(x)));
        return fullHistory.OrderBy(x => x.Item1);
    }

    private Tuple<StardewTime, IHistory> MakeActivityHistory(KeyValuePair<string, int> x)
    {
        // 修复：将 Game1.season 强制转换为 ValleyTalk.Season
        var timeNow = new StardewTime(Game1.year, (Season)Game1.season, Game1.dayOfMonth, Game1.timeOfDay);
        var targetDate = timeNow.AddDays(-x.Value);
        return new(targetDate, new ActivityHistory(x.Key));
    }

    internal bool SpokeJustNow()
    {
        var history = DialogueHistoryManager.Instance.GetHistory(Name);
        if (!history.Any())
        {
            return false;
        }
        var lastEntry = history.Last();
        return lastEntry.Timestamp.IsJustNow();
    }

    internal void ClearConversationHistory()
    {
        DialogueHistoryManager.Instance.ClearHistory(Name);
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
    public ConcurrentBag<Tuple<DialogueContext,DialogueValue>> CreatedDialogue { get; private set; } = new ();
    internal BioData Bio
    {
        get
        { 
            CheckBio(); 
            return _bioData; 
        }
    }

    public List<string> PossiblePreoccupations { get; internal set;}
    public string Preoccupation { get; internal set; }
    public WorldDate PreoccupationDate { get; internal set; }
}