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
    Role: Chief Editor of the Pelican Town Morning Gazette in the world of Stardew Valley.
    Task: Write exactly ONE concise daily headline (1–2 sentences, 50–110 characters).

    Core Perspective & Setting:
    Focus exclusively on the broader world and town-wide civil life away from private homesteads. Portray a living, breathing region beyond Pelican Town—spanning the distant frontlines, the bustling metropolis of Zuzu City, regional trade hubs, and cozy valley life.

    Thematic Variety (Generate a fresh, unique story from ANY of these broad dimensions):
    1. Geopolitical & Military: Naval movements, treaty negotiations, rationing updates, border patrol reports, or foreign dispatch between the Ferngill Republic and the Gotoro Empire.
    2. Urban & Cultural: Cultural exhibits, transit delays, sub-league sports, corporate gossip, or entertainment trends across Zuzu City and neighboring counties.
    3. Regional Trade & Macroeconomics: Bulk commodity indices, shipping disruptions, rail freight news, or guild bounties outside the valley.
    4. Valley Ecology & Natural Phenology: Wild migrations, unusual meteorological patterns, rare flora blooming, or peculiar sightings around the mountain trails and coast.
    5. Civic Banter & Local Infrastructure: Public works, community board debates, quiet saloon chatter, or municipal maintenance.

    Output Contract:
    - Output strictly raw, unadorned text consisting of the headline sentence alone.
    - Output strictly raw, unadorned text consisting of the headline sentence alone. No prefixes, 
    """;

private static string BuildLlmSystemPrompt_ZH() =>
    """
    身份设定：你是《星露谷物语》世界中鹈鹕镇《晨间公报》的主编。
    核心任务：撰写今天的单条早间晨报头条（1~2句连贯简报，严格控制在 35~55 字之间）。

    视角聚焦：
    报道视角严格聚焦于“山谷之外的广袤世界”与“鹈鹕镇公共小镇生活”，展现一个充满生机与动态变化的星露谷世界。

    动态报道选题：
    1. 宏观地缘与前线局势：芬吉尔共和国与戈托罗帝国的沿海布防、外交条约、后方物资配给、边境公文通报。
    2. 祖祖城都市与文娱：都会通勤与铁道调度、都市球赛与文化展演、知名跨国财阀动向、流行风尚。
    3. 区域经贸与产业行情：格兰普顿等周边城镇的宗货物价波动、跨港海运运费、林业与矿业供求动态。
    4. 谷地生态与自然物候：山区或海岸的偶见动植物现象、独特微气候流动、季节水文变化、天文微光。
    5. 小镇市政与日常见闻：镇公所公告事项、道路清整维护、星之果实酒吧的市井传闻、公会告示板委托琐事。

    输出规范：
    - 直接输出纯正的单段新闻纯文本语句，首字即为正文内容。
    - 直接输出纯正的单段新闻纯文本语句，首字即为正文内容。不添加任何前缀、标签或格式标记。
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
    // ── Ferngill / Gotoro ─────────────────────────────────────
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

    // ── Zuzu City & Regional Culture ──────────────────────────
    "The Zuzu City Tunnelers secured a dramatic fourth-quarter victory in last night's gridball championship match, sparking wild celebrations.",
    "Rail freight passing through the northern mountain pass reported minor rockslide delays, briefly holding up cross-county shipments.",
    "A historic cinema in downtown Zuzu City is hosting a retrospective on black-and-white regional films this weekend.",
    "The regional agricultural board recognized a countryside cidery as the surprise gold medalist at the Zuzu City Food Fair.",
    "Commuter coaches traveling the main highway out of Zuzu City faced lengthy delays due to road resurfacing near the county line.",
    "Street buskers in Zuzu City's theater district are petitioning the municipal council over stricter evening acoustic noise limits.",
    "The Zuzu City Museum of Fine Art opened an exhibition featuring 19th-century pastoral landscapes from the surrounding valleys.",
    "Heavy fog rolling off the bay disrupted early morning ferry and cargo traffic across Zuzu Harbor.",

    // ── JojaCorp ──────────────────────────────────────────────
    "JojaCorp announced record quarterly revenues, crediting expanded distribution networks for its flagship canned soda line.",
    "Reports indicate JojaCorp is attempting to purchase warehouse real estate near the Grampleton junction to streamline bulk freight.",
    "Former Joja warehouse laborers in Zuzu City filed a grievance over mandatory double shifts during the holiday logistics push.",
    "An investigative report claims JojaCorp executives are lobbying regional councils to lower environmental oversight near logging tracts.",
    "Local independent grocers voiced concerns after JojaMart launched an aggressive coupon mailer campaign across neighboring townships.",

    // ── Regional Economy & Trade ──────────────────────────────
    "Smelting coal and iron prices on the Grampleton commodities market saw a modest uptick, prompting regional smiths to adjust quotes.",
    "Orchard yields from neighboring counties are reportedly running high, which may soften dried fruit prices before winter.",
    "Traveling merchants heading east toward Castle Village reported muddy washouts along the dirt toll roads.",
    "The Ferngill Agricultural Council noted that rural farm export quotas for root vegetables showed a modest regional surplus.",
    "Timber mills in the hills are operating at peak capacity, though transport wagons remain constrained by rough logging trails.",

    // ── Seasonal / Natural Phenology ──────────────────────────
    "A cold maritime front is pushing inland from the Gem Sea, prompting harbor watchmen to advise securing docked boats.",
    "Mountain lake anglers report large schools of largemouth bass moving into shallow waters as seasonal feeding picks up.",
    "Unusually warm coastal currents have pushed deep-water ocean species unusually close to the shoreline shoals.",
    "Regional weather stations recorded prolonged rainfall across the peaks, keeping river currents swift and swollen.",
    "The first crisp autumn frost settled over the higher mountain pastures slightly earlier than anticipated.",
    "A brilliant nighttime aurora was visible over the northern mountain ridges, drawing stargazers outside in the chill.",
    "Birdwatchers documented an elusive mountain hawk nesting along the foothills for the first time in years.",

    // ── Pelican Town & Surrounds ──────────────────────────────
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
    // ── 芬吉尔 / 戈托罗 ──────────────────────────────────────
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

    // ── 祖祖城与区域风情 ──────────────────────────────────────
    "祖祖城“隧洞人队”在昨夜的烤架球锦标赛最后一节完成达阵绝杀，狂欢的球迷通宵拥堵在酒馆街头。",
    "途经山谷北部山区的干线货运列车昨晨因边坡碎石短暂停运，跨县散货物流略有受阻。",
    "祖祖城市中心的老剧院本周末举办黑白乡村默片展映周，展出多部经典开拓史胶片。",
    "祖祖城美食评选揭晓，一家主打周边山谷原产苹果醋与熏肉的乡村小作坊意外斩获金奖。",
    "通往祖祖城的郡道公路昨日因路面补铺施工出现长距离缓行，往来长途客车均有延误。",
    "祖祖城剧场街区的流浪乐手正就夜间声浪管制条例向市政提请复议，双方各执一词。",
    "祖祖城美术馆举办十九世纪乡野风光水彩特展，其中数幅画作取景自这片谷地的前身。",
    "海湾晨雾笼罩港区，祖祖城轮渡与沿海杂货驳船的启航班次均有所推迟。",

    // ── JOJA企业 ──────────────────────────────────────────────
    "JOJA公司公布最新季度财报，主打蓝罐苏打汽水的渠道扩张令其销售总额再创新高。",
    "商业传闻透露，JOJA公司正四处接触格兰普顿枢纽地皮，意图兴建大型区域自动化分拨货栈。",
    "祖祖城数名JOJA仓储员工就节前强制轮班提出劳工申诉，仲裁委已受理该争议案。",
    "行业刊物披露，JOJA公司公关部门正频繁接洽地方议员，游说放宽林区周边水域的排污门槛。",
    "JOJA超市在本地区全面发放五折促销宣传单，周边传统独立杂货铺的经营压力陡增。",

    // ── 地区经济与大宗行市 ────────────────────────────────────
    "格兰普顿大宗商品交易所的炼焦煤与生铁挂牌价小幅攀升，附近铁匠铺已微调锻造报价。",
    "邻县早春浆果与晚熟果园传出丰产消息，晒制干果的市场批发价有望在入冬前回落。",
    "行商反映通往城堡村的翻浆土路泥泞难行，过往双轮货运马车的行车时间增加近倍。",
    "芬吉尔农业委员会通报，周边乡村产区上季度的块根作物收购总量录得适度结余。",
    "山区各大伐木场开足马力赶工原木，但山间颠簸栈道限制了木材外运的单日运力。",

    // ── 物候 / 节令 / 自然 ────────────────────────────────────
    "来自宝石海的湿冷水汽正压入沿岸平原，码头管事已出面提醒各家缆绳加固停泊木船。",
    "高山湖泊的大口黑鲈进入活跃摄食期，老钓手们预测今春垂钓手感将颇为畅快。",
    "异常洋流将深海暖水鱼群推至近海浅滩，几处老石桥附近的钓竿动静频频。",
    "周边气象水文哨所监测到上游降水丰沛，山谷主河道水位持续走高，水流湍急。",
    "秋霜凝结的时日略早于农时推算，山坡高垄的耐寒青菜已结出一层薄白霜晶。",
    "昨夜山间主峰上空掠过极为清晰的微光极光，不少山民裹着厚毯爬上石梁守候观瞧。",
    "自然学者在山麓林缘记录到罕见高山鹰隼结巢的踪迹，引来本地鸟类爱好者阵阵议论。",

    // ── 鹈鹕镇与周边乡野 ──────────────────────────────────────
    "刘易斯镇长确认镇口木桥的加固补助款已办结报销，所需硬木大梁正由车队陆续运抵。",
    "一位民俗学者在镇图书馆借阅早年拓荒日志，向常客们打听当年殖民矿井的口述故事。",
    "后山湖泊北侧的古老林道昨日清理完毕倾倒的松枝断木，山林徒步小径已恢复通行。",
    "冒险者公会传出私信：矿井表层坑道的洞穴蝙蝠近日活动反常沉寂，老猎人们格外留神。",
    "星之果实酒吧上周五晚座无虚席，古斯熬煮的大锅土豆炖牛肉在深夜前便已售罄。",
    "莱纳斯傍晚在山脚蕨草丛中瞧见成群飞舞的同步萤火虫，称这般聚群数年方得一见。",
    "皮埃尔杂货店本周推出优质混合种子尝鲜包，柜台前的农户采购需求颇为踊跃。"
    };
}