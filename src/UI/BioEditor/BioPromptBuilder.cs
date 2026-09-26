using System;
using System.Collections.Generic;
using System.Linq;

namespace ValleytalkReborn
{
    /// <summary>
    /// AI 润色提示词工厂：集中产出送往 LLM 的 (system, user) 提示词对。
    /// 菜单层禁止直接硬编码业务文案，一律经由此处组装外发。
    /// 模板文本已外移至 BioPromptTemplates；本类保留数据提取与解析逻辑。
    /// </summary>
    internal static class BioPromptBuilder
    {
        private const int AnchorCap = 1500;

        // ── 解析器双语键常量（E-3）──
        private static readonly string[] KeyHearts = { "心数:", "Hearts:" };
        private static readonly string[] KeyMarried = { "已婚:", "Married:" };
        private static readonly string[] KeyAttitude = { "态度:", "Attitude:" };
        private static readonly string[] KeyMindset = { "心智:", "Mindset:" };
        private static readonly string[] KeyFocus = { "关注:", "Focus:" };
        private static readonly string[] KeyVoice = { "口吻:", "Voice:" };
        private static readonly string[] KeyHabits = { "口头禅:", "Habits:" };
        private static readonly string[] KeyLenses = { "观察透镜:", "Lenses:" };
        private static readonly string[] KeyPreocc = { "关注词条:", "Focus:" };

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
                return BioPromptTemplates.AnchorMissingProfile(npcName);

            string trimmed = bio.Length > AnchorCap ? bio.Substring(0, AnchorCap) + "…" : bio;
            return $"角色：{npcName}\n核心身份档案：\n{trimmed}";
        }

        /// <summary>供 Templates 内部调用的 anchor 工厂（无 vm 上下文，返回双语 fallback）。</summary>
        internal static string GetCorePersonaAnchorPublic(string npcName) =>
            BioPromptTemplates.AnchorMissingProfile(npcName);

        public static (string System, string User) BuildBehaviorRulesPrompt(
            BioEditorViewModel vm, string npcName, string currentText, string demand)
        {
            string anchor = GetCorePersonaAnchor(vm, npcName);
            string constitution = BioPromptTemplates.GetDesignConstitution();
            return BioPromptTemplates.BehaviorRules(anchor, constitution, npcName, currentText, demand);
        }

        public static (string System, string User) BuildDialogueExamplesPrompt(
            BioEditorViewModel vm, string npcName, string currentText, string demand)
        {
            string anchor = GetCorePersonaAnchor(vm, npcName);
            string tone = vm != null
                ? (vm.GetTraitDescriptionOrNull("BehavioralRules") ?? BioPromptTemplates.ToneFallback())
                : BioPromptTemplates.ToneFallback();
            return BioPromptTemplates.DialogueExamples(anchor, tone, npcName, currentText, demand);
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
            string constitution = BioPromptTemplates.GetDesignConstitution();
            return BioPromptTemplates.StageLadder(anchor, constitution, npcName, count, ladder, marriedNote, demand);
        }

        public static (string System, string User) BuildAmbientExtractionPrompt(
            BioEditorViewModel vm, string npcName, string demand)
        {
            string anchor = GetCorePersonaAnchor(vm, npcName);
            string behaviorRef = vm != null
                ? (vm.GetTraitDescriptionOrNull("BehavioralRules") ?? BioPromptTemplates.BehaviorFallback())
                : BioPromptTemplates.BehaviorFallback();
            return BioPromptTemplates.AmbientExtraction(anchor, behaviorRef, npcName, demand);
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

            string demandText = string.IsNullOrWhiteSpace(userDemand)
                ? BioPromptTemplates.NoDemandFallback()
                : userDemand;

            return BioPromptTemplates.InitialBiography(npcName, rawGameContext, contextRequirement, userDemand, demandText);
        }

        /// <summary>
        /// 追问/二次迭代提示词：以上一轮草稿为基准底稿，按玩家新指令定向修改。
        /// 三参均非 null（调用方契约）；currentDraft 允许为空串（确定性输出，不做特判）。
        /// </summary>
        public static (string System, string User) BuildRefinementPrompt(
            string baseSystemPrompt, string currentDraft, string followUpDemand)
        {
            return BioPromptTemplates.Refinement(baseSystemPrompt, currentDraft, followUpDemand);
        }

        // ── 解析器（E-3 双语别名扩展）──

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

            string[] ambientEnd = EndMarkers(KeyHabits, KeyLenses, KeyPreocc);
            voice = StripLeadingFieldHeader(ReadSection(text, KeyVoice, ambientEnd) ?? "");
            habits = StripLeadingFieldHeader(ReadSection(text, KeyHabits, ambientEnd) ?? "");
            lenses = StripLeadingFieldHeader(ReadSection(text, KeyLenses, ambientEnd) ?? "");
            preoccupations = ReadOccupations(text, KeyPreocc);

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

            var blocks = System.Text.RegularExpressions.Regex.Split(text, @"^\x23\x23\x23\s*(?:档位|Stage)\s*\d+", System.Text.RegularExpressions.RegexOptions.Multiline);
            int heartsSpecified = 0;
            foreach (var raw in blocks)
            {
                string block = raw.Trim();
                if (string.IsNullOrEmpty(block))
                    continue;

                // 模板外前导寒暄（不含任何字段键）静默跳过；含字段但缺态度的真畸形块仍整体失败
                if (!BlockHasAnyField(block, KeyAttitude.Concat(KeyHearts).ToArray()))
                    continue;

                int? hearts = ReadHeartsFieldOrNull(block, KeyHearts);
                if (hearts.HasValue)
                    heartsSpecified++;
                bool married = ReadBoolField(block, KeyMarried);
                // 态度段为必备：任一块缺失即整体失败（剥标头前判 null，保留既有失败语义）
                string attitudeRaw = ReadSection(block, KeyAttitude);
                if (attitudeRaw == null)
                {
                    stages = new List<BioData.ProgressStateEntry>();
                    return false;
                }
                string attitude = StripLeadingFieldHeader(attitudeRaw);
                string mindset = StripLeadingFieldHeader(ReadSection(block, KeyMindset));
                List<string> occ = ReadOccupations(block, KeyFocus);

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

        private static int? ReadHeartsFieldOrNull(string block, params string[] keys)
        {
            // 心数按 1 心 1 刻度原样接受（含奇数），仅 clamp 到 0–14；
            // 不再对齐 NumberStepper 步进（奇数属于预期输入）。
            // 字段缺失/不可解析 → null（由调用方区分"显式 0"与"未提供"）。
            foreach (var key in keys)
            {
                string v = ReadLineValue(block, key);
                if (v != null)
                {
                    if (int.TryParse(v, out int n))
                        return Math.Clamp(n, 0, 14);
                    return null;
                }
            }
            return null;
        }

        private static bool ReadBoolField(string block, params string[] keys)
        {
            foreach (var key in keys)
            {
                string v = ReadLineValue(block, key);
                if (v != null)
                    return v == "是" || v == "yes" || v == "Yes" || v == "YES" || v == "y" || v == "Y" || v == "true" || v == "True" || v == "TRUE";
            }
            return false;
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
            // 模板行式 "Hearts: 0 | Married: no" 中 | 也是值边界
            int pipe = block.IndexOf('|', start);
            if (pipe >= 0 && (end < 0 || pipe < end))
                end = pipe;
            string v = end < 0 ? block.Substring(start) : block.Substring(start, end - start);
            return v.Trim();
        }

        private static string ReadSection(string block, params string[] keys)
        {
            // 默认边界：态度/心智/关注（含全角变体，带 \n 前缀）
            string[] endMarkers = EndMarkers(KeyAttitude, KeyMindset, KeyFocus);
            foreach (var key in keys)
            {
                string result = ReadSection(block, key, endMarkers);
                if (result != null)
                    return result;
            }
            return null;
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

        /// <summary>多键重载：对每个键尝试读取，首个命中即返回。</summary>
        private static string ReadSection(string block, string[] keys, string[] endMarkers)
        {
            foreach (var key in keys)
            {
                string result = ReadSection(block, key, endMarkers);
                if (result != null)
                    return result;
            }
            return null;
        }

        private static List<string> ReadOccupations(string block, params string[] keys)
        {
            foreach (var key in keys)
            {
                string v = ReadLineValue(block, key);
                if (v != null)
                {
                    if (v == "无" || v == "none" || v == "None")
                        return null;
                    var parts = v.Split(new[] { '、', ',', ';', '，', '；' }, StringSplitOptions.RemoveEmptyEntries)
                                 .Select(p => p.Trim())
                                 .Where(p => !string.IsNullOrEmpty(p))
                                 .ToList();
                    return parts.Count > 0 ? parts : null;
                }
            }
            return null;
        }

        private static bool BlockHasAnyField(string block, params string[] keys)
        {
            foreach (var key in keys)
            {
                if (block.IndexOf(key, StringComparison.Ordinal) >= 0
                    || block.IndexOf(key.Replace(":", "："), StringComparison.Ordinal) >= 0)
                    return true;
            }
            return false;
        }

        /// <summary>测试辅助：暴露 ReadOccupations 逻辑。</summary>
        internal static List<string> ReadOccupationsPublic(string block, params string[] keys) =>
            ReadOccupations(block, keys);

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

        /// <summary>为字段边界标记生成带 \n 前缀的全角/半角变体，避免模型输出全角键名时切段失败。</summary>
        private static string[] EndMarkers(params string[][] keyGroups)
        {
            var list = new List<string>();
            foreach (var group in keyGroups)
            {
                foreach (var k in group)
                {
                    list.Add("\n" + k);
                    list.Add("\n" + k.Replace(":", "："));
                }
            }
            return list.ToArray();
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
