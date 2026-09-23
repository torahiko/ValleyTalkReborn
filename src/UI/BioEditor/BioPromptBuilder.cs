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

        /// <summary>
        /// 角色设计宪法：所有内容型 Builder 共用，注入 system 提示词。
        /// token 成本约 250/请求，四模块+起号共用一份文案。
        /// </summary>
        private static string GetDesignConstitution()
        {
            return "【角色设计宪法（所有自由文本字段一体适用）】\n" +
                "1. 绝对正向描写：不写角色“不做什么”，写角色“正在把注意力放在什么具体事物上、正在做什么”。例：“避免眼神接触”→“眼神对视两秒后移开”；“从不考虑婚后家务”→“全神贯注于眼前的每日重体力活”。\n" +
                "2. 摄影机原则：禁止抽象文学比喻与主观心理独白（如“内心的苦涩如冷水”）；所有动作与神态必须是摄影机能物理记录的细节（指节泛白、拇指摩挲杯沿、喉结滚动、肩膀紧绷、低头清嗓子、鞋底碾过门槛）。\n" +
                "3. 关注词条纪律：每条 2~4 个词的短语（推荐“动名词+名词”，如“擦拭皮球”）；严禁绑定固定时段（下午/清晨——雨夜会穿帮）；严禁绑定固定静态姿势（坐在栅栏上——与走动状态冲突）。\n" +
                "4. 字段值纯净：任何字段值内不得出现 [VOICE]、[STAGE] 之类的中括号标题。";
        }

        /// <summary>
        /// 剥行首的中括号字段标头（如 "[VOICE] xxx" → "xxx"）；仅剥行首、不碰正文；null/空安全。
        /// </summary>
        private static string StripLeadingFieldHeader(string value)
        {
            return System.Text.RegularExpressions.Regex.Replace(value ?? "", "^[ \t]*\\[[^\\]]+\\][ \t]*", "").Trim();
        }

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
            string constitution = GetDesignConstitution();
            string system =
                "你是一名专业的《星露谷物语》NPC 人设编辑助手，正在润色角色的【言行举止 (BehavioralRules)】模块。\n" +
                "润色时必须以该角色的核心身份为锚点，不得偏离人格设定：\n" +
                $"{anchor}\n" +
                $"{constitution}\n" +
                "硬性要求：\n" +
                $"1. 润色对象为 {npcName} 的言行规则（语气、节奏、口头习惯、即时反应），保持条目化、可执行。\n" +
                "2. 保留 [VOICE] / [SPEECH PATTERNS] / [MANNERISMS] / [IMMEDIATE REFLEXES] / [CONTEXT OVERRIDE] 等既有分节结构。\n" +
                "   [MANNERISMS] 必须是摄影机可拍摄的标志性肢体微动作（抹汗、摸后颈、手指敲门框）；\n" +
                "   [IMMEDIATE REFLEXES] 写“收到难懂文本/被挑衅/被提起痛处”时的第一瞬间肢体反应；\n" +
                "   [CONTEXT OVERRIDE] 写室内外体态与音量反差。\n" +
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
                "3. 表情符使用原版五码：$h 开心 / $s 难过 / $u 独特 / $l 爱意 / $a 生气，句尾自然嵌入。\n" +
                "4. 每条对白末尾提供 2~3 行以 % 开头的玩家可选回答；翻页用 #$b#。\n" +
                "5. 严禁寒暄、解释、标题，只输出对白范例纯文本。\n" +
                "6. 严禁 Markdown 代码围栏（```）、严禁 JSON 注释（// 或 /* */）。";

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
            string constitution = GetDesignConstitution();

            string system =
                "你是一名专业的《星露谷物语》NPC 人设编辑助手，正在为角色推演完整的【好感阶梯（ProgressStates）】。\n" +
                "推演时必须以该角色的核心身份为锚点，让各档位的心态、关注点随好感递进自然演变：\n" +
                $"{anchor}\n" +
                $"{constitution}\n" +
                "硬性要求：\n" +
                $"1. 共输出 {count} 档，覆盖以下门槛（心数必须为偶数）：{ladder}。\n" +
                $"2. {marriedNote}\n" +
                "3. 态度段写面对玩家时镜头可拍的体态与站位（低心：对视两秒移开、保持安全距离；高心：肩膀放松、主动拉近站姿）；心智段写该阶段纯文本心态与注意力流向，禁止任何中括号标头。\n" +
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
            string constitution = GetDesignConstitution();
            string system =
                "你是一名专业的《星露谷物语》NPC 人设编辑助手，正在为角色一次性萃取完整的【环境心智（Ambient Bark）】。\n" +
                "萃取时必须以该角色的核心身份与言行规则为锚点，让口吻、口头禅、观察视角协调一致：\n" +
                $"{anchor}\n" +
                "既有言行规则（语气与表达习惯参照，萃取结果应与之匹配）：\n" +
                $"{behaviorRef}\n" +
                $"{constitution}\n" +
                "硬性要求：\n" +
                "1. 口吻段（常驻发声共鸣/身体原型/社会本能）用 1-2 句概括基本语调与情绪底色，值内严禁 [VOICE] 等标题。\n" +
                "2. 口头禅段（句式节奏/常用起手式/日常语调）给出若干该角色常用的口头禅、叹气声、起手式（自由文本，可多行）。\n" +
                "3. 观察透镜段（仅独处 Bark 生效的感官透镜）给出 4 个领域（例：铁匠注意锈蚀金属与矿石硬度），每条以 \"- \" 起行。\n" +
                "4. 关注词条列 8-10 个全局保底关注池（铁律 3 纪律），用中文顿号分隔。\n" +
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

        public static (string System, string User) BuildInitialBiographyPrompt(
            string npcName, string rawGameContext, string userDemand)
        {
            string contextSection = string.IsNullOrEmpty(rawGameContext)
                ? string.Empty
                : $"该 NPC 的原生锚点（来自游戏数据的客观事实，创作时必须尊重）：\n{rawGameContext}\n";

            string contextRequirement = string.IsNullOrEmpty(rawGameContext)
                ? "②无原生锚点，依据合理推断创作；"
                : "②专属最爱物品等原生锚点可作为性格意象隐喻自然融入，不得生硬罗列；";
            string constitution = GetDesignConstitution();

            string system =
                "你是一名专业的《星露谷物语》NPC 身份档案创作助手，正在为该 NPC 全新创作完整身份档案。\n" +
                "创作时必须基于已知事实，描写镜头可见的物理体态、动作、行为与具体事实：\n" +
                $"{contextSection}" +
                "硬性要求：\n" +
                "①输出必须包含 [IDENTITY] 与 [PSYCHOLOGICAL CONFLICTS] 标准分节（结构对齐一期插入模板）。\n" +
                $"{contextRequirement}" +
                $"{constitution}\n" +
                "③严禁寒暄、解释、Markdown 围栏（```）、JSON。";

            string demandText = string.IsNullOrWhiteSpace(userDemand)
                ? "无特殊设想，请依据原生锚点自由创作"
                : userDemand;

            string user =
                $"【玩家设想】{demandText}\n\n" +
                "请直接输出完整身份档案。";

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
            voice = StripLeadingFieldHeader(ReadSection(text, "口吻:", ambientEnd) ?? "");
            habits = StripLeadingFieldHeader(ReadSection(text, "口头禅:", ambientEnd) ?? "");
            lenses = StripLeadingFieldHeader(ReadSection(text, "观察透镜:", ambientEnd) ?? "");
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
                // 态度段为必备：任一块缺失即整体失败（剥标头前判 null，保留既有失败语义）
                string attitudeRaw = ReadSection(block, "态度:");
                if (attitudeRaw == null)
                {
                    stages = new List<BioData.ProgressStateEntry>();
                    return false;
                }
                string attitude = StripLeadingFieldHeader(attitudeRaw);
                string mindset = StripLeadingFieldHeader(ReadSection(block, "心智:"));
                List<string> occ = ReadOccupations(block, "关注:");

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
