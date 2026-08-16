using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace ValleytalkReborn;

/// <summary>
/// Daily Headline Generator — "Pelican Town Morning News"
///
/// Produces 1–2 Track 1 (GlobalGossip) entries per day using a strict priority cascade:
///
///   S-tier (lifetime 40h): Major life events — marriage, divorce, new child,
///     Krobus moves in, Community Center completed.
///
///   A-tier (lifetime 20h): Extraordinary farmer feats — legendary fish, 500k+ gold,
///     100+ monster kills, 10+ trash cans, new dating relationship.
///     50% coin-flip prevents daily dominance.
///
///   B-tier (lifetime 24h): World and regional news. Two paths:
///     1. [Primary]  At 7:20 AM, request one fresh headline from the LLM.
///     2. [Fallback] At DayStarted, a deterministic-random headline from the hardcoded
///                   pool is pre-written to Track 1. If the LLM call succeeds later,
///                   it overwrites the fallback entry (same key "WorldNews").
/// </summary>
internal static class DailyHeadlineGenerator
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
    private const int LlmHeadlineTimeoutSeconds = 30;

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
                ? $"今天是第{year}年{SeasonZH(season)}第{day}天。请生成今日的鹈鹕镇早间头条新闻一条。"
                : $"Today is {season} day {day} of year {year}. Generate one Pelican Town morning headline for today.";

            using var cts = new System.Threading.CancellationTokenSource(
                TimeSpan.FromSeconds(LlmHeadlineTimeoutSeconds));

            // ✅ 修复 CS7036：补齐 RunInference 所需的占位参数，匹配 ModEntry 中的调用签名
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
            if (string.IsNullOrWhiteSpace(headline))
            {
                ModEntry.SMonitor?.Log(
                    "[DailyHeadlineGenerator] LLM returned empty headline after cleanup. Fallback retained.",
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
    //  LLM prompt builders
    // ─────────────────────────────────────────────────────────────
    private static string BuildLlmSystemPrompt_EN() =>
        """
        You are the editor of the Pelican Town Morning Gazette, a small-town newspaper
        in the world of Stardew Valley.
        Your job is to write ONE short news headline (1–2 sentences, under 120 characters)
        for today's edition. The headline should feel authentic to the Stardew Valley universe:
        Topics you can draw from (pick one at random):
        - Ferngill Republic vs. Gotoro Empire geopolitics (naval clashes, trade, veterans, diplomacy)
        - Zuzu City life (transit, sports, music, culture, JojaCorp)
        - Regional economy (Grampleton commodities, lumber, import prices)
        - Seasonal weather or nature events in the valley
        - Quiet local Pelican Town happenings (road work, the saloon, Adventurer's Guild, rare sightings)
        Rules:
        - Do NOT mention the farmer or any specific farmer action.
        - Do NOT use markdown, bullet points, quotes, or dashes.
        - Output ONLY the single headline sentence. Nothing else.
        """;

    private static string BuildLlmSystemPrompt_ZH() =>
        """
        你是鹈鹕镇《晨间公报》的编辑，负责为《星露谷》世界观下的小镇报纸撰写每日头条。
        你的任务是为今天的版面写一条简短的头条新闻（1~2句话，不超过60个汉字）。
        新闻内容必须符合《星露谷》世界观，真实感强，读来像是真实发生的事。
        可选题材（从中随机选一个方向）：
        - 芬吉尔共和国与戈托罗帝国的地缘局势（海战、贸易、外交、老兵）
        - 祖祖城都市动态（轻轨、球赛、音乐节、乔佳公司）
        - 地区经济（格兰普顿物价、木材、进口商品）
        - 山谷季节性天气或自然奇景
        - 鹈鹕镇本地小事（道路施工、酒馆、冒险者公会、罕见目击）
        规则：
        - 不得提及农夫或任何农夫的具体行为。
        - 不得使用 Markdown 格式、引号、破折号或符号。
        - 只输出一条头条句子本身，不输出任何其他内容。
        """;

    // ─────────────────────────────────────────────────────────────
    //  LLM output cleanup
    // ─────────────────────────────────────────────────────────────
    private static string CleanLlmHeadline(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        string s = raw.Trim();

        // Remove markdown bold/italic markers
        s = s.Replace("**", "").Replace("__", "").Replace("*", "").Replace("_", "");

        // ✅ 优化：将换行符统一替换为空格，防止轻量模型折行导致截断
        s = s.Replace("\r\n", " ").Replace("\n", " ").Trim();

        // Remove common LLM prefixes like "Headline:", "News:", "Title:"
        foreach (var prefix in new[] { "Headline:", "headline:", "News:", "news:", "Title:", "title:", "头条：", "新闻：" })
        {
            if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                s = s.Substring(prefix.Length).TrimStart();
        }

        // Strip leading bullet / dash characters
        s = s.TrimStart('-', '•', '·', '>', ' ');

        // Strip surrounding quotation marks (English and Chinese)
        s = s.Trim('"', '"', '"', '\'', '「', '」', '『', '』');

        // Collapse multiple spaces and trim again
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\s{2,}", " ").Trim();

        // ✅ 此时 s 已经是单行文本，无需再 Split('\n') 遍历
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

        bool   isZh     = IsZh;
        // ✅ 修复：将 Context（NPC名字）传递给翻译方法
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
                $"马尔隆摇着头，难以置信——农夫今天在矿井里消灭了 {num} 只怪物。这不叫务农，这叫打仗。",
                $"吉尔见过不少冒险者，但单日 {num} 次击杀？连他都不得不称赞。",
                $"冒险者公会里炸开了锅——据说农夫今天一口气清了 {num} 只矿井怪物。",
            }),
            "TrashCan" => PerceptionManager.PickVariant(new[]
            {
                $"乔治真的发火了——农夫今天翻遍了整整 {num} 个垃圾桶，一个都没放过。",
                $"海莉看起来极为崩溃。农夫今天大白天翻了 {num} 个垃圾桶，就在大家眼皮底下。",
                $"莱纳斯轻声说起这件事——今天 {num} 个垃圾桶。他说他理解，但邻居们不理解。",
            }),
            // ✅ 新增：恋爱官宣中文翻译
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
        // ── Ferngill / Gotoro ─────────────────────────────────────
        "The Ferngill Republic Daily reports that naval vessels intercepted a Gotoro supply fleet near the southern Gem Sea. Tensions remain high.",
        "Parliament is debating tighter trade restrictions following renewed skirmishes along the Gem Sea border. Merchants worry about import prices.",
        "A Ferngill spokesperson confirmed former prisoners of war are being repatriated — news that will mean a great deal to some valley families.",
        "Border patrols along the Gem Sea coast have been reinforced this week, per the Republic's Ministry of Defense.",
        "Rumor has it Gotoro Empire officials have quietly opened back-channel talks with Ferngill diplomats. No official statement yet.",
        "A Gotoro merchant who recently crossed the border reports unusual shortages of basic goods inside the Empire — cause unknown.",
        "The Ferngill Republic is considering a commemorative holiday for veterans of the Gem Sea conflict. Lewis says Pelican Town would support it.",
        "Military exercises near the southern cape have disrupted fishing routes. Coastal fishers report smaller hauls this week.",
        "The Republic's latest trade census shows a modest rise in cross-border smuggling. Customs say they are monitoring the situation.",
        "A Ferngill senator is pushing a bill to expand veteran support programs — broad public backing but stalled in committee.",
        // ── Zuzu City ─────────────────────────────────────────────
        "Zuzu City's Light Rail Line 2 suffered a power failure this morning, causing delays on long-distance coach routes into the valley.",
        "The Zuzu City Tunnelers won last night's stickball league match in a dramatic comeback. Fans celebrated well into the night.",
        "The Zuzu City Independent Music Festival wrapped up last night. Indie band 'The Luau' took home Best New Act.",
        "A new high-rise development near Zuzu City Central Station cleared its final zoning approval. Urban planners call it transformative.",
        "Zuzu City's annual tech expo opened today, showcasing automated farm tools and solar-powered irrigation systems.",
        "A viral street performance in Zuzu City's arts district is drawing record crowds — and reigniting the busking permit debate.",
        "Zuzu City transit authority announced fare increases starting next season, citing rising infrastructure costs.",
        "The Zuzu City Culinary Awards named a small valley-sourced restaurant as this year's surprise winner.",
        "Road construction on Zuzu City's main avenue has caused hour-long commuter delays.",
        "A Zuzu City gallery is hosting a retrospective of rural landscape art — several pieces reportedly depict the valley.",
        // ── JojaCorp ──────────────────────────────────────────────
        "JojaCorp unveiled its latest carbonated concentrate line at a Zuzu City press event. First-day sales reportedly broke records.",
        "JojaCorp is expanding its automated warehouse network, with a rumored new facility near the valley region.",
        "Former JojaMart employees filed a class-action suit over unpaid overtime. The case proceeds to arbitration.",
        "An anonymous whistleblower claims JojaCorp is lobbying to relax environmental protections near rural waterways.",
        "JojaCorp's quarterly earnings beat analyst expectations again. Shareholders are pleased; local shop owners less so.",
        // ── Regional economy ──────────────────────────────────────
        "Coal and iron ore prices at the Grampleton commodities exchange rose slightly. Smiths in the region are adjusting their rates.",
        "Early harvest reports from the northern valleys are optimistic. Pierre may stock specialty seeds earlier than usual.",
        "A traveling trader reported rough road conditions east of Grampleton — deliveries are running late.",
        "The valley's crop export numbers for last season are expected to show a modest surplus.",
        "Pelican Town's spring output beat regional averages for the third consecutive year, per the Ferngill Agricultural Council.",
        "Lumber prices in the wider region have risen sharply. Robin says it hasn't hit her supply chain yet, but she's watching.",
        // ── Seasonal / nature ─────────────────────────────────────
        "A strong weather system is moving in from the coast. Lewis urges residents to secure outdoor equipment.",
        "The bass migration season has begun in the mountain lakes. Willy says it's shaping up to be a great year for anglers.",
        "Unusually warm ocean currents pushed rare deep-water fish closer to shore. Fishers are excited; scientists are studying it.",
        "A regional weather station recorded the highest single-day rainfall in a decade. River levels remain elevated.",
        "The first frost arrived earlier than predicted, catching some late-season farmers off guard.",
        "An unusually vivid aurora was visible from the valley peaks last night. Several residents climbed up to watch.",
        "A rare bird not seen in the valley for twenty years was spotted near the mountain foothills. The nature society is abuzz.",
        // ── Pelican Town ──────────────────────────────────────────
        "Lewis confirmed the valley road resurfacing project has been approved. Construction begins after the current season.",
        "A traveling historian was spotted at the library, researching the valley's pre-settlement ruins. Robin mentioned it at the saloon.",
        "The old mountain trail north of town has been cleared of fallen timber. Hikers can use it again.",
        "Word from the Adventurer's Guild: monster activity in the deeper mine levels has been unusually quiet this week.",
        "The Stardrop Saloon recorded its busiest night of the season last Friday. Gus had to turn away customers by midnight.",
        "Linus spotted an unusual cluster of fireflies near the mountain summit. He says it only happens every few years.",
        "Pierre is running a seasonal discount on mixed seed packets. He says demand has been higher than expected.",
    };

    private static readonly string[] WorldNewsPool_ZH =
    {
        // ── 芬吉尔 / 戈托罗 ──────────────────────────────────────
        "《芬吉尔共和国日报》报道：海军舰队昨日在宝石海南部拦截了一支戈托罗补给船队，双方局势持续紧张。",
        "议会正就宝石海边境冲突讨论收紧贸易管制措施，商贩们对进口物价上涨深感忧虑。",
        "芬吉尔政府确认，一批战俘正在陆续遣返——这对山谷里的某些家庭意义重大。",
        "据国防部消息，宝石海沿岸边境巡逻力量本周已得到加强。",
        "据传戈托罗帝国官员已秘密向芬吉尔方面传达和谈意愿，但官方尚无正式表态。",
        "一名刚越境的戈托罗商人透露，帝国境内出现罕见的基础物资短缺，原因不明。",
        "芬吉尔共和国正考虑为宝石海冲突老兵设立纪念节日，刘易斯镇长表示全力支持。",
        "宝石海南部岬角附近的军事演习干扰了正常渔业通道，沿岸渔民反映渔获量明显减少。",
        "共和国最新贸易普查显示跨境走私活动有所抬头，海关当局表示正密切关注。",
        "一位芬吉尔参议员正推动扩大老兵援助法案——民间支持广泛，但在委员会陷入僵局。",
        // ── 祖祖城 ────────────────────────────────────────────────
        "祖祖城轻轨二号线今晨供电故障，导致通往山谷的长途大巴全线延误。",
        "祖祖城\"隧洞人队\"昨晚在球棒球联赛中上演惊天逆转，球迷们通宵狂欢庆祝。",
        "祖祖城独立音乐节昨晚落幕，独立乐队\"The Luau\"斩获最佳新人大奖。",
        "祖祖城中央车站附近的高层开发项目通过最终规划审批，城市规划师称将彻底改变地貌。",
        "祖祖城年度科技博览会今日开幕，展品涵盖自动化农用工具及太阳能灌溉系统。",
        "祖祖城艺术区一场街头即兴表演迅速走红，同时引发了关于街头演出许可的争议。",
        "祖祖城交通局宣布下季度上调票价，原因为基础设施维护成本上升。",
        "祖祖城美食大奖今年爆冷——一家主打山谷本地食材的小餐厅意外夺魁。",
        "祖祖城主干道施工导致交通大面积瘫痪，通勤族反映等待超过一小时。",
        "祖祖城一家画廊正举办乡村风景画回顾展，据说部分展品描绘的正是这片山谷。",
        // ── 乔佳企业 ──────────────────────────────────────────────
        "乔佳公司在祖祖城发布会上推出最新款气泡苏打水，据称首日销量打破历史纪录。",
        "据消息人士透露，乔佳公司正扩大自动化仓储网络，疑似有意在山谷附近新建物流中心。",
        "乔佳超市前员工就拖欠加班费提起集体诉讼，案件已进入仲裁程序。",
        "一位匿名举报人称，乔佳公司正秘密游说放宽农村水道周边的环境保护法规。",
        "乔佳公司本季度财报再超预期，股东喜笑颜开，本地小店主则愁眉不展。",
        // ── 地区经济 ──────────────────────────────────────────────
        "格兰普顿商品交易所本周煤炭与铁矿石价格小幅上扬，附近铁匠已开始调整报价。",
        "北部山谷早期收成报告乐观，皮埃尔表示有望比往年更早补充特色种子库存。",
        "一名途经山谷的商人透露，格兰普顿以东路况极差，各类货物配送均出现延误。",
        "据芬吉尔农业委员会公报，山谷上季农产品出口预计录得小幅盈余。",
        "芬吉尔农业委员会数据：鹈鹕镇春季农产量已连续第三年超越地区平均水平。",
        "地区木材价格近期大幅攀升，罗宾说目前还未影响供应链，但正在密切关注。",
        // ── 季节 / 天气 / 自然 ───────────────────────────────────
        "一股强风暴系统正从海岸逼近。刘易斯镇长提醒居民加固户外设备并检查屋顶。",
        "大口黑鲈洄游季已来临，威利表示今年对垂钓爱好者是个丰收年。",
        "异常温暖的海洋洋流将罕见深海鱼类推近岸边，渔民欣喜，生物学家积极研究成因。",
        "地区气象站记录到上周单日降雨量创近十年最高，河流水位仍居高不下。",
        "本季首次霜冻比预报提前到来，部分种植晚季作物的农人措手不及。",
        "昨夜山谷山峰上空出现异常绚丽的极光，不少居民特地爬上去驻足欣赏。",
        "一种二十年来从未在山谷出现的罕见鸟类昨日现身山麓，当地自然学会热闹了好一阵。",
        // ── 鹈鹕镇与山谷 ─────────────────────────────────────────
        "刘易斯镇长确认山谷公路翻修项目获批，施工将于本季结束后正式启动。",
        "一位行旅历史学家出现在镇图书馆，据称正研究山谷殖民前时代遗址，罗宾在酒馆提到此事。",
        "据悉镇北山区古老山道已清除倒木障碍，徒步爱好者重新可以通行了。",
        "冒险者公会透露：本周矿井深层怪物活动异常平静，令人生疑。",
        "星露酒馆上周五迎来本季最热闹的一夜，古斯在午夜后不得不婉拒新客入场。",
        "莱纳斯昨晚在山顶附近看到一群异常密集的萤火虫，他说这种景象每隔几年才出现一次。",
        "皮埃尔本周对混合种子包进行季节性折扣促销，他说需求量比预期高出不少。",
    };
}