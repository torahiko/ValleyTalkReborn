using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace ValleytalkReborn.Services;

/// <summary>
/// 静态人设覆盖层存储服务。
/// 覆盖层为磁盘上的独立 JSON 文件（全局、不进存档、联机各客户端独立）；
/// 服务实例为单例，跨存档存活（基线与覆盖层均与存档无关）。
/// </summary>
public sealed class BioStorageService
{
    private readonly IModHelper helper;
    private readonly IMonitor monitor;
    private bool _subscribed;

    // 基线缓存：在 OnAssetRequested 中于覆盖层替换前捕获纯净原版资产，
    // 供 GetBaselineBio 与 ResetTabToBaseline 使用。Key = NPC 名（忽略大小写）。
    private readonly Dictionary<string, BioData> _baselineCache = new(StringComparer.OrdinalIgnoreCase);

    // 记录已向日志报告过“文件名含非法字符”的 NPC 原名，避免重复刷屏。
    private static readonly HashSet<string> _warnedSanitize = new();

    public BioStorageService(IModHelper helper, IMonitor monitor)
    {
        this.helper = helper;
        this.monitor = monitor;
    }

    /// <summary>幂等注册：订阅 SMAPI 资产请求事件，防重复订阅。</summary>
    public void RegisterAssetProviders()
    {
        if (_subscribed)
            return;

        this.helper.Events.Content.AssetRequested += OnAssetRequested;
        _subscribed = true;
    }

    /// <summary>指定 NPC 是否已存在磁盘覆盖层。</summary>
    public bool HasCustomOverlay(string npcName)
    {
        return File.Exists(OverlayPathFor(npcName));
    }

    /// <summary>
    /// 加载供编辑器使用的可编辑 BioData：返回覆盖层（若存在），否则返回基线资产的深拷贝。
    /// 覆盖层即完整快照，无需合并。
    /// </summary>
    public BioData LoadEditableBio(string npcName)
    {
        BioData baseBio;
        try
        {
            BioData loaded = Game1.content.LoadLocalized<BioData>(BioAssetFor(npcName));
            baseBio = DeepClone(loaded);
        }
        catch (Exception)
        {
            baseBio = new BioData();
            baseBio.Missing = true;
        }

        BioData ov = TryDeserializeOverlay(npcName);
        return ov ?? baseBio;
    }

    /// <summary>
    /// 获取指定 NPC 的纯净原版基线数据（不含任何自定义覆盖层修改）。
    /// 优先从 _baselineCache 获取（OnAssetRequested 中于覆盖层替换前捕获）；
    /// 若无缓存且当前无覆盖层，则从内容资产直接加载并缓存。
    /// </summary>
    public BioData GetBaselineBio(string npcName)
    {
        if (_baselineCache.TryGetValue(npcName, out var cached))
            return DeepClone(cached);

        try
        {
            BioData loaded = Game1.content.LoadLocalized<BioData>(BioAssetFor(npcName));
            // 如果当前没有覆盖层，loaded 即为纯净基线
            if (!HasCustomOverlay(npcName))
            {
                _baselineCache[npcName] = DeepClone(loaded);
                return DeepClone(loaded);
            }
        }
        catch { }

        return new BioData { Missing = true };
    }

    /// <summary>
    /// 保存覆盖层：创建目录 → 序列化 → 反序列化校验 → 原子写（.tmp + Move）→ 失效资产缓存。
    /// 任意异常不部分写入，return false 且 errorMessage 明确。
    /// </summary>
    public bool SaveOverlay(string npcName, BioData editedBio, out string errorMessage)
    {
        errorMessage = string.Empty;
        try
        {
            string dir = Path.GetDirectoryName(OverlayPathFor(npcName))!;
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            string json = JsonConvert.SerializeObject(editedBio, Formatting.Indented);

            // 验签：反序列化不能为空
            BioData verify = JsonConvert.DeserializeObject<BioData>(json);
            if (verify == null)
            {
                errorMessage = "序列化校验为空";
                this.monitor.Log($"[BioStorage] {errorMessage}: {npcName}", LogLevel.Error);
                return false;
            }

            // 原子写：写 .tmp 后覆盖移动，避免中断产生半写文件。
            string path = OverlayPathFor(npcName);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, overwrite: true);

            InvalidateBioAsset(npcName);
            this.monitor.Log($"[BioStorage] 已保存覆盖层并失效资产缓存: {npcName}", LogLevel.Info);
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            this.monitor.Log($"[BioStorage] 覆盖层保存失败({npcName}): {ex.Message}", LogLevel.Error);
            return false;
        }
    }

    /// <summary>
    /// 删除覆盖层。删除成功或本不存在 → 失效资产缓存 + return true；
    /// 删除抛异常 → errorMessage 明确 + return false（此时覆盖层仍在盘上，不谎报成功）。
    /// </summary>
    public bool ResetOverlay(string npcName, out string errorMessage)
    {
        errorMessage = string.Empty;
        string path = OverlayPathFor(npcName);
        try
        {
            if (File.Exists(path))
                File.Delete(path);

            InvalidateBioAsset(npcName);
            this.monitor.Log($"[BioStorage] 已删除覆盖层并失效资产缓存: {npcName}", LogLevel.Info);
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            this.monitor.Log($"[BioStorage] 覆盖层删除失败({npcName}): {ex.Message}", LogLevel.Error);
            return false;
        }
    }

    /// <summary>资产键：$"ValleyTalkReborn/Bios/{RemoveDotSuffixes(npcName)}"</summary>
    internal static string BioAssetFor(string npcName)
    {
        return $"{VtConstants.BiosPath}/{DialogueCleaner.RemoveDotSuffixes(npcName)}";
    }

    /// <summary>
    /// 从资产名反推 NPC 名：若以 Bios 前缀开头（忽略大小写），截取其后的部分（去掉可能的 ".json"），
    /// 再做 RemoveDotSuffixes；空串返回 null。
    /// </summary>
    internal static string TryGetNpcNameFromAssetName(IAssetName name)
    {
        string fullName = name.Name;
        string prefix = VtConstants.BiosPath;
        if (!fullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return null;

        string remainder = fullName.Substring(prefix.Length).TrimStart('/', '\\');
        if (remainder.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            remainder = remainder.Substring(0, remainder.Length - ".json".Length);

        remainder = DialogueCleaner.RemoveDotSuffixes(remainder);
        if (string.IsNullOrEmpty(remainder))
            return null;

        return remainder;
    }

    private static string OverlayPathFor(string npcName)
    {
        return Path.Combine(ModEntry.SHelper.DirectoryPath, "saves", "custom_bios", SanitizeFileName(npcName) + ".json");
    }

    private static string SanitizeFileName(string npcName)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string sanitized = npcName;
        foreach (char c in invalid)
            sanitized = sanitized.Replace(c, '_');

        if (sanitized != npcName && _warnedSanitize.Add(npcName))
            ModEntry.SMonitor?.Log($"[BioStorage] NPC 文件名含非法字符，已替换为下划线: \"{npcName}\" -> \"{sanitized}\"", LogLevel.Warn);

        return sanitized;
    }

    private static BioData TryDeserializeOverlay(string npcName)
    {
        string path = OverlayPathFor(npcName);
        if (!File.Exists(path))
            return null;

        try
        {
            string json = File.ReadAllText(path);
            BioData data = JsonConvert.DeserializeObject<BioData>(json);
            if (data != null)
                return data;

            ModEntry.SMonitor?.Log($"[BioStorage] 覆盖层损坏({npcName})，回退基线", LogLevel.Warn);
            return null;
        }
        catch (Exception)
        {
            ModEntry.SMonitor?.Log($"[BioStorage] 覆盖层损坏({npcName})，回退基线", LogLevel.Warn);
            return null;
        }
    }

    private static BioData DeepClone(BioData source)
    {
        string json = JsonConvert.SerializeObject(source);
        return JsonConvert.DeserializeObject<BioData>(json) ?? new BioData();
    }

    private void OnAssetRequested(object sender, AssetRequestedEventArgs e)
    {
        string npc = TryGetNpcNameFromAssetName(e.NameWithoutLocale);
        if (npc == null)
            return;

        try
        {
            // Edit 回调在 CP 内容包补丁之后执行（SMAPI C# 编辑提供器语义），是覆盖层晚于基线生效的机制依据。
            e.Edit(editor =>
            {
                if (editor is IAssetData<BioData> d)
                {
                    // 在被覆盖层替换前，d.Data 就是 Content Patcher 处理完毕的纯净原版基准
                    _baselineCache[npc] = DeepClone(d.Data);

                    if (HasCustomOverlay(npc))
                    {
                        BioData ov = TryDeserializeOverlay(npc);
                        if (ov != null)
                            d.ReplaceWith(ov);
                        ModEntry.SMonitor?.Log($"[BioStorage] overlay applied: {npc}", LogLevel.Trace);
                    }
                }
            }, AssetEditPriority.Default, null);
        }
        catch (Exception ex)
        {
            ModEntry.SMonitor?.Log($"[BioStorage] 覆盖层注入失败({npc}): {ex.Message}，本次回退基线", LogLevel.Error);
        }
    }

    private void InvalidateBioAsset(string npcName)
    {
        ModEntry.SHelper.GameContent.InvalidateCache(BioAssetFor(npcName));
    }
}
