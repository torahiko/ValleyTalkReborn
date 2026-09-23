using System;

namespace ValleytalkReborn
{
    /// <summary>
    /// AI 润色提示词工厂：集中产出送往 LLM 的 (system, user) 提示词对。
    /// 菜单层禁止直接硬编码业务文案，一律经由此处组装外发。
    /// </summary>
    internal static class BioPromptBuilder
    {
        private const int AnchorCap = 1500;

        private static string GetCorePersonaAnchor(BioEditorViewModel vm, string npcName)
        {
            string bio = vm?.GetBiography() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(bio))
                return $"角色：{npcName}（身份档案暂未填写，请仅依据言行与对白模块推断语气，不要臆造身份）。";

            string trimmed = bio.Length > AnchorCap ? bio.Substring(0, AnchorCap) + "…" : bio;
            return $"角色：{npcName}\n核心身份档案：\n{trimmed}";
        }

        public static (string System, string User) BuildBehaviorRulesPrompt(
            BioEditorViewModel vm, string npcName, string currentText, string demand)
        {
            string anchor = GetCorePersonaAnchor(vm, npcName);
            string system =
                "你是一名专业的《星露谷物语》NPC 人设编辑助手，正在润色角色的【言行举止 (BehavioralRules)】模块。\n" +
                "润色时必须以该角色的核心身份为锚点，不得偏离人格设定：\n" +
                $"{anchor}\n" +
                "硬性要求：\n" +
                $"1. 润色对象为 {npcName} 的言行规则（语气、节奏、口头习惯、即时反应），保持条目化、可执行。\n" +
                "2. 保留 [VOICE] / [SPEECH PATTERNS] / [MANNERISMS] / [IMMEDIATE REFLEXES] / [CONTEXT OVERRIDE] 等既有分节结构。\n" +
                "3. 严格遵循玩家给出的调整方向，不得自行扩展与角色身份冲突的内容。\n" +
                "4. 严禁寒暄、解释、标题，只输出言行规则纯文本。\n" +
                "5. 严禁 Markdown 代码围栏（```）、严禁 JSON 注释（// 或 /* */）。";

            string user =
                $"【当前言行规则】\n{currentText}\n\n" +
                $"【玩家调整方向】\n{demand}\n\n" +
                "请直接输出优化后的言行规则全文，保留分节标记。";

            return (system, user);
        }

        public static (string System, string User) BuildDialogueExamplesPrompt(
            BioEditorViewModel vm, string npcName, string currentText, string demand)
        {
            string anchor = GetCorePersonaAnchor(vm, npcName);
            string tone = vm != null
                ? (vm.GetTraitDescriptionOrNull("BehavioralRules") ?? "(以原版标准语气为准)")
                : "(以原版标准语气为准)";
            string system =
                "你是一名专业的《星露谷物语》NPC 人设编辑助手，正在润色角色的【对白范例 (DialogueExamples)】模块。\n" +
                "润色时必须以该角色的核心身份为锚点，不得偏离人格设定：\n" +
                $"{anchor}\n" +
                "关联言行基调（仅供语气参照，非润色对象）：\n" +
                $"{tone}\n" +
                "硬性要求：\n" +
                $"1. 为 {npcName} 生成 3 条对白范例，分别对应：日常问候、闲聊、情绪低落。\n" +
                "2. 对白必须体现角色性格与当下关系亲疏，语气与言行规则一致。\n" +
                "3. 支持原版表情符（$h、$s 等）与换行分段；玩家选项以 % 开头。\n" +
                "4. 严禁寒暄、解释、标题，只输出对白范例纯文本。\n" +
                "5. 严禁 Markdown 代码围栏（```）、严禁 JSON 注释（// 或 /* */）。";

            string user =
                $"【当前对白范例】\n{currentText}\n\n" +
                $"【玩家调整方向】\n{demand}\n\n" +
                "请直接输出 3 条优化后的对白范例（日常问候 / 闲聊 / 情绪低落），保留表情符与换行分段。";

            return (system, user);
        }

        public static (string System, string User) BuildStageLadderPrompt(
            BioEditorViewModel vm, string npcName, bool isDatable, string demand)
        {
            throw new NotSupportedException("BuildStageLadderPrompt 由 VT-AI-11 填充（本单仅保留签名）。");
        }

        public static (string System, string User) BuildAmbientExtractionPrompt(
            BioEditorViewModel vm, string npcName, string demand)
        {
            throw new NotSupportedException("BuildAmbientExtractionPrompt 由 VT-AI-12 填充（本单仅保留签名）。");
        }
    }
}
