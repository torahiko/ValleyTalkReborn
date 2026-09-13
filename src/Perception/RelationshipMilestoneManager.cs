using System;
using System.Collections.Generic;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace ValleytalkReborn;

/// <summary>
/// 关系重大里程碑与节日余韵感知管理器。
/// </summary>
internal class RelationshipMilestoneManager
{
    public static readonly RelationshipMilestoneManager Instance = new();

    private IModHelper _helper;
    private IMonitor _monitor;

    // 持久化 ModData 键名
    private const string ModDataFlowerDancePartnerKey = "ValleytalkReborn.YesterdayFlowerDancePartner";

    // 内存暂存：花舞节当天捕捉到的舞伴
    private string _stagedDancePartnerToday = null;

    // 单日消费集合：确保当天构建注入后即完成消费，防止单日反复复读（阅后即焚）
    private readonly HashSet<string> _consumedMilestonesToday = new(StringComparer.OrdinalIgnoreCase);

    private static bool IsZh =>
        LocalizedContentManager.CurrentLanguageCode == LocalizedContentManager.LanguageCode.zh;

    public void Initialize(IModHelper helper, IMonitor monitor)
    {
        // A1 决议：幂等防重入守卫，防止 Entry 异常重入导致重复订阅
        if (_helper != null) return;

        _helper = helper;
        _monitor = monitor;

        _helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
        _helper.Events.GameLoop.DayEnding    += OnDayEnding;
        _helper.Events.GameLoop.DayStarted   += OnDayStarted;
    }

    /// <summary>
    /// A1 决议：事件订阅随进程常驻，处理器自带 IsWorldReady 守卫；此处仅清理内存状态。
    /// </summary>
    public void Cleanup()
    {
        _consumedMilestonesToday.Clear();
        _stagedDancePartnerToday = null;
    }

    /// <summary>
    /// 为指定 NPC 构建关系里程碑/节日余韵提示词块。
    /// 优先级：离婚申请当天 > 本人结婚倒计时 > 多配偶下新伴侣订婚 > 节日伴侣/吃醋。
    /// 遵循“注入即焚”原则：一旦成功构建并注入 Prompt，立即标记当天已阅，无论 AI 是否选用均不再同日复读。
    /// </summary>
    public string BuildMilestoneBlock(Character character)
    {
        if (character == null || !Context.IsWorldReady) return null;

        string npcName = character.Name;
        Farmer player = Game1.player;
        if (player == null) return null;

        // 当天已构建注入过重大事件，不再重复注入（阅后即焚保障）
        if (_consumedMilestonesToday.Contains(npcName))
            return null;

        bool isZh = IsZh;

        // ── 1. 优先级最高：离婚当天（镇长处已申请，明晨搬走） ──
        if (player.divorceTonight.Value)
        {
            if (string.Equals(player.spouse, npcName, StringComparison.OrdinalIgnoreCase))
            {
                ConfirmConsumed(npcName);
                return isZh
                    ? "<relationship_conflict type=\"divorce_pending\">\n" +
                      "- 事实：农夫今天已正式在镇长刘易斯处提交了与你的离婚申请。明天清晨你将会收拾行李搬离农场。\n" +
                      "- 你现在心里翻着什么，嘴上就会漏出什么——心碎也好、火气也好、冷了心的干脆也好，都顺着你平时的脾气和自尊走。\n" +
                      "</relationship_conflict>"
                    : "<relationship_conflict type=\"divorce_pending\">\n" +
                      "- Fact: The farmer has officially filed for divorce at Mayor Lewis's manor today. You will pack up and leave the farm tomorrow morning.\n" +
                      "- Whatever's churning in you now is what comes out — heartbreak, anger, a cold goodbye, or a hard question. Let it come out the way your usual temper and pride would have it.\n" +
                      "</relationship_conflict>";
            }
        }

        // ── 2. 优先级次之：本人结婚倒计时（支持原版与 PolyamorySweet 自定义天数） ──
        if (player.friendshipData != null && player.friendshipData.TryGetValue(npcName, out Friendship fs))
        {
            if (fs.IsEngaged() && fs.CountdownToWedding > 0)
            {
                int days = fs.CountdownToWedding;
                string timeStr = days == 1
                    ? (isZh ? "明天" : "tomorrow")
                    : (isZh ? $"{days}天后" : $"in {days} days");

                ConfirmConsumed(npcName);
                return isZh
                    ? $"<relationship_milestone type=\"wedding_countdown\">\n" +
                      $"- 事实：你与农夫已经正式订婚，你们的婚礼将在【{timeStr}】正式举行。\n" +
                      $"- 这件事一直搁在你脑子里，说起话来自然就带出来了。可能是一句盼头，可能是准备中的琐事，也可能是一丝紧张。\n" +
                      "</relationship_milestone>"
                    : $"<relationship_milestone type=\"wedding_countdown\">\n" +
                      $"- Fact: You and the farmer are officially engaged. Your wedding ceremony will be held {timeStr}.\n" +
                      $"- It's been sitting in the back of your mind — it'll find its way into how you talk today without you forcing it. Anticipation, prep details, or nerves, whichever fits.\n" +
                      "</relationship_milestone>";
            }
        }

        // ── 2.5 多配偶扩展：当前 NPC 已是配偶，而农夫又与新伴侣订婚 ──
        bool isCurrentNpcSpouse = SpouseQueryService.Instance.IsMarried(npcName, player)
            || string.Equals(player.spouse, npcName, StringComparison.OrdinalIgnoreCase);

        if (isCurrentNpcSpouse && player.friendshipData != null)
        {
            string otherFianceName = null;
            int otherCountdown = int.MaxValue;

            foreach (var key in player.friendshipData.Keys)
            {
                if (string.Equals(key, npcName, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (player.friendshipData.TryGetValue(key, out Friendship otherFs) && otherFs != null)
                {
                    if (otherFs.IsEngaged() && otherFs.CountdownToWedding > 0)
                    {
                        // 取倒计时最近的新伴侣
                        if (otherFs.CountdownToWedding < otherCountdown)
                        {
                            otherCountdown = otherFs.CountdownToWedding;
                            otherFianceName = key;
                        }
                    }
                }
            }

            if (!string.IsNullOrEmpty(otherFianceName))
            {
                string fianceDisplayName = GetNpcDisplayName(otherFianceName);
                string timeStr = otherCountdown == 1
                    ? (isZh ? "明天" : "tomorrow")
                    : (isZh ? $"{otherCountdown}天后" : $"in {otherCountdown} days");

                ConfirmConsumed(npcName);
                return isZh
                    ? $"<relationship_milestone type=\"spouse_new_engagement\">\n" +
                      $"- 事实：农夫作为你的伴侣，已正式与【{fianceDisplayName}】订婚，他们的婚礼将在【{timeStr}】举行。\n" +
                      $"- 这事你心里有数。你对多伴侣的态度、你自己的自尊和你们俩的交情，都会自然从话里带出来——可能微酸、可能坦然、可能好奇、也可能拿来打趣。\n" +
                      "</relationship_milestone>"
                    : $"<relationship_milestone type=\"spouse_new_engagement\">\n" +
                      $"- Fact: The farmer, who is already married to you, has officially gotten engaged to [{fianceDisplayName}]. Their wedding will take place {timeStr}.\n" +
                      $"- You know about it. How you take it — a little stung, fine with it, curious, or having fun with it — comes out of your usual temper, your pride, and how you two handle sharing the house.\n" +
                      "</relationship_milestone>";
            }
        }

        // ── 3. 节日余韵：花舞节次日（春季25日） ──
        if (Game1.currentSeason == "spring" && Game1.dayOfMonth == 25)
        {
            string dancePartner = GetYesterdayDancePartner();
            if (!string.IsNullOrEmpty(dancePartner))
            {
                // 分支 A：当前交谈者正是昨天被选中的舞伴
                if (string.Equals(npcName, dancePartner, StringComparison.OrdinalIgnoreCase))
                {
                    ConfirmConsumed(npcName);
                    return isZh
                        ? "<festival_afterglow type=\"flower_dance_partner\">\n" +
                          "- 事实：在昨天（春季24日）的花舞节上，农夫邀请了你作为舞伴，你们在全镇村民面前一起跳了双人舞。\n" +
                          "- 昨天那场舞还在你身上留着余温——嘴上可能是一句回味、一点藏不住的开心、一丝不好意思，也可能轻描淡写地绕过去。\n" +
                          "</festival_afterglow>"
                        : "<festival_afterglow type=\"flower_dance_partner\">\n" +
                          "- Fact: At yesterday's Flower Dance, the farmer invited you to be their dance partner, and you danced together before the whole town.\n" +
                          "- That dance is still warm in you — it could come out as fondness, a little glow, some shyness, or you shrug it off like it was nothing. Let it be what it is.\n" +
                          "</festival_afterglow>";
                }

                // 分支 B：当前交谈者是玩家的配偶，但农夫昨天选了别人跳舞（修罗场）
                if (isCurrentNpcSpouse)
                {
                    string partnerDisplayName = GetNpcDisplayName(dancePartner);

                    ConfirmConsumed(npcName);
                    return isZh
                        ? $"<relationship_conflict type=\"flower_dance_jealousy\">\n" +
                          $"- 事实：在昨天（春季24日）的花舞节上，农夫作为你的伴侣却没有邀请你跳舞，而是选择了与【{partnerDisplayName}】共舞。\n" +
                          "- 这件事你心里是有数的。会不会提、怎么提、提几分，全看你平时的脾气、自尊和你们俩的交情。\n" +
                          "</relationship_conflict>"
                        : $"<relationship_conflict type=\"flower_dance_jealousy\">\n" +
                          $"- Fact: At yesterday's Flower Dance, your spouse did not dance with you, but instead chose to dance with [{partnerDisplayName}].\n" +
                          "- You've noticed. Whether you bring it up, how you bring it up, and how sharp it is — all runs off your usual temper, your pride, and where you two stand.\n" +
                          "</relationship_conflict>";
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 注入后即刻标记已阅（防同日复读，阅后即焚）。
    /// 保证幂等调用；哪怕外部系统（如取消对话）重复触发也不会产生副作用。
    /// </summary>
    public void ConfirmConsumed(string npcName)
    {
        if (!string.IsNullOrWhiteSpace(npcName))
        {
            _consumedMilestonesToday.Add(npcName);
        }
    }

    private void OnUpdateTicked(object sender, UpdateTickedEventArgs e)
    {
        if (!e.IsMultipleOf(60) || !Context.IsWorldReady) return;

        // 春季24日花舞节期间，捕获农夫选定的舞伴
        if (Game1.currentSeason == "spring" && Game1.dayOfMonth == 24 && Game1.isFestival())
        {
            var partner = Game1.player?.dancePartner?.Value;
            if (partner != null && !string.IsNullOrEmpty(partner.Name))
            {
                _stagedDancePartnerToday = partner.Name;
            }
        }
    }

    private void OnDayEnding(object sender, DayEndingEventArgs e)
    {
        if (!Context.IsWorldReady || Game1.player == null) return;

        // 过夜持久化舞伴数据
        if (Game1.currentSeason == "spring" && Game1.dayOfMonth == 24)
        {
            if (!string.IsNullOrEmpty(_stagedDancePartnerToday))
            {
                Game1.player.modData[ModDataFlowerDancePartnerKey] = _stagedDancePartnerToday;
                _monitor?.Log($"[RelationshipMilestone] 成功记录花舞节舞伴: {_stagedDancePartnerToday}", LogLevel.Debug);
            }
        }
    }

    private void OnDayStarted(object sender, DayStartedEventArgs e)
    {
        _consumedMilestonesToday.Clear();
        _stagedDancePartnerToday = null;

        // 春季26日清除前日的舞伴记录（生命周期结束，自动焚毁）
        if (Game1.currentSeason == "spring" && Game1.dayOfMonth >= 26)
        {
            Game1.player?.modData?.Remove(ModDataFlowerDancePartnerKey);
        }
    }

    private string GetYesterdayDancePartner()
    {
        if (Game1.player != null &&
            Game1.player.modData.TryGetValue(ModDataFlowerDancePartnerKey, out string partnerName))
        {
            return partnerName;
        }
        return null;
    }

    private static string GetNpcDisplayName(string name)
    {
        var npc = Game1.getCharacterFromName(name);
        return npc?.displayName ?? name;
    }
}