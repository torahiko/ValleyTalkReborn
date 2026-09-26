using System.Collections.Generic;
using System.Linq;

namespace ValleytalkReborn;

/// <summary>
/// TIE-002: static content catalog for the Town Incident Scriptwriter.
/// Holds the bilingual Contest fallback scripts (installed immediately at
/// incident creation, usable without any LLM) and the compact JSON-only
/// scriptwriter prompt templates. Pure C# — no game or SMAPI access, safe
/// to compile and exercise from parser tests.
/// </summary>
internal static class TownIncidentTemplateCatalog
{
    // ─────────────────────────────────────────────────────────────
    //  Static fallback — Contest phase scripts
    // ─────────────────────────────────────────────────────────────
    internal static Dictionary<IncidentPhase, Dictionary<string, RolePhaseBrief>> BuildContestPhaseScriptsEn()
    {
        return new Dictionary<IncidentPhase, Dictionary<string, RolePhaseBrief>>
        {
            [IncidentPhase.Inception] = new Dictionary<string, RolePhaseBrief>
            {
                ["Gus"] = new RolePhaseBrief
                {
                    Motivation = "Announce the Saloon Cook-Off, recruit contestants and fill every seat in the saloon.",
                    PublicOpinion = "Townsfolk are curious but noncommittal; some suspect it is just a saloon promotion.",
                },
                ["Abigail"] = new RolePhaseBrief
                {
                    Motivation = "Defend her title with a daring new recipe and prove the quiet miner's daughter can win twice.",
                    PublicOpinion = "Admirers call her fearless; traditionalists whisper that her ingredients are simply strange.",
                },
                ["Alex"] = new RolePhaseBrief
                {
                    Motivation = "Point out that the same regulars always win and push for an outside judge.",
                    PublicOpinion = "A handful of villagers admit the judging looks cozy, but most tell him to lighten up.",
                },
            },
            [IncidentPhase.Escalation] = new Dictionary<string, RolePhaseBrief>
            {
                ["Gus"] = new RolePhaseBrief
                {
                    Motivation = "Keep the contest from boiling over while rumors and side bets fill the saloon.",
                    PublicOpinion = "The town has split into camps; everyone has picked a favorite.",
                },
                ["Abigail"] = new RolePhaseBrief
                {
                    Motivation = "Train in secret, shrug off the sabotage rumors and let her cooking answer the doubt.",
                    PublicOpinion = "Her fans grow louder; her critics claim the fix is already in.",
                },
                ["Alex"] = new RolePhaseBrief
                {
                    Motivation = "Escalate the fairness complaints and float the idea of boycotting the finale.",
                    PublicOpinion = "More villagers start asking who is really judging.",
                },
            },
            [IncidentPhase.Climax] = new Dictionary<string, RolePhaseBrief>
            {
                ["Gus"] = new RolePhaseBrief
                {
                    Motivation = "Host a clean, dramatic finale and crown a winner the whole town can accept.",
                    PublicOpinion = "The saloon is packed; the whole town wants a fair result.",
                },
                ["Abigail"] = new RolePhaseBrief
                {
                    Motivation = "Plate her boldest dish yet and silence the doubters for good.",
                    PublicOpinion = "Even her critics admit the finale is must-see.",
                },
                ["Alex"] = new RolePhaseBrief
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
                ["Gus"] = new RolePhaseBrief
                {
                    Motivation = "宣布星之果实酒吧烹饪大赛，招募参赛者，让酒吧座无虚席。",
                    PublicOpinion = "镇民好奇却观望，有人怀疑这不过是酒吧的促销噱头。",
                },
                ["Abigail"] = new RolePhaseBrief
                {
                    Motivation = "用一道大胆的新菜卫冕冠军，证明矿工家的安静女孩也能连赢两届。",
                    PublicOpinion = "仰慕者说她无所畏惧；守旧派却嘀咕她的食材简直古怪。",
                },
                ["Alex"] = new RolePhaseBrief
                {
                    Motivation = "指出获胜者永远是那几张熟面孔，要求引入外部评委。",
                    PublicOpinion = "少数村民承认评审看着有点内定意味，但多数人劝他别较真。",
                },
            },
            [IncidentPhase.Escalation] = new Dictionary<string, RolePhaseBrief>
            {
                ["Gus"] = new RolePhaseBrief
                {
                    Motivation = "在流言与赌注填满酒吧之际，别让比赛彻底失控。",
                    PublicOpinion = "全镇分成了两派，人人都选好了自己支持的对象。",
                },
                ["Abigail"] = new RolePhaseBrief
                {
                    Motivation = "私下苦练，对破坏传闻一笑置之，用手艺回应质疑。",
                    PublicOpinion = "她的支持者声势渐涨；批评者则宣称结果早已内定。",
                },
                ["Alex"] = new RolePhaseBrief
                {
                    Motivation = "把公平性质疑推向高潮，扬言要抵制决赛。",
                    PublicOpinion = "越来越多的村民开始追问：评审到底是谁？",
                },
            },
            [IncidentPhase.Climax] = new Dictionary<string, RolePhaseBrief>
            {
                ["Gus"] = new RolePhaseBrief
                {
                    Motivation = "主持一场干净而有戏剧性的决赛，选出全镇都服气的赢家。",
                    PublicOpinion = "酒吧座无虚席，全镇都想看到一个公正的结果。",
                },
                ["Abigail"] = new RolePhaseBrief
                {
                    Motivation = "端出她最大胆的一道菜，让质疑者彻底闭嘴。",
                    PublicOpinion = "连她的批评者都承认这场决赛不容错过。",
                },
                ["Alex"] = new RolePhaseBrief
                {
                    Motivation = "像鹰一样盯着评审过程，在认输之前先要求透明公开。",
                    PublicOpinion = "他放话若结果干净，愿赌服输自打嘴巴。",
                },
            },
        };
    }

    // ─────────────────────────────────────────────────────────────
    //  Static fallback — Contest keyword groups.
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

    internal static string BuildUserPrompt(EventSlotContract shell, bool isChinese)
    {
        string roles = string.Join(isChinese ? "；" : "; ",
            shell.AssignedRoles.Select(kv => $"{kv.Key}={kv.Value}"));
        string groupKeys = string.Join(", ", shell.BranchOutcomes.Keys);

        if (isChinese)
        {
            return $"""
                事件「{shell.EventName}」（{shell.IncidentId}），原型 {shell.ArchetypeId}。主题：{shell.IncidentTheme}。
                角色（原样回显）：{roles}。
                时间线：自游戏第 {shell.StartGameDay} 天起共 {shell.DurationDays} 天。阶段：Inception = 第 0-2 天，Escalation = 第 3-5 天，Climax = 第 6-7 天（决赛地点 {shell.ClimaxLocation}，时间 {shell.ClimaxTimeOfDay}）。
                分支组键（必须原样使用）：{groupKeys}。
                现在输出完整 JSON 对象。
                """;
        }

        return $"""
            Incident "{shell.EventName}" ({shell.IncidentId}), archetype {shell.ArchetypeId}. Theme: {shell.IncidentTheme}.
            Roles (echo exactly): {roles}.
            Timeline: {shell.DurationDays} days starting game day {shell.StartGameDay}. Phases: Inception = elapsed days 0-2, Escalation = 3-5, Climax = 6-7 (finale at the {shell.ClimaxLocation}, {shell.ClimaxTimeOfDay}).
            Branch group keys (use exactly these): {groupKeys}.
            Write the complete JSON object now.
            """;
    }
}
