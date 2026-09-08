using Newtonsoft.Json.Linq;
using StardewValley;

namespace ValleytalkReborn;

/// <summary>
/// 定义 ValleyTalk 具身 Agent 的核心工具 Schema，
/// 支持输出 OpenAI / Anthropic / Google Gemini 三种格式。
/// </summary>
internal static class AgentToolDefinitions
{
    // ── Tool name constants (shared with AgentToolDispatcher) ─────────────────
    internal const string ToolScheduleDate   = "schedule_date";
    internal const string ToolEndDate        = "end_current_date";
    internal const string ToolPhysicalAction = "trigger_physical_action";
    internal const string ToolSpeakInBubble  = "speak_in_bubble";

    // Maximum characters allowed in a speech bubble before truncation
    internal const int BubbleMaxChars = 30;

    // 🔧 FIX: 集合表达式 ["..."] 是 C# 12 语法，改为 new[] { ... } 兼容 C# 10/11
    private static readonly string[] LocationIds =
        new[] { "Saloon", "Beach", "Town", "Forest", "Mountain", "FarmHouse", "Farm" };

    private static readonly string[] ActionTypes =
        new[] { "STEP:FORWARD", "STEP:BACKWARD", "STEP:LEFT", "STEP:RIGHT", "STEP:UP", "STEP:DOWN", "FOLLOW", "STAY_HOME", "ALL_DAY_FOLLOW" };

    private static bool IsChinese =>
        LocalizedContentManager.CurrentLanguageCode == LocalizedContentManager.LanguageCode.zh;

    // ─────────────────────────────────────────────────────────────────────
    //  OpenAI / OAI-Compatible format
    // ─────────────────────────────────────────────────────────────────────

    public static JArray GetOpenAiToolsArray()
    {
        bool zh = IsChinese;
        return new JArray
        {
            new JObject
            {
                ["type"] = "function",
                ["function"] = new JObject
                {
                    ["name"] = ToolScheduleDate,
                    ["description"] = zh
                        ? "当真诚接受玩家今晚的约会邀请时调用此工具。"
                        : "Call this tool when agreeing to go on a date with the player tonight. Only call it when you sincerely accept the invitation.",
                    ["parameters"] = new JObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JObject
                        {
                            ["location_id"] = new JObject
                            {
                                ["type"] = "string",
                                ["description"] = zh
                                    ? "今晚 20:00 约会举行的地点。"
                                    : "The location where the date will take place tonight at 20:00.",
                                ["enum"] = new JArray(LocationIds)
                            }
                        },
                        ["required"] = new JArray("location_id")
                    }
                }
            },
            new JObject
            {
                ["type"] = "function",
                ["function"] = new JObject
                {
                    ["name"] = ToolEndDate,
                    ["description"] = zh
                        ? "优雅地结束当前约会时调用此工具（例如玩家道别或准备睡觉）。"
                        : "Call this tool to gracefully end the current date (e.g., when the player says goodbye or mentions going to bed).",
                    ["parameters"] = new JObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JObject
                        {
                            ["reason"] = new JObject
                            {
                                ["type"] = "string",
                                ["description"] = "The reason for ending the date, e.g., 'player_goodbye', 'time_to_sleep', 'date_complete'."
                            }
                        },
                        ["required"] = new JArray("reason")
                    }
                }
            },
            new JObject
            {
                ["type"] = "function",
                ["function"] = new JObject
                {
                    ["name"] = ToolPhysicalAction,
                    ["description"] = zh
                        ? "使自己执行物理移动、跟随玩家或调整陪伴状态。"
                        : "Call this tool to make yourself perform a physical movement, follow the player, or adjust companion state.",
                    ["parameters"] = new JObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JObject
                        {
                            ["action_type"] = new JObject
                            {
                                ["type"] = "string",
                                ["description"] = zh
                                    ? "要执行的动作类型。STAY_HOME：让配偶今天留在家里不外出。ALL_DAY_FOLLOW：让配偶今天全程陪伴玩家。"
                                    : "The type of movement or physical action to perform. STAY_HOME: keep the spouse at home all day. ALL_DAY_FOLLOW: have the spouse accompany the player all day.",
                                ["enum"] = new JArray(ActionTypes)
                            }
                        },
                        ["required"] = new JArray("action_type")
                    }
                }
            },
            new JObject
            {
                ["type"] = "function",
                ["function"] = new JObject
                {
                    ["name"] = ToolSpeakInBubble,
                    ["description"] = zh
                        ? $"在头顶显示简短悬浮气泡，不打开对话框。字数限制在 {BubbleMaxChars} 字以内。"
                        : $"Show a short floating speech bubble above your head WITHOUT opening a dialogue box. Use for brief, non-blocking reactions: casual acknowledgements, movement comments, greetings. Keep text under {BubbleMaxChars} characters. Mutually exclusive with a dialogue-box response — if you call this tool, do NOT output text in your reply.",
                    ["parameters"] = new JObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JObject
                        {
                            ["text"] = new JObject
                            {
                                ["type"] = "string",
                                ["description"] = $"The short text to display in the bubble. Max {BubbleMaxChars} characters."
                            }
                        },
                        ["required"] = new JArray("text")
                    }
                }
            }
        };
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Anthropic format
    // ─────────────────────────────────────────────────────────────────────

    public static JArray GetAnthropicToolsArray()
    {
        bool zh = IsChinese;
        return new JArray
        {
            new JObject
            {
                ["name"] = ToolScheduleDate,
                ["description"] = zh
                    ? "当真诚接受玩家今晚的约会邀请时调用此工具。"
                    : "Call this tool when agreeing to go on a date with the player tonight. Only call it when you sincerely accept the invitation.",
                ["input_schema"] = new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject
                    {
                        ["location_id"] = new JObject
                        {
                            ["type"] = "string",
                            ["description"] = "The location where the date will take place tonight at 20:00.",
                            ["enum"] = new JArray(LocationIds)
                        }
                    },
                    ["required"] = new JArray("location_id")
                }
            },
            new JObject
            {
                ["name"] = ToolEndDate,
                ["description"] = zh
                    ? "优雅地结束当前约会时调用此工具（例如玩家道别或准备睡觉）。"
                    : "Call this tool to gracefully end the current date (e.g., when the player says goodbye or mentions going to bed).",
                ["input_schema"] = new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject
                    {
                        ["reason"] = new JObject
                        {
                            ["type"] = "string",
                            ["description"] = "The reason for ending the date, e.g., 'player_goodbye', 'time_to_sleep', 'date_complete'."
                        }
                    },
                    ["required"] = new JArray("reason")
                }
            },
            new JObject
            {
                ["name"] = ToolPhysicalAction,
                ["description"] = zh
                    ? "使自己执行物理移动、跟随玩家或调整陪伴状态。"
                    : "Call this tool to make yourself perform a physical movement, follow the player, or adjust companion state.",
                ["input_schema"] = new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject
                    {
                        ["action_type"] = new JObject
                        {
                            ["type"] = "string",
                            ["description"] = zh
                                ? "要执行的动作类型。STAY_HOME：让配偶今天留在家里不外出。ALL_DAY_FOLLOW：让配偶今天全程陪伴玩家。"
                                : "The type of movement or physical action to perform. STAY_HOME: keep the spouse at home all day. ALL_DAY_FOLLOW: have the spouse accompany the player all day.",
                            ["enum"] = new JArray(ActionTypes)
                        }
                    },
                    ["required"] = new JArray("action_type")
                }
            },
            new JObject
            {
                ["name"] = ToolSpeakInBubble,
                ["description"] = zh
                    ? $"在头顶显示简短悬浮气泡，不打开对话框。字数限制在 {BubbleMaxChars} 字以内。"
                    : $"Show a short floating speech bubble above your head WITHOUT opening a dialogue box. Use for brief, non-blocking reactions: casual acknowledgements, movement comments, greetings. Keep text under {BubbleMaxChars} characters. Mutually exclusive with a dialogue-box response — if you call this tool, do NOT output text in your reply.",
                ["input_schema"] = new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject
                    {
                        ["text"] = new JObject
                        {
                            ["type"] = "string",
                            ["description"] = $"The short text to display in the bubble. Max {BubbleMaxChars} characters."
                        }
                    },
                    ["required"] = new JArray("text")
                }
            }
        };
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Google Gemini format
    // ─────────────────────────────────────────────────────────────────────

    public static JArray GetGeminiToolsArray()
    {
        bool zh = IsChinese;
        return new JArray
        {
            new JObject
            {
                ["name"] = ToolScheduleDate,
                ["description"] = zh
                    ? "当真诚接受玩家今晚的约会邀请时调用此工具。"
                    : "Call this tool when agreeing to go on a date with the player tonight. Only call it when you sincerely accept the invitation.",
                ["parameters"] = new JObject
                {
                    ["type"] = "OBJECT",
                    ["properties"] = new JObject
                    {
                        ["location_id"] = new JObject
                        {
                            ["type"] = "STRING",
                            ["description"] = "The location where the date will take place tonight at 20:00.",
                            ["enum"] = new JArray(LocationIds)
                        }
                    },
                    ["required"] = new JArray("location_id")
                }
            },
            new JObject
            {
                ["name"] = ToolEndDate,
                ["description"] = zh
                    ? "优雅地结束当前约会时调用此工具（例如玩家道别或准备睡觉）。"
                    : "Call this tool to gracefully end the current date (e.g., when the player says goodbye or mentions going to bed).",
                ["parameters"] = new JObject
                {
                    ["type"] = "OBJECT",
                    ["properties"] = new JObject
                    {
                        ["reason"] = new JObject
                        {
                            ["type"] = "STRING",
                            ["description"] = "The reason for ending the date, e.g., 'player_goodbye', 'time_to_sleep', 'date_complete'."
                        }
                    },
                    ["required"] = new JArray("reason")
                }
            },
            new JObject
            {
                ["name"] = ToolPhysicalAction,
                ["description"] = zh
                    ? "使自己执行物理移动、跟随玩家或调整陪伴状态。"
                    : "Call this tool to make yourself perform a physical movement, follow the player, or adjust companion state.",
                ["parameters"] = new JObject
                {
                    ["type"] = "OBJECT",
                    ["properties"] = new JObject
                    {
                        ["action_type"] = new JObject
                        {
                            ["type"] = "STRING",
                            ["description"] = zh
                                ? "要执行的动作类型。STAY_HOME：让配偶今天留在家里不外出。ALL_DAY_FOLLOW：让配偶今天全程陪伴玩家。"
                                : "The type of movement or physical action to perform. STAY_HOME: keep the spouse at home all day. ALL_DAY_FOLLOW: have the spouse accompany the player all day.",
                            ["enum"] = new JArray(ActionTypes)
                        }
                    },
                    ["required"] = new JArray("action_type")
                }
            },
            new JObject
            {
                ["name"] = ToolSpeakInBubble,
                ["description"] = zh
                    ? $"在头顶显示简短悬浮气泡，不打开对话框。字数限制在 {BubbleMaxChars} 字以内。"
                    : $"Show a short floating speech bubble above your head WITHOUT opening a dialogue box. Use for brief, non-blocking reactions: casual acknowledgements, movement comments, greetings. Keep text under {BubbleMaxChars} characters. Mutually exclusive with a dialogue-box response — if you call this tool, do NOT output text in your reply.",
                ["parameters"] = new JObject
                {
                    ["type"] = "OBJECT",
                    ["properties"] = new JObject
                    {
                        ["text"] = new JObject
                        {
                            ["type"] = "STRING",
                            ["description"] = $"The short text to display in the bubble. Max {BubbleMaxChars} characters."
                        }
                    },
                    ["required"] = new JArray("text")
                }
            }
        };
    }
}