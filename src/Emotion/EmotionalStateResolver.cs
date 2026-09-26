using System;
using System.Collections.Generic;
using System.Text;
using StardewModdingAPI;
using StardewValley;

namespace ValleytalkReborn;

public static class EmotionalStateResolver
{
    /// <summary>
    /// 判断当前游戏语言是否为中文，其他任何语言/小语种均回落至英语。
    /// 带有异常捕获以支持非游戏上下文运行（如独立单元测试）。
    /// </summary>
    private static bool IsChinese()
    {
        try
        {
            return LocalizedContentManager.CurrentLanguageCode == LocalizedContentManager.LanguageCode.zh;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 三轴基线合成（pure）。authored 非 null → 直接采用；null → 原生映射。
    /// ProgressStates 修正与基线来源无关（婚姻/心数是关系态，不是性格来源）。
    /// </summary>
    public static (float v, float a, float o) ComposeBaseline(
        EmotionalBaseline authored, int optimism, int socialAnxiety, bool marriedOrEngaged, int heartLevel)
    {
        float v, a, o;
        if (authored != null)
        {
            v = authored.Valence;
            a = authored.Arousal;
            o = authored.Openness;
        }
        else
        {
            v = optimism switch { 0 => 0.35f, 1 => 0.05f, 2 => -0.25f, _ => 0.05f };
            o = socialAnxiety switch { 0 => 0.75f, 1 => 0.50f, 2 => 0.25f, _ => 0.50f };
            a = socialAnxiety switch { 0 => 0.65f, 1 => 0.50f, 2 => 0.35f, _ => 0.50f };
        }

        if (marriedOrEngaged)
        {
            v += 0.15f;
            o += 0.20f;
        }
        else if (heartLevel >= 8)
        {
            o += 0.10f;
        }

        return (v, a, o);
    }

    /// <summary>
    /// 生成期快照：baseline + 场景 Bias + Shock 聚合（各自 Clamp）。主线程。
    /// </summary>
    public static EmotionSnapshot PrepareSnapshot(Character character, NPC rawNpc)
    {
        int optimism = rawNpc?.Optimism ?? 1;
        int socialAnxiety = rawNpc?.SocialAnxiety ?? 1;
        bool marriedOrEngaged = rawNpc?.isMarriedOrEngaged() ?? false;
        int heartLevel = rawNpc != null && Game1.player != null
            ? Game1.player.getFriendshipHeartLevelForNPC(rawNpc.Name)
            : 0;

        var baseline = ComposeBaseline(
            character?.Bio?.EmotionalBaseline, optimism, socialAnxiety, marriedOrEngaged, heartLevel);

        var scene = character?.CurrentTodayScene;
        var (sv, sa, so) = MoodShockStore.GetAggregatedDeltas(character?.Name ?? rawNpc?.Name);

        float v = Math.Clamp(baseline.v + (scene?.Bias?.Valence ?? 0f) + sv, -1f, 1f);
        float a = Math.Clamp(baseline.a + (scene?.Bias?.Arousal ?? 0f) + sa, 0f, 1f);
        float o = Math.Clamp(baseline.o + (scene?.Bias?.Openness ?? 0f) + so, 0f, 1f);

        return new EmotionSnapshot(v, a, o, Math.Clamp(baseline.o, 0f, 1f));
    }

    /// <summary>
    /// 薄适配：从 Character/NPC 提取参数后调用纯核心 CompileCore。
    /// </summary>
    public static (string PromptBlock, string NarrationLine) Compile(
        Character character, NPC rawNpc, EmotionSnapshot snapshot)
    {
        bool isZh = IsChinese();
        int manners = rawNpc?.Manners ?? 0;
        var scene = character?.CurrentTodayScene;
        string displayName = isZh
            ? NpcNameLocalizer.GetZhName(rawNpc?.Name ?? character?.Name ?? "")
            : (rawNpc?.displayName ?? rawNpc?.Name ?? character?.Name ?? "");
        return CompileCore(snapshot, manners, scene, displayName, isZh);
    }

    /// <summary>
    /// 情绪指令编译（pure，无 Game1/NPC 访问）。
    /// </summary>
    public static (string PromptBlock, string NarrationLine) CompileCore(
        EmotionSnapshot snapshot, int manners, TodayScene scene, string displayName, bool? isZh = null)
    {
        bool zh = isZh ?? IsChinese();

        float v = snapshot.Valence;
        float a = snapshot.Arousal;
        float o = snapshot.Openness;
        float baselineOpenness = snapshot.BaselineOpenness;

        var directives = new List<string>();

        // ① 长度约束 (Length Budgeting)
        if (o < 0.35f)
        {
            directives.Add(zh
                ? "[HARD_CONSTRAINT] 回复长度严格限制在 1~2 句以内，禁止长篇展开。"
                : "[HARD_CONSTRAINT] Response length strictly limited to 1-2 sentences. Do not elaborate.");
        }
        else if (o > 0.75f)
        {
            directives.Add(zh
                ? "[STYLE_GUIDELINE] 允许自然延展对话，提供相对充分的表达与细节。"
                : "[STYLE_GUIDELINE] Elaborate naturally; express thoughts with descriptive depth.");
        }

        // ② 反问与探问控制 (Interrogation Suppression / Promotion)
        bool isTemporarilyClosed = o < 0.35f && (o < baselineOpenness - 0.05f || v < -0.25f);
        if (isTemporarilyClosed)
        {
            directives.Add(zh
                ? "[NEGATIVE_CONSTRAINT] 绝对禁止向玩家提问或发起反问。严禁出现问号（?）及疑问句式（“吗/吧/呢/是不是/难道”等）。"
                : "[NEGATIVE_CONSTRAINT] Forbid asking questions or counter-inquiries to the player. Strictly suppress all interrogative phrasing and question marks (?).");
        }
        else if (baselineOpenness < 0.35f && o < 0.50f)
        {
            directives.Add(zh
                ? "[STYLE_GUIDELINE] 保持内敛克制与人际距离，禁止主动打探玩家隐私，避免主动抛出疑问。"
                : "[STYLE_GUIDELINE] Maintain reserved social boundaries. Do not probe into the player's personal affairs; avoid initiating questions.");
        }
        else if (o > 0.70f)
        {
            directives.Add(zh
                ? "[STYLE_GUIDELINE] 允许顺带提出反问或追问，主动推进对话互动。"
                : "[STYLE_GUIDELINE] Naturally pose a counter-question or follow-up to advance conversation engagement.");
        }

        // ③ 语气与肖像映射 (Tone & Portrait Mapping)
        if (v < -0.25f && a > 0.60f)
        {
            directives.Add(zh
                ? "[PORTRAIT_MAPPING] 语气高度紧绷且具防御性。强制优先指定肖像标记: $a"
                : "[PORTRAIT_MAPPING] Tone is tense, defensive, and hostile. Prioritize portrait token: $a");
        }
        else if (v < -0.25f && a <= 0.60f)
        {
            directives.Add(zh
                ? "[PORTRAIT_MAPPING] 情绪处于低落疲惫状态。强制优先指定肖像标记: $s"
                : "[PORTRAIT_MAPPING] Tone is dejected, drained, and weary. Prioritize portrait token: $s");
        }
        else if (v > 0.35f)
        {
            directives.Add(zh
                ? "[PORTRAIT_MAPPING] 语气轻快随和、积极开放。强制优先指定肖像标记: $h"
                : "[PORTRAIT_MAPPING] Tone is lighthearted, approachable, and warm. Prioritize portrait token: $h");
        }

        // 状态平滑转移触发器：仅 V<-0.25 注入
        if (v < -0.25f)
        {
            directives.Add(zh
                ? "[STATE_TRANSITION] 若玩家言行促成了真实的情绪破冰或有效抚慰，禁止机械化固守负面僵持；执行自然的情绪软化，解禁肖像标记 $h。"
                : "[STATE_TRANSITION] If player interaction organically defuses tension or provides comfort, do not rigidly sustain hostility. Permit emotional softening and unlock portrait token $h.");
        }

        // ④ 社交礼节机制 (Manners Profile，仅 V<-0.25)
        if (v < -0.25f)
        {
            if (zh)
            {
                directives.Add(manners switch
                {
                    1 => "[MANNER_PROFILE] 维持克制礼节与体面疏离，禁止直白攻击或谩骂，采取冷淡委婉的方式回避互动。",
                    2 => "[MANNER_PROFILE] 措辞极简生硬、直截了当，明确拉开人际距离。",
                    _ => "[MANNER_PROFILE] 表现焦虑躁动与烦躁，言语间显露对当下环境琐事的不耐与抱怨。"
                });
            }
            else
            {
                directives.Add(manners switch
                {
                    1 => "[MANNER_PROFILE] Maintain polite reserve and defensive courtesy; avoid overt hostility. Deflect engagement diplomatically.",
                    2 => "[MANNER_PROFILE] Terse, blunt, and aloof. Explicitly reject deep interaction to enforce personal boundaries.",
                    _ => "[MANNER_PROFILE] Restless and irritable; overtly complain about immediate trivialities and ambient nuisances."
                });
            }
        }

        // ⑤ 组装 Prompt 结构块
        var sb = new StringBuilder();
        sb.AppendLine("<emotional_state>");
        if (scene != null)
        {
            string sceneHeader = zh ? "[情境底色]" : "[SCENE_CONTEXT]";
            string preoccupyHeader = zh ? "[潜意识挂念]" : "[CORE_PREOCCUPATION]";

            if (!string.IsNullOrEmpty(scene.Scene))
                sb.AppendLine($"{sceneHeader} {scene.Scene}");
            if (!string.IsNullOrEmpty(scene.Preoccupation))
                sb.AppendLine($"{preoccupyHeader} {scene.Preoccupation}");
        }

        sb.AppendLine(zh ? "[行为与表达约束]" : "[BEHAVIORAL_DIRECTIVES]");
        foreach (var directive in directives)
        {
            sb.AppendLine(directive.StartsWith("- ", StringComparison.Ordinal) ? directive : "- " + directive);
        }
        sb.Append("</emotional_state>");

        return (sb.ToString(), CompileNarration(v, a, o, displayName, zh));
    }

    // ⑥ 旁白动作渲染：自上而下命中即止
    private static string CompileNarration(float v, float a, float o, string displayName, bool zh)
    {
        if (v < -0.30f && a > 0.60f)
        {
            return zh
                ? $"（{displayName} 眉头紧锁，动作显得有些生硬粗暴）"
                : $"({displayName} frowns deeply, movements stiff and harsh.)";
        }
        if (v < -0.25f && a <= 0.60f)
        {
            return zh
                ? $"（{displayName} 看起来神情萎靡，眼神有些发沉）"
                : $"({displayName} looks visibly drained, gaze heavy and tired.)";
        }
        if (v > 0.35f && a > 0.65f)
        {
            return zh
                ? $"（{displayName} 显得格外精神亢奋，浑身带着干劲）"
                : $"({displayName} appears noticeably energized, radiating enthusiasm and drive.)";
        }
        if (v > 0.30f)
        {
            return zh
                ? $"（{displayName} 神色轻松，嘴角带着一丝笑意）"
                : $"({displayName} looks relaxed, a faint smile touching their lips.)";
        }
        if (a < 0.35f && o < 0.40f)
        {
            return zh
                ? $"（{displayName} 动作迟缓，看起来有些心不在焉）"
                : $"({displayName} moves sluggishly, appearing somewhat preoccupied.)";
        }
        return string.Empty;
    }

    /// <summary>
    /// 对话提交后副作用：冷落 Shock 衰减（幂等，未命中仍置位标志）。禁止任何 modData 写入。
    /// </summary>
    public static void NotifyDialogueCommitted(Character character)
    {
        if (character == null) return;
        if (character.NeglectDampenedToday) return;

        MoodShockStore.DampenShock(character.Name, EmotionShockIds.Neglect(character.Name), 120);
        character.NeglectDampenedToday = true;
    }
}