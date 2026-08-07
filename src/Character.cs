using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using ValleyTalk;
using StardewValley;
using System.Threading.Tasks;
using Microsoft.Xna.Framework.Content;
using StardewModdingAPI.Events;
using System.Threading;

namespace ValleyTalk;

public class Character
{
    private BioData _bioData;

    private static readonly Dictionary<string,TimeSpan> filterTimes = new() { { "House", TimeSpan.Zero }, { "Action", TimeSpan.Zero }, { "Received Gift", TimeSpan.Zero }, { "Given Gift", TimeSpan.Zero }, { "Editorial", TimeSpan.Zero }, { "Gender", TimeSpan.Zero }, { "Question", TimeSpan.Zero } };
    private DialogueFile dialogueData;
    private Season? _sampleCacheSeason;
    private int? _sampleCacheDay;
    private int? _sampleCacheHeartLevel;
    private DialogueValue[] _sampleCache;
    private StardewTime _historyCutoff;
    private WorldDate _historyCutoffCacheDate;

    public NPC StardewNpc { get; internal set; }
    public List<string> ValidPortraits { get; internal set; }
    private readonly Dictionary<string,string> HistoryEvents = new()
    {
        { "cc_Bus", Util.GetString("cc_Bus_Repaired") },
        { "cc_Boulder", Util.GetString("cc_Boulder_Removed") },
        { "cc_Bridge", Util.GetString("cc_Bridge") },
        { "cc_Complete", Util.GetString("cc_Complete") },
        { "cc_Greenhouse", Util.GetString("cc_Greenhouse") },
        { "cc_Minecart", Util.GetString("cc_Minecart") },
        { "wonIceFishing", Util.GetString("wonIceFishing") },
        { "wonGrange", Util.GetString("wonGrange") },
        { "wonEggHunt", Util.GetString("wonEggHunt") }
    };

    public Character(string name, NPC stardewNpc)
    {
        Name = name;
        BioFilePath = $"{VtConstants.BiosPath}/{RemoveDotSuffixes(Name)}";
        StardewNpc = stardewNpc;

        ModEntry.SHelper.Events.Content.AssetRequested += (sender, e) =>
        {
            if (e.Name.IsEquivalentTo(BioFilePath))
            {
                e.LoadFrom(() => new BioData(), AssetLoadPriority.High);
            }
        };
        ModEntry.SHelper.Events.Content.AssetsInvalidated += (object sender, AssetsInvalidatedEventArgs e) =>
        {
            if (e.NamesWithoutLocale.Any(an => an.IsEquivalentTo(BioFilePath)))
            {
                _bioData = null;
            }
        };

    }

    private string RemoveDotSuffixes(string name)
    {
        var suffixCharacters = new char[] {'·', '•' ,'-' };
        var result = name.TrimEnd(suffixCharacters);
        return result;
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
            var manager = new ContentManager(Game1.content.ServiceProvider, Game1.content.RootDirectory);
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

    public async Task<string[]> CreateBasicDialogue(DialogueContext context)
    {
        string[] results = Array.Empty<string>();
        Prompts prompts = null;

        // 将 Prompts 创建放在 try-catch 中，防止打包失败导致崩溃
        try
        {
            prompts = new Prompts(context, this);

            // 注入玩家自定义记忆（高优先级，放在 System 最前面）
            // Extract the latest player input from chat history
            string lastPlayerInput = "";
            if (context.ChatHistory != null)
            {
                var lastPlayerLine = context.ChatHistory.LastOrDefault(x => x.IsPlayerLine);
                if (lastPlayerLine != null)
                    lastPlayerInput = lastPlayerLine.Text;
            }

            var memoryCtx = MemoryManager.Instance.GetSmartMemoryContext(Name, lastPlayerInput);
            if (!string.IsNullOrEmpty(memoryCtx))
            {
                prompts.System = memoryCtx + "\n\n" + prompts.System;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"[ValleyTalk] 提示词打包失败 (Prompts.cs 报错): {Name}");
            ModEntry.SMonitor.Log($"Prompts Error StackTrace: {ex}", StardewModdingAPI.LogLevel.Error);
            return new string[] { "..." };
        }

        const int maxRetryAttempts = 4;
        int timeoutSeconds = ModEntry.Config.QueryTimeout;
        int retryCount = 0;
        Exception lastException = null;
        LlmResponse result;

        for (int attempt = 0; attempt <= maxRetryAttempts; attempt++)
        {
            retryCount = attempt + 1;

            try
            {
                // Apply delay before retry (no delay for first attempt or second attempt)
                if (attempt >= 2)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5));
                    timeoutSeconds *= 2; // Double the timeout for each retry after the first
                }

                // Execute with timeout
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));

                string[] resultsInternal;

                try
                {
                    var inferenceTask = Llm.Instance.RunInference(
                        prompts.System,
                        $"{prompts.GameConstantContext}",
                        $"{prompts.NpcConstantContext}",
                        $"{prompts.CorePrompt}{prompts.Instructions}{prompts.Command}",
                        prompts.ResponseStart
                    );

                    result = await inferenceTask.WaitAsync(cts.Token);

                    if (result.IsSuccess)
                    {
                        // Apply relaxed validation if this is the second retry
                        resultsInternal = ProcessLines(result.Text, retryCount > 2).ToArray();
                    }
                    else
                    {
                        resultsInternal = Array.Empty<string>();
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, $"Error generating AI response for {StardewNpc.displayName}");
                    throw;
                }

                if (resultsInternal.Length > 0)
                {
                    results = resultsInternal;
                    break; // Success, exit retry loop
                }

                Log.Warning("No valid response generated from AI model.");
                if (result != null && !string.IsNullOrWhiteSpace(result.ErrorMessage))
                {
                    Log.Warning($"API Error Message: {result.ErrorMessage}");
                }
                else if (result != null && !string.IsNullOrWhiteSpace(result.Text))
                {
                    Log.Warning($"API Response: {result.Text}");
                }

                if (ModEntry.Config.Debug)
                {
                    Log.Debug($"Context:");
                    Log.Debug($"-------------------");
                    Log.Debug($"Name: {Name}");
                    Log.Debug($"Marriage: {context.Married}");
                    Log.Debug($"Birthday: {context.Birthday}");
                    Log.Debug($"Location: {context.Location}");
                    Log.Debug($"Weather: {string.Concat(context.Weather)}");
                    Log.Debug($"Time of Day: {context.TimeOfDay}");
                    Log.Debug($"Day of Season: {context.DayOfSeason}");
                    Log.Debug($"Gift: {context.Accept}");
                    Log.Debug($"Spouse Action: {context.SpouseAct}");
                    Log.Debug($"Random Action: {context.RandomAct}");
                    if (context.ScheduleLine != "")
                    {
                        Log.Debug($"Original Line: {context.ScheduleLine}");
                    }
                    Log.Debug($"-------------------");
                    Log.Debug($"System Prompt: {prompts.System}");
                    Log.Debug($"Game Constant Context: {prompts.GameConstantContext}");
                    Log.Debug($"NPC Constant Context: {prompts.NpcConstantContext}");
                    Log.Debug($"Core Prompt: {prompts.CorePrompt}");
                    Log.Debug($"Instructions: {prompts.Instructions}");
                    Log.Debug($"Command: {prompts.Command}");
                    Log.Debug($"Response Start: {prompts.ResponseStart}");
                    Log.Debug($"-------------------");
                    // ✅ 安全输出 resultsInternal
                    if (resultsInternal.Length > 0)
                    {
                        Log.Debug($"Results: {resultsInternal[0]}");
                        if (resultsInternal.Length > 1)
                        {
                            foreach (var resultLine in resultsInternal.Skip(1))
                            {
                                Log.Debug($"Response: {resultLine}");
                            }
                        }
                    }
                    else
                    {
                        Log.Debug("Results: (empty)");
                    }
                    Log.Debug("--------------------------------------------------");
                }

            }
            catch (Exception ex)
            {
                lastException = ex;

                // If this is the last attempt, don't continue
                if (attempt == maxRetryAttempts)
                {
                    break;
                }
            }
        }

        // Handle final result
        if (results.Length == 0 && lastException != null)
        {
            ModEntry.SMonitor.Log($"Error generating AI response for {Name}: {lastException}", StardewModdingAPI.LogLevel.Error);
            results = new string[] { "..." };
        }

        if (!string.IsNullOrWhiteSpace(prompts?.GiveGift) && results.Length > 0)
        {
            results[0] += $"[{prompts.GiveGift}]";
        }

        return results;
    }

    public IEnumerable<string> ProcessLines(string resultString, bool relaxedValidation = false)
    {
        try
        {
            var resultLines = resultString.Split('\n').AsEnumerable();
            // Remove any line breaks
            resultLines = resultLines.Select(x => x.Replace("\n", "").Replace("\r", "").Trim());
            resultLines = resultLines.Where(x => !string.IsNullOrWhiteSpace(x));

            // 优先查找以 '-' 开头的标准格式行
            var dialogueLine = resultLines.FirstOrDefault(x => x.StartsWith("-"));
            string dialogueContent = null;

            if (dialogueLine != null)
            {
                // 原有逻辑：处理标准的 '-' 格式
                dialogueContent = CommonCleanup(dialogueLine);
                dialogueContent = DialogueLineCleanup(dialogueContent, relaxedValidation);
            }
            else
            {
                // 【改进后的回退逻辑】找第一行不是以 '%' 开头的行作为对话内容
                var fallbackLine = resultLines.FirstOrDefault(x => !x.StartsWith("%"));
                if (!string.IsNullOrWhiteSpace(fallbackLine))
                {
                    var cleanedFirstLine = CommonCleanup(fallbackLine);
                    var cleanedDialogue = DialogueLineCleanup(cleanedFirstLine, relaxedValidation);
                    if (!string.IsNullOrWhiteSpace(cleanedDialogue))
                    {
                        dialogueContent = cleanedDialogue;
                        Log.Debug($"使用了回退逻辑处理回复。原始行: {fallbackLine}");
                    }
                }
            }

            // 如果经过上述步骤仍没有有效的对话内容，则返回空
            if (string.IsNullOrWhiteSpace(dialogueContent))
            {
                return Array.Empty<string>();
            }

            // 后续处理选项行（以 '%' 开头）的逻辑保持不变
            var responseLines = resultLines
                .Where(x => x.StartsWith("%"))
                .Select(x => CommonCleanup(x))
                .Select(x => ResponseLineCleanup(x))
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToList();

            // 如果选项少于2个，则不作为选项
            if (responseLines.Count < 2)
            {
                responseLines.Clear();
            }

            // 组合最终结果
            var finalResult = new List<string> { dialogueContent };
            finalResult.AddRange(responseLines);
            return finalResult;
        }
        catch (Exception ex)
        {
            Log.Error($"ProcessLines 发生异常: {ex.Message}\n{ex.StackTrace}");
            return Array.Empty<string>();
        }
    }

    private string CommonCleanup(string line)
    {
        if (string.IsNullOrEmpty(line))
            return string.Empty;

        // 移除前导/尾随符号
        line = line.Trim().TrimStart('-', ' ', '"', '%');
        line = line.TrimEnd('"');

        // 安全移除 #$b# 或 #$e# 前缀/后缀
        if (line.StartsWith("#$b#") && line.Length >= 4)
            line = line[4..];
        if (line.EndsWith("#$b#") && line.Length >= 4)
            line = line[..^4];
        if (line.StartsWith("#$e#") && line.Length >= 4)
            line = line[4..];
        if (line.EndsWith("#$e#") && line.Length >= 4)
            line = line[..^4];

        // 移除所有引号
        line = line.Replace("\"", "");
        return line;
    }

    private string DialogueLineCleanup(string line, bool relaxedValidation = false)
    {
        if (string.IsNullOrWhiteSpace(line)) return string.Empty;

        // 1. 规范化基础格式
        line = line.Replace("$e", "#$e").Replace("$b", "#$b");
        line = line.Replace("##$e", "#$e").Replace("##$b", "#$b");
        line = line.Replace("#$c .5#", "");
        line = line.Replace("@@", "@");

        // 2. 清理合法的表情标记（如 #$h 转为 $h）
        if (ValidPortraits != null)
        {
            foreach (var indicator in ValidPortraits)
            {
                line = line.Replace($"#${indicator}", $"${indicator}");
            }
        }

        // 3. 【优化】使用正则清理非法 '$' 标记，但只处理后面跟字母的情况
        try
        {
            // 只匹配 $ 后面跟着字母的情况，避免误删 $c、$e、$b 等
            line = System.Text.RegularExpressions.Regex.Replace(line, @"\$([a-zA-Z]+)", match =>
            {
                string val = match.Groups[1].Value;
                // 保留合法的：e, c, b 以及 ValidPortraits 中的值
                if (val == "e" || val == "c" || val == "b" || (ValidPortraits != null && ValidPortraits.Contains(val)))
                {
                    return match.Value; // 保留合法的
                }
                return ""; // 移除不合法的
            });
        }
        catch (Exception ex)
        {
            Log.Warning($"Regex cleanup error in DialogueLineCleanup: {ex.Message}");
        }

        line = line.Trim();
        var elements = line.Split('#');

        // 4. 处理超长文本
        if (elements.Any(x => x.Length > 200 && !relaxedValidation))
        {
            List<string> newElements = new();
            foreach (var element in elements)
            {
                if (element.Length <= 200)
                {
                    newElements.Add(element);
                }
                else
                {
                    string remainder = element;
                    string indicator = "";

                    // 【优化】安全提取末尾表情符
                    if (remainder.Length > 2 && ValidPortraits != null)
                    {
                        int lastDollar = remainder.LastIndexOf('$');
                        // 确保 $ 存在，且不是最后一个字符，且后面跟的是合法肖像
                        if (lastDollar >= 0 && lastDollar < remainder.Length - 1)
                        {
                            string possibleIndicator = remainder.Substring(lastDollar + 1);
                            if (ValidPortraits.Contains(possibleIndicator))
                            {
                                indicator = remainder.Substring(lastDollar);
                                remainder = remainder.Substring(0, lastDollar);
                            }
                        }
                    }

                    while (remainder.Length > 200 - indicator.Length)
                    {
                        int maxChunkLen = Math.Min(200 - indicator.Length, remainder.Length);
                        var elementStart = remainder.Substring(0, maxChunkLen);

                        // 支持中文标点
                        var lastPeriod = elementStart.LastIndexOfAny(new char[] { '.', '!', '?', '。', '！', '？' });

                        if (lastPeriod > 0 && lastPeriod < elementStart.Length - 1)
                        {
                            newElements.Add(remainder.Substring(0, lastPeriod + 1) + indicator);
                            remainder = remainder.Substring(lastPeriod + 1).Trim();
                        }
                        else
                        {
                            // 强制按最大安全长度截断
                            newElements.Add(elementStart + indicator);
                            remainder = remainder.Length > maxChunkLen ? remainder.Substring(maxChunkLen).Trim() : string.Empty;
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(remainder))
                    {
                        newElements.Add(remainder + indicator);
                    }
                }
            }

            if (newElements.Any(x => x.Length > 200 && !relaxedValidation))
            {
                return string.Empty;
            }
            elements = newElements.ToArray();
        }

        // 5. 补全末尾标点（支持中文）
        if (ModEntry.FixPunctuation)
        {
            for (int i = 0; i < elements.Length; i++)
            {
                var element = elements[i];
                var dollarIndex = element.IndexOf('$');
                // 【优化】使用更安全的方式分割
                string upToDollar;
                string rest = "";
                if (dollarIndex >= 0 && dollarIndex < element.Length)
                {
                    upToDollar = element.Substring(0, dollarIndex);
                    rest = element.Substring(dollarIndex);
                }
                else
                {
                    upToDollar = element;
                }

                upToDollar = upToDollar.Trim();
                if (upToDollar.Length > 0 &&
                    !upToDollar.EndsWith(".") && !upToDollar.EndsWith("!") && !upToDollar.EndsWith("?") &&
                    !upToDollar.EndsWith("。") && !upToDollar.EndsWith("！") && !upToDollar.EndsWith("？"))
                {
                    elements[i] = upToDollar + "." + rest;
                }
            }
            line = string.Join("#", elements);
        }

        return line;
    }

    private string ResponseLineCleanup(string line)
    {
        // Remove any hashes
        line = line.Replace("#", "");
        // If the string contains any commands preceded by a $, remove them
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == '$')
            {
                if (i + 1 < line.Length)
                {
                    line = line.Remove(i, 2);
                }
                else
                {
                    line = line.Remove(i, 1);
                }
            }
        }
        if (line.Contains('@'))
        {
            var farmerName = Game1.player.Name;
            line = line.Replace("@", farmerName);
        }
        line = line.Trim();
        // If the line doesn't end with a sentence end punctuation, add a period
        if (ModEntry.FixPunctuation && !line.EndsWith(".") && !line.EndsWith("!") && !line.EndsWith("?"))
        {
            line += ".";
        }
        if (line.Length > 90)
        {
            //Log.Debug("Long line detected in AI response.  Returning nothing.");
            return string.Empty;
        }
        return line;
    }

    internal IEnumerable<Tuple<StardewTime, IHistory>> EventHistorySample()
    {
        var allPreviousActivities = Game1.getPlayerOrEventFarmer().previousActiveDialogueEvents.First();
        var previousActivites = allPreviousActivities.Where(x => HistoryEvents.ContainsKey(x.Key) && (x.Value < 112 || x.Value % 112 == 0)).ToList();

        var newHistory = DialogueHistoryManager.Instance.GetHistory(Name)
            .Select(e => new Tuple<StardewTime, IHistory>(e.Timestamp, new DialogueHistoryAdapter(e)));

        var fullHistory = newHistory.Concat(previousActivites.Select(x => MakeActivityHistory(x)));
        if (!fullHistory.Any())
        {
            return Array.Empty<Tuple<StardewTime, IHistory>>();
        }
        if (Game1.Date != _historyCutoffCacheDate)
        {
            _historyCutoff = fullHistory.OrderBy(x => x.Item1).TakeLast(20).FirstOrDefault()?.Item1;
            _historyCutoffCacheDate = Game1.Date;
        }
        return fullHistory.Where(x => x.Item1.After(_historyCutoff)).OrderBy(x => x.Item1);
    }

    private Tuple<StardewTime, IHistory> MakeActivityHistory(KeyValuePair<string, int> x)
    {
        var timeNow = new StardewTime(Game1.year, Game1.season, Game1.dayOfMonth, Game1.timeOfDay);
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
