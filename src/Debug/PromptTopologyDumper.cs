// PromptTopologyDumper.cs
// ═══════════════════════════════════════════════════════════════════════════
// VT3-E — Multi-turn A/B parity dumper (A1-A4 + rotation probe + reuse).
//
// Read-only layered assertions over the production prompt-injection pipeline.
// For a given NPC it builds a Prompts instance the same way LlmDialogueService
// does, then runs the layered multi-turn assertions (A1-A4 + rotation probe) and
// exposes the flat line-bag comparator plus its 5-case self-test.
//
// One command is registered:
//   vt_ab_topology_multi <NpcName>   – layered multi-turn assertions
//   vt_ab_topology_multi --selftest  – comparator self-test (5 cases)
// ═══════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using StardewModdingAPI;
using StardewValley;

namespace ValleytalkReborn;

/// <summary>
/// Debug utility that runs layered multi-turn A/B assertions for a given NPC and
/// hosts the flat line-bag comparator self-test.
/// Registered as the SMAPI console command `vt_ab_topology_multi <NpcName>`.
/// </summary>
internal static class PromptTopologyDumper
{
    private const string Prefix = "[vt_multi]";

    /// <summary>
    /// Registers the console command. Mirrors EmotionDebugCommands.Register.
    /// </summary>
    public static void Register(ICommandHelper console)
    {
        console.Add(
            "vt_ab_topology_multi",
            "Multi-turn A/B parity check with layered assertions (A1-A4 + rotation probe + reuse). "
            + "Usage: vt_ab_topology_multi <NpcName>   e.g. vt_ab_topology_multi Abigail   |   "
            + "vt_ab_topology_multi --selftest (comparator self-test)",
            OnAbMultiCommand);
    }

    private static void OnAbMultiCommand(string command, string[] args)
    {
        if (args.Length >= 1 && args[0] == "--selftest")
        {
            RunComparatorSelfTest();
            return;
        }
        if (!Context.IsWorldReady) { Info("世界未就绪。"); return; }
        if (args.Length < 1) { Info("用法: vt_ab_topology_multi <NpcName>"); return; }
        string npcName = args[0];
        var character = FindCharacter(npcName);
        if (character == null) { Info($"NPC 不存在: {npcName}"); return; }

        const int turns = 3;
        var segA = new string[turns];
        var segX = new string[turns];
        var segB = new string[turns];
        var corePrompts = new string[turns];
        var reusedFlags = new bool[turns];
        var a3Ok = new bool[turns];
        var a4Ok = new bool[turns];

        for (int turn = 1; turn <= turns; turn++)
        {
            try
            {
                SessionCache.ClearForNpc(character.Name);
                var context = BuildPopulatedContext(character);
                context.ChatHistory = BuildTurnChatHistory(turn);
                context.RoutingFlags.IncludeShortTermContext = true;

                var prompts = new Prompts(context, character);
                var plan = ConversationDirectorInstance.BuildPlan(context, character, prompts);

                // 段捕获：Tier2a 用独立 fresh 实例渲染，避免与 AssembleCore 的
                // _emittedBlockKeys 台账交叉（A4 字节级拼接完整性的前提）。
                var segPrompts = new Prompts(context, character);
                segA[turn - 1] = Prompts.AssembleTier1Segment(plan);
                segX[turn - 1] = segPrompts.AssembleTier2aSegment(context, character);
                segB[turn - 1] = Prompts.AssembleTier2bSegment(plan);
                reusedFlags[turn - 1] = plan.IsTier1Reused;

                prompts.AssembleCore(plan, context, character);
                corePrompts[turn - 1] = prompts.CorePrompt;

                // A3（逐轮构造一致性，非跨轮）：段捕获后重渲同一 plan，必须字节一致。
                a3Ok[turn - 1] = segB[turn - 1] == Prompts.AssembleTier2bSegment(plan);

                // A4（拼接完整性，逐轮，字节级）。
                a4Ok[turn - 1] = corePrompts[turn - 1] == segA[turn - 1] + segX[turn - 1] + segB[turn - 1];
            }
            catch (Exception ex)
            {
                Info($"[turn{turn}] ERROR: {ex.Message}");
            }
        }

        // A1: Tier1 frozen across turns（active 续用保证）。
        bool a1 = segA.All(s => s != null) && segA[0] == segA[1] && segA[1] == segA[2];
        Info($"A1 (Tier1 frozen, Seg_A(1)==(2)==(3)): {(a1 ? "PASS" : "FAIL")}");

        // A2: Seg_X(1)⊑Seg_X(2)⊑Seg_X(3) 字节前缀链（累积历史 + continuity 已清）。
        int window = Math.Clamp(ModEntry.Config?.PromptHistoryWindow ?? 6, 1, 20);
        bool a2 = segX.All(s => s != null)
            && segX[1].StartsWith(segX[0], StringComparison.Ordinal)
            && segX[2].StartsWith(segX[1], StringComparison.Ordinal);
        Info($"A2 (Seg_X(1)⊑(2)⊑(3) 字节前缀链, historyWindow={window}): {(a2 ? "PASS" : "FAIL")}");
        if (!a2)
            Info($"  A2 归因: len(1)={segX[0]?.Length ?? -1}, len(2)={segX[1]?.Length ?? -1}, len(3)={segX[2]?.Length ?? -1} "
                + "(window<4 时尾部截断破坏前缀链；turn1 空历史时受 SpokeJustNow 影响)");

        // A3: Seg_B(i)==RenderTier2b(plan_i) 逐轮构造一致性。
        int a3Count = a3Ok.Count(b => b);
        Info($"A3 (Seg_B(i)==RenderTier2b(plan_i) 逐轮构造一致): {(a3Count == turns ? $"PASS ({turns}/{turns})" : $"FAIL ({a3Count}/{turns})")}");

        // A4: CorePrompt(i)==Seg_A⊕Seg_X⊕Seg_B 拼接完整性。
        int a4Count = a4Ok.Count(b => b);
        Info($"A4 (CorePrompt(i)==Seg_A⊕Seg_X⊕Seg_B 拼接完整): {(a4Count == turns ? $"PASS ({turns}/{turns})" : $"FAIL ({a4Count}/{turns})")}");

        // Rotation probe (turn4): Normal → Greeting。
        // rotationNotReused 供 Reuse 断言使用（rotation=F 即 turn4 未复用）。
        bool rotationReused = false;
        bool rotationOk = false;
        try
        {
            SessionCache.ClearForNpc(character.Name);
            var ctx4 = BuildPopulatedContext(character);
            ctx4.RoutingFlags.IsSimpleGreeting = true;
            ctx4.RoutingFlags.IsMovementRequested = false;
            var prompts4 = new Prompts(ctx4, character);
            var plan4 = ConversationDirectorInstance.BuildPlan(ctx4, character, prompts4);
            string segA4 = Prompts.AssembleTier1Segment(plan4);
            rotationReused = plan4.IsTier1Reused;
            rotationOk = !rotationReused && segA4 != segA[0];
            Info($"Rotation (turn4 Normal→Greeting): reused={rotationReused}, segA changed={segA4 != segA[0]} (expect F,T) → {(rotationOk ? "PASS" : "FAIL")}");
        }
        catch (Exception ex)
        {
            Info($"Rotation probe ERROR: {ex.Message}");
        }
        bool rotationNotReused = !rotationReused;

        // Reuse 断言（VT3-D-FIX2 第三项 multi 卫生）：
        // turn1 ∈ {F,T}（环境依赖，注明来源）且 turn2/3=T 且 rotation=F。
        // 不再静默放行 turn1=True：turn1=T 时必须注明来源。
        string turn1Source = reusedFlags[0]
            ? "env:reuse-from-prior-active-session (Store 残留 active 会话跨轮复用)"
            : "fresh (新会话)";
        bool turn2Reused = reusedFlags[1];
        bool turn3Reused = reusedFlags[2];
        bool reuseAssertion = turn2Reused && turn3Reused && rotationNotReused;
        Info($"Reuse: turn1={reusedFlags[0]} ({turn1Source}) / turn2={turn2Reused} (expect T) / turn3={turn3Reused} (expect T) / rotation reused={rotationReused} (expect F) → {(reuseAssertion ? "PASS" : "FAIL")}");
        Info($"Multi summary: A1={(a1 ? "PASS" : "FAIL")} / A2={(a2 ? "PASS" : "FAIL")} / A3={a3Count}/{turns} / A4={a4Count}/{turns} / Rotation={(rotationOk ? "PASS" : "FAIL")} / Reuse={(reuseAssertion ? "PASS" : "FAIL")}");
    }

    /// <summary>比对器自测（VT3-E-INS2 五例）：行袋权威模式 + 对称剔除 + 审计。
    /// 0. 自反性（A vs A → PASS，行袋全等）。
    /// 1. 同内容不同拼接（胶合 vs 分离）→ PASS（拼接/顺序不敏感）。
    /// 2. 同内容不同块顺序 → PASS（顺序不敏感）。
    /// 3. 缺一块 → FAIL 且定位到缺失块的行（不漏）。
    /// 4. 对称剔除审计——随机类行两侧同剔，剔除清单入报告。</summary>
    private static void RunComparatorSelfTest()
    {
        Info("── 比对器自测 (--selftest) ──");

        // 自测 0：自反性（A vs A → PASS，行袋全等）。
        string sideA = string.Join("\n\n", new[]
        {
            "## GameState\n今天是阳光明媚的春天。",
            "## Relation\n你和农夫是好朋友。",
            "## Current Conversation\n- 农夫: 你好！\n- Abigail: 嗨！",
        });
        var r0 = CompareFlattened(sideA, sideA);
        bool t0 = r0.IsEqual && r0.ExtraInLegacy.Count == 0 && r0.ExtraInNovel.Count == 0
            && r0.RemovedFromLegacy.Count == 0 && r0.RemovedFromNovel.Count == 0;
        Info($"SELFTEST-0 自反性 (A vs A): {(t0 ? "PASS (行袋全等)" : $"FAIL (equal={r0.IsEqual}, extraA={r0.ExtraInLegacy.Count}, extraB={r0.ExtraInNovel.Count})")}");

        // 自测 1：同内容不同拼接（胶合 vs 分离）→ PASS（拼接/顺序不敏感）。
        string glueA = "## GameState\nfoo\n\n## Relation\nbar";
        string glueB = "## GameState\nfoo\n## Relation\nbar";
        var r1 = CompareFlattened(glueA, glueB);
        bool t1 = r1.IsEqual && r1.ExtraInLegacy.Count == 0 && r1.ExtraInNovel.Count == 0;
        Info($"SELFTEST-1 拼接不敏感 (胶合 vs 分离): {(t1 ? "PASS (行袋相等)" : $"FAIL (equal={r1.IsEqual}, extraA=[{string.Join(",", r1.ExtraInLegacy)}], extraB=[{string.Join(",", r1.ExtraInNovel)}])")}");

        // 自测 2：同内容不同块顺序 → PASS（顺序不敏感）。
        string orderA = "## GameState\nfoo\n\n## Relation\nbar";
        string orderB = "## Relation\nbar\n\n## GameState\nfoo";
        var r2 = CompareFlattened(orderA, orderB);
        bool t2 = r2.IsEqual && r2.ExtraInLegacy.Count == 0 && r2.ExtraInNovel.Count == 0;
        Info($"SELFTEST-2 顺序不敏感 (块逆序): {(t2 ? "PASS (行袋相等)" : $"FAIL (equal={r2.IsEqual}, extraA=[{string.Join(",", r2.ExtraInLegacy)}], extraB=[{string.Join(",", r2.ExtraInNovel)}])")}");

        // 自测 3：缺一块 → FAIL 且定位到缺失块的行（不漏）。
        string full = "## GameState\nfoo\n\n## Relation\nbar\n\n## Current Conversation\n对话内容";
        string missing = "## GameState\nfoo\n\n## Current Conversation\n对话内容";
        var r3 = CompareFlattened(full, missing);
        bool locatedRelation = r3.ExtraInLegacy.Contains("## Relation") && r3.ExtraInLegacy.Contains("bar");
        bool t3 = !r3.IsEqual && locatedRelation && r3.ExtraInNovel.Count == 0;
        Info($"SELFTEST-3 缺块定位 (缺 ## Relation): {(t3
            ? $"PASS (FAIL 且定位: extraInA=[{string.Join(" | ", r3.ExtraInLegacy)}])"
            : $"FAIL (equal={r3.IsEqual}, located={locatedRelation}, extraA=[{string.Join(" | ", r3.ExtraInLegacy)}], extraB=[{string.Join(" | ", r3.ExtraInNovel)}])")}");

        // 自测 4：对称剔除审计——随机类行两侧同剔，剔除清单入报告。
        string randA = "## GameState\nfoo\n\n[preoccupation] 想法A";
        string randB = "## GameState\nfoo\n\n[preoccupation] 想法B";
        var r4 = CompareFlattened(randA, randB);
        bool t4 = r4.IsEqual && r4.RemovedFromLegacy.Count == 1 && r4.RemovedFromNovel.Count == 1
            && r4.RemovedFromLegacy[0].Contains("[preoccupation]") && r4.RemovedFromNovel[0].Contains("[preoccupation]");
        Info($"SELFTEST-4 对称剔除审计 (Preoccupation 双侧同剔): {(t4
            ? $"PASS (A剔 {r4.RemovedFromLegacy.Count} 行, B剔 {r4.RemovedFromNovel.Count} 行)"
            : $"FAIL (equal={r4.IsEqual}, removedA={r4.RemovedFromLegacy.Count}, removedB={r4.RemovedFromNovel.Count})")}");

        int selftestPass = (t0 ? 1 : 0) + (t1 ? 1 : 0) + (t2 ? 1 : 0) + (t3 ? 1 : 0) + (t4 ? 1 : 0);
        Info($"Selftest summary: {selftestPass}/5 PASS {(selftestPass == 5 ? "→ 比对器可用" : "→ 比对器不可用，A/B 结果禁止采信")}");
    }

    // ── 扁平化权威比较（VT3-E-INS2 行袋模式）──
    // 权威判定 = 行袋比较：双侧 CorePrompt → 删除空行 → 行多重集（Bag，计重复）
    // → 双侧对称剔除随机类行 → 剩余 Bag 相等 = PASS。
    // 顺序不敏感、计重复、空行无关；剔除清单入审计报告（防剔除吞真差异）。

    /// <summary>压平：删除空行、逐行 Trim、保留行序。</summary>
    private static List<string> FlattenLines(string text)
    {
        var lines = new List<string>();
        if (string.IsNullOrEmpty(text)) return lines;
        foreach (var line in text.Split('\n'))
        {
            string t = line.Trim();
            if (!string.IsNullOrEmpty(t)) lines.Add(t);
        }
        return lines;
    }

    /// <summary>随机类行分类器（权威清单，仅 Preoccupation/Gift 类。
    /// 扩大清单须架构师批准——见 contract_specifications）。</summary>
    private static bool IsRandomLine(string line) =>
        line.Contains("[preoccupation]", StringComparison.OrdinalIgnoreCase)
        || line.Contains("giftGiving", StringComparison.OrdinalIgnoreCase)
        || line.Contains("你刚刚收到了", StringComparison.OrdinalIgnoreCase)
        || line.Contains("You just received", StringComparison.OrdinalIgnoreCase);

    /// <summary>扁平化行袋比较结果。</summary>
    private sealed class FlatCompareResult
    {
        public bool IsEqual { get; init; }
        public List<string> RemovedFromLegacy { get; init; } = new();
        public List<string> RemovedFromNovel { get; init; } = new();
        public List<string> ExtraInLegacy { get; init; } = new();
        public List<string> ExtraInNovel { get; init; } = new();
    }

    /// <summary>行袋权威比较（VT3-E-INS2）：
    /// 双侧压平 → 对称剔除随机类行（记录审计清单）→ 行多重集比较。
    /// 剩余 Bag 相等 = PASS；否则 FAIL，并输出剩余差异行供诊断定位。</summary>
    private static FlatCompareResult CompareFlattened(string legacy, string novel)
    {
        var legAll = FlattenLines(legacy);
        var novAll = FlattenLines(novel);

        var legCore = new List<string>();
        var removedLeg = new List<string>();
        foreach (var l in legAll)
        {
            if (IsRandomLine(l)) removedLeg.Add(l);
            else legCore.Add(l);
        }
        var novCore = new List<string>();
        var removedNov = new List<string>();
        foreach (var l in novAll)
        {
            if (IsRandomLine(l)) removedNov.Add(l);
            else novCore.Add(l);
        }

        var legCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var l in legCore) legCounts[l] = legCounts.GetValueOrDefault(l) + 1;
        var novCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var l in novCore) novCounts[l] = novCounts.GetValueOrDefault(l) + 1;

        var extraLeg = new List<string>();
        var extraNov = new List<string>();
        foreach (var kvp in legCounts)
        {
            int nov = novCounts.GetValueOrDefault(kvp.Key);
            for (int i = 0; i < kvp.Value - nov; i++) extraLeg.Add(kvp.Key);
        }
        foreach (var kvp in novCounts)
        {
            int leg = legCounts.GetValueOrDefault(kvp.Key);
            for (int i = 0; i < kvp.Value - leg; i++) extraNov.Add(kvp.Key);
        }

        return new FlatCompareResult
        {
            IsEqual = extraLeg.Count == 0 && extraNov.Count == 0,
            RemovedFromLegacy = removedLeg,
            RemovedFromNovel = removedNov,
            ExtraInLegacy = extraLeg,
            ExtraInNovel = extraNov,
        };
    }

    /// <summary>构建全要素填充的 DialogueContext（天气、好感、子代、地点、时间），
    /// 避免 Prompts 组装期因空引用产生空白块。对应 2ebfb2db 的 CreatePopulatedContext 核心字段。</summary>
    private static DialogueContext BuildPopulatedContext(Character character)
    {
        // 天气
        var weather = new List<string>();
        if (Game1.isRaining) weather.Add("rain");
        else if (Game1.isSnowing) weather.Add("snow");
        else if (Game1.isLightning) weather.Add("storm");
        else weather.Add("sun");

        // 好感 / 婚姻
        Friendship friendship = null;
        if (Game1.player?.friendshipData != null)
            Game1.player.friendshipData.TryGetValue(character.Name, out friendship);
        int? heartLevel = friendship != null ? friendship.Points / 250 : (int?)null;

        // 子代
        var children = new List<ChildDescription>();
        try
        {
            if (Game1.player != null)
            {
                foreach (var c in Game1.player.getChildren())
                {
                    children.Add(new ChildDescription(c.Name, c.Gender == StardewValley.Gender.Male, c.Age));
                }
            }
        }
        catch { }

        string locName = character.StardewNpc?.currentLocation?.Name
            ?? Game1.currentLocation?.Name ?? "FarmHouse";

        return new DialogueContext
        {
            Hearts = heartLevel,
            Season = (ValleytalkReborn.Season)Game1.season,
            Year = Game1.year,
            DayOfSeason = Game1.dayOfMonth,
            TimeOfDay = Game1.timeOfDay.ToString(),
            Location = locName,
            MaleFarmer = Game1.player?.IsMale ?? true,
            Children = children,
            Weather = weather,
            ChatHistory = new List<ConversationElement>(),
            CanGiveGift = false,
        };
    }

    private static List<ConversationElement> BuildTurnChatHistory(int turn)
    {
        // 累积式罐头历史：turn N 含 2*(N-1) 行（0/2/4），行文本跨轮稳定（hist-line{i}），
        // 使 CurrentConversation 逐轮只追加 → Seg_X 呈字节前缀链（A2）。
        var history = new List<ConversationElement>();
        int lines = 2 * (turn - 1);
        for (int i = 0; i < lines; i++)
            history.Add(new ConversationElement($"hist-line{i}", i % 2 == 0));
        return history;
    }

    // ── Helpers ──

    private static Character FindCharacter(string npcName)
    {
        var exact = DialogueBuilder.Instance.GetCharacterByName(npcName);
        if (exact != null) return exact;
        foreach (var c in DialogueBuilder.Instance.GetAllLoadedCharacters())
        {
            if (string.Equals(c.Name, npcName, StringComparison.OrdinalIgnoreCase))
                return c;
        }
        return null;
    }

    private static void Info(string message) =>
        ModEntry.SMonitor?.Log($"{Prefix} {message}", LogLevel.Info);

    // ConversationDirector access (internal in Director namespace).
    private static readonly ConversationDirector ConversationDirectorInstance = new();
}
