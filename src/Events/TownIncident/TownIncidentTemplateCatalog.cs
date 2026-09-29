using System;
using System.Collections.Generic;
using System.Linq;

namespace ValleytalkReborn;

/// <summary>
/// TIE-002: static content catalog for the Town Incident Scriptwriter.
/// TIE-009C: holds the bilingual fallback scripts for all four archetypes
/// (installed immediately at incident creation, usable without any LLM) and
/// the compact JSON-only scriptwriter prompt templates. Pure C# — no game or
/// SMAPI access, safe to compile and exercise from parser tests.
/// </summary>
internal static class TownIncidentTemplateCatalog
{
    // ─────────────────────────────────────────────────────────────
    //  Static fallback — phase scripts, one block per archetype.
    //  TIE-009C: inner keys are the archetype's RequiredRoles names;
    //  CreateFallback projects them onto the shell's assigned NPC
    //  names, so one fallback serves any cast of the same archetype.
    // ─────────────────────────────────────────────────────────────
    internal static Dictionary<IncidentPhase, Dictionary<string, RolePhaseBrief>> BuildContestPhaseScriptsEn()
    {
        return new Dictionary<IncidentPhase, Dictionary<string, RolePhaseBrief>>
        {
            [IncidentPhase.Inception] = new Dictionary<string, RolePhaseBrief>
            {
                ["Host"] = new RolePhaseBrief
                {
                    Motivation = "Announce the Saloon Cook-Off, recruit contestants and fill every seat in the saloon.",
                    PublicOpinion = "Townsfolk are curious but noncommittal; some suspect it is just a saloon promotion.",
                },
                ["Champion"] = new RolePhaseBrief
                {
                    Motivation = "Defend her title with a daring new recipe and prove the quiet miner's daughter can win twice.",
                    PublicOpinion = "Admirers call her fearless; traditionalists whisper that her ingredients are simply strange.",
                },
                ["Skeptic"] = new RolePhaseBrief
                {
                    Motivation = "Point out that the same regulars always win and push for an outside judge.",
                    PublicOpinion = "A handful of villagers admit the judging looks cozy, but most tell him to lighten up.",
                },
            },
            [IncidentPhase.Escalation] = new Dictionary<string, RolePhaseBrief>
            {
                ["Host"] = new RolePhaseBrief
                {
                    Motivation = "Keep the contest from boiling over while rumors and side bets fill the saloon.",
                    PublicOpinion = "The town has split into camps; everyone has picked a favorite.",
                },
                ["Champion"] = new RolePhaseBrief
                {
                    Motivation = "Train in secret, shrug off the sabotage rumors and let her cooking answer the doubt.",
                    PublicOpinion = "Her fans grow louder; her critics claim the fix is already in.",
                },
                ["Skeptic"] = new RolePhaseBrief
                {
                    Motivation = "Escalate the fairness complaints and float the idea of boycotting the finale.",
                    PublicOpinion = "More villagers start asking who is really judging.",
                },
            },
            [IncidentPhase.Climax] = new Dictionary<string, RolePhaseBrief>
            {
                ["Host"] = new RolePhaseBrief
                {
                    Motivation = "Host a clean, dramatic finale and crown a winner the whole town can accept.",
                    PublicOpinion = "The saloon is packed; the whole town wants a fair result.",
                },
                ["Champion"] = new RolePhaseBrief
                {
                    Motivation = "Plate her boldest dish yet and silence the doubters for good.",
                    PublicOpinion = "Even her critics admit the finale is must-see.",
                },
                ["Skeptic"] = new RolePhaseBrief
                {
                    Motivation = "Watch the judging like a hawk and demand transparency before conceding anything.",
                    PublicOpinion = "He promised to eat his words if the result is clean.",
                },
            },
        };
    }

    internal static Dictionary<IncidentPhase, Dictionary<string, RolePhaseBrief>> BuildContestPhaseScriptsZh()
    {
        return new Dictionary<IncidentPhase, Dictionary<string, RolePhaseBrief>>
        {
            [IncidentPhase.Inception] = new Dictionary<string, RolePhaseBrief>
            {
                ["Host"] = new RolePhaseBrief
                {
                    Motivation = "宣布星之果实酒吧烹饪大赛，招募参赛者，让酒吧座无虚席。",
                    PublicOpinion = "镇民好奇却观望，有人怀疑这不过是酒吧的促销噱头。",
                },
                ["Champion"] = new RolePhaseBrief
                {
                    Motivation = "用一道大胆的新菜卫冕冠军，证明矿工家的安静女孩也能连赢两届。",
                    PublicOpinion = "仰慕者说她无所畏惧；守旧派却嘀咕她的食材简直古怪。",
                },
                ["Skeptic"] = new RolePhaseBrief
                {
                    Motivation = "指出获胜者永远是那几张熟面孔，要求引入外部评委。",
                    PublicOpinion = "少数村民承认评审看着有点内定意味，但多数人劝他别较真。",
                },
            },
            [IncidentPhase.Escalation] = new Dictionary<string, RolePhaseBrief>
            {
                ["Host"] = new RolePhaseBrief
                {
                    Motivation = "在流言与赌注填满酒吧之际，别让比赛彻底失控。",
                    PublicOpinion = "全镇分成了两派，人人都选好了自己支持的对象。",
                },
                ["Champion"] = new RolePhaseBrief
                {
                    Motivation = "私下苦练，对破坏传闻一笑置之，用手艺回应质疑。",
                    PublicOpinion = "她的支持者声势渐涨；批评者则宣称结果早已内定。",
                },
                ["Skeptic"] = new RolePhaseBrief
                {
                    Motivation = "把公平性质疑推向高潮，扬言要抵制决赛。",
                    PublicOpinion = "越来越多的村民开始追问：评审到底是谁？",
                },
            },
            [IncidentPhase.Climax] = new Dictionary<string, RolePhaseBrief>
            {
                ["Host"] = new RolePhaseBrief
                {
                    Motivation = "主持一场干净而有戏剧性的决赛，选出全镇都服气的赢家。",
                    PublicOpinion = "酒吧座无虚席，全镇都想看到一个公正的结果。",
                },
                ["Champion"] = new RolePhaseBrief
                {
                    Motivation = "端出她最大胆的一道菜，让质疑者彻底闭嘴。",
                    PublicOpinion = "连她的批评者都承认这场决赛不容错过。",
                },
                ["Skeptic"] = new RolePhaseBrief
                {
                    Motivation = "像鹰一样盯着评审过程，在认输之前先要求透明公开。",
                    PublicOpinion = "他放话若结果干净，愿赌服输自打嘴巴。",
                },
            },
        };
    }

    // ─────────────────────────────────────────────────────────────
    //  Static fallback — keyword groups, one block per archetype.
    //  Group keys are the persisted RuntimeFlags flag names and stay
    //  language-independent; keywords match the player's dialogue
    //  language so RecordChoice keeps working for both locales.
    // ─────────────────────────────────────────────────────────────
    internal static Dictionary<string, Dictionary<string, string>> BuildContestBranchOutcomesEn()
    {
        return new Dictionary<string, Dictionary<string, string>>
        {
            ["backed_champion"] = new Dictionary<string, string>
            {
                ["cheer"] = "The town sees the player openly backing the champion.",
                ["root for"] = "The town sees the player openly backing the champion.",
                ["encourage"] = "The town sees the player openly backing the champion.",
                ["you can win"] = "The town sees the player openly backing the champion.",
                ["believe in you"] = "The town sees the player openly backing the champion.",
            },
            ["backed_skeptic"] = new Dictionary<string, string>
            {
                ["rigged"] = "The player's doubts embolden the skeptic camp.",
                ["unfair"] = "The player's doubts embolden the skeptic camp.",
                ["fixed"] = "The player's doubts embolden the skeptic camp.",
                ["boycott"] = "The player's doubts embolden the skeptic camp.",
                ["doubt"] = "The player's doubts embolden the skeptic camp.",
            },
            ["stayed_neutral"] = new Dictionary<string, string>
            {
                ["wait and see"] = "The player keeps the town guessing about where they stand.",
                ["fair judge"] = "The player keeps the town guessing about where they stand.",
                ["may the best"] = "The player keeps the town guessing about where they stand.",
                ["let the cooking speak"] = "The player keeps the town guessing about where they stand.",
            },
        };
    }

    internal static Dictionary<string, Dictionary<string, string>> BuildContestBranchOutcomesZh()
    {
        return new Dictionary<string, Dictionary<string, string>>
        {
            ["backed_champion"] = new Dictionary<string, string>
            {
                ["加油"] = "全镇看到玩家公开力挺卫冕者。",
                ["支持"] = "全镇看到玩家公开力挺卫冕者。",
                ["挺你"] = "全镇看到玩家公开力挺卫冕者。",
                ["你能赢"] = "全镇看到玩家公开力挺卫冕者。",
                ["相信你"] = "全镇看到玩家公开力挺卫冕者。",
            },
            ["backed_skeptic"] = new Dictionary<string, string>
            {
                ["黑幕"] = "玩家的质疑壮大了怀疑派的声音。",
                ["不公"] = "玩家的质疑壮大了怀疑派的声音。",
                ["有猫腻"] = "玩家的质疑壮大了怀疑派的声音。",
                ["内定"] = "玩家的质疑壮大了怀疑派的声音。",
                ["抵制"] = "玩家的质疑壮大了怀疑派的声音。",
            },
            ["stayed_neutral"] = new Dictionary<string, string>
            {
                ["再看看"] = "玩家让全镇猜不透他的立场。",
                ["公正"] = "玩家让全镇猜不透他的立场。",
                ["让菜品说话"] = "玩家让全镇猜不透他的立场。",
                ["祝最好的赢"] = "玩家让全镇猜不透他的立场。",
            },
        };
    }

    // ─────────────────────────────────────────────────────────────
    //  Static fallback — Friction phase scripts (Victim, Culprit, Witness)
    // ─────────────────────────────────────────────────────────────
    internal static Dictionary<IncidentPhase, Dictionary<string, RolePhaseBrief>> BuildFrictionPhaseScriptsEn()
    {
        return new Dictionary<IncidentPhase, Dictionary<string, RolePhaseBrief>>
        {
            [IncidentPhase.Inception] = new Dictionary<string, RolePhaseBrief>
            {
                ["Victim"] = new RolePhaseBrief
                {
                    Motivation = "Track down the shipment the ledger says arrived and prove the missing goods are not his imagination.",
                    PublicOpinion = "Neighbors are sympathetic, but nobody wants to be dragged into an argument over receipts.",
                },
                ["Culprit"] = new RolePhaseBrief
                {
                    Motivation = "Defend the store ledger line by line and refuse to reopen a total already settled.",
                    PublicOpinion = "Shop regulars believe the books; a few think he is digging in out of pride.",
                },
                ["Witness"] = new RolePhaseBrief
                {
                    Motivation = "Repeat what he saw behind the counter and stay clear of the fallout.",
                    PublicOpinion = "Both sides want his version, and everyone now asks him to pick one.",
                },
            },
            [IncidentPhase.Escalation] = new Dictionary<string, RolePhaseBrief>
            {
                ["Victim"] = new RolePhaseBrief
                {
                    Motivation = "Collect receipts and ask the neighbors to say what they saw at the counter.",
                    PublicOpinion = "The town is splitting into camps over a few missing crates.",
                },
                ["Culprit"] = new RolePhaseBrief
                {
                    Motivation = "Hold the line on the numbers and warn that doubting the ledger doubts every order.",
                    PublicOpinion = "Some call him principled; others say he would rather be right than neighborly.",
                },
                ["Witness"] = new RolePhaseBrief
                {
                    Motivation = "Stick to the details he is sure of while both sides press him for more.",
                    PublicOpinion = "His account is the one everyone quotes now, whether he likes it or not.",
                },
            },
            [IncidentPhase.Climax] = new Dictionary<string, RolePhaseBrief>
            {
                ["Victim"] = new RolePhaseBrief
                {
                    Motivation = "Lay the receipts on the counter and ask for a ruling the whole town can hear.",
                    PublicOpinion = "The store is crowded; everyone wants the ledger settled out loud.",
                },
                ["Culprit"] = new RolePhaseBrief
                {
                    Motivation = "Read the ledger aloud and accept whatever the room decides about the missing order.",
                    PublicOpinion = "Even his supporters admit the dispute has gone on long enough.",
                },
                ["Witness"] = new RolePhaseBrief
                {
                    Motivation = "Say exactly what happened and let the town weigh it against the ledger.",
                    PublicOpinion = "Whatever he says now will settle the argument.",
                },
            },
        };
    }

    internal static Dictionary<IncidentPhase, Dictionary<string, RolePhaseBrief>> BuildFrictionPhaseScriptsZh()
    {
        return new Dictionary<IncidentPhase, Dictionary<string, RolePhaseBrief>>
        {
            [IncidentPhase.Inception] = new Dictionary<string, RolePhaseBrief>
            {
                ["Victim"] = new RolePhaseBrief
                {
                    Motivation = "找出账本上那批「已到货」的货物，证明失踪的货不是他凭空想象出来的。",
                    PublicOpinion = "邻居们同情他，但没人愿意被卷进一场收据之争。",
                },
                ["Culprit"] = new RolePhaseBrief
                {
                    Motivation = "逐条守住账本，拒绝重算一个早已结清的总数。",
                    PublicOpinion = "常客都相信账本；也有人觉得他只是拉不下面子。",
                },
                ["Witness"] = new RolePhaseBrief
                {
                    Motivation = "如实重复自己在柜台后看到的一切，同时别被这场风波波及。",
                    PublicOpinion = "两边都想听他的说法，如今人人都逼他选一边。",
                },
            },
            [IncidentPhase.Escalation] = new Dictionary<string, RolePhaseBrief>
            {
                ["Victim"] = new RolePhaseBrief
                {
                    Motivation = "四处收集收据，请邻居说出他们在柜台边看到的事。",
                    PublicOpinion = "全镇因为几只不见的货箱分成了两派。",
                },
                ["Culprit"] = new RolePhaseBrief
                {
                    Motivation = "在数字上寸步不让，并警告质疑账本就等于质疑每一笔订单。",
                    PublicOpinion = "有人说他有原则；也有人说他宁可做对也不肯讲人情。",
                },
                ["Witness"] = new RolePhaseBrief
                {
                    Motivation = "只坚持自己确定的细节，不理会两边的施压。",
                    PublicOpinion = "他的说法现在人人都在引用，不管他愿不愿意。",
                },
            },
            [IncidentPhase.Climax] = new Dictionary<string, RolePhaseBrief>
            {
                ["Victim"] = new RolePhaseBrief
                {
                    Motivation = "把收据摊在柜台上，要求一个全镇都听得见的结论。",
                    PublicOpinion = "店里挤满了人，大家都想听账本给出一个说法。",
                },
                ["Culprit"] = new RolePhaseBrief
                {
                    Motivation = "把账本念出来，并接受在场所有人关于这批失踪货物的判断。",
                    PublicOpinion = "连支持他的人也承认这场争执拖得太久了。",
                },
                ["Witness"] = new RolePhaseBrief
                {
                    Motivation = "说出当时到底发生了什么，让全镇自己去和账本对照。",
                    PublicOpinion = "他现在说什么都会为这场争论定调。",
                },
            },
        };
    }

    // ─────────────────────────────────────────────────────────────
    //  Static fallback — Friction keyword groups
    // ─────────────────────────────────────────────────────────────
    internal static Dictionary<string, Dictionary<string, string>> BuildFrictionBranchOutcomesEn()
    {
        return new Dictionary<string, Dictionary<string, string>>
        {
            ["backed_victim"] = new Dictionary<string, string>
            {
                ["missing shipment"] = "The town sees the player siding with the victim of the ledger dispute.",
                ["check the ledger"] = "The town sees the player siding with the victim of the ledger dispute.",
                ["he is right"] = "The town sees the player siding with the victim of the ledger dispute.",
                ["not his fault"] = "The town sees the player siding with the victim of the ledger dispute.",
                ["counted twice"] = "The town sees the player siding with the victim of the ledger dispute.",
            },
            ["backed_culprit"] = new Dictionary<string, string>
            {
                ["ledger is right"] = "The town sees the player standing behind the shopkeeper's ledger.",
                ["books never lie"] = "The town sees the player standing behind the shopkeeper's ledger.",
                ["trust the numbers"] = "The town sees the player standing behind the shopkeeper's ledger.",
                ["already settled"] = "The town sees the player standing behind the shopkeeper's ledger.",
                ["misread"] = "The town sees the player standing behind the shopkeeper's ledger.",
            },
            ["stayed_neutral"] = new Dictionary<string, string>
            {
                ["wait for the receipts"] = "The player keeps both sides guessing about the ledger dispute.",
                ["both of you"] = "The player keeps both sides guessing about the ledger dispute.",
                ["let Pierre decide"] = "The player keeps both sides guessing about the ledger dispute.",
                ["check the receipts"] = "The player keeps both sides guessing about the ledger dispute.",
            },
        };
    }

    internal static Dictionary<string, Dictionary<string, string>> BuildFrictionBranchOutcomesZh()
    {
        return new Dictionary<string, Dictionary<string, string>>
        {
            ["backed_victim"] = new Dictionary<string, string>
            {
                ["货没到"] = "全镇看到玩家站在账本争端中吃亏的一方。",
                ["查账本"] = "全镇看到玩家站在账本争端中吃亏的一方。",
                ["相信他"] = "全镇看到玩家站在账本争端中吃亏的一方。",
                ["不是他的错"] = "全镇看到玩家站在账本争端中吃亏的一方。",
                ["数错了"] = "全镇看到玩家站在账本争端中吃亏的一方。",
            },
            ["backed_culprit"] = new Dictionary<string, string>
            {
                ["账本没错"] = "全镇看到玩家站在店主账本这一边。",
                ["账本不会骗人"] = "全镇看到玩家站在店主账本这一边。",
                ["相信数字"] = "全镇看到玩家站在店主账本这一边。",
                ["早就结清了"] = "全镇看到玩家站在店主账本这一边。",
                ["看错了"] = "全镇看到玩家站在店主账本这一边。",
            },
            ["stayed_neutral"] = new Dictionary<string, string>
            {
                ["等收据"] = "玩家在这场账本争端中让双方都摸不清立场。",
                ["你们两个"] = "玩家在这场账本争端中让双方都摸不清立场。",
                ["让皮埃尔决定"] = "玩家在这场账本争端中让双方都摸不清立场。",
                ["先查收据"] = "玩家在这场账本争端中让双方都摸不清立场。",
            },
        };
    }

    // ─────────────────────────────────────────────────────────────
    //  Static fallback — Mystery phase scripts (Loser, Suspect, Investigator)
    // ─────────────────────────────────────────────────────────────
    internal static Dictionary<IncidentPhase, Dictionary<string, RolePhaseBrief>> BuildMysteryPhaseScriptsEn()
    {
        return new Dictionary<IncidentPhase, Dictionary<string, RolePhaseBrief>>
        {
            [IncidentPhase.Inception] = new Dictionary<string, RolePhaseBrief>
            {
                ["Loser"] = new RolePhaseBrief
                {
                    Motivation = "Search every corner of the saloon for the missing heirloom and refuse to believe it is simply gone.",
                    PublicOpinion = "The town feels sorry for her, though a few quietly wonder whether she misplaced it.",
                },
                ["Suspect"] = new RolePhaseBrief
                {
                    Motivation = "Carry on as usual and deny any part in the disappearance.",
                    PublicOpinion = "Nobody has accused her outright, but the looks across the bar say otherwise.",
                },
                ["Investigator"] = new RolePhaseBrief
                {
                    Motivation = "Ask who was in the saloon and take every answer seriously before rumors harden.",
                    PublicOpinion = "People are glad someone is asking; nobody wants to be the one named.",
                },
            },
            [IncidentPhase.Escalation] = new Dictionary<string, RolePhaseBrief>
            {
                ["Loser"] = new RolePhaseBrief
                {
                    Motivation = "Retrace the whole evening and press anyone who stood near that shelf.",
                    PublicOpinion = "Sympathy is thinning as the questions keep coming.",
                },
                ["Suspect"] = new RolePhaseBrief
                {
                    Motivation = "Keep her distance from the saloon while the story grows around her.",
                    PublicOpinion = "The more she stays away, the more the town reads it as guilt.",
                },
                ["Investigator"] = new RolePhaseBrief
                {
                    Motivation = "Compare what each regular claims to have seen and note what does not line up.",
                    PublicOpinion = "Everyone has a theory now, and most of them point at someone.",
                },
            },
            [IncidentPhase.Climax] = new Dictionary<string, RolePhaseBrief>
            {
                ["Loser"] = new RolePhaseBrief
                {
                    Motivation = "Hear the investigator out in front of everyone and accept whatever the night turns up.",
                    PublicOpinion = "The saloon has gone quiet; everyone wants the heirloom found or the blame named.",
                },
                ["Suspect"] = new RolePhaseBrief
                {
                    Motivation = "Answer the question directly, in front of the room, and let the town judge.",
                    PublicOpinion = "Every eye in the saloon is on her answer.",
                },
                ["Investigator"] = new RolePhaseBrief
                {
                    Motivation = "Lay out what the accounts agree on and say plainly what still cannot be proven.",
                    PublicOpinion = "The room wants a name, but it will settle for the truth.",
                },
            },
        };
    }

    internal static Dictionary<IncidentPhase, Dictionary<string, RolePhaseBrief>> BuildMysteryPhaseScriptsZh()
    {
        return new Dictionary<IncidentPhase, Dictionary<string, RolePhaseBrief>>
        {
            [IncidentPhase.Inception] = new Dictionary<string, RolePhaseBrief>
            {
                ["Loser"] = new RolePhaseBrief
                {
                    Motivation = "翻遍酒吧的每个角落寻找那件失踪的传家宝，绝不相信它就这么没了。",
                    PublicOpinion = "全镇都为她难过，但也有人暗自嘀咕是不是她自己放错了地方。",
                },
                ["Suspect"] = new RolePhaseBrief
                {
                    Motivation = "照常过日子，否认自己和东西失踪有任何关系。",
                    PublicOpinion = "没有人当面指认她，可吧台对面的眼神说明了另一回事。",
                },
                ["Investigator"] = new RolePhaseBrief
                {
                    Motivation = "挨个问清当晚谁在酒吧，在流言定形之前认真对待每一句回答。",
                    PublicOpinion = "大家很高兴有人在查，但谁都不想被点名。",
                },
            },
            [IncidentPhase.Escalation] = new Dictionary<string, RolePhaseBrief>
            {
                ["Loser"] = new RolePhaseBrief
                {
                    Motivation = "把整晚的经过重走一遍，追问每个靠近过那面架子的人。",
                    PublicOpinion = "随着追问不断，同情正在一点点变薄。",
                },
                ["Suspect"] = new RolePhaseBrief
                {
                    Motivation = "在议论越滚越大时，刻意远离酒吧。",
                    PublicOpinion = "她越是躲着不来，全镇越把那当成心虚。",
                },
                ["Investigator"] = new RolePhaseBrief
                {
                    Motivation = "比对每个常客声称看到的事，记下对不上的地方。",
                    PublicOpinion = "如今人人都有一个说法，而且大多指向某个人。",
                },
            },
            [IncidentPhase.Climax] = new Dictionary<string, RolePhaseBrief>
            {
                ["Loser"] = new RolePhaseBrief
                {
                    Motivation = "当着所有人的面听完调查者的说法，接受今晚查出的任何结果。",
                    PublicOpinion = "酒吧安静下来，大家都想找回传家宝，或者至少要个说法。",
                },
                ["Suspect"] = new RolePhaseBrief
                {
                    Motivation = "当着全场的面正面回答那个问题，让全镇自己判断。",
                    PublicOpinion = "酒吧里每一双眼睛都盯着她的回答。",
                },
                ["Investigator"] = new RolePhaseBrief
                {
                    Motivation = "摆出各份说法一致的地方，也坦白说清仍然无法证实的部分。",
                    PublicOpinion = "全场想要一个名字，但最终会接受真相。",
                },
            },
        };
    }

    // ─────────────────────────────────────────────────────────────
    //  Static fallback — Mystery keyword groups
    // ─────────────────────────────────────────────────────────────
    internal static Dictionary<string, Dictionary<string, string>> BuildMysteryBranchOutcomesEn()
    {
        return new Dictionary<string, Dictionary<string, string>>
        {
            ["backed_loser"] = new Dictionary<string, string>
            {
                ["keep searching"] = "The player's help keeps the search for the vanished heirloom alive.",
                ["help her look"] = "The player's help keeps the search for the vanished heirloom alive.",
                ["it will turn up"] = "The player's help keeps the search for the vanished heirloom alive.",
                ["check the saloon"] = "The player's help keeps the search for the vanished heirloom alive.",
                ["she would never"] = "The player's help keeps the search for the vanished heirloom alive.",
            },
            ["backed_suspect"] = new Dictionary<string, string>
            {
                ["you took it"] = "The player's accusation turns the town against the suspect.",
                ["saw you there"] = "The player's accusation turns the town against the suspect.",
                ["confess"] = "The player's accusation turns the town against the suspect.",
                ["it was you"] = "The player's accusation turns the town against the suspect.",
                ["suspicious"] = "The player's accusation turns the town against the suspect.",
            },
            ["stayed_neutral"] = new Dictionary<string, string>
            {
                ["no accusations"] = "The player refuses to name a thief and the town stays unsure.",
                ["wait for proof"] = "The player refuses to name a thief and the town stays unsure.",
                ["anyone could have"] = "The player refuses to name a thief and the town stays unsure.",
                ["let Lewis handle it"] = "The player refuses to name a thief and the town stays unsure.",
            },
        };
    }

    internal static Dictionary<string, Dictionary<string, string>> BuildMysteryBranchOutcomesZh()
    {
        return new Dictionary<string, Dictionary<string, string>>
        {
            ["backed_loser"] = new Dictionary<string, string>
            {
                ["继续找"] = "玩家的帮助让寻找传家宝的行动继续下去。",
                ["帮她找"] = "玩家的帮助让寻找传家宝的行动继续下去。",
                ["会找到的"] = "玩家的帮助让寻找传家宝的行动继续下去。",
                ["搜一下酒吧"] = "玩家的帮助让寻找传家宝的行动继续下去。",
                ["她不会骗人"] = "玩家的帮助让寻找传家宝的行动继续下去。",
            },
            ["backed_suspect"] = new Dictionary<string, string>
            {
                ["是你拿的"] = "玩家的指控让全镇把怀疑指向那个嫌疑人。",
                ["看见你了"] = "玩家的指控让全镇把怀疑指向那个嫌疑人。",
                ["承认吧"] = "玩家的指控让全镇把怀疑指向那个嫌疑人。",
                ["就是你"] = "玩家的指控让全镇把怀疑指向那个嫌疑人。",
                ["很可疑"] = "玩家的指控让全镇把怀疑指向那个嫌疑人。",
            },
            ["stayed_neutral"] = new Dictionary<string, string>
            {
                ["别乱指认"] = "玩家不愿指认小偷，全镇仍然疑云未散。",
                ["等证据"] = "玩家不愿指认小偷，全镇仍然疑云未散。",
                ["谁都有可能"] = "玩家不愿指认小偷，全镇仍然疑云未散。",
                ["让路易斯处理"] = "玩家不愿指认小偷，全镇仍然疑云未散。",
            },
        };
    }

    // ─────────────────────────────────────────────────────────────
    //  Static fallback — Collaboration phase scripts (Organizer, Worker, Slacker)
    // ─────────────────────────────────────────────────────────────
    internal static Dictionary<IncidentPhase, Dictionary<string, RolePhaseBrief>> BuildCollaborationPhaseScriptsEn()
    {
        return new Dictionary<IncidentPhase, Dictionary<string, RolePhaseBrief>>
        {
            [IncidentPhase.Inception] = new Dictionary<string, RolePhaseBrief>
            {
                ["Organizer"] = new RolePhaseBrief
                {
                    Motivation = "Round up volunteers for the restoration and promise the community center will shine again.",
                    PublicOpinion = "The town likes the idea, but nobody has taken the heavy part of it yet.",
                },
                ["Worker"] = new RolePhaseBrief
                {
                    Motivation = "Start hauling materials alone so the work is under way before the talking ends.",
                    PublicOpinion = "People admire the effort and quietly let him keep doing it.",
                },
                ["Slacker"] = new RolePhaseBrief
                {
                    Motivation = "Praise the project loudly while staying carefully uncommitted to any shift.",
                    PublicOpinion = "Nobody is angry yet, but the absences have already been noticed.",
                },
            },
            [IncidentPhase.Escalation] = new Dictionary<string, RolePhaseBrief>
            {
                ["Organizer"] = new RolePhaseBrief
                {
                    Motivation = "Keep recruiting and defend the schedule now that the workload starts to bite.",
                    PublicOpinion = "Enthusiasm is thinning now that the work has become real.",
                },
                ["Worker"] = new RolePhaseBrief
                {
                    Motivation = "Carry another load alone and make it clear he cannot keep doing all of it.",
                    PublicOpinion = "The town is starting to admit the load is not being shared.",
                },
                ["Slacker"] = new RolePhaseBrief
                {
                    Motivation = "Offer encouragement and one more reason to be somewhere else today.",
                    PublicOpinion = "Patience with him runs out one missed morning at a time.",
                },
            },
            [IncidentPhase.Climax] = new Dictionary<string, RolePhaseBrief>
            {
                ["Organizer"] = new RolePhaseBrief
                {
                    Motivation = "Put a plain rota on the wall and ask the town to take a shift each.",
                    PublicOpinion = "Everyone agrees the restoration is worth it; nobody wants to be the only one working.",
                },
                ["Worker"] = new RolePhaseBrief
                {
                    Motivation = "Say what he has carried and let the room decide how the rest is shared.",
                    PublicOpinion = "The room is on his side, and the slacker can feel it.",
                },
                ["Slacker"] = new RolePhaseBrief
                {
                    Motivation = "Show up and take a real shift, or own the fact that he never did.",
                    PublicOpinion = "Whatever happens today will be remembered by everyone who helped.",
                },
            },
        };
    }

    internal static Dictionary<IncidentPhase, Dictionary<string, RolePhaseBrief>> BuildCollaborationPhaseScriptsZh()
    {
        return new Dictionary<IncidentPhase, Dictionary<string, RolePhaseBrief>>
        {
            [IncidentPhase.Inception] = new Dictionary<string, RolePhaseBrief>
            {
                ["Organizer"] = new RolePhaseBrief
                {
                    Motivation = "四处招募志愿者，并向大家保证社区中心会重新亮起来。",
                    PublicOpinion = "全镇都觉得这主意不错，但还没人接下最累的那部分。",
                },
                ["Worker"] = new RolePhaseBrief
                {
                    Motivation = "在讨论结束之前就独自开始搬运材料，让工程先动起来。",
                    PublicOpinion = "人们佩服这份干劲，也默许他一个人继续干下去。",
                },
                ["Slacker"] = new RolePhaseBrief
                {
                    Motivation = "大声称赞这个计划，同时小心地不承诺任何一班工。",
                    PublicOpinion = "目前还没人生气，但缺席已经被大家看在眼里。",
                },
            },
            [IncidentPhase.Escalation] = new Dictionary<string, RolePhaseBrief>
            {
                ["Organizer"] = new RolePhaseBrief
                {
                    Motivation = "一边继续招人，一边在工作量开始咬人时为工期辩护。",
                    PublicOpinion = "活变成真的了，热情也随之变淡。",
                },
                ["Worker"] = new RolePhaseBrief
                {
                    Motivation = "又独自扛下一趟，并明确表示自己不可能一直全包。",
                    PublicOpinion = "全镇开始承认这份活并没有被分摊。",
                },
                ["Slacker"] = new RolePhaseBrief
                {
                    Motivation = "送上鼓励，再给出一个今天得去别处的理由。",
                    PublicOpinion = "大家对他的耐心，正随着一个个缺席的早晨耗尽。",
                },
            },
            [IncidentPhase.Climax] = new Dictionary<string, RolePhaseBrief>
            {
                ["Organizer"] = new RolePhaseBrief
                {
                    Motivation = "把一张清楚的值班表贴在墙上，请每家各认领一班。",
                    PublicOpinion = "人人都认为这次修复值得做；没人想成为唯一干活的那个人。",
                },
                ["Worker"] = new RolePhaseBrief
                {
                    Motivation = "说出自己扛下了多少，让在场的人决定剩下的怎么分。",
                    PublicOpinion = "全场都站在他这边，那个偷懒的人也感觉到了。",
                },
                ["Slacker"] = new RolePhaseBrief
                {
                    Motivation = "要么到场认领一班真正的活，要么承认自己从没出过力。",
                    PublicOpinion = "今天发生的一切，每个出过力的人都会记着。",
                },
            },
        };
    }

    // ─────────────────────────────────────────────────────────────
    //  Static fallback — Collaboration keyword groups
    // ─────────────────────────────────────────────────────────────
    internal static Dictionary<string, Dictionary<string, string>> BuildCollaborationBranchOutcomesEn()
    {
        return new Dictionary<string, Dictionary<string, string>>
        {
            ["backed_organizer"] = new Dictionary<string, string>
            {
                ["sign me up"] = "The player's support rallies the town behind the restoration organizer.",
                ["count me in"] = "The player's support rallies the town behind the restoration organizer.",
                ["we can do it"] = "The player's support rallies the town behind the restoration organizer.",
                ["organize"] = "The player's support rallies the town behind the restoration organizer.",
                ["let's rally"] = "The player's support rallies the town behind the restoration organizer.",
            },
            ["backed_worker"] = new Dictionary<string, string>
            {
                ["you carried it"] = "The player backs the worker who has carried the restoration alone.",
                ["that is unfair"] = "The player backs the worker who has carried the restoration alone.",
                ["give him help"] = "The player backs the worker who has carried the restoration alone.",
                ["you did the work"] = "The player backs the worker who has carried the restoration alone.",
                ["share the load"] = "The player backs the worker who has carried the restoration alone.",
            },
            ["stayed_neutral"] = new Dictionary<string, string>
            {
                ["everyone helps"] = "The player stays out of the workload argument.",
                ["not my fight"] = "The player stays out of the workload argument.",
                ["see how it goes"] = "The player stays out of the workload argument.",
                ["let them sort it"] = "The player stays out of the workload argument.",
            },
        };
    }

    internal static Dictionary<string, Dictionary<string, string>> BuildCollaborationBranchOutcomesZh()
    {
        return new Dictionary<string, Dictionary<string, string>>
        {
            ["backed_organizer"] = new Dictionary<string, string>
            {
                ["我报名"] = "玩家的支持让全镇团结在修复工程的组织者身后。",
                ["算我一个"] = "玩家的支持让全镇团结在修复工程的组织者身后。",
                ["我们能做到"] = "玩家的支持让全镇团结在修复工程的组织者身后。",
                ["组织起来"] = "玩家的支持让全镇团结在修复工程的组织者身后。",
                ["大家一起"] = "玩家的支持让全镇团结在修复工程的组织者身后。",
            },
            ["backed_worker"] = new Dictionary<string, string>
            {
                ["都是你扛的"] = "玩家力挺那个独自扛下修复工作的村民。",
                ["这不公平"] = "玩家力挺那个独自扛下修复工作的村民。",
                ["帮帮他"] = "玩家力挺那个独自扛下修复工作的村民。",
                ["活都是你干的"] = "玩家力挺那个独自扛下修复工作的村民。",
                ["分担一下"] = "玩家力挺那个独自扛下修复工作的村民。",
            },
            ["stayed_neutral"] = new Dictionary<string, string>
            {
                ["大家都出力"] = "玩家不介入这场分工争执。",
                ["不关我事"] = "玩家不介入这场分工争执。",
                ["看看再说"] = "玩家不介入这场分工争执。",
                ["让他们自己解决"] = "玩家不介入这场分工争执。",
            },
        };
    }

    // ─────────────────────────────────────────────────────────────
    //  TIE-009C — fallback dispatch. Bounded lookup on the persisted
    //  archetype id: an unknown id yields false with no content, so
    //  the caller can refuse to activate instead of borrowing the
    //  Contest script. Inner phase keys are RequiredRoles names.
    // ─────────────────────────────────────────────────────────────
    internal static bool TryGetFallback(
        string archetypeId,
        bool isChinese,
        out Dictionary<IncidentPhase, Dictionary<string, RolePhaseBrief>> phaseScripts,
        out Dictionary<string, Dictionary<string, string>> branchOutcomes)
    {
        if (string.Equals(archetypeId, nameof(TownIncidentArchetype.Contest), StringComparison.Ordinal))
        {
            phaseScripts = isChinese ? BuildContestPhaseScriptsZh() : BuildContestPhaseScriptsEn();
            branchOutcomes = isChinese ? BuildContestBranchOutcomesZh() : BuildContestBranchOutcomesEn();
            return true;
        }

        if (string.Equals(archetypeId, nameof(TownIncidentArchetype.Friction), StringComparison.Ordinal))
        {
            phaseScripts = isChinese ? BuildFrictionPhaseScriptsZh() : BuildFrictionPhaseScriptsEn();
            branchOutcomes = isChinese ? BuildFrictionBranchOutcomesZh() : BuildFrictionBranchOutcomesEn();
            return true;
        }

        if (string.Equals(archetypeId, nameof(TownIncidentArchetype.Mystery), StringComparison.Ordinal))
        {
            phaseScripts = isChinese ? BuildMysteryPhaseScriptsZh() : BuildMysteryPhaseScriptsEn();
            branchOutcomes = isChinese ? BuildMysteryBranchOutcomesZh() : BuildMysteryBranchOutcomesEn();
            return true;
        }

        if (string.Equals(archetypeId, nameof(TownIncidentArchetype.Collaboration), StringComparison.Ordinal))
        {
            phaseScripts = isChinese ? BuildCollaborationPhaseScriptsZh() : BuildCollaborationPhaseScriptsEn();
            branchOutcomes = isChinese ? BuildCollaborationBranchOutcomesZh() : BuildCollaborationBranchOutcomesEn();
            return true;
        }

        phaseScripts = null;
        branchOutcomes = null;
        return false;
    }

    // ─────────────────────────────────────────────────────────────
    //  TIE-009D — incident rumor line templates, one per archetype.
    //  Positional placeholders: {0} = the NPC spreading the rumor,
    //  {1}/{2}/{3} = the archetype's RequiredRoles in catalog order,
    //  {4} = EventName. Contest entries keep the exact pre-TIE-009D
    //  strings, so existing Contest output is byte-identical.
    // ─────────────────────────────────────────────────────────────
    private static readonly Dictionary<string, string> IncidentRumorTemplatesEn =
        new(StringComparer.Ordinal)
        {
            ["Contest"] = "{0} has heard the talk of the town: {1} is hosting the {4}, {2} is out to defend the title, and {3} keeps telling anyone who will listen that the judging favors the regulars.",
            ["Friction"] = "{0} has heard the talk of the town: {1} swears the {4} started with an order that never arrived, {2} insists the ledger has it right, and {3} says they saw the whole thing from the counter.",
            ["Mystery"] = "{0} has heard the talk of the town: {1} has been hunting for the {4} all week, {2} goes quiet whenever it comes up, and {3} is asking everyone who was in the room.",
            ["Collaboration"] = "{0} has heard the talk of the town: {1} keeps rounding up help for the {4}, {2} has hauled most of it alone so far, and {3} always has a reason to be somewhere else.",
        };

    private static readonly Dictionary<string, string> IncidentRumorTemplatesZh =
        new(StringComparer.Ordinal)
        {
            ["Contest"] = "{0} 听说了镇上最近的热议：{1} 要在酒吧办一场烹饪大赛，{2} 准备卫冕冠军，而 {3} 见人就嘀咕评审偏袒熟面孔。",
            ["Friction"] = "{0} 听说了镇上最近的热议：{1} 咬定「{4}」起于一批没送到的货，{2} 坚持账本从没记错，而 {3} 说当时就站在柜台边看得一清二楚。",
            ["Mystery"] = "{0} 听说了镇上最近的热议：{1} 为「{4}」找了整整一周，{2} 一被问起就沉默，而 {3} 正在挨个盘问当晚在场的人。",
            ["Collaboration"] = "{0} 听说了镇上最近的热议：{1} 还在为「{4}」四处招人，{2} 几乎一个人扛下了所有搬运，而 {3} 总有理由出现在别的地方。",
        };

    /// <summary>
    /// TIE-009D: one deterministic rumor line for the given archetype, built
    /// from the claiming NPC name, the archetype's assigned role NPCs (in
    /// <see cref="IncidentArchetypeDefinition.RequiredRoles"/> order) and the
    /// shell's EventName. Returns null when the archetype has no template.
    /// </summary>
    internal static string BuildIncidentRumor(
        string archetypeId, bool isChinese, string npcName, IReadOnlyList<string> roleNpcs, string eventName)
    {
        var templates = isChinese ? IncidentRumorTemplatesZh : IncidentRumorTemplatesEn;
        if (!templates.TryGetValue(archetypeId, out string template))
            return null;

        var args = new List<string>(roleNpcs.Count + 2) { npcName };
        args.AddRange(roleNpcs);
        args.Add(eventName);

        return string.Format(template, args.ToArray());
    }

    // ─────────────────────────────────────────────────────────────
    //  Scriptwriter prompt templates (compact, JSON-only)
    // ─────────────────────────────────────────────────────────────
    internal static string BuildSystemPrompt(bool isChinese)
    {
        return isChinese ? BuildSystemPromptZh() : BuildSystemPromptEn();
    }

    private static string BuildSystemPromptEn() =>
        """
        You are the scriptwriter for a Stardew Valley town-incident mod. You fill one incident with acting briefs.
        Reply with ONE raw JSON object and nothing else — no markdown fences, no commentary. Exact shape:
        {"IncidentId":"","ArchetypeId":"","AssignedRoles":{"Role":"NPCName"},"EventName":"","IncidentTheme":"","PhaseScripts":{"Inception":{"NPCName":{"Motivation":"","PublicOpinion":""}},"Escalation":{},"Climax":{}},"BranchOutcomes":{"GroupKey":{"keyword":"one-sentence outcome"}}}
        Rules:
        - Echo IncidentId, ArchetypeId and AssignedRoles exactly as given; never change the role assignment.
        - Every phase (Inception, Escalation, Climax) must contain a Motivation and PublicOpinion brief for every assigned NPC, keyed by the exact NPC names.
        - BranchOutcomes must use exactly the given group keys; each keyword is a short phrase a player might say, mapping to a one-sentence outcome.
        - Maximum lengths in characters: Motivation 200, PublicOpinion 200, EventName 40, IncidentTheme 160, keyword 40, outcome 200.
        - Write all narrative text in natural English; keep characters true to their Stardew Valley personalities.
        """;

    private static string BuildSystemPromptZh() =>
        """
        你是《星露谷物语》镇事件模组的剧本撰写者，负责为一个镇事件撰写角色行动简报。
        只回复一个原始 JSON 对象——不要 Markdown 代码块、不要任何解释。严格结构：
        {"IncidentId":"","ArchetypeId":"","AssignedRoles":{"Role":"NPCName"},"EventName":"","IncidentTheme":"","PhaseScripts":{"Inception":{"NPCName":{"Motivation":"","PublicOpinion":""}},"Escalation":{},"Climax":{}},"BranchOutcomes":{"GroupKey":{"关键词":"一句话后果"}}}
        规则：
        - IncidentId、ArchetypeId、AssignedRoles 必须与给定内容完全一致，严禁改动角色分配。
        - 每个阶段（Inception、Escalation、Climax）都必须为每个已分配 NPC 撰写 Motivation 与 PublicOpinion 简报，键名使用 NPC 的确切名字。
        - BranchOutcomes 必须原样使用给定的组键；每个关键词是玩家可能说出的短语，对应一句话后果。
        - 字符上限：Motivation 200、PublicOpinion 200、EventName 40、IncidentTheme 160、关键词 40、后果 200。
        - 所有叙事文本使用自然的中文；角色性格须符合星露谷物语原作设定。
        """;

    /// <summary>
    /// TIE-009C: the elapsed-day window of each phase, derived from the shell
    /// duration D — boundary = ceil(D/3); Inception covers elapsed days below
    /// the boundary, Escalation below twice the boundary, Climax the rest.
    /// An 8-day shell keeps emitting the pre-TIE-009C text.
    /// </summary>
    internal static string BuildPhaseWindowText(int durationDays, bool isChinese)
    {
        int boundary = (durationDays + 2) / 3;
        int inceptionEnd = boundary - 1;
        int escalationEnd = (2 * boundary) - 1;
        int climaxStart = 2 * boundary;
        int climaxEnd = durationDays - 1;

        return isChinese
            ? $"Inception = 第 0-{inceptionEnd} 天，Escalation = 第 {boundary}-{escalationEnd} 天，Climax = 第 {climaxStart}-{climaxEnd} 天"
            : $"Inception = elapsed days 0-{inceptionEnd}, Escalation = {boundary}-{escalationEnd}, Climax = {climaxStart}-{climaxEnd}";
    }

    internal static string BuildUserPrompt(EventSlotContract shell, bool isChinese)
    {
        string roles = string.Join(isChinese ? "；" : "; ",
            shell.AssignedRoles.Select(kv => $"{kv.Key}={kv.Value}"));
        string groupKeys = string.Join(", ", shell.BranchOutcomes.Keys);
        string phaseWindow = BuildPhaseWindowText(shell.DurationDays, isChinese);

        if (isChinese)
        {
            return $"""
                事件「{shell.EventName}」（{shell.IncidentId}），原型 {shell.ArchetypeId}。主题：{shell.IncidentTheme}。
                角色（原样回显）：{roles}。
                时间线：自游戏第 {shell.StartGameDay} 天起共 {shell.DurationDays} 天。阶段：{phaseWindow}（决赛地点 {shell.ClimaxLocation}，时间 {shell.ClimaxTimeOfDay}）。
                分支组键（必须原样使用）：{groupKeys}。
                现在输出完整 JSON 对象。
                """;
        }

        return $"""
            Incident "{shell.EventName}" ({shell.IncidentId}), archetype {shell.ArchetypeId}. Theme: {shell.IncidentTheme}.
            Roles (echo exactly): {roles}.
            Timeline: {shell.DurationDays} days starting game day {shell.StartGameDay}. Phases: {phaseWindow} (finale at the {shell.ClimaxLocation}, {shell.ClimaxTimeOfDay}).
            Branch group keys (use exactly these): {groupKeys}.
            Write the complete JSON object now.
            """;
    }
}
