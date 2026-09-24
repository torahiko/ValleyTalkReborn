// InstructionsBranch.cs
// VT3-B — 对话指令分支枚举。决定 Tier 1 快照的复用与会话续用语义。
// 与 PromptTopologyDumper 的四分支夹具（Normal / StoodUp / Greeting / Date）一一对应。

namespace ValleytalkReborn;

/// <summary>
/// 标识一次 LLM 请求所处的对话分支。
/// Tier 1 快照按 branch 隔离复用：同一 NPC 在相同 branch 下可续用会话，
/// branch 切换（如 Normal → Date）会触发旧会话退役。
/// </summary>
public enum InstructionsBranch
{
    Normal,
    StoodUp,
    Date,
    Greeting,
}
