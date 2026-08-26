using System.Collections.Generic;

namespace ValleytalkReborn;

/// <summary>
/// A2A 提示词快照：主线程构建，后台 LLM 只接收纯数据。
/// 不允许放入 NPC、Farmer、GameLocation、Character、Friendship 等游戏对象。
/// </summary>
internal sealed class A2APromptSnapshot
{
    internal string SessionId { get; set; }
    internal List<string> ParticipantNames { get; set; } =
        new List<string>();

    internal List<string> DisplayNames { get; set; } =
        new List<string>();

    internal string SystemPrompt { get; set; }
    internal string UserPrompt { get; set; }
    internal string NamesLog { get; set; }
    internal bool IsChinese { get; set; }
}
