// ConversationDirector.cs
// VT3-D — 对话注入管线编排器。
// 单次 LLM 请求的完整注入计划（InjectionPlan）由此装配：探测 → 分支 → 快照 → 脉冲 → 组装。
// 主线程调用；Memory-only；零 ModData；零 Harmony；零反射（同程序集 internal 直达）。

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using StardewModdingAPI;
using StardewValley;
using StardewValley.GameData.Characters;

namespace ValleytalkReborn;

public interface IConversationDirector
{
    InjectionPlan BuildPlan(DialogueContext context, Character character, Prompts prompts);
}

public sealed class ConversationDirector : IConversationDirector
{
    public InjectionPlan BuildPlan(DialogueContext context, Character character, Prompts prompts)
    {
        if (context == null) throw new ArgumentNullException(nameof(context));
        if (character == null) throw new ArgumentNullException(nameof(character));

        // ── Step 0: 里程碑探测（consume-on-read：BuildMilestoneBlock 内部 ConfirmConsumed，仅可调用一次）──
        string milestoneBlock = RelationshipMilestoneManager.Instance?.BuildMilestoneBlock(character);
        bool hasMilestone = !string.IsNullOrEmpty(milestoneBlock);

        // ── Step 1: 分支路由（含 DATE 冲突清除 stood-up 标志）──
        InstructionsBranch branch = ResolveBranch(context, character, hasMilestone);

        // ── Step 3.5（②/R3）：Date 分支记录对话（从 BuildBranchTheme Date case 迁出）──
        // 置于 BuildTier1Snapshot 之前——保持遗留 record-then-render 相对序。
        if (branch == InstructionsBranch.Date)
        {
            var lastPlayerLine = context.ChatHistory?.LastOrDefault(x => x.IsPlayerLine)?.Text;
            if (!string.IsNullOrWhiteSpace(lastPlayerLine))
                DateManager.Instance?.RecordDateDialogue(Game1.player?.Name ?? "Farmer", lastPlayerLine);
        }

        // ── Step 2: 会话复用探测 ──
        bool reused = Tier1SnapshotStore.TryReuseSession(character, branch, out string sessionId);

        // ── Step 3: Tier 1 快照（复用或重建）──
        Tier1SnapshotContext snapshot;
        if (reused && Tier1SnapshotStore.TryGetSnapshot(sessionId, out Tier1SnapshotContext existing))
        {
            snapshot = existing;
        }
        else
        {
            snapshot = BuildTier1Snapshot(context, character, branch, milestoneBlock);
            string locationName = character.StardewNpc?.currentLocation?.NameOrUniqueName
                ?? character.StardewNpc?.currentLocation?.Name ?? string.Empty;
            Tier1SnapshotStore.RegisterActiveSession(sessionId, character.Name, locationName, branch, snapshot);
        }

        // ⑧/A1 不变式：快照永不为 null。
        if (snapshot == null)
            throw new InvalidOperationException("[Director] Tier1Snapshot must be non-null after build/reuse.");

        // ── Step 4: 解析活跃脉冲（C1 静态生产者）──
        IReadOnlyDictionary<string, string> impulses = ResolveActiveImpulses(context, character, branch, prompts);

        // ── Echo：桥优先（consume-on-read，每请求一次），并置 EchoFromBridge ──
        var activeImpulses = new Dictionary<string, string>(impulses, StringComparer.Ordinal);
        bool echoFromBridge = false;
        string bridgeBlock = FreshBarkBridgeStore.BuildBridgeBlock(character.Name);
        if (!string.IsNullOrEmpty(bridgeBlock))
        {
            activeImpulses[Tier2bBlockIds.Echo] = bridgeBlock;
            echoFromBridge = true;
        }
        else
        {
            string echoBlock = ImmediateEchoStore.BuildEchoBlock(
                character.Name, character.StardewNpc?.currentLocation?.Name);
            if (!string.IsNullOrEmpty(echoBlock))
                activeImpulses[Tier2bBlockIds.Echo] = echoBlock;
        }

        if (hasMilestone && !string.IsNullOrEmpty(milestoneBlock))
            activeImpulses[Tier2bBlockIds.Milestone] = milestoneBlock;

        string impulseIds = string.Join(",", activeImpulses.Keys);

        var plan = new InjectionPlan
        {
            SessionId = sessionId,
            IsTier1Reused = reused,
            Branch = branch,
            Tier1Snapshot = snapshot,
            HistoryWindowSize = Math.Clamp(ModEntry.Config.PromptHistoryWindow, 1, 20),
            ActiveImpulses = activeImpulses,
            EchoFromBridge = echoFromBridge,
            EnableSuggestedResponses = ModEntry.Config.EnableSuggestedResponses,
        };

        if (ModEntry.Config.Debug)
        {
            ModEntry.SMonitor?.Log(
                $"[Director] Plan branch={branch} session={sessionId} reused={reused} impulses=[{impulseIds}]",
                LogLevel.Debug);
        }

        return plan;
    }

    // ── Step 1: 分支路由 ──
    // ① 落定：DATE 冲突清除 stood-up 标志必须先于 HasStoodUpPending 判定。
    private InstructionsBranch ResolveBranch(DialogueContext context, Character character, bool hasMilestone)
    {
        var flags = context.RoutingFlags;
        string name = character.Name;

        // 遗留 Prompts.cs:301-310 逐字：HasStoodUpPending && IsOnDate → 清除 stood-up，优先 DATE。
        if (flags != null && flags.HasStoodUpPending == true && flags.IsOnDate == true)
        {
            ModEntry.SMonitor?.Log(
                $"[Director] Conflict detected for {name}: HasStoodUpPending && IsOnDate both true. " +
                "Prioritizing IsOnDate and clearing stood-up flag.",
                LogLevel.Warn);
            flags.HasStoodUpPending = false;
            flags.StoodUpDate = string.Empty;
        }

        // StoodUp（OQ4 已闭：遗留门控逐字保留）
        if (flags?.HasStoodUpPending == true && ModEntry.Config.EnableDateSystem)
            return InstructionsBranch.StoodUp;

        // Date
        if (ModEntry.Config.EnableDateSystem && DateManager.Instance?.IsOnDate(name) == true)
            return InstructionsBranch.Date;

        // Greeting
        if (flags?.IsSimpleGreeting == true && flags?.IsMovementRequested != true && !hasMilestone)
            return InstructionsBranch.Greeting;

        return InstructionsBranch.Normal;
    }

    // ── Step 3: 构建 Tier 1 快照（11 项，顺序固定 = Tier1BlockSequence）──
    // 按分支门控填充：FULL 全 11；StoodUp/Date/Greeting 仅填充公共前缀
    // （GameState/EventHistory/BranchTheme/Scene/EvolvedTraits），剔除 FULL-only 块。
    // 权威 = 薄壳 GetCorePrompt 调用点（审计 §2.1）。
    private Tier1SnapshotContext BuildTier1Snapshot(
        DialogueContext context, Character character, InstructionsBranch branch, string milestoneBlock)
    {
        var snapshot = new Dictionary<string, string>(StringComparer.Ordinal);
        var flags = context.RoutingFlags;
        bool isZh = Prompts.PromptsBlocks.IsZh();
        string name = character.StardewNpc?.displayName ?? character.Name;
        CharacterData npcData = character.StardewNpc?.GetData();
        bool npcIsNull = character.StardewNpc == null;
        bool isFull = branch == InstructionsBranch.Normal;

        // 公共前缀（四分支均有）。
        SetTier1(snapshot, Tier1BlockIds.GameState, Prompts.PromptsBlocks.BuildGameState(character));
        SetTier1(snapshot, Tier1BlockIds.EventHistory, Prompts.PromptsBlocks.BuildEventHistory(character, context));
        SetTier1(snapshot, Tier1BlockIds.BranchTheme, Prompts.PromptsBlocks.BuildBranchTheme(character, context, branch));
        SetTier1(snapshot, Tier1BlockIds.Scene, Prompts.PromptsBlocks.BuildMicroEnvironment(character, context, flags));

        // FULL-only 块（StoodUp/Date/Greeting 薄壳不渲染）。
        if (isFull)
        {
            if (flags?.CompanionFocus == CompanionFocusMode.RegularFollow)
                SetTier1(snapshot, Tier1BlockIds.CompanionFocus, Prompts.PromptsBlocks.BuildCompanionWalkingContext(character, context, isZh));
            SetTier1(snapshot, Tier1BlockIds.GreetingContext, Prompts.PromptsBlocks.BuildGreetingContext(character, context));

            // RelationBase：友谊/婚姻关系层级（遗留 Normal 分支逐字，StardewNpc 空 → 跳过）
            if (!npcIsNull)
            {
                var relationBase = BuildRelationBase(character, context, npcData, name, milestoneBlock, flags);
                SetTier1(snapshot, Tier1BlockIds.RelationBase, relationBase);
            }

            var allPreviousActivities = Game1.getPlayerOrEventFarmer()?.previousActiveDialogueEvents?.LastOrDefault();
            SetTier1(snapshot, Tier1BlockIds.RecentEvents, Prompts.PromptsBlocks.BuildRecentEvents(character, allPreviousActivities));
            SetTier1(snapshot, Tier1BlockIds.SpecialDates, Prompts.PromptsBlocks.BuildSpecialDatesAndBirthday(character, context, name));
            SetTier1(snapshot, Tier1BlockIds.SpouseAction, Prompts.PromptsBlocks.BuildSpouseAction(character, context, name));
        }

        SetTier1(snapshot, Tier1BlockIds.EvolvedTraits, EvolvedTraitManager.GetPromptBlock(character.Name, context));

        return new Tier1SnapshotContext(snapshot);
    }

    private string BuildRelationBase(
        Character character, DialogueContext context, CharacterData npcData, string name, string milestoneBlock, ContextFlags flags)
    {
        var prompt = new StringBuilder();
        bool npcIsMale = npcData.Gender == StardewValley.Gender.Male;

        Friendship friendship = null;
        Game1.getPlayerOrEventFarmer()?.friendshipData?.TryGetValue(character.Name, out friendship);
        bool isMarriedOrRoommate = friendship != null && (friendship.IsMarried() || friendship.IsRoommate());

        if (isMarriedOrRoommate)
        {
            if (friendship.IsRoommate())
            {
                prompt.AppendLine(Util.GetString(character, "coreRoommates", new { Name = name }));
            }
            else
            {
                prompt.AppendLine(Util.GetString(character, "coreMarried",
                    new { Name = name, Pronoun = npcIsMale ? "his" : "her" }));
                prompt.Append(Prompts.PromptsBlocks.BuildChildren(character, context, friendship, name));
            }
            prompt.Append(Prompts.PromptsBlocks.BuildSpouse(character, name));
            if (flags?.IncludeFarmDetails == true)
                prompt.Append(Prompts.PromptsBlocks.BuildTrinkets(character, name));
            prompt.Append(Prompts.PromptsBlocks.BuildMarriageFeelings(character, context, name));
        }
        else
        {
            prompt.Append(Prompts.PromptsBlocks.BuildNonSpouseFriendshipLevel(character, context, npcData));
            prompt.Append(Prompts.PromptsBlocks.BuildSpouse(character, name));
            prompt.Append(Prompts.PromptsBlocks.BuildSpecialRelationshipStatus(character, context, friendship, milestoneBlock, name));
        }

        return prompt.ToString();
    }

    // ── Step 4: 解析活跃脉冲（Tier 2b）──
    // 显式 per-branch 白名单（权威 = 薄壳 GetCorePrompt 调用点）。
    // 禁止依赖值门控隐式覆盖分支结构约束：白名单即结构契约，值门控仅作内容过滤。
    private IReadOnlyDictionary<string, string> ResolveActiveImpulses(
        DialogueContext context, Character character, InstructionsBranch branch, Prompts prompts)
    {
        var impulses = new Dictionary<string, string>(StringComparer.Ordinal);
        var flags = context.RoutingFlags;
        bool isZh = Prompts.PromptsBlocks.IsZh();
        string name = character.StardewNpc?.displayName ?? character.Name;
        bool npcIsNull = character.StardewNpc == null;
        var thoughts = prompts.InjectedPrivateThoughtsMutable;

        // 每分支 Tier2b 白名单（Authority: Prompts.GetCorePrompt 调用点）。
        // FULL = 全 16；其余分支按薄壳实况剔除 FULL-only 块。
        var whitelist = Tier2bWhitelist(branch);

        if (whitelist.Contains(Tier2bBlockIds.Interaction))
            SetImpulse(impulses, Tier2bBlockIds.Interaction,
                Prompts.PromptsBlocks.BuildInteractionState(character, context, flags, name, IsMarriedOrRoommate(character)));

        if (whitelist.Contains(Tier2bBlockIds.Jealousy))
            SetImpulse(impulses, Tier2bBlockIds.Jealousy,
                Prompts.PromptsBlocks.BuildJealousyTrigger(character, flags, isZh));

        if (whitelist.Contains(Tier2bBlockIds.Preoccupation))
            SetImpulse(impulses, Tier2bBlockIds.Preoccupation,
                Prompts.PromptsBlocks.BuildPreoccupation(character, context, thoughts, name));

        if (whitelist.Contains(Tier2bBlockIds.PendingTopic))
            SetImpulse(impulses, Tier2bBlockIds.PendingTopic,
                Prompts.PromptsBlocks.BuildPendingTopic(character, name, thoughts));

        if (whitelist.Contains(Tier2bBlockIds.Gift))
            SetImpulse(impulses, Tier2bBlockIds.Gift,
                Prompts.PromptsBlocks.BuildGift(character, Prompts.PromptsBlocks.SelectGiftGiven(character), name));

        if (whitelist.Contains(Tier2bBlockIds.Eavesdrop))
            SetImpulse(impulses, Tier2bBlockIds.Eavesdrop,
                EavesdropInjector.BuildBlock(character.Name));

        // SpouseWaiting：白名单 + 探测 + 构建
        if (whitelist.Contains(Tier2bBlockIds.SpouseWaiting)
            && SpouseWaitingEvent.HasPendingSpouseDialogue(character.Name))
        {
            string porchCtx = SpouseWaitingEvent.GetPorchContext();
            if (!string.IsNullOrEmpty(porchCtx))
                SetImpulse(impulses, Tier2bBlockIds.SpouseWaiting, SpouseWaitingEvent.BuildStatusPrompt(porchCtx));
        }

        if (whitelist.Contains(Tier2bBlockIds.LocalPerception))
            SetImpulse(impulses, Tier2bBlockIds.LocalPerception,
                PerceptionInjector.BuildLocalBlock(character.Name));

        // Emotion：遗留 try/catch 保留，异常 → 空串 + 日志；副作用 character.PendingEmotion = snapshot。
        if (whitelist.Contains(Tier2bBlockIds.Emotion)
            && ModEntry.Config.EnableEmotionSystem && !npcIsNull)
        {
            try
            {
                var snapshot = EmotionalStateResolver.PrepareSnapshot(character, character.StardewNpc);
                character.PendingEmotion = snapshot;
                var compiled = EmotionalStateResolver.Compile(character, character.StardewNpc, snapshot);
                SetImpulse(impulses, Tier2bBlockIds.Emotion, compiled.PromptBlock);
            }
            catch (Exception ex)
            {
                character.PendingEmotion = null;
                ModEntry.SMonitor?.Log($"[EmotionState] 编译失败已降级: {ex.Message}", LogLevel.Warn);
            }
        }

        // PlayerProfile：白名单 + Greeting 用 ""，其余用末条玩家台词。
        if (whitelist.Contains(Tier2bBlockIds.PlayerProfile) && !npcIsNull)
        {
            string playerInput = branch == InstructionsBranch.Greeting
                ? ""
                : context.ChatHistory?.LastOrDefault(x => x.IsPlayerLine)?.Text ?? "";
            SetImpulse(impulses, Tier2bBlockIds.PlayerProfile,
                PlayerProfileManager.BuildProfileText(character.StardewNpc, playerInput, flags));
        }

        if (whitelist.Contains(Tier2bBlockIds.DateInvite))
            SetImpulse(impulses, Tier2bBlockIds.DateInvite,
                Prompts.PromptsBlocks.BuildDateInvitationProtocol(flags));

        if (whitelist.Contains(Tier2bBlockIds.FollowProto))
            SetImpulse(impulses, Tier2bBlockIds.FollowProto,
                Prompts.PromptsBlocks.BuildFollowInvitationProtocol(character, flags));

        if (whitelist.Contains(Tier2bBlockIds.DateEndProto))
            SetImpulse(impulses, Tier2bBlockIds.DateEndProto,
                Prompts.PromptsBlocks.BuildDateEndingProtocol(flags));

        if (whitelist.Contains(Tier2bBlockIds.Movement))
            SetImpulse(impulses, Tier2bBlockIds.Movement,
                Prompts.PromptsBlocks.BuildMovementInstruction(character, flags, Prompts.PromptsBlocks.MovementInstructionApplicable(flags)));

        return impulses;
    }

    /// <summary>每分支 Tier2b 白名单（权威 = 薄壳 GetCorePrompt 调用点，参见审计 §2.1/§1.2/§1.3）。
    /// FULL = 全 16；StoodUp/Date/Greeting 按薄壳实况剔除 FULL-only 块。</summary>
    private static HashSet<string> Tier2bWhitelist(InstructionsBranch branch)
    {
        switch (branch)
        {
            case InstructionsBranch.StoodUp:
                // 薄壳 StoodUp 块（Prompts.cs:315-353）：PendingTopic, Eavesdrop, SpouseWaiting, Echo, Milestone,
                // EvolvedTraits(Tier1), LocalPerception, Emotion, DateInvite, FollowProto。
                return new HashSet<string>(StringComparer.Ordinal)
                {
                    Tier2bBlockIds.PendingTopic, Tier2bBlockIds.Eavesdrop, Tier2bBlockIds.SpouseWaiting,
                    Tier2bBlockIds.Echo, Tier2bBlockIds.Milestone, Tier2bBlockIds.LocalPerception,
                    Tier2bBlockIds.Emotion, Tier2bBlockIds.DateInvite, Tier2bBlockIds.FollowProto,
                };
            case InstructionsBranch.Date:
                // 薄壳 Date 块（Prompts.cs:355-388）：Echo, Milestone, PendingTopic, EvolvedTraits(Tier1),
                // LocalPerception, Emotion, DateInvite, FollowProto, DateEndProto。
                // 聚焦裁剪：Eavesdrop/SpouseWaiting 与约会现场冲突（VT-FOCUS-02, Prompts.cs:360）。
                return new HashSet<string>(StringComparer.Ordinal)
                {
                    Tier2bBlockIds.Echo, Tier2bBlockIds.Milestone, Tier2bBlockIds.PendingTopic,
                    Tier2bBlockIds.LocalPerception, Tier2bBlockIds.Emotion, Tier2bBlockIds.DateInvite,
                    Tier2bBlockIds.FollowProto, Tier2bBlockIds.DateEndProto,
                };
            case InstructionsBranch.Greeting:
                // 薄壳 Greeting 块（Prompts.cs:390-432）：PendingTopic, Eavesdrop, SpouseWaiting, Echo,
                // EvolvedTraits(Tier1), LocalPerception, Emotion, PlayerProfile(""), DateInvite。
                // 已知必删：Preoccupation、RelationBase（Tier1）。
                return new HashSet<string>(StringComparer.Ordinal)
                {
                    Tier2bBlockIds.PendingTopic, Tier2bBlockIds.Eavesdrop, Tier2bBlockIds.SpouseWaiting,
                    Tier2bBlockIds.Echo, Tier2bBlockIds.LocalPerception, Tier2bBlockIds.Emotion,
                    Tier2bBlockIds.PlayerProfile, Tier2bBlockIds.DateInvite,
                };
            case InstructionsBranch.Normal:
            default:
                // FULL = 全 16（FollowProto/DateEndProto 值门控为空，不出现在输出中）。
                return new HashSet<string>(StringComparer.Ordinal)
                {
                    Tier2bBlockIds.Interaction, Tier2bBlockIds.Jealousy, Tier2bBlockIds.Preoccupation,
                    Tier2bBlockIds.PendingTopic, Tier2bBlockIds.Gift, Tier2bBlockIds.Milestone,
                    Tier2bBlockIds.Echo, Tier2bBlockIds.Eavesdrop, Tier2bBlockIds.SpouseWaiting,
                    Tier2bBlockIds.LocalPerception, Tier2bBlockIds.Emotion, Tier2bBlockIds.PlayerProfile,
                    Tier2bBlockIds.DateInvite, Tier2bBlockIds.FollowProto, Tier2bBlockIds.DateEndProto,
                    Tier2bBlockIds.Movement,
                };
        }
    }

    // ── 辅助 ──

    private static void SetTier1(Dictionary<string, string> snapshot, string blockId, string text)
    {
        if (!string.IsNullOrEmpty(text))
            snapshot[blockId] = text;
    }

    private static void SetImpulse(IDictionary<string, string> impulses, string blockId, string text)
    {
        if (!string.IsNullOrEmpty(text))
            impulses[blockId] = text;
    }

    private static bool IsMarriedOrRoommate(Character character)
    {
        Friendship friendship = null;
        Game1.getPlayerOrEventFarmer()?.friendshipData?.TryGetValue(character.Name, out friendship);
        return friendship != null && (friendship.IsMarried() || friendship.IsRoommate());
    }

}
