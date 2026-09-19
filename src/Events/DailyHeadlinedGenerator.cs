using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace ValleytalkReborn;

/// <summary>
/// Daily Headline Generator — "Pelican Town Morning Gazette"
///
/// Produces Track 1 (GlobalGossip) entries per day using a strict priority cascade:
///   S-tier: Major life events (Marriage, Divorce, Child, Krobus, Community Center).
///   A-tier: Extraordinary farmer feats (RichDay, Slayers, Trash cans, etc.).
///   B-tier: World & regional news (Pre-written fallback or LLM generation).
/// </summary>
internal static class DailyHeadlinedGenerator
{
    private static bool _initialized          = false;
    private static bool _llmHeadlineRequested = false;
    private static bool _llmHeadlineDelivered = false;

    private static bool IsZh =>
        LocalizedContentManager.CurrentLanguageCode == LocalizedContentManager.LanguageCode.zh;

    // ── Life-event snapshot ───────────────────────────────────────
    private static string _prevSpouse     = string.Empty;
    private static int    _prevChildren   = 0;
    private static bool   _prevKrobus     = false;
    private static bool   _prevCCComplete = false;

    // ── LLM request timeout (seconds) ────────────────────────────
    private const int LlmHeadlineTimeoutSeconds = 25;

    // ─────────────────────────────────────────────────────────────
    public static void Initialize()
    {
        if (_initialized || ModEntry.SHelper == null) return;
        ModEntry.SHelper.Events.GameLoop.DayStarted   += OnDayStarted;
        ModEntry.SHelper.Events.GameLoop.DayEnding    += OnDayEnding;
        ModEntry.SHelper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
        _initialized = true;
        ModEntry.SMonitor?.Log("[DailyHeadlineGenerator] Initialized.", LogLevel.Debug);
    }

    public static void Cleanup()
    {
        if (!_initialized || ModEntry.SHelper == null) return;
        ModEntry.SHelper.Events.GameLoop.DayStarted   -= OnDayStarted;
        ModEntry.SHelper.Events.GameLoop.DayEnding    -= OnDayEnding;
        ModEntry.SHelper.Events.GameLoop.UpdateTicked -= OnUpdateTicked;
        _initialized = false;
    }

    // ─────────────────────────────────────────────────────────────
    //  Game-loop handlers
    // ─────────────────────────────────────────────────────────────
    private static void OnDayStarted(object sender, DayStartedEventArgs e)
    {
        if (!ModEntry.Config.EnablePerceptionSystem) return;
        _llmHeadlineRequested = false;
        _llmHeadlineDelivered = false;
        try
        {
            TryInjectLifeEvent();
            InjectFallbackWorldNews();
        }
        catch (Exception ex)
        {
            ModEntry.SMonitor?.Log(
                $"[DailyHeadlineGenerator] OnDayStarted error: {ex.Message}", LogLevel.Warn);
        }
    }

    private static void OnUpdateTicked(object sender, UpdateTickedEventArgs e)
    {
        if (!ModEntry.Config.EnablePerceptionSystem) return;
        if (_llmHeadlineRequested) return;
        if (!Context.IsWorldReady) return;
        if (Game1.timeOfDay < 720) return;

        _llmHeadlineRequested = true;
        _ = RequestLlmHeadlineAsync();
    }

    private static void OnDayEnding(object sender, DayEndingEventArgs e)
    {
        if (!ModEntry.Config.EnablePerceptionSystem) return;
        try
        {
            TryInjectExtremeActivity();
        }
        catch (Exception ex)
        {
            ModEntry.SMonitor?.Log(
                $"[DailyHeadlineGenerator] OnDayEnding error: {ex.Message}", LogLevel.Warn);
        }
    }

    // ─────────────────────────────────────────────────────────────
    //  B-tier — Fallback hardcoded world news (DayStarted)
    // ─────────────────────────────────────────────────────────────
    private static void InjectFallbackWorldNews()
    {
        bool isZh = IsZh;
        var pool  = isZh ? WorldNewsPool_ZH : WorldNewsPool_EN;
        if (pool.Length == 0) return;

        ulong seed = Game1.uniqueIDForThisGame ^ (ulong)(Game1.stats.DaysPlayed * 2654435761u);
        int idx    = (int)(seed % (uint)pool.Length);

        PerceptionManager.Instance.RecordGossip("WorldNews", pool[idx], lifetimeHours: 24);
        ModEntry.SMonitor?.Log(
            $"[DailyHeadlineGenerator] B-tier fallback injected (idx={idx}/{pool.Length}).",
            LogLevel.Debug);
    }

    // ─────────────────────────────────────────────────────────────
    //  B-tier — LLM headline request (7:20 AM)
    // ─────────────────────────────────────────────────────────────
    private static async Task RequestLlmHeadlineAsync()
    {
        try
        {
            ModEntry.SMonitor?.Log(
                "[DailyHeadlineGenerator] Requesting LLM world headline...", LogLevel.Debug);

            bool   isZh   = IsZh;
            string season = Game1.currentSeason;
            int    day    = Game1.dayOfMonth;
            int    year   = Game1.year;

            string systemPrompt = isZh
                ? BuildLlmSystemPrompt_ZH()
                : BuildLlmSystemPrompt_EN();

            string userPrompt = isZh
                ? $"时间：第{year}年{SeasonZH(season)}第{day}天。请撰写一条鹈鹕镇早间电报头条，用 <headline> 与 </headline> 包裹。"
                : $"Date: {season} {day}, Year {year}. Write one morning headline about town life, trade, or war news. Wrap strictly inside <headline> and </headline>.";

            using var cts = new System.Threading.CancellationTokenSource(
                TimeSpan.FromSeconds(LlmHeadlineTimeoutSeconds));

            var inferenceTask = Llm.Instance.RunInference(
                systemPrompt,
                "",          // history / userContent
                "",          // npcCacheString
                userPrompt   // corePrompt
            );

            LlmResponse result = await inferenceTask.WaitAsync(cts.Token);

            if (!result.IsSuccess || string.IsNullOrWhiteSpace(result.Text))
            {
                ModEntry.SMonitor?.Log(
                    $"[DailyHeadlineGenerator] LLM headline failed: {result.ErrorMessage}. Fallback retained.",
                    LogLevel.Debug);
                return;
            }

            string headline = CleanLlmHeadline(result.Text);
            if (string.IsNullOrWhiteSpace(headline) || headline.Length < 10)
            {
                ModEntry.SMonitor?.Log(
                    $"[DailyHeadlineGenerator] LLM returned invalid or empty headline after cleanup (Raw: '{result.Text}'). Fallback retained.",
                    LogLevel.Debug);
                return;
            }

            AsyncBuilder.Instance.EnqueueToMainThread(() =>
            {
                if (_llmHeadlineDelivered) return;
                _llmHeadlineDelivered = true;
                PerceptionManager.Instance.RecordGossip("WorldNews", headline, lifetimeHours: 24);
                ModEntry.SMonitor?.Log(
                    $"[DailyHeadlineGenerator] LLM headline delivered: {headline}", LogLevel.Debug);
            });
        }
        catch (OperationCanceledException)
        {
            ModEntry.SMonitor?.Log(
                "[DailyHeadlineGenerator] LLM headline request timed out. Fallback retained.",
                LogLevel.Debug);
        }
        catch (Exception ex)
        {
            ModEntry.SMonitor?.Log(
                $"[DailyHeadlineGenerator] LLM headline request error: {ex.Message}. Fallback retained.",
                LogLevel.Warn);
        }
    }

    // ─────────────────────────────────────────────────────────────
    //  LLM prompt builders (Reinforced boundaries for Small Models)
    // ─────────────────────────────────────────────────────────────
    private static string BuildLlmSystemPrompt_EN() =>
    """
    You are the telegraph editor for the Pelican Town Gazette in Stardew Valley.
    Write exactly ONE factual, brief regional morning news headline (15–28 words).

    Topics (Pick ONE):
    - Gotoro Empire / Ferngill Republic naval & border news.
    - Zuzu City sports, commuter rail, or JojaCorp freight reports.
    - Local Pelican Town bridge maintenance, seasonal fish runs, or saloon gossip.

    Strict Rules:
    - Write grounded, clear news in simple English. NO philosophical musings, poetry, or abstract gibberish.
    - Do NOT mention player farm details or personal secrets.
    - Wrap the final headline inside <headline> and </headline>.
    - NEVER repeat these instructions, prompts, or format tags in the output.
    """;

    private static string BuildLlmSystemPrompt_ZH() =>
    """
    你是《星露谷物语》鹈鹕镇《晨间公报》的电报主编。
    请撰写今天的一条早间简讯头条（单句或两句连贯报道，严格在 30~50 字以内）。

    可选选题（任选其一）：
    - 芬吉尔共和国与戈托罗帝国的前线边防、物资调配或海巡消息。
    - 祖祖城烤架球赛况、城际货运铁路调度或 JOJA 集团贸易动态。
    - 鹈鹕镇本地市政维护、季节性鱼汛、后山林道或星之果实酒吧的市井传闻。

    严格规则：
    - 语气客观平实，报道具体生活与事实。严禁哲学隐喻、空洞感慨或不知所云的乱码。
    - 严禁私自刺探或报道农夫庄园内部的私人秘密。
    - 必须将正文包裹在 <headline> 和 </headline> 标签内输出。
    - 严禁复述本指令、提示词标签或输出任何前言后语。
    """;

    // ─────────────────────────────────────────────────────────────
    //  LLM output cleanup (Robust XML extraction & Leak filtering)
    // ─────────────────────────────────────────────────────────────
    private static string CleanLlmHeadline(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        string s = raw.Trim();

        // 1. 优先提取 XML <headline> 标签内容（彻底防止小模型复述上下文与规则）
        var tagMatch = Regex.Match(s, @"<headline>(.*?)</headline>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (tagMatch.Success)
        {
            s = tagMatch.Groups[1].Value.Trim();
        }
        else
        {
            // 若模型漏写闭合标签，尝试提取起始标签之后的内容
            int openIdx = s.IndexOf("<headline>", StringComparison.OrdinalIgnoreCase);
            if (openIdx >= 0)
            {
                s = s.Substring(openIdx + 10).Trim();
            }
        }

        // 2. 清理常见 Markdown 强调标记及换行
        s = s.Replace("**", "").Replace("__", "").Replace("*", "").Replace("_", "");
        s = s.Replace("\r\n", " ").Replace("\n", " ").Trim();

        // 3. 过滤常见的前缀泄漏
        string[] prefixes =
        {
            "Headline:", "headline:", "News:", "news:", "Title:", "title:", 
            "Breaking News:", "Report:", "Gazette:", "头条：", "新闻：", "早报：", "简讯："
        };

        foreach (var prefix in prefixes)
        {
            if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                s = s.Substring(prefix.Length).TrimStart();
            }
        }

        // 4. 清理首尾列表符与引号
        s = s.TrimStart('-', '•', '·', '>', ' ', '"', '\'');
        s = s.Trim('"', '\'', '“', '”', '「', '」', '『', '』');

        // 5. 坍缩多余空格
        s = Regex.Replace(s, @"\s{2,}", " ").Trim();

        // 6. 过滤包含明显提示词泄漏/元指令复述的乱码内容
        if (s.Contains("Emote bubble", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("Turn tags", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("SystemPrompt", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("Pelican Town Morning Gazette in the world", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return s;
    }

    private static string SeasonZH(string season) => season switch
    {
        "spring" => "春季",
        "summer" => "夏季",
        "fall"   => "秋季",
        "winter" => "冬季",
        _        => season
    };

    // ─────────────────────────────────────────────────────────────
    //  S-tier — Life Events
    // ─────────────────────────────────────────────────────────────
    private static void TryInjectLifeEvent()
    {
        var player = Game1.player;
        if (player == null) return;

        bool   isZh     = IsZh;
        string farmer   = player.Name;
        string curSpouse = player.spouse ?? string.Empty;
        int    curKids   = player.getChildren()?.Count ?? 0;
        bool   curKrobus = player.hasRoommate();
        bool   curCC     = IsCommunityCenter();
        string headline = null;

        // ── Community Center ─────────────────────────────────────
        if (curCC && !_prevCCComplete)
        {
            headline = isZh
                ? PerceptionManager.PickVariant(new[]
                  {
                      "举镇同庆！经过漫长的修缮岁月，社区中心终于在昨日正式竣工重开，刘易斯镇长亲自出席了剪彩仪式。",
                      "鹈鹕镇历史性的一天——昨日，社区中心修缮完成，大门重新向所有居民敞开。",
                  })
                : PerceptionManager.PickVariant(new[]
                  {
                      "Pelican Town is celebrating — the Community Center has finally been fully restored. Mayor Lewis cut the ribbon himself.",
                      "Historic news: the Community Center restoration is complete. After years of work, it's open to everyone again.",
                  });
        }
        // ── Divorce ──────────────────────────────────────────────
        else if (!string.IsNullOrEmpty(_prevSpouse) && string.IsNullOrEmpty(curSpouse) && !curKrobus)
        {
            string ex = _prevSpouse;
            headline = isZh
                ? PerceptionManager.PickVariant(new[]
                  {
                      $"今早消息传遍全镇：{farmer}与{ex}昨日正式办理了离婚，两人已回归普通邻里关系。",
                      $"鹈鹕镇悄悄传开了——{farmer}和{ex}的婚姻走到了尽头。",
                      $"据说{farmer}与{ex}昨日正式离婚，镇上的人都没想到会走到这一步。",
                  })
                : PerceptionManager.PickVariant(new[]
                  {
                      $"Word spread through town this morning: {farmer} and {ex} have officially divorced.",
                      $"Pelican Town is buzzing — {farmer} and {ex}'s marriage has come to an end.",
                      $"Apparently {farmer} and {ex} finalized their divorce yesterday. Nobody saw it coming.",
                  });
        }
        // ── New marriage ─────────────────────────────────────────
        else if (!string.IsNullOrEmpty(curSpouse) && !curKrobus 
                                                  && !string.Equals(_prevSpouse, curSpouse, StringComparison.OrdinalIgnoreCase))
        {
            headline = isZh
                ? PerceptionManager.PickVariant(new[]
                  {
                      $"整个鹈鹕镇都沸腾了！{farmer}与{curSpouse}昨日在小镇广场举行了婚礼，喜气洋洋。",
                      $"恭喜！{farmer}与{curSpouse}正式完婚，镇上居民纷纷送上了最诚挚的祝福。",
                      $"大喜讯：{farmer}和{curSpouse}结婚了！据说婚礼现场笑声不断，热闹非凡。",
                  })
                : PerceptionManager.PickVariant(new[]
                  {
                      $"The whole town is celebrating — {farmer} and {curSpouse} got married yesterday!",
                      $"Big news: {farmer} and {curSpouse} are officially married. Pelican Town turned out in full.",
                      $"Everyone's talking about the wedding — {farmer} and {curSpouse} tied the knot. What a day.",
                  });
        }
        // ── Krobus moves in ──────────────────────────────────────
        else if (!_prevKrobus && curKrobus)
        {
            headline = isZh
                ? PerceptionManager.PickVariant(new[]
                  {
                      $"消息灵通人士透露：科罗布斯已搬进了{farmer}的农舍，成为其同居室友，镇上对这个奇特组合议论纷纷。",
                      $"据说下水道的科罗布斯现在和{farmer}住在一起了，大家对这对奇特室友组合看法不一。",
                  })
                : PerceptionManager.PickVariant(new[]
                  {
                      $"Word is that Krobus has moved in with {farmer}. An unconventional arrangement, to say the least.",
                      $"Curious news — {farmer} now shares the farmhouse with Krobus from the sewers. The town is intrigued.",
                  });
        }
        // ── New child ────────────────────────────────────────────
        else if (curKids > _prevChildren)
        {
            headline = isZh
                ? PerceptionManager.PickVariant(new[]
                  {
                      $"农场迎来了新生命！{farmer}家的小宝宝昨日降生，全镇居民都在送上最诚挚的祝福。",
                      $"喜讯传遍鹈鹕镇：{farmer}的家庭又添新丁！大家都说这是小镇今年最暖心的消息。",
                  })
                : PerceptionManager.PickVariant(new[]
                  {
                      $"A new arrival at the farm! {farmer} welcomed a new baby yesterday. The whole town is sending well-wishes.",
                      $"Happy news — {farmer}'s family has grown. Pelican Town couldn't be more delighted.",
                  });
        }

        // ── Update snapshot ───────────────────────────────────────
        _prevSpouse     = curSpouse;
        _prevChildren   = curKids;
        _prevKrobus     = curKrobus;
        _prevCCComplete = curCC;

        if (headline == null) return;
        PerceptionManager.Instance.RecordGossip("LifeEvent", headline, lifetimeHours: 40);
        ModEntry.SMonitor?.Log("[DailyHeadlineGenerator] S-tier life event injected.", LogLevel.Debug);
    }

    // ─────────────────────────────────────────────────────────────
    //  A-tier — Extreme Activity (DayEnding, 50% flip)
    // ─────────────────────────────────────────────────────────────
    private static void TryInjectExtremeActivity()
    {
        var candidate = ExtremeActivityTracker.TryGetExtremeCandidate();
        if (candidate == null) return;

        ulong flipSeed = Game1.uniqueIDForThisGame ^ (ulong)(Game1.stats.DaysPlayed * 1234567891u);
        if ((flipSeed & 1UL) != 0)
        {
            ModEntry.SMonitor?.Log(
                $"[DailyHeadlineGenerator] A-tier [{candidate.Value.Key}] skipped (coin flip).",
                LogLevel.Debug);
            return;
        }

        bool isZh = IsZh;
        string template = isZh
            ? TranslateExtremeTemplate(candidate.Value.Key, candidate.Value.Template, candidate.Value.Context)
            : candidate.Value.Template;

        PerceptionManager.Instance.RecordGossip("ExtremeActivity", template, lifetimeHours: 20);
        ModEntry.SMonitor?.Log(
            $"[DailyHeadlineGenerator] A-tier [{candidate.Value.Key}] injected.", LogLevel.Debug);
    }

    // ─────────────────────────────────────────────────────────────
    //  Extreme template translation (EN → ZH, key-based)
    // ─────────────────────────────────────────────────────────────
    private static string TranslateExtremeTemplate(string key, string englishTemplate, string context = "")
    {
        string num = ExtractFirstNumber(englishTemplate);
        return key switch
        {
            "LegendaryFish" => PerceptionManager.PickVariant(new[]
            {
                "威利激动得语无伦次——农夫今天真的钓上了一条传说中的鱼王！据说他在码头大叫了好一会儿。",
                "消息传遍全镇：农夫今日竟然钓到了传说鱼王，威利说他从业这么多年还是头一次亲眼见到。",
                "艾利欧特说他亲眼目睹了那一刻——农夫把传说鱼王拖出了水面，威利当场差点晕过去。",
            }),
            "RichDay" => PerceptionManager.PickVariant(new[]
            {
                $"皮埃尔听到消息后不得不坐下来缓一缓——农夫今天单日入账超过了 {num} 金币。",
                $"古斯在柜台后面听到了那个数字，农夫今天居然赚了 {num} 金币，就一天。",
                $"刘易斯镇长震惊了。据报农夫今日进账 {num} 金币——整个山谷多少年没见过这种数字了。",
            }),
            "Slayer" => PerceptionManager.PickVariant(new[]
            {
                $"马龙摇着头，难以置信——农夫今天在矿井里消灭了 {num} 只怪物。这不叫务农，这叫打仗。",
                $"吉尔见过不少冒险者，但单日 {num} 次击杀？连他都不得不称赞。",
                $"冒险者公会里炸开了锅——据说农夫今天一口气清了 {num} 只矿井怪物。",
            }),
            "TrashCan" => PerceptionManager.PickVariant(new[]
            {
                $"乔治真的发火了——农夫今天翻遍了整整 {num} 个垃圾桶，一个都没放过。",
                $"海莉看起来极为崩溃。农夫今天大白天翻了 {num} 个垃圾桶，就在大家眼皮底下。",
                $"莱纳斯轻声说起这件事——今天 {num} 个垃圾桶。他说他理解，但邻居们不理解。",
            }),
            "DatingAnnouncement" => PerceptionManager.PickVariant(new[]
            {
                $"镇上都在传——农夫和{context}正式确立恋爱关系了！",
                $"皮埃尔笑着说，农夫最近给{context}送了一束花，看来好事将近。",
                $"消息灵通人士透露，农夫和{context}的关系已经不只是朋友了，整个鹈鹕镇都在议论。",
            }),
            _ => englishTemplate
        };
    }

    private static string ExtractFirstNumber(string text)
    {
        var sb = new System.Text.StringBuilder();
        bool inNum = false;
        foreach (char c in text)
        {
            if (char.IsDigit(c) || (inNum && c == ','))
            { sb.Append(c); inNum = true; }
            else if (inNum) break;
        }
        return sb.Length > 0 ? sb.ToString() : "?";
    }

    // ─────────────────────────────────────────────────────────────
    //  Community Center helper
    // ─────────────────────────────────────────────────────────────
    private static bool IsCommunityCenter()
    {
        try { return Game1.MasterPlayer?.mailReceived?.Contains("ccIsComplete") == true; }
        catch { return false; }
    }

    // ─────────────────────────────────────────────────────────────
    //  Hardcoded fallback pools
    // ─────────────────────────────────────────────────────────────
    private static readonly string[] WorldNewsPool_EN =
    {
        "The Ferngill Republic Daily reports that naval vessels intercepted a Gotoro supply fleet near the southern Gem Sea. Tensions remain high.",
        "Parliament is debating tighter maritime trade restrictions following skirmishes along the Gem Sea. Regional merchants worry about spice imports.",
        "A Republic spokesperson confirmed another group of repatriated soldiers has arrived in Zuzu City — welcome news for families across the valley.",
        "Border patrols along the southern coast have been reinforced this week per orders from the Ministry of Defense.",
        "Diplomatic envoys report that back-channel peace talks with Gotoro officials have stalled over southern trade lanes.",
        "A merchant traveling out of the border zones reports severe shortages of basic iron and grain inside the Gotoro Empire.",
        "The Ferngill Senate is reviewing a bill to grant rural land plots to veterans of the Gem Sea conflict.",
        "Naval artillery drills near the southern cape have temporarily forced deep-sea fishing trawlers to adjust their routes.",
        "Republic customs officials seized an unregistered cargo vessel off the coast carrying contraband mineral ores.",
        "Veterans affairs representatives are touring regional towns this season to update disability pension ledgers.",
        "The Zuzu City Tunnelers secured a dramatic fourth-quarter victory in last night's gridball championship match, sparking wild celebrations.",
        "Rail freight passing through the northern mountain pass reported minor rockslide delays, briefly holding up cross-county shipments.",
        "A historic cinema in downtown Zuzu City is hosting a retrospective on black-and-white regional films this weekend.",
        "The regional agricultural board recognized a countryside cidery as the surprise gold medalist at the Zuzu City Food Fair.",
        "Commuter coaches traveling the main highway out of Zuzu City faced lengthy delays due to road resurfacing near the county line.",
        "Street buskers in Zuzu City's theater district are petitioning the municipal council over stricter evening acoustic noise limits.",
        "The Zuzu City Museum of Fine Art opened an exhibition featuring 19th-century pastoral landscapes from the surrounding valleys.",
        "Heavy fog rolling off the bay disrupted early morning ferry and cargo traffic across Zuzu Harbor.",
        "JojaCorp announced record quarterly revenues, crediting expanded distribution networks for its flagship canned soda line.",
        "Reports indicate JojaCorp is attempting to purchase warehouse real estate near the Grampleton junction to streamline bulk freight.",
        "Former Joja warehouse laborers in Zuzu City filed a grievance over mandatory double shifts during the holiday logistics push.",
        "An investigative report claims JojaCorp executives are lobbying regional councils to lower environmental oversight near logging tracts.",
        "Local independent grocers voiced concerns after JojaMart launched an aggressive coupon mailer campaign across neighboring townships.",
        "Smelting coal and iron prices on the Grampleton commodities market saw a modest uptick, prompting regional smiths to adjust quotes.",
        "Orchard yields from neighboring counties are reportedly running high, which may soften dried fruit prices before winter.",
        "Traveling merchants heading east toward Castle Village reported muddy washouts along the dirt toll roads.",
        "The Ferngill Agricultural Council noted that rural farm export quotas for root vegetables showed a modest regional surplus.",
        "Timber mills in the hills are operating at peak capacity, though transport wagons remain constrained by rough logging trails.",
        "A cold maritime front is pushing inland from the Gem Sea, prompting harbor watchmen to advise securing docked boats.",
        "Mountain lake anglers report large schools of largemouth bass moving into shallow waters as seasonal feeding picks up.",
        "Unusually warm coastal currents have pushed deep-water ocean species unusually close to the shoreline shoals.",
        "Regional weather stations recorded prolonged rainfall across the peaks, keeping river currents swift and swollen.",
        "The first crisp autumn frost settled over the higher mountain pastures slightly earlier than anticipated.",
        "A brilliant nighttime aurora was visible over the northern mountain ridges, drawing stargazers outside in the chill.",
        "Birdwatchers documented an elusive mountain hawk nesting along the foothills for the first time in years.",
        "Mayor Lewis confirmed the regional bridge restoration grant has cleared final paperwork, with structural timber shipments en route.",
        "A traveling folklore archivist was seen reviewing pioneer records at the library desk, inquiring about the valley's settlement era.",
        "The high forest trail behind the mountain lake has been cleared of fallen pine boughs, reopening the path for forage walks.",
        "Reports from the Adventurer's Guild suggest cavern monster migration has quieted down along the upper mine shafts this week.",
        "The Stardrop Saloon saw a lively crowd Friday evening, with Gus serving piping-hot stews well past midnight.",
        "Hikers near the mountain base spotted clusters of synchronized fireflies drifting through the ferns at dusk.",
        "Pierre's shop reported steady customer foot traffic following a seasonal bulk promotion on vegetable seeds."
    };

    private static readonly string[] WorldNewsPool_ZH =
    {
        "《芬吉尔共和国日报》报道：海军舰队昨日在宝石海南部截获一支戈托罗补给船队，沿海防线戒备森严。",
        "议会正就宝石海局势商讨收紧海上贸易管控，周边商贩对香料与布匹进口价格上涨颇感忧虑。",
        "共和国发言人证实，又一批遣返的退伍军人已抵达祖祖城——这对谷地不少挂念前线的家庭而言是莫大的宽慰。",
        "国防部发布通报，宝石海沿岸哨所本周已增派防卫巡逻，防备边境摩擦。",
        "外交使团透露，与戈托罗帝国关于南部通商航道的秘密交涉陷入僵局，双方暂未达成新协定。",
        "一名经边境关隘入境的客商称，戈托罗帝国腹地出现生活铁料与粮食紧缺的迹象。",
        "芬吉尔参议院正审议一项退伍军人安置草案，计划向参战老兵拨发定额乡村垦荒土地。",
        "宝石海南岬角附近的海防炮击演练影响了深海渔路，沿海渔民本周的远洋渔获略有减少。",
        "海关缉私巡逻艇在沿海暗礁区截获一艘未悬挂旗帜的货船，查没了一批违禁矿石原料。",
        "老兵福利公署代表本季正巡访周边市镇，核对抚恤记录与伤残津贴账目。",
        "祖祖城“隧洞人队”在昨夜的烤架球锦标赛最后一节完成达阵绝杀，狂欢的球迷通宵拥堵在酒馆街头。",
        "途经山谷北部山区的干线货运列车昨晨因边坡碎石短暂停运，跨县散货物流略有受阻。",
        "祖祖城市中心的老剧院本周末举办黑白乡村默片展映周，展出多部经典开拓史胶片。",
        "祖祖城美食评选揭晓，一家主打周边山谷原产苹果醋与熏肉的乡村小作坊意外斩获金奖。",
        "通往祖祖城的郡道公路昨日因路面补铺施工出现长距离缓行，往来长途客车均有延误。",
        "祖祖城剧场街区的流浪乐手正就夜间声浪管制条例向市政提请复议，双方各执一词。",
        "祖祖城美术馆举办十九世纪乡野风光水彩特展，其中数幅画作取景自这片谷地的前身。",
        "海湾晨雾笼罩港区，祖祖城轮渡与沿海杂货驳船的启航班次均有所推迟。",
        "JOJA公司公布最新季度财报，主打蓝罐苏打汽水的渠道扩张令其销售总额再创新高。",
        "商业传闻透露，JOJA公司正四处接触格兰普顿枢纽地皮，意图兴建大型区域自动化分拨货栈。",
        "祖祖城数名JOJA仓储员工就节前强制轮班提出劳工申诉，仲裁委已受理该争议案。",
        "行业刊物披露，JOJA公司公关部门正频繁接洽地方议员，游说放宽林区周边水域的排污门槛。",
        "JOJA超市在本地区全面发放五折促销宣传单，周边传统独立杂货铺的经营压力陡增。",
        "格兰普顿大宗商品交易所的炼焦煤与生铁挂牌价小幅攀升，附近铁匠铺已微调锻造报价。",
        "邻县早春浆果与晚熟果园传出丰产消息，晒制干果的市场批发价有望在入冬前回落。",
        "行商反映通往城堡村的翻浆土路泥泞难行，过往双轮货运马车的行车时间增加近倍。",
        "芬吉尔农业委员会通报，周边乡村产区上季度的块根作物收购总量录得适度结余。",
        "山区各大伐木场开足马力赶工原木，但山间颠簸栈道限制了木材外运的单日运力。",
        "来自宝石海的湿冷水汽正压入沿岸平原，码头管事已出面提醒各家缆绳加固停泊木船。",
        "高山湖泊的大口黑鲈进入活跃摄食期，老钓手们预测今春垂钓手感将颇为畅快。",
        "异常洋流将深海暖水鱼群推至近海浅滩，几处老石桥附近的钓竿动静频频。",
        "周边气象水文哨所监测到上游降水丰沛，山谷主河道水位持续走高，水流湍急。",
        "秋霜凝结的时日略早于农时推算，山坡高垄的耐寒青菜已结出一层薄白霜晶。",
        "昨夜山间主峰上空掠过极为清晰的微光极光，不少山民裹着厚毯爬上石梁守候观瞧。",
        "自然学者在山麓林缘记录到罕见高山鹰隼结巢的踪迹，引来本地鸟类爱好者阵阵议论。",
        "刘易斯镇长确认镇口木桥的加固补助款已办结报销，所需硬木大梁正由车队陆续运抵。",
        "一位民俗学者在镇图书馆借阅早年拓荒日志，向常客们打听当年殖民矿井的口述故事。",
        "后山湖泊北侧的古老林道昨日清理完毕倾倒的松枝断木，山林徒步小径已恢复通行。",
        "冒险者公会传出私信：矿井表层坑道的洞穴蝙蝠近日活动反常沉寂，老猎人们格外留神。",
        "星之果实酒吧上周五晚座无虚席，古斯熬煮的大锅土豆炖牛肉在深夜前便已售罄。",
        "莱纳斯傍晚在山脚蕨草丛中瞧见成群飞舞的同步萤火虫，称这般聚群数年方得一见。",
        "皮埃尔杂货店本周推出优质混合种子尝鲜包，柜台前的农户采购需求颇为踊跃。"
    };
}