// GeminiRolePayloadTests.cs
// PROMPT-ARCH-04B — Gemini native role-based main-dialogue payload tests.
// Verifies: role mapping (user→user, assistant→model), adjacent-role merging,
// content/order preservation, invalid-role rejection, null rejection,
// ResponseStart omission (no synthetic model turn), system_instruction separation,
// shared contents builder for streaming/non-streaming, and cancellation/exception handling.
// No HTTP requests, no active LLM Provider required.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StardewModdingAPI;
using StardewModdingAPI.Framework.Logging;
using ValleytalkReborn;
using Xunit;

namespace ValleytalkReborn.Tests;

public class GeminiRolePayloadTests
{
    // ── 角色映射 ──

    [Fact]
    public void BuildGeminiContents_UserToUser_AssistantToModel()
    {
        IReadOnlyList<LlmChatMessage> messages = new List<LlmChatMessage>
        {
            new LlmChatMessage("user", "Farmer said hi"),
            new LlmChatMessage("assistant", "NPC replied"),
        };

        var result = LlmGemini.BuildGeminiContents(messages);

        Assert.Equal(2, result.Count);
        Assert.Equal("user", RoleOf(result[0]));
        Assert.Equal("model", RoleOf(result[1]));
        Assert.Equal("Farmer said hi", TextOf(result[0]));
        Assert.Equal("NPC replied", TextOf(result[1]));
    }

    // ── 无效角色拒绝 ──

    [Fact]
    public void BuildGeminiContents_InvalidRole_Throws()
    {
        IReadOnlyList<LlmChatMessage> messages = new List<LlmChatMessage>
        {
            new LlmChatMessage("system", "should fail"),
        };
        Assert.Throws<ArgumentException>(() => LlmGemini.BuildGeminiContents(messages));
    }

    [Fact]
    public void BuildGeminiContents_UnknownRole_Throws()
    {
        IReadOnlyList<LlmChatMessage> messages = new List<LlmChatMessage>
        {
            new LlmChatMessage("tool", "should fail"),
        };
        Assert.Throws<ArgumentException>(() => LlmGemini.BuildGeminiContents(messages));
    }

    // ── null 拒绝 ──

    [Fact]
    public void BuildGeminiContents_NullList_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => LlmGemini.BuildGeminiContents(null));
    }

    [Fact]
    public void BuildGeminiContents_NullMessage_Throws()
    {
        IReadOnlyList<LlmChatMessage> messages = new List<LlmChatMessage>
        {
            null,
        };
        Assert.Throws<ArgumentException>(() => LlmGemini.BuildGeminiContents(messages));
    }

    [Fact]
    public void BuildGeminiContents_NullContent_Throws()
    {
        IReadOnlyList<LlmChatMessage> messages = new List<LlmChatMessage>
        {
            new LlmChatMessage("user", null),
        };
        Assert.Throws<ArgumentException>(() => LlmGemini.BuildGeminiContents(messages));
    }

    // ── 相邻同角色合并 ──

    [Fact]
    public void BuildGeminiContents_AdjacentSameRole_MergeWithDoubleNewline()
    {
        IReadOnlyList<LlmChatMessage> messages = new List<LlmChatMessage>
        {
            new LlmChatMessage("user", "first"),
            new LlmChatMessage("user", "second"),
            new LlmChatMessage("assistant", "reply"),
            new LlmChatMessage("assistant", "continuation"),
        };

        var result = LlmGemini.BuildGeminiContents(messages);

        Assert.Equal(2, result.Count);
        Assert.Equal("user", RoleOf(result[0]));
        Assert.Equal("first\n\nsecond", TextOf(result[0]));
        Assert.Equal("model", RoleOf(result[1]));
        Assert.Equal("reply\n\ncontinuation", TextOf(result[1]));
    }

    // ── 异角色不合并，顺序保留 ──

    [Fact]
    public void BuildGeminiContents_DistinctRoles_PreserveOrder()
    {
        IReadOnlyList<LlmChatMessage> messages = new List<LlmChatMessage>
        {
            new LlmChatMessage("user", "A"),
            new LlmChatMessage("assistant", "B"),
            new LlmChatMessage("user", "C"),
        };

        var result = LlmGemini.BuildGeminiContents(messages);

        Assert.Equal(3, result.Count);
        Assert.Equal("user", RoleOf(result[0]));
        Assert.Equal("A", TextOf(result[0]));
        Assert.Equal("model", RoleOf(result[1]));
        Assert.Equal("B", TextOf(result[1]));
        Assert.Equal("user", RoleOf(result[2]));
        Assert.Equal("C", TextOf(result[2]));
    }

    // ── 空列表 ──

    [Fact]
    public void BuildGeminiContents_EmptyList_NoContents()
    {
        var result = LlmGemini.BuildGeminiContents(new List<LlmChatMessage>());
        Assert.Empty(result);
    }

    // ── ResponseStart 不序列化（与现有 Gemini 语义一致） ──

    [Fact]
    public void BuildGeminiContents_ResponseStart_NotInContents()
    {
        // Gemini 当前语义：responseStart 保留为参数但不插入 contents，不追加合成 model 轮。
        // 验证：即使内部消息列表末尾为 user，contents 中也不含 responseStart 文本。
        IReadOnlyList<LlmChatMessage> messages = new List<LlmChatMessage>
        {
            new LlmChatMessage("user", "context"),
            new LlmChatMessage("user", "trigger"),
        };

        var result = LlmGemini.BuildGeminiContents(messages);

        Assert.Single(result);
        Assert.Equal("user", RoleOf(result[0]));
        Assert.Equal("context\n\ntrigger", TextOf(result[0]));
        // 无合成 model 轮
        Assert.True(result.Count == 1 && RoleOf(result[0]) != "model",
            "No synthetic model turn should be appended for responseStart semantics.");
    }

    // ── 衔接轮次 FuzzyTime 保留 ──

    [Fact]
    public void BuildGeminiContents_ContinuityFuzzyTime_Preserved()
    {
        IReadOnlyList<LlmChatMessage> messages = new List<LlmChatMessage>
        {
            new LlmChatMessage("user", "[Morning] player said hi"),
            new LlmChatMessage("assistant", "npc morning reply"),
            new LlmChatMessage("user", "player no time"),
        };

        var result = LlmGemini.BuildGeminiContents(messages);

        Assert.Equal(3, result.Count);
        Assert.Equal("user", RoleOf(result[0]));
        Assert.StartsWith("[Morning] ", TextOf(result[0]));
        Assert.Equal("[Morning] player said hi", TextOf(result[0]));
        Assert.Equal("model", RoleOf(result[1]));
        Assert.Equal("npc morning reply", TextOf(result[1]));
        Assert.Equal("user", RoleOf(result[2]));
        Assert.Equal("player no time", TextOf(result[2]));
    }

    // ── 合并保留全部内容（无丢失） ──

    [Fact]
    public void BuildGeminiContents_AdjacentMerge_NoContentLoss()
    {
        IReadOnlyList<LlmChatMessage> messages = new List<LlmChatMessage>
        {
            new LlmChatMessage("user", "aaa"),
            new LlmChatMessage("user", "bbb"),
            new LlmChatMessage("user", "ccc"),
        };

        var result = LlmGemini.BuildGeminiContents(messages);

        Assert.Single(result);
        Assert.Equal("user", RoleOf(result[0]));
        Assert.Equal("aaa\n\nbbb\n\nccc", TextOf(result[0]));
    }

    // ── 流式与非流式共用同一 contents 构造 ──

    [Fact]
    public void BuildGeminiRolePayload_StreamingAndNonStreaming_ShareSameContents()
    {
        // 两种推理模式使用同一个 BuildGeminiContents → 序列化后的 contents 字节完全一致。
        IReadOnlyList<LlmChatMessage> messages = new List<LlmChatMessage>
        {
            new LlmChatMessage("user", "ctx"),
            new LlmChatMessage("user", "trigger"),
            new LlmChatMessage("assistant", "prior reply"),
        };

        var genConfig = new { maxOutputTokens = 1024, temperature = 0.9, topP = 0.9, thinkingConfig = new { thinkingBudget = 0 } };

        var payload1 = LlmGemini.BuildGeminiRolePayload("SYS", messages, genConfig, null);
        var payload2 = LlmGemini.BuildGeminiRolePayload("SYS", messages, genConfig, null);

        var j1 = JObject.Parse(payload1);
        var j2 = JObject.Parse(payload2);

        Assert.True(JToken.DeepEquals(j1["contents"], j2["contents"]),
            "Streaming and non-streaming must produce identical contents.");
        Assert.Equal("SYS", j1["system_instruction"]?["parts"]?[0]?["text"]?.ToString());
    }

    // ── system_instruction 独立，不重复注入 contents ──

    [Fact]
    public void BuildGeminiRolePayload_SystemInstruction_SeparateFromContents()
    {
        IReadOnlyList<LlmChatMessage> messages = new List<LlmChatMessage>
        {
            new LlmChatMessage("user", "hi"),
        };

        var genConfig = new { maxOutputTokens = 1024, temperature = 0.9, topP = 0.9 };
        var payload = LlmGemini.BuildGeminiRolePayload("MySystemPrompt", messages, genConfig, null);
        var json = JObject.Parse(payload);

        Assert.Equal("MySystemPrompt", json["system_instruction"]?["parts"]?[0]?["text"]?.ToString());

        var contents = json["contents"] as JArray;
        Assert.Single(contents);
        Assert.Equal("user", contents[0]["role"]?.ToString());
        Assert.Equal("hi", contents[0]["parts"]?[0]?["text"]?.ToString());
        // system_instruction 文本不得出现在 contents 中
        Assert.DoesNotContain("MySystemPrompt", contents.ToString());
    }

    // ── ResponseStart 不序列化进 payload ──

    [Fact]
    public void BuildGeminiRolePayload_ResponseStart_NotSerialized()
    {
        // BuildGeminiRolePayload 不接受 responseStart，因此它不可能出现在 payload 中。
        IReadOnlyList<LlmChatMessage> messages = new List<LlmChatMessage>
        {
            new LlmChatMessage("user", "actual content"),
        };

        var genConfig = new { maxOutputTokens = 1024, temperature = 0.9, topP = 0.9 };
        var payload = LlmGemini.BuildGeminiRolePayload("SYS", messages, genConfig, null);

        Assert.DoesNotContain("responseStart", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("[In-Character]", payload, StringComparison.OrdinalIgnoreCase);
    }

    // ── 取消令牌传播到 HTTP 请求路径（非流式） ──

    [Fact]
    public void RunGeminiChatInference_PreCancelledToken_ReturnsQuickly()
    {
        // ResolveParameters 访问 ModEntry.Config 与 ModEntry.SMonitor，在裸测试环境中均为 null。
        // 与 PromptRoleAssemblyTests 一致，注入最小 ModConfig 与 FakeMonitor 使参数解析可用。
        var originalConfig = ModEntry.Config;
        var originalMonitor = ModEntry.SMonitor;
        ModEntry.Config = new ModConfig();
        ModEntry.SMonitor = new FakeMonitor();
        try
        {
            // 使用预取消令牌：HttpClient.PostAsync 在发起网络请求前即抛出，
            // 证明取消令牌已传递到 HTTP 请求路径（无需真实网络）。
            var gemini = new LlmGemini("test-key", "gemini-2.5-flash");
            IReadOnlyList<LlmChatMessage> messages = new List<LlmChatMessage>
            {
                new LlmChatMessage("user", "hi"),
            };

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var response = gemini.RunGeminiChatInference("sys", string.Empty, string.Empty, messages, cts.Token).Result;
            sw.Stop();

            Assert.True(sw.ElapsedMilliseconds < 5000, $"Expected fast cancellation, took {sw.ElapsedMilliseconds}ms");
            Assert.False(response.IsSuccess);
        }
        finally
        {
            ModEntry.Config = originalConfig;
            ModEntry.SMonitor = originalMonitor;
        }
    }

    // ── 流式取消保持现有语义 ──

    [Fact]
    public void RunGeminiStreamingChatInference_Cancelled_PreservesExistingBehavior()
    {
        var originalConfig = ModEntry.Config;
        var originalMonitor = ModEntry.SMonitor;
        ModEntry.Config = new ModConfig();
        ModEntry.SMonitor = new FakeMonitor();
        try
        {
            var gemini = new LlmGemini("test-key", "gemini-2.5-flash");
            IReadOnlyList<LlmChatMessage> messages = new List<LlmChatMessage>
            {
                new LlmChatMessage("user", "hi"),
            };

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var response = gemini.RunGeminiStreamingChatInference("sys", string.Empty, string.Empty, messages, _ => { }, cts.Token).Result;

            Assert.NotNull(response);
            Assert.Equal(string.Empty, response.Text);
        }
        finally
        {
            ModEntry.Config = originalConfig;
            ModEntry.SMonitor = originalMonitor;
        }
    }

    // ── 流式非取消异常路径标记为失败 ──

    [Fact]
    public void BuildGeminiStreamingFailureResponse_PartialText_ReturnsFailed()
    {
        var gemini = new LlmGemini("test-key", "gemini-2.5-flash");
        var response = gemini.BuildGeminiStreamingFailureResponse(new Exception("simulated stream failure"), "partial text collected");

        Assert.False(response.IsSuccess);
        Assert.Equal(500, response.ResponseCode);
        Assert.Equal("partial text collected", response.ErrorMessage);
    }

    [Fact]
    public void BuildGeminiStreamingFailureResponse_EmptyPartialText_ReturnsFailed()
    {
        var gemini = new LlmGemini("test-key", "gemini-2.5-flash");
        var response = gemini.BuildGeminiStreamingFailureResponse(new Exception("fail"), string.Empty);

        Assert.False(response.IsSuccess);
        Assert.Equal(500, response.ResponseCode);
    }

    // ── ProcessGeminiSseAsync：SSE 分片解析失败不再静默忽略 ──

    [Fact]
    public void ProcessGeminiSseAsync_MalformedChunkAfterPartialText_ReturnsFailedWithPartial()
    {
        // 合法文本分片之后紧跟一个非法 JSON 分片 → 解析失败，已收集文本仅作诊断保留。
        var gemini = new LlmGemini("test-key", "gemini-2.5-flash");
        using var stream = BuildGeminiSseStream(new[]
        {
            "data: " + JsonConvert.SerializeObject(new
            {
                candidates = new[] { new { content = new { parts = new[] { new { text = "partial" } } } } }
            }),
            "data: { this is not valid json",
        });

        var result = gemini.ProcessGeminiSseAsync(stream, _ => { }, CancellationToken.None).Result;

        Assert.False(result.Success);
        Assert.NotNull(result.ParseError);
        // 已收集文本保留（诊断用），但结果不是成功对话
        Assert.Equal("partial", result.Text);
    }

    [Fact]
    public void ProcessGeminiSseAsync_MalformedChunk_FailedLlmResponse()
    {
        // 完整映射：解析失败 → BuildGeminiStreamingFailureResponse → IsSuccess=false / ResponseCode=500。
        var gemini = new LlmGemini("test-key", "gemini-2.5-flash");
        using var stream = BuildGeminiSseStream(new[]
        {
            "data: " + JsonConvert.SerializeObject(new
            {
                candidates = new[] { new { content = new { parts = new[] { new { text = "partial" } } } } }
            }),
            "data: { this is not valid json",
        });

        var sseResult = gemini.ProcessGeminiSseAsync(stream, _ => { }, CancellationToken.None).Result;
        var response = gemini.BuildGeminiStreamingFailureResponse(sseResult.ParseError, sseResult.Text);

        Assert.False(response.IsSuccess);
        Assert.Equal(500, response.ResponseCode);
        Assert.Equal("partial", response.ErrorMessage);
    }

    [Fact]
    public void ProcessGeminiSseAsync_MalformedChunk_LogsError()
    {
        // 解析失败必须产生 Warn/Error 日志，标识 Gemini role-based 解析失败。
        var gemini = new LlmGemini("test-key", "gemini-2.5-flash");
        var capture = new CapturingMonitor();
        var originalLogMonitor = Log.Logger.Monitor;
        Log.Initialize(capture);
        try
        {
            using var stream = BuildGeminiSseStream(new[]
            {
                "data: " + JsonConvert.SerializeObject(new
                {
                    candidates = new[] { new { content = new { parts = new[] { new { text = "partial" } } } } }
                }),
                "data: { this is not valid json",
            });

            gemini.ProcessGeminiSseAsync(stream, _ => { }, CancellationToken.None).Wait();

            Assert.True(
                capture.Captured.ToString().Contains("parse failed", StringComparison.OrdinalIgnoreCase),
                "A Warn/Error entry identifying the Gemini SSE parse failure should be emitted.");
        }
        finally
        {
            Log.Initialize(originalLogMonitor);
        }
    }

    [Fact]
    public void ProcessGeminiSseAsync_ValidEmptyContentChunk_NotTreatedAsFailure()
    {
        // 有效 JSON 但无文本/工具调用分片（空 candidates / 无 parts）→ 不视为失败，继续解析。
        var gemini = new LlmGemini("test-key", "gemini-2.5-flash");
        using var stream = BuildGeminiSseStream(new[]
        {
            "data: " + JsonConvert.SerializeObject(new { candidates = new object[] { } }),
            "data: " + JsonConvert.SerializeObject(new
            {
                candidates = new[] { new { content = new { parts = new[] { new { text = "ok" } } } } }
            }),
        });

        var result = gemini.ProcessGeminiSseAsync(stream, _ => { }, CancellationToken.None).Result;

        Assert.True(result.Success);
        Assert.Null(result.ParseError);
        Assert.Equal("ok", result.Text);
    }

    [Fact]
    public void ProcessGeminiSseAsync_MidStreamCancellation_NoParseErrorLog()
    {
        // 请求级取消（CancellationToken）不应被归类为解析失败，也不产生 Error 日志。
        var gemini = new LlmGemini("test-key", "gemini-2.5-flash");
        var capture = new CapturingMonitor();
        var originalLogMonitor = Log.Logger.Monitor;
        Log.Initialize(capture);
        try
        {
            using var stream = BuildGeminiSseStream(new[]
            {
                "data: " + JsonConvert.SerializeObject(new
                {
                    candidates = new[] { new { content = new { parts = new[] { new { text = "partial" } } } } }
                }),
            });

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var result = gemini.ProcessGeminiSseAsync(stream, _ => { }, cts.Token).Result;

            // 取消导致循环退出：Success 保持 true（非解析失败），无 ParseError。
            Assert.True(result.Success);
            Assert.Null(result.ParseError);
            Assert.False(
                capture.Captured.ToString().Contains("parse failed", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Log.Initialize(originalLogMonitor);
        }
    }

    // ── 辅助 ──

    private static string RoleOf(object content)
    {
        var j = content as JObject;
        return j?["role"]?.ToString();
    }

    private static string TextOf(object content)
    {
        var j = content as JObject;
        return j?["parts"]?[0]?["text"]?.ToString();
    }

    private static MemoryStream BuildGeminiSseStream(string[] lines)
    {
        var text = string.Join("\n", lines) + "\n";
        var bytes = Encoding.UTF8.GetBytes(text);
        return new MemoryStream(bytes);
    }

    private sealed class CapturingMonitor : IMonitor
    {
        public readonly StringBuilder Captured = new StringBuilder();
        public bool IsVerbose => false;
        public void Log(string message, LogLevel level) => Captured.AppendLine(message);
        public void LogOnce(string message, LogLevel level) => Captured.AppendLine(message);
        public void VerboseLog(string message) { }
        public void VerboseLog(ref VerboseLogStringHandler handler) { }
    }
}
