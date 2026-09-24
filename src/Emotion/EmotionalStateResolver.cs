using System;
using System.Collections.Generic;
using System.Text;
using StardewModdingAPI;
using StardewValley;

namespace ValleytalkReborn;

public static class EmotionalStateResolver
{
    // 契约逐字文案：仅 V<-0.25 时注入一次
    private const string TransitionLine =
        "- 若农夫的言行让你原本的情绪出现真实的松动，不必刻意维持冷硬；真实的情绪转折比强装的负面更可信。此时允许转用 $h 肖像。";

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
        // NPC.isEngaged() 在 1.6 不存在（isEngaged 是 Farmer 方法）；
        // isMarriedOrEngaged() 为 NPC 侧等价 API（既有用法见 GiftVerdictResolver），覆盖已婚+订婚两态
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
        int manners = rawNpc?.Manners ?? 0;
        var scene = character?.CurrentTodayScene;
        string displayName = NpcNameLocalizer.GetZhName(rawNpc?.Name ?? character?.Name ?? "");
        return CompileCore(snapshot, manners, scene, displayName);
    }

    /// <summary>
    /// 情绪指令编译（pure，无 Game1/NPC 访问）。
    /// </summary>
    public static (string PromptBlock, string NarrationLine) CompileCore(
        EmotionSnapshot snapshot, int manners, TodayScene scene, string displayName)
    {
        float v = snapshot.Valence;
        float a = snapshot.Arousal;
        float o = snapshot.Openness;
        float baselineOpenness = snapshot.BaselineOpenness;

        var directives = new List<string>();

        // ① 话长
        if (o < 0.35f)
        {
            directives.Add("[硬约束] 回复控制在 1~2 句以内，不得展开长篇");
        }
        else if (o > 0.75f)
        {
            directives.Add("回复可自然展开，允许较充分的表达");
        }

        // ② 反问双轨
        bool isTemporarilyClosed = o < 0.35f && (o < baselineOpenness - 0.05f || v < -0.25f);
        if (isTemporarilyClosed)
        {
            directives.Add("[硬约束] 严禁向农夫提问，禁止使用疑问语气，禁止以“吗、吧、呢、是不是、难道”等疑问词收尾或句中反问");
        }
        else if (baselineOpenness < 0.35f && o < 0.50f)
        {
            directives.Add("[表达风格] 言语克制内敛，不主动探听对方私事，通常不主动抛出反问");
        }
        else if (o > 0.70f)
        {
            directives.Add("可自然顺带向农夫提一句反问，推进话题");
        }

        // ③ 语气与肖像
        if (v < -0.25f && a > 0.60f)
        {
            directives.Add("语气紧绷抗拒，优先使用 $a 肖像");
        }
        else if (v < -0.25f && a <= 0.60f)
        {
            directives.Add("情绪低落疲倦，优先使用 $s 肖像");
        }
        else if (v > 0.35f)
        {
            directives.Add("语气轻快随和，优先使用 $h 肖像");
        }

        // 转折文案：仅 V<-0.25，注入一次
        if (v < -0.25f)
        {
            directives.Add(TransitionLine);
        }

        // ④ Manners（仅 V<-0.25）
        if (v < -0.25f)
        {
            directives.Add(manners switch
            {
                1 => "保持文雅守礼，不恶言相向，以委婉方式回避",
                2 => "用直率短句回应，主动拉开距离",
                _ => "坐立不安，言语间抱怨身边的琐碎事物"
            });
        }

        // ⑤ 组装
        var sb = new StringBuilder();
        sb.AppendLine("<emotional_state>");
        if (scene != null)
        {
            if (!string.IsNullOrEmpty(scene.Scene))
                sb.AppendLine($"[今日生活底色] {scene.Scene}");
            if (!string.IsNullOrEmpty(scene.Preoccupation))
                sb.AppendLine($"[脑中主要挂念] {scene.Preoccupation}");
        }
        sb.AppendLine("[此刻行为禁令]");
        foreach (var directive in directives)
        {
            sb.AppendLine(directive.StartsWith("- ", StringComparison.Ordinal) ? directive : "- " + directive);
        }
        sb.Append("</emotional_state>");

        return (sb.ToString(), CompileNarration(v, a, o, displayName));
    }

    // ⑥ Narration 优先级：自上而下首个命中即返回，全不命中 → string.Empty
    private static string CompileNarration(float v, float a, float o, string displayName)
    {
        if (v < -0.30f && a > 0.60f)
            return $"（{displayName} 眉头紧锁，动作显得有些生硬粗暴）";
        if (v < -0.25f && a <= 0.60f)
            return $"（{displayName} 看起来神情萎靡，眼神有些发沉）";
        if (v > 0.35f && a > 0.65f)
            return $"（{displayName} 显得格外精神亢奋，浑身带着干劲）";
        if (v > 0.30f)
            return $"（{displayName} 神色轻松，嘴角带着一丝笑意）";
        if (a < 0.35f && o < 0.40f)
            return $"（{displayName} 动作迟缓，看起来有些心不在焉）";
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
