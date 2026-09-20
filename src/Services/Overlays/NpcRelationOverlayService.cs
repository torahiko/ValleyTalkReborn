#nullable enable
using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using ValleytalkReborn.Services.Overlays;

namespace ValleytalkReborn.Services;

/// <summary>NPC 关系覆盖层存储服务（FileName = "npc_relations.json"）。资产挂接：晚于 CP 合成基线注入 upsert/墓碑。</summary>
internal sealed class NpcRelationOverlayService : OverlayStorageServiceBase<NpcRelationOverlayFile>
{
    // 必须与 NpcRelationRegistry.RELATIONS_ASSET_KEY 一致（该常量为 private）。
    private const string RelationsAssetKey = "ValleytalkReborn/NpcRelations";

    private readonly IModHelper _helper;
    private readonly IMonitor _monitor;
    private bool _subscribed;
    private NpcRelationOverlayFile? _cachedOverlay;
    private bool _dirty = true;
    private List<NpcRelationEntry>? _baseline;

    public NpcRelationOverlayService(IModHelper helper, IMonitor monitor)
        : base(helper, monitor, "custom_overlays")
    {
        _helper = helper;
        _monitor = monitor;
    }

    protected override string FileName => "npc_relations.json";

    protected override string GetEntrySummary(NpcRelationOverlayFile file)
        => $"Relations={file.Relations.Count}";

    public void RegisterAssetProviders()
    {
        if (_subscribed)
            return;
        _helper.Events.Content.AssetRequested += OnAssetRequested;
        _subscribed = true;
    }

    public void InvalidateCache()
    {
        try
        {
            _helper.GameContent.InvalidateCache(RelationsAssetKey);
        }
        catch (Exception ex)
        {
            _monitor.Log($"[NpcRelationOverlay] 失效资产缓存失败: {ex.Message}", LogLevel.Warn);
        }
    }

    private NpcRelationOverlayFile? GetOverlay()
    {
        if (_dirty || (_cachedOverlay == null && HasOverlay))
        {
            _cachedOverlay = LoadOrNull();
            _dirty = false;
        }
        return _cachedOverlay;
    }

    public override bool Save(NpcRelationOverlayFile file, out string errorMessage)
    {
        bool ok = base.Save(file, out errorMessage);
        _dirty = true;
        if (ok)
            InvalidateCache();
        return ok;
    }

    public override bool DeleteOverlay(out string errorMessage)
    {
        bool ok = base.DeleteOverlay(out errorMessage);
        _dirty = true;
        if (ok)
            InvalidateCache();
        return ok;
    }

    private void OnAssetRequested(object? sender, AssetRequestedEventArgs e)
    {
        if (!e.NameWithoutLocale.IsEquivalentTo(RelationsAssetKey))
            return;

        try
        {
            e.Edit(editor =>
            {
                if (editor is IAssetData<NpcRelationsFile> d)
                {
                    _baseline = DeepCloneList(d.Data.Relations);
                    var ov = GetOverlay();
                    if (ov == null)
                        return;

                    foreach (var entry in ov.Relations)
                    {
                        if (string.IsNullOrWhiteSpace(entry.NpcA) || string.IsNullOrWhiteSpace(entry.NpcB))
                        {
                            _monitor.Log($"[NpcRelationOverlay] 跳过含空白 NPC 名的关系条目", LogLevel.Warn);
                            continue;
                        }
                        if (string.Equals(entry.NpcA, entry.NpcB, StringComparison.OrdinalIgnoreCase))
                        {
                            _monitor.Log($"[NpcRelationOverlay] 跳过自环关系: {entry.NpcA}", LogLevel.Warn);
                            continue;
                        }

                        string key = MakeKey(entry.NpcA, entry.NpcB);
                        if (entry.Disabled)
                        {
                            // 墓碑：移除同键条目（MakeKey 输出组件保留输入大小写，收敛手工编辑的同对异写）
                            d.Data.Relations.RemoveAll(r =>
                                MakeKey(r.NpcA, r.NpcB).Equals(key, StringComparison.OrdinalIgnoreCase));
                            continue;
                        }

                        // upsert：先移除同键再追加，保持列表追加语义（MakeKey 输出组件保留输入大小写，收敛手工编辑的同对异写）
                        d.Data.Relations.RemoveAll(r =>
                            MakeKey(r.NpcA, r.NpcB).Equals(key, StringComparison.OrdinalIgnoreCase));
                        d.Data.Relations.Add(entry);
                    }

                    _monitor.Log($"[NpcRelationOverlay] applied {ov.Relations.Count} relations", LogLevel.Trace);
                }
            }, AssetEditPriority.Default, null);
        }
        catch (Exception ex)
        {
            _monitor.Log($"[NpcRelationOverlay] 覆盖层注入失败: {ex.Message}，本次回退内容包基线", LogLevel.Error);
        }
    }

    // 唯一权威键归一化，UI 与合并流共用（照抄 NpcRelationRegistry.MakeKey 的字母序归一化）。
    internal static string MakeKey(string a, string b) =>
        string.Compare(a, b, StringComparison.OrdinalIgnoreCase) <= 0
            ? $"{a}|{b}"
            : $"{b}|{a}";

    private static List<NpcRelationEntry> DeepCloneList(List<NpcRelationEntry> source)
    {
        var json = JsonConvert.SerializeObject(source);
        return JsonConvert.DeserializeObject<List<NpcRelationEntry>>(json) ?? new List<NpcRelationEntry>();
    }
}
