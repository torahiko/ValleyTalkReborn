using StardewValley;

namespace ValleytalkReborn
{
    /// <summary>
    /// 语言选择器：实时求值游戏内语言，驱动模板双语选择。无状态，禁止缓存。
    /// </summary>
    internal static class BioPromptLanguage
    {
        public static bool IsChineseTemplate => I18n.IsChinese;

        /// <summary>游戏语言英文名映射；未知枚举值回退 "the game's current language"（BOUNDARY 容错）。</summary>
        public static string GameLanguageName => LanguageNameOf(LocalizedContentManager.CurrentLanguageCode);

        /// <summary>
        /// 输出语言指令：三分支。
        /// (1) IsChineseTemplate → 空串（zh 指令内嵌于 zh 模板）
        /// (2) !IsChineseTemplate && GameLanguageName == "English" → 空串
        /// (3) 其余 → EN_LANG_DIRECTIVE（{LANG} 替换为 GameLanguageName）
        /// </summary>
        public static string OutputLanguageDirective
        {
            get
            {
                if (IsChineseTemplate)
                    return string.Empty;
                string lang = GameLanguageName;
                if (lang == "English")
                    return string.Empty;
                return EnLangDirective(lang);
            }
        }

        /// <summary>语言名映射（internal 便于测试注入覆盖）。未知值回退 + Warn 日志。</summary>
        internal static string LanguageNameOf(LocalizedContentManager.LanguageCode code)
        {
            switch (code)
            {
                case LocalizedContentManager.LanguageCode.zh: return "Simplified Chinese";
                case LocalizedContentManager.LanguageCode.ja: return "Japanese";
                case LocalizedContentManager.LanguageCode.ru: return "Russian";
                case LocalizedContentManager.LanguageCode.de: return "German";
                case LocalizedContentManager.LanguageCode.es: return "Spanish";
                case LocalizedContentManager.LanguageCode.fr: return "French";
                case LocalizedContentManager.LanguageCode.it: return "Italian";
                case LocalizedContentManager.LanguageCode.ko: return "Korean";
                case LocalizedContentManager.LanguageCode.pt: return "Portuguese";
                case LocalizedContentManager.LanguageCode.tr: return "Turkish";
                case LocalizedContentManager.LanguageCode.hu: return "Hungarian";
                case LocalizedContentManager.LanguageCode.en:
                default: return "English";
            }
        }

        internal static string EnLangDirective(string lang) =>
            "[OUTPUT LANGUAGE]\n" +
            $"Write ALL human-readable content (field values, attitude text, dialogue lines) in {lang}.\n" +
            "Keep every structural marker, field label, and template line exactly as written in the OUTPUT FORMAT section — never translate them.";
    }

    /// <summary>
    /// 双语提示词模板库：所有 prompt 文本的唯一来源。无状态，全部实时求值。
    /// </summary>
    internal static class BioPromptTemplates
    {
        // ── 设计宪法（zh 来源，en 来源见 EnDesignConstitution）──
        internal static string GetDesignConstitution()
        {
            return "【角色设计宪法（所有自由文本字段一体适用）】\n" +
                "1. 绝对正向描写：不写角色“不做什么”，写角色“正在把注意力放在什么具体事物上、正在做什么”。例：“避免眼神接触”→“眼神对视两秒后移开”；“从不考虑婚后家务”→“全神贯注于眼前的每日重体力活”。\n" +
                "2. 摄影机原则：禁止抽象文学比喻与主观心理独白（如“内心的苦涩如冷水”）；所有动作与神态必须是摄影机能物理记录的细节（指节泛白、拇指摩挲杯沿、喉结滚动、肩膀紧绷、低头清嗓子、鞋底碾过门槛）。\n" +
                "3. 关注词条纪律：每条 2~4 个词的短语（推荐“动名词+名词”，如“擦拭皮球”）；严禁绑定固定时段（下午/清晨——雨夜会穿帮）；严禁绑定固定静态姿势（坐在栅栏上——与走动状态冲突）。\n" +
                "4. 字段值纯净：任何字段值内不得出现 [VOICE]、[STAGE] 之类的中括号标题。";
        }

        private static string ZhDesignConstitution => GetDesignConstitution();

        private static string EnDesignConstitution =>
            "[DESIGN CONSTITUTION — applies to every free-text field]\n" +
            "C1. Positive framing only: describe what the character actively does and attends to; never define the character by avoidance. (Bad: \"avoids eye contact\". Good: \"holds eye contact for two seconds, then looks away\".)\n" +
            "C2. Camera rule: no abstract metaphors, no inner monologue. Every gesture must be physically observable on camera (white knuckles, thumb rubbing the cup rim, a swallow, shoulders tensing, scuffing the doorstep).\n" +
            "C3. Focus-pool discipline: each entry is a 2-4 word verb+noun phrase (\"polishing a ball\"). Never bind an entry to a time of day or a fixed static pose.\n" +
            "C4. Field purity: field values must not contain bracket headers such as [VOICE] or [STAGE].";

        // ── en 模板（C 节定稿）──

        private static string EnIdentityPolishSystem(string npc, string anchor, string outputLanguageSlot) =>
            "[ROLE] Professional Stardew Valley NPC profile editor.\n" +
            $"[TASK] Polish the IDENTITY profile for {npc}.\n" +
            $"[CHARACTER ANCHOR — do not drift from this persona]\n{anchor}\n" +
            "[HARD CONSTRAINTS]\n" +
            "1. Preserve the section markers [IDENTITY] and [PSYCHOLOGICAL CONFLICTS] and the existing section layout.\n" +
            "2. Keep the character's core temperament; do not invent new identity, history, relationships, or conflicts.\n" +
            "3. Follow the player's request strictly.\n" +
            "4. DO NOT output greetings, explanations, titles, Markdown code fences (```), or JSON. Output the complete polished plain text only.\n" +
            $"{outputLanguageSlot}";

        private static string EnIdentityPolishUser(string sourceText, string demand) =>
            "[CURRENT IDENTITY]\n" +
            $"{sourceText}\n\n" +
            "[PLAYER REQUEST]\n" +
            $"{demand}\n\n" +
            "Output the complete polished identity profile only.";

        private static string EnBehaviorRulesSystem(string npc, string anchor, string constitution, string outputLanguageSlot) =>
            "[ROLE] Professional Stardew Valley NPC profile editor.\n" +
            $"[TASK] Polish the BEHAVIOR RULES (BehavioralRules) for {npc}.\n" +
            $"[CHARACTER ANCHOR — do not drift from this persona]\n{anchor}\n" +
            $"{constitution}\n" +
            "[HARD CONSTRAINTS]\n" +
            $"1. Polish tone, cadence, verbal habits, and immediate reactions for {npc}; keep entries itemized and executable.\n" +
            "2. Preserve the existing section markers: [VOICE], [SPEECH PATTERNS], [MANNERISMS], [IMMEDIATE REFLEXES], [CONTEXT OVERRIDE].\n" +
            "   [MANNERISMS]: signature camera-visible micro-gestures.\n" +
            "   [IMMEDIATE REFLEXES]: first-instant physical reaction to confusing text, provocation, or a sore spot being touched.\n" +
            "   [CONTEXT OVERRIDE]: indoor/outdoor contrast in posture and volume.\n" +
            "3. Follow the player's request; do not add content that conflicts with the anchor.\n" +
            "4. DO NOT output greetings, explanations, titles, Markdown code fences (```), or JSON comments. Plain text only.\n" +
            $"{outputLanguageSlot}";

        private static string EnBehaviorRulesUser(string currentText, string demand) =>
            "[CURRENT BEHAVIOR RULES]\n" +
            $"{currentText}\n\n" +
            "[PLAYER REQUEST]\n" +
            $"{demand}\n\n" +
            "Output the complete polished behavior rules only, keeping all section markers.";

        private static string EnDialogueExamplesSystem(string npc, string anchor, string tone, string outputLanguageSlot) =>
            "[ROLE] Professional Stardew Valley NPC profile editor.\n" +
            $"[TASK] Polish the DIALOGUE EXAMPLES (DialogueExamples) for {npc}.\n" +
            $"[CHARACTER ANCHOR — do not drift from this persona]\n{anchor}\n" +
            "[BEHAVIOR TONE REFERENCE — for tone matching only, not a polish target]\n" +
            $"{tone}\n" +
            "[HARD CONSTRAINTS]\n" +
            $"1. Produce exactly 3 dialogue examples for {npc}: daily greeting, casual chat, feeling low.\n" +
            "2. Each example must reflect the character's personality and current relationship distance, consistent with the behavior tone.\n" +
            "3. Embed vanilla portrait codes at natural sentence ends: $h happy, $s sad, $u unique, $l love, $a angry.\n" +
            "4. End each example with 2-3 player-selectable replies, one per line, each starting with \"%\". Use \"#$b#\" for page breaks.\n" +
            "5. DO NOT output greetings, explanations, titles, code fences, or JSON. Plain text only.\n" +
            $"{outputLanguageSlot}";

        private static string EnDialogueExamplesUser(string currentText, string demand) =>
            "[CURRENT DIALOGUE EXAMPLES]\n" +
            $"{currentText}\n\n" +
            "[PLAYER REQUEST]\n" +
            $"{demand}\n\n" +
            "Output exactly 3 polished dialogue examples (greeting / chat / feeling low), keeping portrait codes and line breaks.";

        private static string EnStageLadderSystem(string npc, string anchor, string constitution, int count, string ladderSpec, string marriedNote, string outputLanguageSlot) =>
            "[ROLE] Professional Stardew Valley NPC profile editor.\n" +
            $"[TASK] Derive the full AFFECTION LADDER (ProgressStates) for {npc}.\n" +
            $"[CHARACTER ANCHOR — stages must evolve naturally with affection]\n{anchor}\n" +
            $"{constitution}\n" +
            "[HARD CONSTRAINTS]\n" +
            $"1. Output exactly {count} stages at these heart thresholds (even heart counts only): {ladderSpec}.\n" +
            $"2. {marriedNote}\n" +
            "3. Attitude: camera-visible stance and positioning toward the player (low hearts: brief glances then look away, keep a safe distance; high hearts: relaxed shoulders, close the distance). Mindset: plain-text state of mind and attention flow; no bracket headers.\n" +
            "4. Focus: 3-5 topics this stage prioritizes, comma-separated; write \"none\" if nothing specific.\n" +
            "5. DO NOT generate gate fields (Joja Mart, bus repair, specific spouse) — those are configured manually elsewhere.\n" +
            "6. DO NOT output Markdown code fences (```), JSON comments, JSON objects, or fields beyond the template.\n" +
            "7. Follow this exact per-stage template; field order is fixed:\n" +
            "### Stage 1 | Hearts: 0 | Married: no\n" +
            "Attitude:\n" +
            "(free text, multi-line)\n" +
            "Mindset:\n" +
            "(free text, multi-line)\n" +
            "Focus: topicA, topicB, topicC\n" +
            $"{outputLanguageSlot}";

        private static string EnStageLadderUser(int count, string demand) =>
            "[OVERALL MOOD & TRAJECTORY]\n" +
            $"{demand}\n\n" +
            $"Output exactly {count} stages per the template above, hearts increasing per stage, no fields beyond the template.";

        private static string EnAmbientExtractionSystem(string npc, string anchor, string behaviorRef, string constitution, string outputLanguageSlot) =>
            "[ROLE] Professional Stardew Valley NPC profile editor.\n" +
            $"[TASK] Extract the complete AMBIENT PROFILE (voice / habits / lenses / focus pool) for {npc} in one pass.\n" +
            $"[CHARACTER ANCHOR — voice, habits, and lenses must stay coherent with this persona]\n{anchor}\n" +
            "[BEHAVIOR RULES REFERENCE — extraction must match this established voice]\n" +
            $"{behaviorRef}\n" +
            $"{constitution}\n" +
            "[HARD CONSTRAINTS]\n" +
            "1. Voice: 1-2 sentences on baseline tone and emotional undertone; no bracket headers inside the value.\n" +
            "2. Habits: catchphrases, sighs, signature openers (free text, multi-line).\n" +
            "3. Lenses: exactly 4 solitary-bark sensory lenses, one per line starting with \"- \" (e.g. a smith notices rusted metal and ore hardness).\n" +
            "4. Focus: 8-10 global focus-pool entries (follow constitution C3), comma-separated.\n" +
            "5. DO NOT output Markdown code fences (```), JSON comments, JSON objects, or fields beyond the template.\n" +
            "6. Follow this exact template; field order is fixed:\n" +
            "Voice:\n" +
            "(1-2 sentences)\n" +
            "Habits:\n" +
            "(short lines, multi-line)\n" +
            "Lenses:\n" +
            "(4 lines, each starting with \"- \")\n" +
            "Focus: entry1, entry2, ...\n" +
            $"{outputLanguageSlot}";

        private static string EnAmbientExtractionUser(string demand) =>
            "[OVERALL TONE & OBSERVATION BIAS]\n" +
            $"{demand}\n\n" +
            "Output the four fields exactly per the template above; no fields beyond the template.";

        private static string EnInitialBiographySystem(string npc, string rawGameContext, string contextRequirement, string constitution, string outputLanguageSlot) =>
            "[ROLE] Professional Stardew Valley identity-profile writer.\n" +
            $"[TASK] Create a complete identity profile for {npc} from scratch.\n" +
            "[NATIVE ANCHORS — objective facts from game data; must be respected]\n" +
            $"{rawGameContext}\n" +
            "[HARD CONSTRAINTS]\n" +
            "1. Output MUST contain the [IDENTITY] and [PSYCHOLOGICAL CONFLICTS] sections.\n" +
            $"2. {contextRequirement}\n" +
            $"{constitution}\n" +
            "3. DO NOT output greetings, explanations, Markdown fences (```), or JSON.\n" +
            $"{outputLanguageSlot}";

        private static string EnInitialBiographyUser(string demandText) =>
            "[PLAYER VISION]\n" +
            $"{demandText}\n\n" +
            "Output the complete identity profile only.";

        private static string EnRefinementRules =>
            "[REFINEMENT RULES]\n" +
            "R1. This is a second-pass edit; the previous draft is the base document. Modify it per the new request.\n" +
            "R2. Keep unchanged sections and all structural markers byte-stable; change only what the request targets.\n" +
            "R3. DO NOT output greetings, explanations, or preamble. Output the complete revised plain text only.";

        private static string EnRefinementUser(string currentDraft, string followUpDemand) =>
            "[PREVIOUS DRAFT]\n" +
            $"{currentDraft}\n\n" +
            "[PLAYER REQUEST]\n" +
            $"{followUpDemand}\n\n" +
            "Output the complete revised content only.";

        // ── zh 模板（D 节重排）──

        private static string ZhIdentityPolishSystem(string npc, string anchor) =>
            "【角色】专业的《星露谷物语》NPC 人设编辑助手。\n" +
            $"【任务】润色 {npc} 的身份设定（Identity）。\n" +
            $"【角色锚点——不得偏离该人格设定】\n{anchor}\n" +
            "【硬性约束】\n" +
            "1. 保留 [IDENTITY]、[PSYCHOLOGICAL CONFLICTS] 等核心结构标记与原有分节结构。\n" +
            "2. 保持角色核心气质，不得擅自改变身份、经历、关系或心理矛盾。\n" +
            "3. 严格遵循玩家给出的调整方向。\n" +
            "4. 禁止输出：寒暄、解释、标题、Markdown 代码围栏（```）、JSON。\n" +
            "【输出语言】\n全部人设内容使用简体中文（玩家当前游戏语言）；结构标记与字段标签保持模板原样，禁止翻译。";

        private static string ZhIdentityPolishUser(string sourceText, string demand) =>
            "【当前身份设定】\n" +
            $"{sourceText}\n\n" +
            "【玩家调整方向】\n" +
            $"{demand}\n\n" +
            "请直接输出润色后的完整身份档案。";

        private static string ZhBehaviorRulesSystem(string npc, string anchor, string constitution) =>
            "【角色】专业的《星露谷物语》NPC 人设编辑助手。\n" +
            $"【任务】润色 {npc} 的言行举止（BehavioralRules）模块。\n" +
            $"【角色锚点——不得偏离该人格设定】\n{anchor}\n" +
            $"{constitution}\n" +
            "【硬性约束】\n" +
            $"1. 润色对象为 {npc} 的言行规则（语气、节奏、口头习惯、即时反应），保持条目化、可执行。\n" +
            "2. 保留 [VOICE] / [SPEECH PATTERNS] / [MANNERISMS] / [IMMEDIATE REFLEXES] / [CONTEXT OVERRIDE] 等既有分节结构。\n" +
            "   [MANNERISMS] 必须是摄影机可拍摄的标志性肢体微动作（抹汗、摸后颈、手指敲门框）；\n" +
            "   [IMMEDIATE REFLEXES] 写“收到难懂文本/被挑衅/被提起痛处”时的第一瞬间肢体反应；\n" +
            "   [CONTEXT OVERRIDE] 写室内外体态与音量反差。\n" +
            "3. 严格遵循玩家给出的调整方向，不得自行扩展与角色身份冲突的内容。\n" +
            "4. 禁止输出：寒暄、解释、标题、Markdown 代码围栏（```）、JSON 注释。";

        private static string ZhBehaviorRulesUser(string currentText, string demand) =>
            "【当前言行规则】\n" +
            $"{currentText}\n\n" +
            "【玩家调整方向】\n" +
            $"{demand}\n\n" +
            "请直接输出润色后的言行规则全文，保留分节标记。";

        private static string ZhDialogueExamplesSystem(string npc, string anchor, string tone) =>
            "【角色】专业的《星露谷物语》NPC 人设编辑助手。\n" +
            $"【任务】润色 {npc} 的对白范例（DialogueExamples）模块。\n" +
            $"【角色锚点——不得偏离该人格设定】\n{anchor}\n" +
            "【关联言行基调——仅供语气参照，非润色对象】\n" +
            $"{tone}\n" +
            "【硬性约束】\n" +
            $"1. 为 {npc} 生成 3 条对白范例，分别对应：日常问候、闲聊、情绪低落。\n" +
            "2. 对白必须体现角色性格与当下关系亲疏，语气与言行规则一致。\n" +
            "3. 表情符使用原版五码：$h 开心 / $s 难过 / $u 独特 / $l 爱意 / $a 生气，句尾自然嵌入。\n" +
            "4. 每条对白末尾提供 2~3 行以 % 开头的玩家可选回答；翻页用 #$b#。\n" +
            "5. 禁止输出：寒暄、解释、标题、Markdown 代码围栏（```）、JSON。";

        private static string ZhDialogueExamplesUser(string currentText, string demand) =>
            "【当前对白范例】\n" +
            $"{currentText}\n\n" +
            "【玩家调整方向】\n" +
            $"{demand}\n\n" +
            "请直接输出 3 条润色后的对白范例（日常问候 / 闲聊 / 情绪低落），保留表情符与换行分段。";

        private static string ZhStageLadderSystem(string npc, string anchor, string constitution, int count, string ladderSpec, string marriedNote) =>
            "【角色】专业的《星露谷物语》NPC 人设编辑助手。\n" +
            $"【任务】为 {npc} 推演完整的好感阶梯（ProgressStates）。\n" +
            $"【角色锚点——各档位心态与关注点须随好感递进自然演变】\n{anchor}\n" +
            $"{constitution}\n" +
            "【硬性约束】\n" +
            $"1. 共输出 {count} 档，覆盖以下门槛（心数必须为偶数）：{ladderSpec}。\n" +
            $"2. {marriedNote}\n" +
            "3. 态度段写面对玩家时镜头可拍的体态与站位（低心：对视两秒移开、保持安全距离；高心：肩膀放松、主动拉近站姿）；心智段写该阶段纯文本心态与注意力流向，禁止任何中括号标头。\n" +
            "4. 关注池列 3~5 个该阶段优先提及的事物/话题，用中文顿号分隔；若无特别关注点可写\"无\"。\n" +
            "5. 严禁生成 Joja 超市/巴士修复/具体配偶门禁等字段（这些由人工单独配置）。\n" +
            "6. 禁止输出：Markdown 代码围栏（```）、JSON 注释、JSON 对象与多余字段。\n" +
            "7. 【输出格式】严格使用下列固定行式模板输出，每档一段：\n" +
            "### 档位 1 | 心数: 0 | 已婚: 否\n" +
            "态度:\n" +
            "（自由文本，可多行）\n" +
            "心智:\n" +
            "（自由文本，可多行）\n" +
            "关注: 词条A、词条B、词条C";

        private static string ZhStageLadderUser(int count, string demand) =>
            "【整体心境与转变倾向】\n" +
            $"{demand}\n\n" +
            $"请严格按上述模板输出 {count} 档，逐档递增心数，不要添加模板之外的字段。";

        private static string ZhAmbientExtractionSystem(string npc, string anchor, string behaviorRef, string constitution) =>
            "【角色】专业的《星露谷物语》NPC 人设编辑助手。\n" +
            $"【任务】为 {npc} 一次性萃取完整的环境心智（Ambient Bark）。\n" +
            $"【角色锚点——口吻、口头禅、观察视角须与该人格设定协调一致】\n{anchor}\n" +
            "【关联参照——萃取结果须与既有语气与表达习惯匹配】\n" +
            $"{behaviorRef}\n" +
            $"{constitution}\n" +
            "【硬性约束】\n" +
            "1. 口吻段用 1-2 句概括基本语调与情绪底色，值内严禁 [VOICE] 等标题。\n" +
            "2. 口头禅段给出若干该角色常用的口头禅、叹气声、起手式（自由文本，可多行）。\n" +
            "3. 观察透镜段（仅独处 Bark 生效的感官透镜）给出 4 个领域（例：铁匠注意锈蚀金属与矿石硬度），每条以 \"- \" 起行。\n" +
            "4. 关注词条列 8-10 个全局保底关注池（铁律 3 纪律），用中文顿号分隔。\n" +
            "5. 禁止输出：Markdown 代码围栏（```）、JSON 注释、JSON 对象与多余字段。\n" +
            "6. 【输出格式】严格使用下列固定行式模板输出，字段顺序不可调换：\n" +
            "口吻:\n" +
            "（1-2 句概括）\n" +
            "口头禅:\n" +
            "（短句若干，可多行）\n" +
            "观察透镜:\n" +
            "（4 条，每条以 \"- \" 起行）\n" +
            "关注词条: 词条1、词条2、…";

        private static string ZhAmbientExtractionUser(string demand) =>
            "【整体语气与观察倾向】\n" +
            $"{demand}\n\n" +
            "请严格按上述模板一次性输出四个字段，不要添加模板之外的字段。";

        private static string ZhInitialBiographySystem(string npc, string rawGameContext, string contextRequirement, string constitution) =>
            "【角色】专业的《星露谷物语》NPC 身份档案创作助手。\n" +
            $"【任务】为 {npc} 全新创作完整身份档案。\n" +
            "【原生锚点——游戏数据中的客观事实，创作时必须尊重】\n" +
            $"{rawGameContext}\n" +
            "【硬性约束】\n" +
            "①输出必须包含 [IDENTITY] 与 [PSYCHOLOGICAL CONFLICTS] 标准分节（结构对齐一期插入模板）。\n" +
            $"②{contextRequirement}\n" +
            $"{constitution}\n" +
            "③禁止输出：寒暄、解释、Markdown 围栏（```）、JSON。\n" +
            "【输出语言】\n全部人设内容使用简体中文（玩家当前游戏语言）；结构标记与字段标签保持模板原样，禁止翻译。";

        private static string ZhInitialBiographyUser(string demandText) =>
            "【玩家设想】\n" +
            $"{demandText}\n\n" +
            "请直接输出完整身份档案。";

        private static string ZhRefinementRules =>
            "【追问迭代规则】\n" +
            "R1. 本轮为基于上一轮草稿的二次迭代，以上一轮草稿为基准底稿定向修改。\n" +
            "R2. 保持既有结构标记与不需修改的段落原样稳定，仅按新指令修改指定部分。\n" +
            "R3. 禁止寒暄、解释、前导语，直接输出修改后的完整纯文本。";

        private static string ZhRefinementUser(string currentDraft, string followUpDemand) =>
            "【上一轮草稿】\n" +
            $"{currentDraft}\n\n" +
            "【玩家本次追问】\n" +
            $"{followUpDemand}\n\n" +
            "请结合上述要求，输出修改与优化后的完整内容。";

        // ── 公共 API ──

        public static (string System, string User) IdentityPolish(string npcName, string sourceText, string demand)
        {
            string anchor = BioPromptBuilder.GetCorePersonaAnchorPublic(npcName);
            bool zh = BioPromptLanguage.IsChineseTemplate;
            string system = zh
                ? ZhIdentityPolishSystem(npcName, anchor)
                : EnIdentityPolishSystem(npcName, anchor, BioPromptLanguage.OutputLanguageDirective);
            string user = zh
                ? ZhIdentityPolishUser(sourceText, demand)
                : EnIdentityPolishUser(sourceText, demand);
            return (system, user);
        }

        public static (string System, string User) BehaviorRules(string anchor, string constitution, string npcName, string currentText, string demand)
        {
            bool zh = BioPromptLanguage.IsChineseTemplate;
            string system = zh
                ? ZhBehaviorRulesSystem(npcName, anchor, constitution)
                : EnBehaviorRulesSystem(npcName, anchor, constitution, BioPromptLanguage.OutputLanguageDirective);
            string user = zh
                ? ZhBehaviorRulesUser(currentText, demand)
                : EnBehaviorRulesUser(currentText, demand);
            return (system, user);
        }

        public static (string System, string User) DialogueExamples(string anchor, string tone, string npcName, string currentText, string demand)
        {
            bool zh = BioPromptLanguage.IsChineseTemplate;
            string system = zh
                ? ZhDialogueExamplesSystem(npcName, anchor, tone)
                : EnDialogueExamplesSystem(npcName, anchor, tone, BioPromptLanguage.OutputLanguageDirective);
            string user = zh
                ? ZhDialogueExamplesUser(currentText, demand)
                : EnDialogueExamplesUser(currentText, demand);
            return (system, user);
        }

        public static (string System, string User) StageLadder(string anchor, string constitution, string npcName, int stageCount, string ladderSpec, string marriedNote, string demand)
        {
            bool zh = BioPromptLanguage.IsChineseTemplate;
            string system = zh
                ? ZhStageLadderSystem(npcName, anchor, constitution, stageCount, ladderSpec, marriedNote)
                : EnStageLadderSystem(npcName, anchor, constitution, stageCount, ladderSpec, marriedNote, BioPromptLanguage.OutputLanguageDirective);
            string user = zh
                ? ZhStageLadderUser(stageCount, demand)
                : EnStageLadderUser(stageCount, demand);
            return (system, user);
        }

        public static (string System, string User) AmbientExtraction(string anchor, string behaviorRef, string npcName, string demand)
        {
            bool zh = BioPromptLanguage.IsChineseTemplate;
            string system = zh
                ? ZhAmbientExtractionSystem(npcName, anchor, behaviorRef, ZhDesignConstitution)
                : EnAmbientExtractionSystem(npcName, anchor, behaviorRef, EnDesignConstitution, BioPromptLanguage.OutputLanguageDirective);
            string user = zh
                ? ZhAmbientExtractionUser(demand)
                : EnAmbientExtractionUser(demand);
            return (system, user);
        }

        public static (string System, string User) InitialBiography(string npcName, string rawGameContext, string contextRequirement, string userDemand, string demandText)
        {
            bool zh = BioPromptLanguage.IsChineseTemplate;
            string system = zh
                ? ZhInitialBiographySystem(npcName, rawGameContext, contextRequirement, ZhDesignConstitution)
                : EnInitialBiographySystem(npcName, rawGameContext, contextRequirement, EnDesignConstitution, BioPromptLanguage.OutputLanguageDirective);
            string user = zh
                ? ZhInitialBiographyUser(demandText)
                : EnInitialBiographyUser(demandText);
            return (system, user);
        }

        public static (string System, string User) Refinement(string baseSystemPrompt, string currentDraft, string followUpDemand)
        {
            bool zh = BioPromptLanguage.IsChineseTemplate;
            string rules = zh ? ZhRefinementRules : EnRefinementRules;
            string system = baseSystemPrompt + "\n\n" + rules;
            string user = zh
                ? ZhRefinementUser(currentDraft, followUpDemand)
                : EnRefinementUser(currentDraft, followUpDemand);
            return (system, user);
        }

        public static string QuestionHeader(int index)
        {
            if (BioPromptLanguage.IsChineseTemplate)
            {
                switch (index)
                {
                    case 1: return "明面身份与生活日常";
                    case 2: return "心理矛盾与深层弱点";
                    case 3: return "说话风格与口吻习惯";
                    case 4: return "对外来农夫第一印象";
                    default: return "";
                }
            }
            else
            {
                switch (index)
                {
                    case 1: return "Surface identity & daily life";
                    case 2: return "Inner conflict & hidden weakness";
                    case 3: return "Speech style & mannerisms";
                    case 4: return "First impression of the farmer";
                    default: return "";
                }
            }
        }

        public static string AnchorMissingProfile(string npcName) =>
            BioPromptLanguage.IsChineseTemplate
                ? $"角色：{npcName}（身份档案暂未填写，请仅依据言行与对白模块推断语气，不要臆造身份）。"
                : $"Character: {npcName} (identity profile is not filled in — infer tone from the behavior and dialogue modules only; do not invent identity).";

        public static string ToneFallback() =>
            BioPromptLanguage.IsChineseTemplate
                ? "(以原版标准语气为准)"
                : "(fall back to the vanilla baseline tone)";

        public static string BehaviorFallback() =>
            BioPromptLanguage.IsChineseTemplate
                ? "(以原版言行规则为准)"
                : "(fall back to vanilla behavior rules)";

        public static string NoDemandFallback() =>
            BioPromptLanguage.IsChineseTemplate
                ? "无特殊设想，请依据原生锚点自由创作"
                : "No specific vision — create freely from the native anchors.";
    }
}
