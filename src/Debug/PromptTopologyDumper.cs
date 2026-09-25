// PromptTopologyDumper.cs
// ═══════════════════════════════════════════════════════════════════════════
// VT3-A — Prompt topology fixture dumper.
//
// Read-only replay of the production prompt-injection管线. For a given NPC it
// builds a Prompts instance the same way LlmDialogueService does, then emits
// the full request text split into labelled sections plus a manifest.json.
//
// Four branch fixtures are produced:
//   Normal    – default flags
//   StoodUp   – flags.HasStoodUpPending = true
//   Greeting  – flags.IsSimpleGreeting = true, no movement
//   Date      – temporarily drives DateManager into Active for the NPC,
//               dumps, then restores prior state.
// ═══════════════════════════════════════════════════════════════════════════

using System;
using System.IO;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StardewModdingAPI;
using StardewValley;
using ValleytalkReborn.Services;

namespace ValleytalkReborn;

/// <summary>
/// Debug utility that captures, per NPC, the exact prompt topology the
/// production管线 would send to the LLM, across the four canonical branches.
/// Registered as the SMAPI console command `vt_dump_topology <NpcName>`.
/// </summary>
internal static class PromptTopologyDumper
{
    private const string Prefix = "[vt_dump_topology]";

    // ── Reflection caches for the internal prompt-block builders ──
    private static MethodInfo _buildGossipBlock;
    private static MethodInfo _buildLocalBlock;
    private static MethodInfo _evolvedTraitBlock;
    private static MethodInfo _eavesdropBlock;
    private static MethodInfo _spouseHasPending;
    private static MethodInfo _spouseBuildStatus;
    private static MethodInfo _bridgeBlock;
    private static MethodInfo _echoBlock;
    private static MethodInfo _milestoneBlock;

    private static bool _reflectionResolved;

    /// <summary>
    /// Registers the console command. Mirrors EmotionDebugCommands.Register.
    /// </summary>
    public static void Register(ICommandHelper console)
    {
        console.Add(
            "vt_dump_topology",
            "Dump the full LLM request topology for an NPC across 4 branches. "
            + "Usage: vt_dump_topology <NpcName>   e.g. vt_dump_topology Abigail",
            OnCommand);
    }

    private static void OnCommand(string command, string[] args)
    {
        if (!Context.IsWorldReady)
        {
            Info("世界未就绪：请进入存档后再执行。");
            return;
        }

        if (args.Length < 1)
        {
            Info("用法: vt_dump_topology <NpcName>  例: vt_dump_topology Abigail");
            return;
        }

        string npcName = args[0];

        // SMAPI 控制台命令本身已在游戏主线程中调度执行，直接运行即可，避免死锁
        try
        {
            EnsureReflection();
            RunDump(npcName);
        }
        catch (Exception ex)
        {
            ModEntry.SMonitor?.Log($"{Prefix} 命令执行失败: {ex}", LogLevel.Error);
        }
    }

    private static void RunDump(string npcName)
    {
        var character = FindCharacter(npcName);
        if (character == null)
        {
            Info($"NPC 不存在或未加载: {npcName}");
            return;
        }

        string outDir = Path.Combine(StorageLayout.ModDirectory, "PromptFixtures");
        try
        {
            Directory.CreateDirectory(outDir);
        }
        catch (Exception ex)
        {
            ModEntry.SMonitor?.Log(
                $"{Prefix} 输出目录不可写 '{outDir}': {ex.Message}",
                LogLevel.Error);
            return;
        }

        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");

        // ── 1. Normal ──
        DumpBranch(outDir, timestamp, "Normal", character, (ctx, flags) => { });

        // ── 2. StoodUp ──
        DumpBranch(outDir, timestamp, "StoodUp", character, (ctx, flags) =>
        {
            flags.HasStoodUpPending = true;
        });

        // ── 3. Greeting (no movement) ──
        DumpBranch(outDir, timestamp, "Greeting", character, (ctx, flags) =>
        {
            flags.IsSimpleGreeting = true;
            flags.IsMovementRequested = false;
        });

        // ── 4. Date (enter → dump → exit & restore) ──
        DumpDateBranch(outDir, timestamp, character);

        // ── manifest ──
        WriteManifest(outDir, timestamp, npcName);

        Info($"完成。已写入 {outDir}");
    }

    private delegate void ConfigureFlags(DialogueContext ctx, ContextFlags flags);

    private static void DumpBranch(
        string outDir,
        string timestamp,
        string branch,
        Character character,
        ConfigureFlags configure)
    {
        try
        {
            var context = new DialogueContext();
            configure(context, context.RoutingFlags);

            string body = DumpFullRequest(character, context);
            string path = Path.Combine(outDir, $"dump_{branch}_{timestamp}.txt");
            File.WriteAllText(path, body);
            Info($"[{branch}] 已写入 {Path.GetFileName(path)} ({body.Length} chars)");
        }
        catch (Exception ex)
        {
            ModEntry.SMonitor?.Log($"{Prefix} [{branch}] 分支 dump 失败: {ex.Message}", LogLevel.Warn);
        }
    }

    private static void DumpDateBranch(string outDir, string timestamp, Character character)
    {
        var dm = DateManager.Instance;
        if (dm == null)
        {
            Info("[Date] DateManager 不可用，跳过。");
            return;
        }

        // 捕获原状态以便恢复
        DatePhase origPhase = dm.Phase;
        string origNpc = dm.ActiveDateNpcName;
        string origLoc = dm.ActiveDateLocation;
        DateManager.DateMode origMode = dm.CurrentDateMode;

        bool entered = false;
        try
        {
            Info("[Date] 进入约会状态…");

            // 通过反射把 DateManager 驱动到 Active（修正：属性名为 "Phase"）
            SetDateField("Phase", typeof(DatePhase), DatePhase.Active);
            SetDateField(nameof(DateManager.ActiveDateNpcName), typeof(string), character.Name);
            SetDateField(nameof(DateManager.ActiveDateLocation), typeof(string),
                Game1.player?.currentLocation?.Name ?? "");
            SetDateField(nameof(DateManager.CurrentDateMode), typeof(DateManager.DateMode), DateManager.DateMode.Follow);
            // IsOnDate 要求 Game1.timeOfDay < DynamicEndTime；置一个足够大的值。
            SetDateField("DynamicEndTime", typeof(int), 2600);

            entered = true;

            var context = new DialogueContext();
            string body = DumpFullRequest(character, context);
            string path = Path.Combine(outDir, $"dump_Date_{timestamp}.txt");
            File.WriteAllText(path, body);
            Info($"[Date] 已写入 {Path.GetFileName(path)} ({body.Length} chars)");
        }
        catch (Exception ex)
        {
            ModEntry.SMonitor?.Log($"{Prefix} [Date] 分支失败: {ex.Message}", LogLevel.Warn);
        }
        finally
        {
            if (entered)
            {
                try
                {
                    Info("[Date] 退出并恢复原状态…");
                    SetDateField("Phase", typeof(DatePhase), origPhase);
                    SetDateField(nameof(DateManager.ActiveDateNpcName), typeof(string), origNpc);
                    SetDateField(nameof(DateManager.ActiveDateLocation), typeof(string), origLoc);
                    SetDateField(nameof(DateManager.CurrentDateMode), typeof(DateManager.DateMode), origMode);
                }
                catch (Exception ex)
                {
                    ModEntry.SMonitor?.Log(
                        $"{Prefix} [Date] 恢复原状态失败（需人工检查）: {ex.Message}",
                        LogLevel.Warn);
                }
            }
        }
    }

    /// <summary>
    /// 按当前生产管线原样构建一次完整请求，返回带分隔标记的全文。
    /// 只读读取管线；但部分内部构建器（gossip / echo / bridge）在构建时会
    /// 推进去重/消费记账——这是反射重放不可避免的副效应。
    /// </summary>
    public static string DumpFullRequest(Character character, DialogueContext context)
    {
        if (character == null) throw new ArgumentNullException(nameof(character));
        if (context == null) throw new ArgumentNullException(nameof(context));

        var prompts = new Prompts(context, character);

        // ── 与 LlmDialogueService.GenerateDialogueAsync 同样的注入顺序 ──
        try
        {
            var memoryCtx = MemoryManager.Instance.GetSmartMemoryContext(character.Name);
            if (!string.IsNullOrEmpty(memoryCtx))
                prompts.SystemPrompt += "\n\n" + memoryCtx;
        }
        catch (Exception ex)
        {
            ModEntry.SMonitor?.Log($"{Prefix} memory 注入失败: {ex.Message}", LogLevel.Trace);
        }

        try
        {
            var traits = InvokeStatic(_evolvedTraitBlock, character.Name) as string;
            if (!string.IsNullOrEmpty(traits))
                prompts.PendingEvolvedTraitsBlock = traits;
        }
        catch (Exception ex)
        {
            ModEntry.SMonitor?.Log($"{Prefix} evolved-trait 注入失败: {ex.Message}", LogLevel.Trace);
        }

        try
        {
            var gossip = InvokeStatic(_buildGossipBlock, character.Name) as string;
            if (!string.IsNullOrEmpty(gossip))
                prompts.SystemPrompt += "\n\n" + gossip;
        }
        catch (Exception ex)
        {
            ModEntry.SMonitor?.Log($"{Prefix} gossip 注入失败: {ex.Message}", LogLevel.Trace);
        }

        try
        {
            var local = InvokeStatic(_buildLocalBlock, character.Name) as string;
            if (!string.IsNullOrEmpty(local))
                prompts.PendingLocalPerceptionBlock = local;
        }
        catch (Exception ex)
        {
            ModEntry.SMonitor?.Log($"{Prefix} local-perception 注入失败: {ex.Message}", LogLevel.Trace);
        }

        try
        {
            var eaves = InvokeStatic(_eavesdropBlock, character.Name) as string;
            if (!string.IsNullOrEmpty(eaves))
                prompts.PendingEavesdropBlock = eaves;
        }
        catch (Exception ex)
        {
            ModEntry.SMonitor?.Log($"{Prefix} eavesdrop 注入失败: {ex.Message}", LogLevel.Trace);
        }

        if (ModEntry.Config.EnableEmotionSystem)
        {
            try
            {
                var snapshot = EmotionalStateResolver.PrepareSnapshot(character, character.StardewNpc);
                character.PendingEmotion = snapshot;
                var compiled = EmotionalStateResolver.Compile(character, character.StardewNpc, snapshot);
                prompts.PendingEmotionBlock = compiled.PromptBlock;
            }
            catch (Exception ex)
            {
                prompts.PendingEmotionBlock = string.Empty;
                character.PendingEmotion = null;
                ModEntry.SMonitor?.Log($"{Prefix} emotion 编译失败已降级: {ex.Message}", LogLevel.Trace);
            }
        }

        try
        {
            if (InvokeStatic(_spouseHasPending, character.Name) is true)
            {
                var porchCtx = InvokeStatic(_spouseBuildStatus) as string;
                if (!string.IsNullOrEmpty(porchCtx))
                    prompts.PendingSpouseWaitingBlock = porchCtx;
            }
        }
        catch (Exception ex)
        {
            ModEntry.SMonitor?.Log($"{Prefix} spouse-waiting 注入失败: {ex.Message}", LogLevel.Trace);
        }

        // Echo（bridge 优先，否则即时 echo）
        try
        {
            var bridge = InvokeStatic(_bridgeBlock, character.Name) as string;
            if (!string.IsNullOrEmpty(bridge))
            {
                prompts.PendingEchoBlock = bridge;
                prompts.PendingEchoIsBridge = true;
            }
            else
            {
                var echo = InvokeStatic(_echoBlock, character.Name,
                    character.StardewNpc?.currentLocation?.Name) as string;
                if (!string.IsNullOrEmpty(echo))
                    prompts.PendingEchoBlock = echo;
            }
        }
        catch (Exception ex)
        {
            ModEntry.SMonitor?.Log($"{Prefix} echo 注入失败: {ex.Message}", LogLevel.Trace);
        }

        try
        {
            object milestoneMgr = GetMilestoneManagerInstance();
            var milestone = InvokeMethod(_milestoneBlock, milestoneMgr, character) as string;
            if (!string.IsNullOrEmpty(milestone))
                prompts.PendingMilestoneBlock = milestone;
        }
        catch (Exception ex)
        {
            ModEntry.SMonitor?.Log($"{Prefix} milestone 注入失败: {ex.Message}", LogLevel.Trace);
        }

        // 终局去重（与生产一致）
        try
        {
            PromptDeduplicatorDeduplicate(prompts);
        }
        catch (Exception ex)
        {
            ModEntry.SMonitor?.Log($"{Prefix} 去重失败（保留未去重）: {ex.Message}", LogLevel.Trace);
        }

        // ── 拼接完整请求文本 ──
        var sb = new StringBuilder();
        AppendSection(sb, "[SystemPrompt]", prompts.SystemPrompt);
        AppendSection(sb, "[GameConstantContext]", prompts.GameConstantContext);
        AppendSection(sb, "[NpcConstantContext]", prompts.NpcConstantContext);
        AppendSection(sb, "[CorePrompt]", prompts.CorePrompt);
        AppendSection(sb, "[Instructions]", prompts.Instructions);
        AppendSection(sb, "[Command]", prompts.Command);
        AppendSection(sb, "[ResponseStart]", prompts.ResponseStart);
        return sb.ToString();
    }

    private static void AppendSection(StringBuilder sb, string label, string content)
    {
        sb.AppendLine($"===== {label} =====");
        sb.AppendLine(content ?? string.Empty);
        sb.AppendLine();
    }

    private static void WriteManifest(string outDir, string timestamp, string npcName)
    {
        try
        {
            var obj = new JObject
            {
                ["commit"] = TryGetGitCommitHash() ?? "unknown",
                ["gameVersion"] = TryGetGameVersion() ?? "unknown",
                ["modVersion"] = GetOwnModVersion(),
                ["capturedAt"] = DateTime.Now.ToString("o"),
                ["inGameDate"] = FormatInGameDate(),
                ["npcName"] = npcName,
                ["branches"] = new JArray("Normal", "StoodUp", "Greeting", "Date"),
            };
            string path = Path.Combine(outDir, $"manifest_{timestamp}.json");
            File.WriteAllText(path, obj.ToString(Formatting.Indented));
        }
        catch (Exception ex)
        {
            ModEntry.SMonitor?.Log($"{Prefix} manifest 写入失败: {ex.Message}", LogLevel.Warn);
        }
    }

    // ── Reflection plumbing ──

    private static void EnsureReflection()
    {
        if (_reflectionResolved) return;

        _buildGossipBlock = ResolveStatic("ValleytalkReborn.PerceptionInjector", "BuildGossipBlock");
        _buildLocalBlock = ResolveStatic("ValleytalkReborn.PerceptionInjector", "BuildLocalBlock");
        _evolvedTraitBlock = ResolveStatic("ValleytalkReborn.EvolvedTraitManager", "GetPromptBlock");
        _eavesdropBlock = ResolveStatic("ValleytalkReborn.EavesdropInjector", "BuildBlock");
        _spouseHasPending = ResolveStatic("ValleytalkReborn.SpouseWaitingEvent", "HasPendingSpouseDialogue");
        _spouseBuildStatus = ResolveStatic("ValleytalkReborn.SpouseWaitingEvent", "BuildStatusPrompt");
        _bridgeBlock = ResolveStatic("ValleytalkReborn.FreshBarkBridgeStore", "BuildBridgeBlock");
        _echoBlock = ResolveStaticTwoArgs("ValleytalkReborn.ImmediateEchoStore", "BuildEchoBlock");
        _milestoneBlock = ResolveMethod(
            typeof(RelationshipMilestoneManager).GetMethod("BuildMilestoneBlock", BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static));

        _reflectionResolved = true;
    }

    private static MethodInfo ResolveStatic(string typeName, string methodName)
    {
        var type = Type.GetType(typeName, throwOnError: false, ignoreCase: false);
        if (type == null)
        {
            ModEntry.SMonitor?.Log($"{Prefix} 反射类型缺失: {typeName}", LogLevel.Warn);
            return null;
        }
        var mi = type.GetMethod(methodName,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
            null, new[] { typeof(string) }, null);
        if (mi == null)
        {
            ModEntry.SMonitor?.Log($"{Prefix} 反射方法缺失: {typeName}.{methodName}(string)", LogLevel.Warn);
        }
        return mi;
    }

    private static MethodInfo ResolveStaticTwoArgs(string typeName, string methodName)
    {
        var type = Type.GetType(typeName, throwOnError: false, ignoreCase: false);
        if (type == null)
        {
            ModEntry.SMonitor?.Log($"{Prefix} 反射类型缺失: {typeName}", LogLevel.Warn);
            return null;
        }
        var mi = type.GetMethod(methodName,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
            null, new[] { typeof(string), typeof(string) }, null);
        if (mi == null)
        {
            ModEntry.SMonitor?.Log($"{Prefix} 反射方法缺失: {typeName}.{methodName}(string,string)", LogLevel.Warn);
        }
        return mi;
    }

    private static MethodInfo ResolveMethod(MethodInfo mi)
    {
        if (mi == null)
            ModEntry.SMonitor?.Log($"{Prefix} 反射方法缺失", LogLevel.Warn);
        return mi;
    }

    private static object InvokeStatic(MethodInfo mi, params object[] args)
    {
        if (mi == null) return null;
        return mi.Invoke(null, args);
    }

    private static object InvokeMethod(MethodInfo mi, object target, params object[] args)
    {
        if (mi == null) return null;
        return mi.Invoke(mi.IsStatic ? null : target, args);
    }

    private static object GetMilestoneManagerInstance()
    {
        var type = typeof(RelationshipMilestoneManager);
        var prop = type.GetProperty("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        if (prop != null) return prop.GetValue(null);

        var field = type.GetField("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        return field?.GetValue(null);
    }

    private static void PromptDeduplicatorDeduplicate(Prompts prompts)
    {
        // LlmDialogueService.PromptDeduplicator 是 private static 嵌套类；反射调用。
        var service = typeof(LlmDialogueService);
        var nested = service.GetNestedType("PromptDeduplicator",
            BindingFlags.NonPublic | BindingFlags.Static);
        var mi = nested?.GetMethod("DeduplicatePrompts",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        mi?.Invoke(null, new object[] { prompts });
    }

    private static void SetDateField(string fieldName, Type fieldType, object value)
    {
        // DatePhase / DateMode 是嵌套枚举，按底层类型设置。
        var field = typeof(DateManager).GetField(fieldName,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (field != null)
        {
            field.SetValue(DateManager.Instance, value);
            return;
        }
        // 兜底：尝试属性（含 private setter）
        var prop = typeof(DateManager).GetProperty(fieldName,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        prop?.GetSetMethod(nonPublic: true)?.Invoke(DateManager.Instance, new[] { value });
    }

    // ── Helpers ──

    private static Character FindCharacter(string npcName)
    {
        var exact = DialogueBuilder.Instance.GetCharacterByName(npcName);
        if (exact != null) return exact;
        foreach (var c in DialogueBuilder.Instance.GetAllLoadedCharacters())
        {
            if (string.Equals(c.Name, npcName, StringComparison.OrdinalIgnoreCase))
                return c;
        }
        return null;
    }

    private static string TryGetGitCommitHash()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                Arguments = "rev-parse HEAD",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            string output = p?.StandardOutput.ReadToEnd()?.Trim();
            p?.WaitForExit(2000);
            return string.IsNullOrEmpty(output) ? null : output;
        }
        catch (Exception ex)
        {
            ModEntry.SMonitor?.Log($"{Prefix} git commit 获取失败: {ex.Message}", LogLevel.Trace);
            return null;
        }
    }

    private static string TryGetGameVersion()
    {
        try
        {
            return Game1.version;
        }
        catch (Exception ex)
        {
            ModEntry.SMonitor?.Log($"{Prefix} game version 获取失败: {ex.Message}", LogLevel.Trace);
            return null;
        }
    }

    private static string FormatInGameDate()
    {
        try
        {
            return $"{Game1.currentSeason} {Game1.dayOfMonth}, Year {Game1.year}, {Game1.timeOfDay}";
        }
        catch
        {
            return "unknown";
        }
    }

    private static string GetOwnModVersion()
    {
        try
        {
            return typeof(ModEntry).Assembly.GetName().Version?.ToString() ?? "unknown";
        }
        catch (Exception ex)
        {
            ModEntry.SMonitor?.Log($"{Prefix} modVersion 获取失败: {ex.Message}", LogLevel.Trace);
            return "unknown";
        }
    }

    private static void Info(string message) =>
        ModEntry.SMonitor?.Log($"{Prefix} {message}", LogLevel.Info);
}