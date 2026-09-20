using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using StardewModdingAPI;
using StardewValley;
using ValleytalkReborn.Services;

namespace ValleytalkReborn;

/// <summary>
/// BioEditor 的视图模型：持有 Memory 作用域的 BioData 与脏态，封装各 Tab 的纯读写/纯函数与持久化。
/// 生命周期 = 菜单实例（菜单构造时由 View 注入 storage；菜单释放即消亡）。
/// 仅主线程；无 Harmony；存档域零触碰（持久化经 BioStorageService 磁盘覆盖层）。
/// </summary>
internal sealed class BioEditorViewModel
{
    /********** 字段 **********/
    private readonly string _npcName;
    private readonly BioStorageService _storage;
    private BioData _bio;
    private bool _dirty;
    private bool _hasOverlay;

    /// <summary>Tab 1 传记脚手架常量（逐字符原样自 BioEditorMenu.L106-108 搬迁）。</summary>
    private static readonly string BiographyScaffold =
        "[IDENTITY]\n- Identity: You are {NPC}.\n- Social Anchor: \n- Living Situation: \n\n" +
        "[PSYCHOLOGICAL CONFLICTS]\n- ";

    /********** 构造 **********/
    public BioEditorViewModel(string npcName, BioStorageService storage)
    {
        _npcName = npcName;
        _storage = storage;
        _bio = storage.LoadEditableBio(npcName);
        _hasOverlay = storage.HasCustomOverlay(npcName);
    }

    /********** 核心状态（Memory 作用域） **********/
    public string NpcName => _npcName;
    public BioData Bio => _bio;
    public bool IsDirty => _dirty;
    public bool HasOverlay => _hasOverlay;

    /// <summary>标记已修改。已收敛为 private（T7）。</summary>
    private void MarkDirty()
    {
        _dirty = true;
    }

    /********** Tab 1 **********/
    public string GetBiography() => _bio.Biography;

    public void SetBiography(string text)
    {
        if (text != _bio.Biography)
        {
            _bio.Biography = text;
            MarkDirty();
        }
    }

    public string GetUnique() => _bio.Unique;

    public void SetUnique(string text)
    {
        if (text != (_bio.Unique ?? string.Empty))
        {
            _bio.Unique = text;
            MarkDirty();
        }
    }

    public bool GetHomeLocationBed() => _bio.HomeLocationBed;

    public void SetHomeLocationBed(bool value)
    {
        if (value != _bio.HomeLocationBed)
        {
            _bio.HomeLocationBed = value;
            MarkDirty();
        }
    }

    public string BuildBiographyScaffold()
    {
        return BiographyScaffold.Replace("{NPC}", _npcName);
    }

    /********** Tab 2 纯函数 **********/
    public static string ApplyDialogueBreakInsert(string current)
    {
        return (current ?? "") + "#$b#";
    }

    public static string ApplyDialogueChoiceInsert(string current)
    {
        return (current ?? "").TrimEnd() + "\n% 选项文本内容";
    }

    /********** 持久化 **********/
    public bool TrySave(out string error)
    {
        if (!_storage.SaveOverlay(_npcName, _bio, out string err))
        {
            error = err;
            return false;
        }
        _dirty = false;
        error = null;
        return true;
    }

    public bool TryReset(out string error)
    {
        if (!_storage.ResetOverlay(_npcName, out string err))
        {
            error = err;
            return false;
        }
        _bio = _storage.LoadEditableBio(_npcName);
        _dirty = false;
        _hasOverlay = false;
        error = null;
        return true;
    }

    // ── Tab 2 / Tab 5 ──────────────────────────────────────────────
    internal enum ScrapeOutcome { Success, NoLines, Failed }

    /// <summary>获取指定 Trait 条目的 Description；条目不存在或为 null 时返回 null（Description 本身可为 null，原样返回）。</summary>
    public string GetTraitDescriptionOrNull(string key)
    {
        if (_bio.Traits.TryGetValue(key, out var e) && e != null)
            return e.Description;
        return null;
    }

    /// <summary>将文本同步到指定 Trait 条目：存在则比对写入；不存在且非空则建条目写入；否则 no-op。</summary>
    public void SyncTraitDescription(string key, string defaultHeading, string text)
    {
        if (_bio.Traits.TryGetValue(key, out var e) && e != null)
        {
            if (e.Description != text) { e.Description = text; MarkDirty(); }
        }
        else if (!string.IsNullOrEmpty(text))
        {
            EnsureTraitEntry(key, defaultHeading).Description = text;
            MarkDirty();
        }
    }

    /// <summary>抓取原版对白并合并到 DialogueExamples 条目。仅 added>0 置脏；DialogueScraper 返回空表→NoLines；异常→Failed。</summary>
    public ScrapeOutcome ScrapeDialogueExamples(out int added, out string error)
    {
        added = 0;
        error = null;
        try
        {
            List<string> lines = DialogueScraper.FetchCleanDialogueExamples(_npcName, 3);
            if (lines == null || lines.Count == 0)
                return ScrapeOutcome.NoLines;

            var entry = EnsureTraitEntry("DialogueExamples", "Dialogue Examples");
            entry.Description = AppendScrapedLines(entry.Description, lines, out added);
            if (added > 0)
                MarkDirty();
            return ScrapeOutcome.Success;
        }
        catch (Exception ex)
        {
            ModEntry.SMonitor?.Log($"抓取对白失败: {ex.Message}", LogLevel.Warn);
            error = ex.Message;
            return ScrapeOutcome.Failed;
        }
    }

    /// <summary>合并抓取行：按行精确 HashSet 去重（大小写不敏感）+ 写入前缀归一化，保证重抓幂等。4000 上限。</summary>
    internal static string AppendScrapedLines(string existing, List<string> lines, out int added)
    {
        added = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in (existing ?? string.Empty).Split('\n'))
        {
            var t = raw.Trim();
            if (t.StartsWith("- ", StringComparison.Ordinal)) t = t.Substring(2).Trim();
            if (t.Length > 0) seen.Add(t);
        }
        var sb = new StringBuilder(existing ?? string.Empty);
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var t = line.Trim();
            if (t.Length == 0) continue;
            if (seen.Contains(t)) continue;
            if (sb.Length > 4000) break;
            if (sb.Length > 0) sb.AppendLine();
            sb.Append("- ").Append(t);
            seen.Add(t);
            added++;
        }
        return sb.ToString().TrimStart();
    }

    public string GetAmbientVoice() => _bio.AmbientBarkPrompt?.VoiceAndAttitude ?? string.Empty;
    public string GetAmbientHabits() => _bio.AmbientBarkPrompt?.SpokenHabits ?? string.Empty;
    public string GetAmbientLenses() => _bio.AmbientBarkPrompt?.ObservationLenses ?? string.Empty;

    /// <summary>同步三条 Bark 字段：prompt 存在则逐字段比对；不存在且任一非空则建对象全赋值；barkChanged 单次置脏。</summary>
    public void SyncAmbientBarks(string voice, string habits, string lenses)
    {
        bool barkChanged = false;
        if (_bio.AmbientBarkPrompt != null)
        {
            if (_bio.AmbientBarkPrompt.VoiceAndAttitude != voice) { _bio.AmbientBarkPrompt.VoiceAndAttitude = voice; barkChanged = true; }
            if (_bio.AmbientBarkPrompt.SpokenHabits != habits) { _bio.AmbientBarkPrompt.SpokenHabits = habits; barkChanged = true; }
            if (_bio.AmbientBarkPrompt.ObservationLenses != lenses) { _bio.AmbientBarkPrompt.ObservationLenses = lenses; barkChanged = true; }
        }
        else if (!string.IsNullOrEmpty(voice) || !string.IsNullOrEmpty(habits) || !string.IsNullOrEmpty(lenses))
        {
            var p = EnsureAmbientBarkPrompt();
            p.VoiceAndAttitude = voice;
            p.SpokenHabits = habits;
            p.ObservationLenses = lenses;
            barkChanged = true;
        }
        if (barkChanged) MarkDirty();
    }

    public bool GetEnableAmbientBarks() => _bio.EnableAmbientBarks;

    public void SetEnableAmbientBarks(bool value)
    {
        if (value != _bio.EnableAmbientBarks)
        {
            _bio.EnableAmbientBarks = value;
            MarkDirty();
        }
    }

    /// <summary>无条件赋值全局 Preoccupations 并置脏（保留原"不比对"语义）。</summary>
    public void SyncGlobalPreoccupations(List<string> tags)
    {
        _bio.Preoccupations = tags;
        MarkDirty();
    }

    private BioData.ListEntry EnsureTraitEntry(string key, string defaultHeading)
    {
        if (!_bio.Traits.TryGetValue(key, out var entry) || entry == null)
        {
            entry = new BioData.ListEntry { id = key, Heading = defaultHeading, Description = string.Empty, RequiredHearts = 0 };
            _bio.Traits[key] = entry;
        }
        return entry;
    }

    private AmbientBarkPrompt EnsureAmbientBarkPrompt()
    {
        _bio.AmbientBarkPrompt ??= new AmbientBarkPrompt();
        return _bio.AmbientBarkPrompt;
    }

    // ── Tab 3（好感档位） ─────────────────────────────────────────
    public int SelectedStageIndex { get; private set; } = -1;

    /// <summary>选中档位；越界 no-op（保留原语义）。</summary>
    public void SelectStage(int index)
    {
        if (index < 0 || index >= _bio.ProgressStates.Count)
            return;
        SelectedStageIndex = index;
    }

    public bool CanAddStage => _bio.ProgressStates.Count < 8;

    /// <summary>新建档位：前置 CanAddStage；Add + 选末位 + 置脏。</summary>
    public void AddStage()
    {
        if (!CanAddStage) return;
        _bio.ProgressStates.Add(new BioData.ProgressStateEntry { RequiredHearts = 0 });
        SelectedStageIndex = _bio.ProgressStates.Count - 1;
        MarkDirty();
    }

    /// <summary>删除当前档位：前置选中有效；RemoveAt + 重定位 + 置脏。</summary>
    public void DeleteSelectedStage()
    {
        int idx = SelectedStageIndex;
        if (idx < 0 || idx >= _bio.ProgressStates.Count)
            return;
        _bio.ProgressStates.RemoveAt(idx);
        idx = Math.Min(idx, _bio.ProgressStates.Count - 1);
        if (idx >= 0)
            SelectedStageIndex = idx;
        MarkDirty();
    }

    public void CycleRequireMarried()
    {
        var s = CurrentStageOrNull();
        if (s != null)
            CycleRequireMarried(s);
        MarkDirty();
    }

    /// <summary>RequireMarried 取反（允许内部置脏语义；无论是否变化均脏）。</summary>
    internal static void CycleRequireMarried(BioData.ProgressStateEntry s)
    {
        s.RequireMarried = !s.RequireMarried;
    }

    public void CycleJojaMartClosed()
    {
        var s = CurrentStageOrNull();
        if (s != null)
            CycleJojaMartClosed(s);
        MarkDirty();
    }

    /// <summary>Closed 三态循环；结果 true 且 Member==true → 清 Member。</summary>
    internal static void CycleJojaMartClosed(BioData.ProgressStateEntry s)
    {
        s.RequireJojaMartClosed = s.RequireJojaMartClosed.HasValue
            ? (s.RequireJojaMartClosed.Value ? false : (bool?)null)
            : true;
        if (s.RequireJojaMartClosed == true && s.RequireJojaMember == true)
            s.RequireJojaMember = null;
    }

    // ⚠ OQ-1 裁决变更（有意行为变更，经产品负责人批准）：
    // 原码 L868-871 在 Member 置 true 与 Closed=true 冲突时清除 RequireJojaMember（自身），
    // 判定为笔误；现改为清除对方 RequireJojaMartClosed，与 CycleJojaMartClosed 完全对称。
    public void CycleJojaMember()
    {
        var s = CurrentStageOrNull();
        if (s != null)
            CycleJojaMember(s);
        MarkDirty();
    }

    /// <summary>Member 三态循环；结果 true 且 Closed==true → 清 Closed（OQ-1 对称化）。</summary>
    internal static void CycleJojaMember(BioData.ProgressStateEntry s)
    {
        s.RequireJojaMember = s.RequireJojaMember.HasValue
            ? (s.RequireJojaMember.Value ? false : (bool?)null)
            : true;
        if (s.RequireJojaMember == true && s.RequireJojaMartClosed == true)
            s.RequireJojaMartClosed = null;
    }

    /// <summary>同步编辑器文本到当前档位：选中有效时逐字段比对写入。</summary>
    public void SyncStageEditor(string stageText, string barkText)
    {
        var s = CurrentStageOrNull();
        if (s == null) return;
        if ((s.Text ?? "") != stageText) { s.Text = stageText; MarkDirty(); }
        if ((s.BarkMindset ?? "") != barkText) { s.BarkMindset = barkText; MarkDirty(); }
    }

    /// <summary>无条件赋值当前档位 Preoccupations 并置脏（保留原不比对语义）；空→null。</summary>
    public void SetStagePreoccupations(List<string> tags)
    {
        var s = CurrentStageOrNull();
        if (s == null) return;
        s.Preoccupations = tags != null && tags.Count > 0 ? tags : null;
        MarkDirty();
    }

    /// <summary>无条件赋值当前档位 RequiredHearts 并置脏（保留原不比对语义）。</summary>
    public void SetStageHearts(int hearts)
    {
        var s = CurrentStageOrNull();
        if (s == null) return;
        s.RequiredHearts = hearts;
        MarkDirty();
    }

    /// <summary>构建门禁摘要："≥N♥"/"已婚"/"超市倒闭"/"会员" 依序 "/" 连接，空则"无门禁"；false 态不显示。</summary>
    public string BuildGateSummary(int stageIndex)
    {
        var p = _bio.ProgressStates[stageIndex];
        var parts = new List<string>();
        if (p.RequiredHearts > 0) parts.Add($"≥{p.RequiredHearts}♥");
        if (p.RequireMarried) parts.Add("已婚");
        if (p.RequireJojaMartClosed == true) parts.Add("超市倒闭");
        if (p.RequireJojaMember == true) parts.Add("会员");
        return parts.Count > 0 ? string.Join("/", parts) : "无门禁";
    }

    private BioData.ProgressStateEntry CurrentStageOrNull()
    {
        int idx = SelectedStageIndex;
        if (idx < 0 || idx >= _bio.ProgressStates.Count)
            return null;
        return _bio.ProgressStates[idx];
    }

    // ── Tab 4（社交关系） ─────────────────────────────────────────
    private readonly List<string> _allNpcs = new();
    private readonly List<string> _filteredNpcs = new();
    private int _relSelectedIndex = -1;
    private string _relSelectedNpc = "";

    public IReadOnlyList<string> FilteredNpcs => _filteredNpcs;
    public int SelectedRelationshipIndex => _relSelectedIndex;
    public string SelectedRelationshipNpc => _relSelectedNpc;

    /// <summary>初始化 NPC 目录：采集 + Relationships 键源 + 消重；不做过滤（View 随后 RecomputeFilteredNpcs）。</summary>
    public void InitNpcCatalog()
    {
        _allNpcs.Clear();
        var rawCandidates = NpcCandidateQueryService.CollectRawCandidates(_npcName);

        if (_bio.Relationships != null)
        {
            foreach (var configuredKey in _bio.Relationships.Keys)
            {
                if (!string.Equals(configuredKey, _npcName, StringComparison.OrdinalIgnoreCase))
                    rawCandidates.Add(configuredKey);
            }
        }

        var friendshipData = Game1.player?.friendshipData;
        var resolved = ResolveDisplayNameConflicts(
            rawCandidates,
            n => Game1.getCharacterFromName(n)?.displayName,
            friendshipData?.Keys);
        _allNpcs.AddRange(resolved.Values);
    }

    /// <summary>按查询重算过滤列表并刷新可见矩形。</summary>
    public void RecomputeFilteredNpcs(string query)
    {
        _filteredNpcs.Clear();
        string q = query?.Trim() ?? "";

        var sorted = _allNpcs.OrderByDescending(n => _bio.Relationships.ContainsKey(n))
                             .ThenBy(n => Game1.getCharacterFromName(n)?.displayName ?? n);

        foreach (var name in sorted)
        {
            string disp = Game1.getCharacterFromName(name)?.displayName ?? name;
            if (string.IsNullOrEmpty(q) ||
                disp.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                name.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                _filteredNpcs.Add(name);
            }
        }
    }

    /// <summary>选择关系：FilteredNpcs 空→no-op；否则 Clamp 写 Index+Npc。</summary>
    public void SelectRelationship(int index)
    {
        if (_filteredNpcs.Count == 0)
            return;
        index = Math.Clamp(index, 0, _filteredNpcs.Count - 1);
        _relSelectedIndex = index;
        _relSelectedNpc = _filteredNpcs[index];
    }

    public string GetRelationshipHeadingOrNull(string npc)
    {
        if (_bio.Relationships.TryGetValue(npc, out var r) && r != null)
            return r.Heading;
        return null;
    }

    public string GetRelationshipDescriptionOrNull(string npc)
    {
        if (_bio.Relationships.TryGetValue(npc, out var r) && r != null)
            return r.Description;
        return null;
    }

    /// <summary>原子复刻原编辑器同步逻辑：npc 空→no-op；有条目逐字段比对写+脏；无条目且任一非空建条目赋值+脏;其余 no-op。</summary>
    public void SyncRelationshipEditor(string npc, string heading, string desc)
    {
        if (string.IsNullOrEmpty(npc))
            return;
        if (_bio.Relationships.TryGetValue(npc, out var rel) && rel != null)
        {
            if (rel.Heading != heading) { rel.Heading = heading; MarkDirty(); }
            if (rel.Description != desc) { rel.Description = desc; MarkDirty(); }
        }
        else if (!string.IsNullOrEmpty(heading) || !string.IsNullOrEmpty(desc))
        {
            var entry = EnsureRelationshipEntry(npc);
            entry.Heading = heading;
            entry.Description = desc;
            MarkDirty();
        }
    }

    public void EnsureRelationship(string npc)
    {
        EnsureRelationshipEntry(npc);
        MarkDirty();
    }

    public void RemoveRelationship(string npc)
    {
        _bio.Relationships?.Remove(npc);
        MarkDirty();
    }

    /// <summary>displayName 冲突消解（纯静态）。空/空白 displayName 回退内部名。</summary>
    internal static Dictionary<string, string> ResolveDisplayNameConflicts(
        IEnumerable<string> rawCandidates,
        Func<string, string> displayNameOf,
        IEnumerable<string> friendshipNames)
    {
        var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var friendshipSet = friendshipNames != null
            ? new HashSet<string>(friendshipNames, StringComparer.OrdinalIgnoreCase)
            : null;

        foreach (var name in rawCandidates)
        {
            string disp = displayNameOf(name);
            if (string.IsNullOrWhiteSpace(disp)) disp = name;

            if (resolved.TryGetValue(disp, out var existingInternalName))
            {
                bool isCurrentTrueMarlon = name.Equals("Marlon", StringComparison.OrdinalIgnoreCase);
                bool isExistingTrueMarlon = existingInternalName.Equals("Marlon", StringComparison.OrdinalIgnoreCase);

                if (isCurrentTrueMarlon && !isExistingTrueMarlon)
                {
                    resolved[disp] = name;
                }
                else if (!isCurrentTrueMarlon && isExistingTrueMarlon)
                {
                    continue;
                }
                else
                {
                    bool existingHasFriendship = friendshipSet != null && friendshipSet.Contains(existingInternalName);
                    bool currentHasFriendship = friendshipSet != null && friendshipSet.Contains(name);
                    if ((currentHasFriendship && !existingHasFriendship) ||
                        (currentHasFriendship == existingHasFriendship && name.Length < existingInternalName.Length))
                    {
                        resolved[disp] = name;
                    }
                }
            }
            else
            {
                resolved[disp] = name;
            }
        }
        return resolved;
    }

    private BioData.ListEntry EnsureRelationshipEntry(string npcName)
    {
        if (_bio.Relationships == null)
            _bio.Relationships = new Dictionary<string, BioData.ListEntry>();
        if (!_bio.Relationships.TryGetValue(npcName, out var entry) || entry == null)
        {
            entry = new BioData.ListEntry { id = npcName, Heading = string.Empty, Description = string.Empty, RequiredHearts = 0 };
            _bio.Relationships[npcName] = entry;
        }
        return entry;
    }
}
