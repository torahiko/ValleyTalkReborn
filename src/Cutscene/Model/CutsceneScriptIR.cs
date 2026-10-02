using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.Xna.Framework;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ValleytalkReborn.Cutscene.Model
{
    /// <summary>
    /// 虚拟导演剧本声明式中间表示 (Cutscene Script IR)
    /// </summary>
    public sealed class CutsceneScriptIR
    {
        [JsonProperty("title")]
        public string Title { get; set; } = "Untitled Cutscene";

        [JsonProperty("description")]
        public string Description { get; set; } = string.Empty;

        [JsonProperty("actors")]
        public List<string> Actors { get; set; } = new();

        [JsonProperty("actions")]
        public List<CutsceneActionIR> Actions { get; set; } = new();
    }

    /// <summary>
    /// 动作节点中间表示 (Cutscene Action IR)
    /// </summary>
    public sealed class CutsceneActionIR
    {
        /// <summary>
        /// 动作类型: camera | move | lookAt | face | emote | speak | wait | sound | choice
        /// </summary>
        [JsonProperty("type")]
        public string Type { get; set; } = string.Empty;

        /// <summary>
        /// 执行主体角色内部名或别名 (如 "Alex")
        /// </summary>
        [JsonProperty("actor")]
        public string Actor { get; set; }

        /// <summary>
        /// 交互目标 (NPC名 / "farmer" / "player")
        /// </summary>
        [JsonProperty("target")]
        public string Target { get; set; }

        /// <summary>
        /// 目标瓦片坐标：宽容支持 [64, 67]、{"x": 64, "y": 67}、"64, 67" 等多形态输入
        /// </summary>
        [JsonProperty("targetTile")]
        public JToken TargetTileToken { get; set; }

        /// <summary>
        /// 对白文本
        /// </summary>
        [JsonProperty("text")]
        public string Text { get; set; }

        /// <summary>
        /// 表情气泡标识 (如 "SURPRISE", "HEART", "QUESTION" 或数字 ID)
        /// </summary>
        [JsonProperty("emote")]
        public string Emote { get; set; }

        /// <summary>
        /// 面向朝向：支持 0, 1, 2, 3 或 "up", "right", "down", "left" 等
        /// </summary>
        [JsonProperty("direction")]
        public JToken Direction { get; set; }

        /// <summary>
        /// 持续时长 (秒): 用于 wait
        /// </summary>
        [JsonProperty("duration")]
        public float? Duration { get; set; }

        /// <summary>
        /// 寻路超时 (秒): 用于 move 看门狗兜底
        /// </summary>
        [JsonProperty("timeout")]
        public float? Timeout { get; set; }

        /// <summary>
        /// 原生音效名称 (如 "dwop", "coin", "purchase")
        /// </summary>
        [JsonProperty("soundName")]
        public string SoundName { get; set; }

        /// <summary>
        /// 是否阻塞等待该动作完成。
        /// 默认为 true；设为 false 时立即执行下一个动作，实现并发演出（如边走边说、运镜协同）。
        /// </summary>
        [JsonProperty("waitForCompletion")]
        public bool WaitForCompletion { get; set; } = true;

        /// <summary>
        /// 供未来交互选择肢扩展的分支列表
        /// </summary>
        [JsonProperty("options")]
        public List<CutsceneChoiceOptionIR> Options { get; set; }

        /// <summary>
        /// choice 动作提示文本 (如 "你要如何回答？")
        /// </summary>
        [JsonProperty("prompt")]
        public string Prompt { get; set; }

        /// <summary>
        /// 将 TargetTileToken 安全解析为瓦片坐标 Vector2
        /// </summary>
        public Vector2? ResolveTargetTile()
        {
            if (TargetTileToken == null) return null;

            try
            {
                // 形态 1: [x, y]
                if (TargetTileToken is JArray arr && arr.Count >= 2)
                {
                    float x = arr[0].Value<float>();
                    float y = arr[1].Value<float>();
                    return new Vector2(x, y);
                }

                // 形态 2: {"x": 64, "y": 67}
                if (TargetTileToken is JObject obj)
                {
                    JToken xTok = obj["x"] ?? obj["X"];
                    JToken yTok = obj["y"] ?? obj["Y"];
                    if (xTok != null && yTok != null)
                    {
                        return new Vector2(xTok.Value<float>(), yTok.Value<float>());
                    }
                }

                // 形态 3: "64, 67" 或 "64,67"
                if (TargetTileToken.Type == JTokenType.String)
                {
                    string str = TargetTileToken.Value<string>();
                    if (!string.IsNullOrWhiteSpace(str))
                    {
                        string[] parts = str.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                        if (parts.Length >= 2 &&
                            float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) &&
                            float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y))
                        {
                            return new Vector2(x, y);
                        }
                    }
                }
            }
            catch
            {
                // 解析失败返回 null，由编译器记录 Warning
            }

            return null;
        }

        /// <summary>
        /// 将 Direction 安全解析为星露谷朝向整数 (0=上, 1=右, 2=下, 3=左)
        /// </summary>
        public int? ResolveDirection()
        {
            if (Direction == null) return null;

            try
            {
                // 数值形态
                if (Direction.Type == JTokenType.Integer || Direction.Type == JTokenType.Float)
                {
                    int val = Direction.Value<int>();
                    return Math.Clamp(val, 0, 3);
                }

                // 字符串形态
                if (Direction.Type == JTokenType.String)
                {
                    string raw = Direction.Value<string>()?.Trim().ToLowerInvariant();
                    return raw switch
                    {
                        "0" or "up" or "north" or "上" or "北" => 0,
                        "1" or "right" or "east" or "右" or "东" => 1,
                        "2" or "down" or "south" or "下" or "南" => 2,
                        "3" or "left" or "west" or "左" or "西" => 3,
                        _ => null
                    };
                }
            }
            catch
            {
                // 解析失败返回 null
            }

            return null;
        }
    }

    /// <summary>
    /// 对话分支选项中间表示（供后续分支扩展）
    /// </summary>
    public sealed class CutsceneChoiceOptionIR
    {
        [JsonProperty("text")]
        public string Text { get; set; } = string.Empty;

        [JsonProperty("friendship")]
        public int Friendship { get; set; } = 0;

        [JsonProperty("feedback")]
        public string Feedback { get; set; }

        [JsonProperty("actions")]
        public List<CutsceneActionIR> Actions { get; set; } = new();
    }
}
