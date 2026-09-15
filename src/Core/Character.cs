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

    // ★ Bios 有效性门禁的实质 Biography 长度阈值（含）：低于此长度视为无实质设定，回退原版对话
    private const int MinValidBioLength = 10;

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
        // 🌟 浅拷贝 NPC 对话数据，避免后续合并 Bio.Dialogue 时污染游戏运行时的原生字典
        Dictionary<string, string> canonDialogue = StardewNpc.Dialogue != null
            ? new Dictionary<string, string>(StardewNpc.Dialogue)
            : new Dictionary<string, string>();

        // 合并 Bio 自定义注入对白
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

        // ★ 核心修复：CP 未提供实质 Biography（含 LoadFrom 兜底空对象）时，
        // 明确标记 Missing = true，杜绝空设定进入 AI 管线产生幻觉
        bool hasSubstantiveBio = !string.IsNullOrWhiteSpace(bioData.Biography)
                                 && bioData.Biography.Trim().Length > MinValidBioLength;
        _bioData.Missing = !hasSubstantiveBio;
        if (_bioData.Missing)
        {
            ModEntry.SMonitor?.Log(
                $"[Character] {Name}: bio 缺失或无实质 Biography（长度 <= {MinValidBioLength}），AI 对话已禁用，回退原版对话。",
                StardewModdingAPI.LogLevel.Trace);
            return;
        }

        ValidPortraits = new List<string>() { "h", "s", "l", "a" };
        ValidPortraits.AddRange(_bioData.ExtraPortraits.Keys);

        PossiblePreoccupations = new List<string>(_bioData.Preoccupations);
        PossiblePreoccupations.AddRange(GetLovedAndHatedGiftNames());
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

    /// <summary>
    /// 派生只读属性：该角色是否具备可用于 AI 管线的有效 Bios。
    /// 用局部变量缓存 Bio，避免重复触发 CheckBio（CheckBio 幂等但含 IO/反射，
    /// 首访后 Missing 标记使守卫短路，后续为 O(1) 字段读取）。
    /// </summary>
    public bool HasValidBio
    {
        get
        {
            var bio = Bio;
            if (bio == null || bio.Missing) return false;
            return !string.IsNullOrWhiteSpace(bio.Biography)
                   && bio.Biography.Trim().Length > MinValidBioLength;
        }
    }

    public List<string> PossiblePreoccupations { get; internal set; }
    public string Preoccupation { get; internal set; }
    public WorldDate PreoccupationDate { get; internal set; }

    /// <summary>
    /// 心事日缓存的阶段键（Memory，不写存档）。
    /// "GLOBAL" = 全局池缓存；其他值 = 阶段池内容签名 string.Join("|", pool)。
    /// 与 Preoccupation / PreoccupationDate 构成复合缓存键，档位变更即失效（T6-Q1b）。
    /// </summary>
    public string PreoccupationStageKey { get; internal set; }
}