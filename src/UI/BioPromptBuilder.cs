using System;
using System.Collections.Generic;
using System.Linq;

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
            string anchor = GetCorePersonaAnchor(vm, npcName);
            int count = isDatable ? 4 : 3;
            string ladder = isDatable ? "0/4/8/已婚(已婚: 是)" : "0/4/8";
            string marriedNote = isDatable
                ? "末档为已婚档：心数固定填 14，已婚: 是（推演婚后语气转变）；其余档心数必须为偶数。"
                : "所有档位心数必须为偶数。";

            string system =
                "你是一名专业的《星露谷物语》NPC 人设编辑助手，正在为角色推演完整的【好感阶梯（ProgressStates）】。\n" +
                "推演时必须以该角色的核心身份为锚点，让各档位的心态、关注点随好感递进自然演变：\n" +
                $"{anchor}\n" +
                "硬性要求：\n" +
                $"1. 共输出 {count} 档，覆盖以下门槛（心数必须为偶数）：{ladder}。\n" +
                $"2. {marriedNote}\n" +
                "3. 态度段写清该阶段对玩家的态度与说话风格（自由文本，可多行）；心智段写清该阶段碎碎念时的心态与注意力流向（自由文本，可多行）。\n" +
                "4. 关注池列 3~5 个该阶段优先提及的事物/话题，用中文顿号分隔；若无特别关注点可写\"无\"。\n" +
                "5. 严禁生成 Joja 超市/巴士修复/具体配偶门禁等字段（这些由人工单独配置）。\n" +
                "6. 严禁 Markdown 代码围栏（```）、严禁 JSON 注释（// 或 /* */）、严禁 JSON 对象与多余字段。\n" +
                "7. 严格使用下列固定行式模板输出，每档一段：\n" +
                "### 档位 1 | 心数: 0 | 已婚: 否\n" +
                "态度:\n" +
                "（自由文本，可多行）\n" +
                "心智:\n" +
                "（自由文本，可多行）\n" +
                "关注: 词条A、词条B、词条C";

            string user =
                $"【整体心境与转变倾向】\n{demand}\n\n" +
                $"请严格按上述模板输出 {count} 档，逐档递增心数，不要添加模板之外的字段。";

            return (system, user);
        }

        public static (string System, string User) BuildAmbientExtractionPrompt(
            BioEditorViewModel vm, string npcName, string demand)
        {
            string anchor = GetCorePersonaAnchor(vm, npcName);
            string behaviorRef = vm != null
                ? (vm.GetTraitDescriptionOrNull("BehavioralRules") ?? "(以原版言行规则为准)")
                : "(以原版言行规则为准)";
            string system =
                "你是一名专业的《星露谷物语》NPC 人设编辑助手，正在为角色一次性萃取完整的【环境心智（Ambient Bark）】。\n" +
                "萃取时必须以该角色的核心身份与言行规则为锚点，让口吻、口头禅、观察视角协调一致：\n" +
                $"{anchor}\n" +
                "既有言行规则（语气与表达习惯参照，萃取结果应与之匹配）：\n" +
                $"{behaviorRef}\n" +
                "硬性要求：\n" +
                "1. 口吻段用 1-2 句概括该角色碎碎念时的基本语调、节奏与情绪底色。\n" +
                "2. 口头禅段给出若干该角色常用的口头禅、叹气声、起手式（自由文本，可多行）。\n" +
                "3. 观察透镜段给出 4 条该角色打量世界的特殊视角，每条以 \"- \" 起行。\n" +
                "4. 关注词条列 8-10 个该角色优先提及的事物/话题，用中文顿号分隔。\n" +
                "5. 严禁 Markdown 代码围栏（```）、严禁 JSON 注释（// 或 /* */）、严禁 JSON 对象与多余字段。\n" +
                "6. 严格使用下列固定行式模板输出，字段顺序不可调换：\n" +
                "口吻:\n" +
                "（1-2 句概括）\n" +
                "口头禅:\n" +
                "（短句若干，可多行）\n" +
                "观察透镜:\n" +
                "（4 条，每条以 \"- \" 起行）\n" +
                "关注词条: 词条1、词条2、…";

            string user =
                $"【整体语气与观察倾向】\n{demand}\n\n" +
                "请严格按上述模板一次性输出四个字段，不要添加模板之外的字段。";

            return (system, user);
        }

        /// <summary>
        /// 解析 AI 输出的行式环境心智文本。透镜为空，或口吻与口头禅同时为空 → false。
        /// </summary>
        public static bool TryParseAmbientProfile(
            string text, out string voice, out string habits, out string lenses, out List<string> preoccupations)
        {
            voice = "";
            habits = "";
            lenses = "";
            preoccupations = new List<string>();
            if (string.IsNullOrWhiteSpace(text))
                return false;

            string[] ambientEnd = WithFullWidthVariants(new[] { "\n口头禅:", "\n观察透镜:", "\n关注词条:" });
            voice = ReadSection(text, "口吻:", ambientEnd) ?? "";
            habits = ReadSection(text, "口头禅:", ambientEnd) ?? "";
            lenses = ReadSection(text, "观察透镜:", ambientEnd) ?? "";
            preoccupations = ReadOccupations(text, "关注词条:");

            bool hasVoice = !string.IsNullOrWhiteSpace(voice);
            bool hasHabits = !string.IsNullOrWhiteSpace(habits);
            bool hasLenses = !string.IsNullOrWhiteSpace(lenses);

            // 透镜必填；口吻与口头禅不可同时为空
            if (!hasLenses || (!hasVoice && !hasHabits))
                return false;

            return true;
        }

        /// <summary>
        /// 解析 AI 输出的行式好感阶梯文本。任一块缺少"态度:"段，或整体无有效块，即返回 false。
        /// </summary>
        public static bool TryParseStageLadder(string text, out List<BioData.ProgressStateEntry> stages)
        {
            stages = new List<BioData.ProgressStateEntry>();
            if (string.IsNullOrWhiteSpace(text))
                return false;

            var blocks = System.Text.RegularExpressions.Regex.Split(text, @"^###\s*档位\s*\d+", System.Text.RegularExpressions.RegexOptions.Multiline);
            int heartsSpecified = 0;
            foreach (var raw in blocks)
            {
                string block = raw.Trim();
                if (string.IsNullOrEmpty(block))
                    continue;

                // 模板外前导寒暄（不含任何字段键）静默跳过；含字段但缺态度的真畸形块仍整体失败
                bool hasAnyField = block.Contains("态度:", StringComparison.Ordinal)
                                || block.Contains("态度：", StringComparison.Ordinal)
                                || block.Contains("心数:", StringComparison.Ordinal)
                                || block.Contains("心数：", StringComparison.Ordinal);
            if (!hasAnyField)
                    continue;

                int? hearts = ReadHeartsFieldOrNull(block, "心数:");
                if (hearts.HasValue)
                    heartsSpecified++;
                bool married = ReadBoolField(block, "已婚:");
                string attitude = ReadSection(block, "态度:");
                string mindset = ReadSection(block, "心智:");
                List<string> occ = ReadOccupations(block, "关注:");

                // 态度段为必备：任一块缺失即整体失败
                if (attitude == null)
                {
                    stages = new List<BioData.ProgressStateEntry>();
                    return false;
                }

                stages.Add(new BioData.ProgressStateEntry
                {
                    RequiredHearts = Math.Clamp(hearts ?? 0, 0, 14),
                    RequireMarried = married,
                    Text = attitude,
                    BarkMindset = mindset,
                    Preoccupations = occ
                });
            }

            // 多档且所有档位均缺失"心数:"行 → 判定为未遵循模板，
            // 拒绝静默生成"全部 0 心"的退化阶梯（二期实测缺陷）
            if (stages.Count > 1 && heartsSpecified == 0)
            {
                stages = new List<BioData.ProgressStateEntry>();
                return false;
            }

            return stages.Count > 0;
        }

        private static int? ReadHeartsFieldOrNull(string block, string key)
        {
            // 心数按 1 心 1 刻度原样接受（含奇数），仅 clamp 到 0–14；
            // 不再对齐 NumberStepper 步进（奇数属于预期输入）。
            // 字段缺失/不可解析 → null（由调用方区分"显式 0"与"未提供"）。
            string v = ReadLineValue(block, key);
            if (string.IsNullOrEmpty(v))
                return null;
            if (int.TryParse(v, out int n))
                return Math.Clamp(n, 0, 14);
            return null;
        }

        private static bool ReadBoolField(string block, string key)
        {
            string v = ReadLineValue(block, key);
            if (string.IsNullOrEmpty(v)) return false;
            return v == "是" || v == "y" || v == "Y" || v == "true" || v == "True" || v == "TRUE";
        }

        private static string ReadLineValue(string block, string key)
        {
            int idx = block.IndexOf(key, StringComparison.Ordinal);
            if (idx < 0)
            {
                // 键名全角冒号容错（如"心数："）；值内容保持原样，不污染中文正文标点
                string fwKey = key.Replace(":", "：");
                idx = block.IndexOf(fwKey, StringComparison.Ordinal);
                if (idx < 0)
                    return null;
                key = fwKey;
            }
            if (idx < 0) return null;
            int start = idx + key.Length;
            int end = block.IndexOf('\n', start);
            string v = end < 0 ? block.Substring(start) : block.Substring(start, end - start);
            return v.Trim();
        }

        private static string ReadSection(string block, string key)
        {
            // 好感阶梯字段段的默认边界（态度/心智/关注）
            return ReadSection(block, key, WithFullWidthVariants(new[] { "\n态度:", "\n心智:", "\n关注:" }));
        }

        /// <summary>读取字段标签后的自由文本段，至任意一个 endMarkers 或块尾为止。</summary>
        private static string ReadSection(string block, string key, string[] endMarkers)
        {
            int idx = block.IndexOf(key, StringComparison.Ordinal);
            if (idx < 0)
            {
                // 键名全角冒号容错（如"态度："）
                string fwKey = key.Replace(":", "：");
                idx = block.IndexOf(fwKey, StringComparison.Ordinal);
                if (idx < 0)
                    return null;
                key = fwKey;
            }
            if (idx < 0) return null;
            int start = idx + key.Length;
            // 跳过字段标签后的换行，取到下一个字段标签或块尾
            string rest = start < block.Length ? block.Substring(start) : string.Empty;
            int cut = IndexOfAny(rest, endMarkers);
            string section = cut < 0 ? rest : rest.Substring(0, cut);
            section = section.Trim();
            return string.IsNullOrEmpty(section) ? "" : section;
        }

        private static List<string> ReadOccupations(string block, string key)
        {
            string v = ReadLineValue(block, key);
            if (string.IsNullOrEmpty(v) || v == "无")
                return null;
            var parts = v.Split(new[] { '、', ',', ';', '，', '；' }, StringSplitOptions.RemoveEmptyEntries)
                         .Select(p => p.Trim())
                         .Where(p => !string.IsNullOrEmpty(p))
                         .ToList();
            return parts.Count > 0 ? parts : null;
        }

        private static int IndexOfAny(string text, string[] markers)
        {
            int best = -1;
            foreach (var m in markers)
            {
                int i = text.IndexOf(m, StringComparison.Ordinal);
                if (i >= 0 && (best < 0 || i < best)) best = i;
            }
            return best;
        }

        /// <summary>为字段边界标记生成全角冒号变体，避免模型输出全角键名时切段失败。</summary>
        private static string[] WithFullWidthVariants(string[] markers)
        {
            var all = new List<string>(markers.Length * 2);
            all.AddRange(markers);
            foreach (var m in markers)
                all.Add(m.Replace(":", "："));
            return all.ToArray();
        }
    }
}
