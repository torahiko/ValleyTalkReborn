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
    public enum BioScope { Global, Local }

    private static string GlobalBaseDir =>
        Path.Combine(Constants.SavesPath, "_ValleyTalkReborn_Global");
    private static string LocalBaseDir =>
        Constants.CurrentSavePath is null
            ? null
            : Path.Combine(Constants.CurrentSavePath, "ValleyTalkReborn_Local");

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

        MigrateLegacyOverlays(this.monitor);
    }

    /// <summary>指定 NPC 是否已存在磁盘覆盖层（Local 优先，Global 次之）。</summary>
    public bool HasCustomOverlay(string npcName)
    {
        if (File.Exists(OverlayPathFor(npcName, BioScope.Global)))
            return true;
        string localDir = LocalBaseDir;
        if (localDir != null && File.Exists(OverlayPathFor(npcName, BioScope.Local)))
            return true;
        return false;
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
    /// 判定指定 NPC 当前活跃的覆盖层作用域。
    /// Local 文件存在 → Local；否则 Global 文件存在 → Global；均不存在 → activeScope 占位 Global，return false。
    /// </summary>
    public bool TryGetActiveScope(string npcName, out BioScope activeScope)
    {
        if (LocalBaseDir != null && File.Exists(OverlayPathFor(npcName, BioScope.Local)))
        {
            activeScope = BioScope.Local;
            return true;
        }
        if (File.Exists(OverlayPathFor(npcName, BioScope.Global)))
        {
            activeScope = BioScope.Global;
            return true;
        }
        activeScope = BioScope.Global;
        return false;
    }

    /// <summary>
    /// 保存覆盖层到 targetScope：创建目录 → 序列化 → 反序列化校验 → 原子写（.tmp + Move）→
    /// 失效资产缓存 → 对称清理另一作用域同名文件（失败仅 Warn 不中断）。
    /// 任意异常不部分写入；失败时 best-effort 删除残留 .tmp，return false 且 errorMessage 明确。
    /// </summary>
    public bool SaveOverlay(string npcName, BioData editedBio, BioScope targetScope, out string errorMessage)
    {
        errorMessage = string.Empty;
        string path = string.Empty;
        string tmp = string.Empty;
        try
        {
            path = OverlayPathFor(npcName, targetScope);
            string dir = Path.GetDirectoryName(path)!;
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
            tmp = path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, overwrite: true);

            InvalidateBioAsset(npcName);

            // 对称清理：删除另一作用域的同名文件，保证同一 NPC 仅单一作用域持有覆盖层。
            string otherScopePath = targetScope == BioScope.Global
                ? ResolveLocalPathOrNull(npcName)
                : OverlayPathFor(npcName, BioScope.Global);
            if (otherScopePath != null && File.Exists(otherScopePath))
            {
                try { File.Delete(otherScopePath); }
                catch (Exception ex)
                {
                    this.monitor.Log($"[BioStorage] 对称清理另一作用域文件失败({npcName}, {targetScope}): {ex.Message}", LogLevel.Warn);
                }
            }

            this.monitor.Log($"[BioStorage] 已保存覆盖层并失效资产缓存: {npcName}", LogLevel.Info);
            return true;
        }
        catch (Exception ex)
        {
            // 失败时 best-effort 删除残留 .tmp，避免污染磁盘。
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }

            errorMessage = ex.Message;
            this.monitor.Log($"[BioStorage] 覆盖层保存失败({npcName}): {ex.Message}", LogLevel.Error);
            return false;
        }
    }

    /// <summary>
    /// 解析 Local 作用域路径；LocalBaseDir==null（载档前）时返回 null，由调用方跳过。
    /// 单独抽出以避免 OverlayPathFor(scope=Local) 在 LocalBaseDir==null 时抛异常。
    /// </summary>
    private static string ResolveLocalPathOrNull(string npcName)
    {
        string localDir = LocalBaseDir;
        return localDir is null ? null : OverlayPathFor(npcName, BioScope.Local);
    }

    /// <summary>
    /// 删除覆盖层：依次尝试 Local（若 LocalBaseDir != null）、Global、遗留 Mod 目录三路径。
    /// 任一存在且删除成功 → InvalidateBioAsset + return true；
    /// 全部不存在 → InvalidateBioAsset + return true（幂等）；
    /// 删除抛异常 → errorMessage 明确 + return false（不谎报成功）。
    /// </summary>
    public bool ResetOverlay(string npcName, out string errorMessage)
    {
        errorMessage = string.Empty;
        try
        {
            bool anyExisted = false;
            string legacyPath = Path.Combine(ModEntry.SHelper.DirectoryPath, "saves", "custom_bios", SanitizeFileName(npcName) + ".json");
            string[] candidates = { ResolveLocalPathOrNull(npcName), OverlayPathFor(npcName, BioScope.Global), legacyPath };

            foreach (string path in candidates)
            {
                if (path == null || !File.Exists(path))
                    continue;

                File.Delete(path);
                anyExisted = true;
            }

            InvalidateBioAsset(npcName);
            this.monitor.Log($"[BioStorage] 已重置覆盖层并失效资产缓存: {npcName} ({(anyExisted ? "已删除文件" : "本就不存在，幂等")})", LogLevel.Info);
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            this.monitor.Log($"[BioStorage] 覆盖层删除失败({npcName}): {ex.Message}", LogLevel.Error);
            return false;
        }
    }

    /// <summary>
    /// 导出覆盖层 JSON 到 Global/exports 目录（文件名带时间戳，避免覆盖）。
    /// 导出文件无需原子性；成功 monitor.Log(LogLevel.Info) 完整路径；异常 return false。
    /// </summary>
    public bool ExportBio(string npcName, BioData data, out string exportPath, out string errorMessage)
    {
        exportPath = string.Empty;
        errorMessage = string.Empty;
        try
        {
            string exportDir = Path.Combine(GlobalBaseDir, "exports");
            if (!Directory.Exists(exportDir))
                Directory.CreateDirectory(exportDir);

            string fileName = $"{SanitizeFileName(npcName)}_Bio_{DateTime.Now:yyyyMMdd_HHmmss}.json";
            string fullPath = Path.Combine(exportDir, fileName);

            string json = JsonConvert.SerializeObject(data, Formatting.Indented);
            File.WriteAllText(fullPath, json);

            exportPath = fullPath;
            this.monitor.Log($"[BioStorage] 已导出覆盖层: {fullPath}", LogLevel.Info);
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            this.monitor.Log($"[BioStorage] 覆盖层导出失败({npcName}): {ex.Message}", LogLevel.Error);
            return false;
        }
    }

    /// <summary>
    /// 仅解析导入的 JSON 文本为 BioData（不落盘）。
    /// 空白输入 → "导入数据为空"；反序列化 null → "反序列化失败"；异常 → ex.Message。
    /// BioData 模型不含 NPC 名字段，无法做粘贴目标校验，由调用方自行选择目标 NPC，此处不做。
    /// </summary>
    public bool TryParseImportedBio(string jsonText, out BioData importedData, out string errorMessage)
    {
        importedData = null;
        errorMessage = string.Empty;

        if (string.IsNullOrWhiteSpace(jsonText))
        {
            errorMessage = "导入数据为空";
            return false;
        }

        try
        {
            importedData = JsonConvert.DeserializeObject<BioData>(jsonText);
            if (importedData == null)
            {
                errorMessage = "反序列化失败";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            importedData = null;
            errorMessage = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 幂等一次性迁移：将遗留 Mod 目录（ModDir/saves/custom_bios/）下的 *.json 覆盖层复制到
    /// Global/custom_bios/。目标已存在 → 跳过并 Log(Debug)；单文件失败 → Log(Warn) 并继续；
    /// 永不删除源文件。由 RegisterAssetProviders() 订阅成功分支末尾调用。
    /// </summary>
    private static void MigrateLegacyOverlays(IMonitor monitor)
    {
        try
        {
            string legacyDir = Path.Combine(ModEntry.SHelper.DirectoryPath, "saves", "custom_bios");
            if (!Directory.Exists(legacyDir))
                return;

            string[] files = Directory.GetFiles(legacyDir, "*.json");
            if (files.Length == 0)
                return;

            string targetDir = Path.Combine(GlobalBaseDir, "custom_bios");
            if (!Directory.Exists(targetDir))
                Directory.CreateDirectory(targetDir);

            foreach (string src in files)
            {
                string dest = Path.Combine(targetDir, Path.GetFileName(src));
                if (File.Exists(dest))
                {
                    monitor?.Log($"[BioStorage] 迁移跳过（目标已存在）: {Path.GetFileName(src)}", LogLevel.Debug);
                    continue;
                }

                try
                {
                    File.Copy(src, dest, overwrite: false);
                }
                catch (Exception ex)
                {
                    monitor?.Log($"[BioStorage] 迁移单文件失败({Path.GetFileName(src)}): {ex.Message}", LogLevel.Warn);
                }
            }
        }
        catch (Exception ex)
        {
            monitor?.Log($"[BioStorage] 迁移过程异常: {ex.Message}", LogLevel.Warn);
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

    /// <summary>
    /// 计算指定 NPC 在指定作用域下的覆盖层 JSON 路径。
    /// scope==Local 且 LocalBaseDir==null（载档前）→ 抛 InvalidOperationException，禁止静默落 Global。
    /// </summary>
    internal static string OverlayPathFor(string npcName, BioScope scope)
    {
        string baseDir = scope switch
        {
            BioScope.Global => GlobalBaseDir,
            BioScope.Local => LocalBaseDir ?? throw new InvalidOperationException("no save loaded"),
            _ => GlobalBaseDir
        };
        return Path.Combine(baseDir, "custom_bios", SanitizeFileName(npcName) + ".json");
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

    /// <summary>
    /// 从活跃作用域反序列化覆盖层：TryGetActiveScope 判定作用域（Local 优先），损坏回退 null + Warn。
    /// 无覆盖层 → return null。
    /// </summary>
    private BioData TryDeserializeOverlay(string npcName)
    {
        if (!TryGetActiveScope(npcName, out BioScope activeScope))
            return null;

        string path = OverlayPathFor(npcName, activeScope);
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
                if (editor is not IAssetData<BioData> d)
                {
                    ModEntry.SMonitor?.Log(
                        $"[BioStorage] 注入跳过({npc}): 资产类型不匹配，期望 {typeof(BioData).Name}，实际 {editor.DataType}",
                        LogLevel.Error);
                    return;
                }

                _baselineCache[npc] = DeepClone(d.Data);
                if (!HasCustomOverlay(npc))
                {
                    ModEntry.SMonitor?.Log($"[BioStorage] 无覆盖层，基线直通: {npc}", LogLevel.Trace);
                    return;
                }

                BioData ov = TryDeserializeOverlay(npc);
                if (ov == null) return;
                d.ReplaceWith(ov);
                TryGetActiveScope(npc, out BioScope scope);
                ModEntry.SMonitor?.Log(
                    $"[BioStorage] overlay applied: {npc} (scope={scope}, Biography={ov.Biography?.Length ?? 0} chars, stages={ov.ProgressStates?.Count ?? 0})",
                    LogLevel.Debug);
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
        ModEntry.SMonitor?.Log($"[BioStorage] 已失效资产缓存: {BioAssetFor(npcName)}", LogLevel.Debug);
    }
}
