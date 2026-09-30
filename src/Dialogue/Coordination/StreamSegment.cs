// StreamSegment.cs
// ═══════════════════════════════════════════════════════════════════════════
// VT-STREAM-02 流式分流器的输出契约（票 VT-STREAM-02-StreamPipeline）
// ═══════════════════════════════════════════════════════════════════════════
//
// StreamTokenPipeline 把 LLM 流式返回的原始字符流切分为三类语义片段：
//   Text     —— 应交给对白框逐字揭示的台词正文
//   Action   —— [ACTION:...] / [UI:...] 等行为控制标签的载荷
//   Portrait —— [MOOD:...] 标签与原版 $h/$s/$u 情绪码
//
// 本文件为纯数据类型，不含任何 Game1 / MonoGame 依赖。

namespace ValleytalkReborn.Dialogue.Coordination;

/// <summary>流式片段的语义类别。</summary>
public enum StreamSegmentType
{
    /// <summary>台词正文，交给对白框打字机揭示。</summary>
    Text,

    /// <summary>行为控制标签载荷（[ACTION:...] / [UI:...] / [241] 等）。</summary>
    Action,

    /// <summary>情绪/表情指令载荷（[MOOD:...] / $h / $5 等）。</summary>
    Portrait
}

/// <summary>流式分流器产出的单个片段。</summary>
/// <param name="Type">片段语义类别。</param>
/// <param name="Payload">片段载荷；已剥离标签定界符（方括号、$ 前缀）。</param>
public sealed record StreamSegment(StreamSegmentType Type, string Payload);
