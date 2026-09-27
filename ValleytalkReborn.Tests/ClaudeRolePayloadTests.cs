// ClaudeRolePayloadTests.cs
// PROMPT-ARCH-04A — Claude native role-based main-dialogue payload tests.
// Verifies: role mapping (user/assistant), adjacent-role merging,
// content/order preservation, invalid-role rejection, null rejection,
// ResponseStart behavior, and FuzzyTime retention for continuity turns.
// No HTTP requests, no active LLM Provider required.

using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using ValleytalkReborn;
using Xunit;

public class ClaudeRolePayloadTests
{
    // ── 角色映射 ──

    [Fact]
    public void BuildClaudeMessages_UserToUser_AssistantToAssistant()
    {
        IReadOnlyList<LlmChatMessage> messages = new List<LlmChatMessage>
        {
            new LlmChatMessage("user", "Farmer said hi"),
            new LlmChatMessage("assistant", "NPC replied"),
        };

        var result = LlmClaude.BuildClaudeMessages(messages, "");

        Assert.Equal(2, result.Count);
        Assert.Equal("user", RoleOf(result[0]));
        Assert.Equal("assistant", RoleOf(result[1]));
        Assert.Equal("Farmer said hi", ContentOf(result[0]));
        Assert.Equal("NPC replied", ContentOf(result[1]));
    }

    // ── 无效角色拒绝 ──

    [Fact]
    public void BuildClaudeMessages_InvalidRole_Throws()
    {
        IReadOnlyList<LlmChatMessage> messages = new List<LlmChatMessage>
        {
            new LlmChatMessage("system", "should fail"),
        };
        Assert.Throws<ArgumentException>(() => LlmClaude.BuildClaudeMessages(messages, ""));
    }

    // ── null 内容拒绝 ──

    [Fact]
    public void BuildClaudeMessages_NullContent_Throws()
    {
        IReadOnlyList<LlmChatMessage> messages = new List<LlmChatMessage>
        {
            new LlmChatMessage("user", null),
        };
        Assert.Throws<ArgumentException>(() => LlmClaude.BuildClaudeMessages(messages, ""));
    }

    // ── 相邻同角色合并 ──

    [Fact]
    public void BuildClaudeMessages_AdjacentSameRole_MergeWithDoubleNewline()
    {
        IReadOnlyList<LlmChatMessage> messages = new List<LlmChatMessage>
        {
            new LlmChatMessage("user", "first"),
            new LlmChatMessage("user", "second"),
            new LlmChatMessage("assistant", "reply"),
            new LlmChatMessage("assistant", "continuation"),
        };

        var result = LlmClaude.BuildClaudeMessages(messages, "");

        Assert.Equal(2, result.Count);
        Assert.Equal("user", RoleOf(result[0]));
        Assert.Equal("first\n\nsecond", ContentOf(result[0]));
        Assert.Equal("assistant", RoleOf(result[1]));
        Assert.Equal("reply\n\ncontinuation", ContentOf(result[1]));
    }

    // ── 异角色不合并，顺序保留 ──

    [Fact]
    public void BuildClaudeMessages_DistinctRoles_PreserveOrder()
    {
        IReadOnlyList<LlmChatMessage> messages = new List<LlmChatMessage>
        {
            new LlmChatMessage("user", "A"),
            new LlmChatMessage("assistant", "B"),
            new LlmChatMessage("user", "C"),
        };

        var result = LlmClaude.BuildClaudeMessages(messages, "");

        Assert.Equal(3, result.Count);
        Assert.Equal("user", RoleOf(result[0]));
        Assert.Equal("A", ContentOf(result[0]));
        Assert.Equal("assistant", RoleOf(result[1]));
        Assert.Equal("B", ContentOf(result[1]));
        Assert.Equal("user", RoleOf(result[2]));
        Assert.Equal("C", ContentOf(result[2]));
    }

    // ── ResponseStart 行为（与现有 Claude 语义一致） ──

    [Fact]
    public void BuildClaudeMessages_ResponseStart_AsTrailingAssistant()
    {
        // 末尾为 user 消息时，responseStart 作为独立 assistant 消息追加。
        // 两个相邻 user 消息会合并为一个。
        IReadOnlyList<LlmChatMessage> messages = new List<LlmChatMessage>
        {
            new LlmChatMessage("user", "context"),
            new LlmChatMessage("user", "trigger"),
        };

        var result = LlmClaude.BuildClaudeMessages(messages, "[In-Character]:");

        Assert.Equal(2, result.Count);
        Assert.Equal("user", RoleOf(result[0]));
        Assert.Equal("context\n\ntrigger", ContentOf(result[0]));
        Assert.Equal("assistant", RoleOf(result[1]));
        Assert.Equal("[In-Character]:", ContentOf(result[1]));
    }

    [Fact]
    public void BuildClaudeMessages_ResponseStart_Empty_NoAssistantAppended()
    {
        IReadOnlyList<LlmChatMessage> messages = new List<LlmChatMessage>
        {
            new LlmChatMessage("user", "context"),
        };

        var result = LlmClaude.BuildClaudeMessages(messages, "");

        Assert.Single(result);
        Assert.Equal("user", RoleOf(result[0]));
    }

    // ── 衔接轮次 FuzzyTime 保留 ──

    [Fact]
    public void BuildClaudeMessages_ContinuityFuzzyTime_Preserved()
    {
        // 模拟 BuildRuntimeChatMessages 产出的衔接角色消息（带 FuzzyTime 前缀）。
        IReadOnlyList<LlmChatMessage> messages = new List<LlmChatMessage>
        {
            new LlmChatMessage("user", "[Morning] player said hi"),
            new LlmChatMessage("assistant", "npc morning reply"),
            new LlmChatMessage("user", "player no time"),
        };

        var result = LlmClaude.BuildClaudeMessages(messages, "");

        Assert.Equal(3, result.Count);
        Assert.Equal("user", RoleOf(result[0]));
        Assert.StartsWith("[Morning] ", ContentOf(result[0]));
        Assert.Equal("[Morning] player said hi", ContentOf(result[0]));
        Assert.Equal("assistant", RoleOf(result[1]));
        Assert.Equal("npc morning reply", ContentOf(result[1]));
        Assert.Equal("user", RoleOf(result[2]));
        Assert.Equal("player no time", ContentOf(result[2]));
    }

    [Fact]
    public void BuildClaudeMessages_ContinuityFuzzyTime_Empty_NoPrefix()
    {
        IReadOnlyList<LlmChatMessage> messages = new List<LlmChatMessage>
        {
            new LlmChatMessage("assistant", "npc reply without time"),
        };

        var result = LlmClaude.BuildClaudeMessages(messages, "");

        Assert.Single(result);
        Assert.Equal("assistant", RoleOf(result[0]));
        Assert.Equal("npc reply without time", ContentOf(result[0]));
        Assert.DoesNotContain("[", ContentOf(result[0]));
    }

    // ── 空列表 ──

    [Fact]
    public void BuildClaudeMessages_EmptyList_NoMessages()
    {
        var result = LlmClaude.BuildClaudeMessages(new List<LlmChatMessage>(), "");
        Assert.Empty(result);
    }

    // ── 辅助 ──

    private static string RoleOf(object msg)
    {
        var j = msg as JObject;
        return j?["role"]?.ToString();
    }

    private static string ContentOf(object msg)
    {
        var j = msg as JObject;
        return j?["content"]?.ToString();
    }
}
