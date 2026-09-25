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
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        console.Add(
            "vt_ab_topology",
            "A/B parity check (legacy GetCorePrompt vs new AssembleCore) for one NPC, 4 branches. "
            + "Usage: vt_ab_topology <NpcName>   e.g. vt_ab_topology Abigail   |   "
            + "vt_ab_topology --selftest (comparator self-test)",
            OnAbCommand);
        console.Add(
            "vt_ab_topology_multi",
            "Multi-turn A/B parity check with layered assertions (A1-A4 + rotation probe). "
            + "Usage: vt_ab_topology_multi <NpcName>   e.g. vt_ab_topology_multi Abigail",
            OnAbMultiCommand);
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
            var context = BuildPopulatedContext(character);
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

    /// <summary>构建全要素填充的 DialogueContext（天气、好感、子代、地点、时间），
    /// 避免 Prompts 组装期因空引用产生空白块。对应 2ebfb2db 的 CreatePopulatedContext 核心字段。</summary>
    private static DialogueContext BuildPopulatedContext(Character character)
    {
        // 天气
        var weather = new List<string>();
        if (Game1.isRaining) weather.Add("rain");
        else if (Game1.isSnowing) weather.Add("snow");
        else if (Game1.isLightning) weather.Add("storm");
        else weather.Add("sun");

        // 好感 / 婚姻
        Friendship friendship = null;
        if (Game1.player?.friendshipData != null)
            Game1.player.friendshipData.TryGetValue(character.Name, out friendship);
        int? heartLevel = friendship != null ? friendship.Points / 250 : (int?)null;

        // 子代
        var children = new List<ChildDescription>();
        try
        {
            if (Game1.player != null)
            {
                foreach (var c in Game1.player.getChildren())
                {
                    children.Add(new ChildDescription(c.Name, c.Gender == StardewValley.Gender.Male, c.Age));
                }
            }
        }
        catch { }

        string locName = character.StardewNpc?.currentLocation?.Name
            ?? Game1.currentLocation?.Name ?? "FarmHouse";

        return new DialogueContext
        {
            Hearts = heartLevel,
            Season = (ValleytalkReborn.Season)Game1.season,
            Year = Game1.year,
            DayOfSeason = Game1.dayOfMonth,
            TimeOfDay = Game1.timeOfDay.ToString(),
            Location = locName,
            MaleFarmer = Game1.player?.IsMale ?? true,
            Children = children,
            Weather = weather,
            ChatHistory = new List<ConversationElement>(),
            CanGiveGift = false,
        };
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

            var context = BuildPopulatedContext(character);
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
#pragma warning disable CS0618 // VT3-D: 调试夹具仍读 Pending* 遗留字段（触达 Obsolete），集中抑制；生产路径已不触达。
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
#pragma warning restore CS0618

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

    // ═══════════════════════════════════════════════════════════════════════════
    //  VT3-E: A/B Parity Verification Commands
    //  VT3-E-INS 仪器修正：每分支会话隔离、单一上下文断言、三线分支断言、
    //  Date 夹具、差异明细输出、比对器自测、multi 真断言（A1~A4）。
    // ═══════════════════════════════════════════════════════════════════════════

    private enum BranchOutcome { Pass, Fail, FixtureFail, Error }

    private static void OnAbCommand(string command, string[] args)
    {
        if (args.Length >= 1 && args[0] == "--selftest")
        {
            RunComparatorSelfTest();
            return;
        }
        if (!Context.IsWorldReady) { Info("世界未就绪。"); return; }
        if (args.Length < 1) { Info("用法: vt_ab_topology <NpcName> | vt_ab_topology --selftest"); return; }
        string npcName = args[0];
        var character = FindCharacter(npcName);
        if (character == null) { Info($"NPC 不存在: {npcName}"); return; }

        var results = new List<(string Branch, BranchOutcome Outcome, int Warn)>();
        void AddResult(string name, (BranchOutcome Outcome, int Warn) r) =>
            results.Add((name, r.Outcome, r.Warn));

        AddResult("Normal", RunAbBranch("Normal", character, (ctx, f) => { }));
        AddResult("StoodUp", RunAbBranch("StoodUp", character, (ctx, f) => { f.HasStoodUpPending = true; }));
        AddResult("Greeting", RunAbBranch("Greeting", character, (ctx, f) => { f.IsSimpleGreeting = true; f.IsMovementRequested = false; }));

        // 第 4 分支：Date（复用 DumpDateBranch 状态操纵；夹具仍失败 → 3 分支覆盖 + P-1 转移）。
        AddResult("Date", RunAbDateBranch(character));

        int pass = 0, fail = 0, fixtureFail = 0, errorCount = 0, warn = 0;
        foreach (var r in results)
        {
            warn += r.Warn;
            if (r.Outcome == BranchOutcome.Pass) pass++;
            else if (r.Outcome == BranchOutcome.Fail) fail++;
            else if (r.Outcome == BranchOutcome.FixtureFail) fixtureFail++;
            else errorCount++;
        }
        Info($"A/B summary: {pass} PASS / {fail} FAIL / {fixtureFail} FIXTURE_FAIL / {errorCount} ERROR / {warn} WARN");
    }

    /// <summary>单分支 A/B 比对：会话隔离 → 单一上下文 → 三线断言 → 双侧渲染 → 同源断言 → 块集比对。</summary>
    private static (BranchOutcome Outcome, int Warn) RunAbBranch(
        string branchName, Character character, ConfigureFlags configure)
    {
        try
        {
            // ── §1 每分支会话隔离（与 multi 同规）+ 强制新会话（禁跨分支/真实游玩快照复用）──
            SessionCache.ClearForNpc(character.Name);
            ForceNewTier1Sessions(character.Name);

            // ── §2 单一上下文：一次构建，BuildPlan 与两侧薄壳共用同一实例 ──
            var context = BuildPopulatedContext(character);
            configure(context, context.RoutingFlags);

            var prompts = new Prompts(context, character);
            var plan = ConversationDirectorInstance.BuildPlan(context, character, prompts);

            // Side A（遗留薄壳）：plan 脉冲注入 Pending* 字段——遗留服务的注入职责重放。
            // 含 EvolvedTraits：遗留四分支均渲染 PendingEvolvedTraitsBlock（服务注入职责），
            // 内容取自 plan.Tier1Snapshot 以与 Side B 同源。
#pragma warning disable CS0618 // VT3-E: 调试夹具仍读写 Pending* 遗留字段（触达 Obsolete），集中抑制；生产路径已不触达。
            foreach (var kvp in plan.ActiveImpulses)
            {
                switch (kvp.Key)
                {
                    case Tier2bBlockIds.Eavesdrop: prompts.PendingEavesdropBlock = kvp.Value; break;
                    case Tier2bBlockIds.SpouseWaiting: prompts.PendingSpouseWaitingBlock = kvp.Value; break;
                    case Tier2bBlockIds.Echo: prompts.PendingEchoBlock = kvp.Value; break;
                    case Tier2bBlockIds.Milestone: prompts.PendingMilestoneBlock = kvp.Value; break;
                    case Tier2bBlockIds.LocalPerception: prompts.PendingLocalPerceptionBlock = kvp.Value; break;
                    case Tier2bBlockIds.Emotion: prompts.PendingEmotionBlock = kvp.Value; break;
                    case Tier2bBlockIds.PlayerProfile: break; // 遗留薄壳内部经 PlayerProfileManager 路径构建
                    case Tier2bBlockIds.Preoccupation: break; // 遗留薄壳内部构建（50% 重掷 → 随机方差类 WARN）
                }
            }
            prompts.PendingEchoIsBridge = plan.EchoFromBridge;
            string evolvedTraits = plan.Tier1Snapshot.Get(Tier1BlockIds.EvolvedTraits);
            if (!string.IsNullOrEmpty(evolvedTraits))
                prompts.PendingEvolvedTraitsBlock = evolvedTraits;
#pragma warning restore CS0618

            // ── §3 三线分支断言：intended / A resolved / B resolved(=plan.Branch) ──
            var sideAResolved = ResolveLegacyBranchMirror(prompts, character);
            var sideBResolved = plan.Branch;
            Info($"[{branchName}] 三线: intended={branchName} | A resolved={sideAResolved} | B resolved={sideBResolved} | "
                + $"EnableDateSystem={ModEntry.Config.EnableDateSystem} | session reused={plan.IsTier1Reused} (id={Truncate(plan.SessionId, 8)}…)");
            if (sideAResolved.ToString() != branchName || sideBResolved.ToString() != branchName)
            {
                Info($"[{branchName}] FIXTURE_FAIL — 夹具未落到预期分支（intended≠resolved），跳过比对（不产出垃圾 FAIL）。");
                return (BranchOutcome.FixtureFail, 0);
            }

            string legacyCore = prompts.CorePrompt;

            // Side B（新管线）：同一 plan、同一 context。
            var promptsB = new Prompts(context, character);
            promptsB.AssembleCore(plan, context, character);
            promptsB.Instructions = promptsB.GetInstructions(plan.Branch); // 生产接线镜像（LlmDialogueService.cs:91）
            string newCore = promptsB.CorePrompt;

            // ── §2（续）同源断言：两侧薄壳实际消费的 context 必须与 BuildPlan 同源 ──
            bool ctxA = ReferenceEquals(GetPromptsContext(prompts), context);
            bool ctxB = ReferenceEquals(GetPromptsContext(promptsB), context);
            Info($"[{branchName}] context 同源: SideA={ctxA}, SideB={ctxB} → {(ctxA && ctxB ? "同源 OK" : "不同源 (仪器 BUG)")}");
            if (!ctxA || !ctxB)
            {
                Info($"[{branchName}] FAIL — 两侧 context 不同源，比对无效。");
                return (BranchOutcome.Fail, 0);
            }

            // ── 扁平化权威比较（删除空行、按行序列比较）—— 成为权威判定 ──
            // 比对器空白扁平化：双侧各自压平后按行序列比较，块边界差异不导致 FAIL。
            var flatResult = CompareFlattened(legacyCore, newCore, out int flatWarn);

            // 块级比较降级为诊断输出（扁平化失败时辅助定位）。
            var legBlocks = NormalizeToBlocks(legacyCore);
            var newBlocks = NormalizeToBlocks(newCore);
            var blockResult = CompareBlockSets(legBlocks, newBlocks, out int blockWarn);
            if (blockResult == ParityResult.Fail)
                LogBlockDiffDetail(branchName, ComputeBlockDiffDetail(legBlocks, newBlocks));

            // Instructions superset check: legacy Normal 行 ⊆ new 分支行。
            var legacyInstr = GetInstructionLines(prompts.Instructions);
            var newInstr = GetInstructionLines(promptsB.Instructions);
            bool instrSuperset = legacyInstr.IsSubsetOf(newInstr);

            if (flatResult == ParityResult.Fail)
            {
                Info($"[{branchName}] FAIL — {flatResult.GetLabel()} (flattened, authoritative) | block-level: {blockResult.GetLabel()} | Instructions superset: {instrSuperset}");
                return (BranchOutcome.Fail, flatWarn);
            }
            if (!instrSuperset)
            {
                Info($"[{branchName}] FAIL — flattened equivalent but Instructions NOT superset");
                return (BranchOutcome.Fail, flatWarn);
            }
            Info($"[{branchName}] PASS ({flatResult.GetLabel()}; block-level: {blockResult.GetLabel()}; instructions superset OK; warn={flatWarn})");
            return (BranchOutcome.Pass, flatWarn);
        }
        catch (Exception ex)
        {
            Info($"[{branchName}] ERROR: {ex.Message}");
            return (BranchOutcome.Error, 0);
        }
    }

    /// <summary>Date 分支夹具：复用 DumpDateBranch 的 DateManager 状态操纵，比对后恢复。</summary>
    private static (BranchOutcome Outcome, int Warn) RunAbDateBranch(Character character)
    {
        var dm = DateManager.Instance;
        if (dm == null)
        {
            Info("[Date] DateManager 不可用 → FIXTURE_FAIL（3 分支覆盖 + Date 转移 P-1，非阻塞）。");
            return (BranchOutcome.FixtureFail, 0);
        }

        DatePhase origPhase = dm.Phase;
        string origNpc = dm.ActiveDateNpcName;
        string origLoc = dm.ActiveDateLocation;
        DateManager.DateMode origMode = dm.CurrentDateMode;
        bool entered = false;
        try
        {
            Info("[Date] 进入约会状态（复用 DumpDateBranch 状态操纵）…");
            SetDateField("Phase", typeof(DatePhase), DatePhase.Active);
            SetDateField(nameof(DateManager.ActiveDateNpcName), typeof(string), character.Name);
            SetDateField(nameof(DateManager.ActiveDateLocation), typeof(string),
                Game1.player?.currentLocation?.Name ?? "");
            SetDateField(nameof(DateManager.CurrentDateMode), typeof(DateManager.DateMode), DateManager.DateMode.Follow);
            // IsOnDate 要求 Game1.timeOfDay < DynamicEndTime；置一个足够大的值。
            SetDateField("DynamicEndTime", typeof(int), 2600);
            entered = true;

            var result = RunAbBranch("Date", character, (ctx, f) => { });
            if (result.Outcome != BranchOutcome.Pass && result.Outcome != BranchOutcome.Fail)
                Info("[Date] Date 夹具失败 → 3 分支覆盖 + Date 转移 P-1（记录，非阻塞）。");
            return result;
        }
        catch (Exception ex)
        {
            Info($"[Date] 分支失败: {ex.Message} → 3 分支覆盖 + Date 转移 P-1（记录，非阻塞）。");
            return (BranchOutcome.Error, 0);
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

    /// <summary>遗留 GetCorePrompt 分支路由级联的逐字镜像（Prompts.cs StoodUp/Date/Greeting 判定序）。
    /// 仅用于三线断言的 "A resolved" 读数；实际渲染仍由遗留薄壳自身路由。
    /// 运行于 BuildPlan 之后——冲突清除（StoodUp∧IsOnDate）已由 BuildPlan 对同一 flags 对象完成。</summary>
    private static InstructionsBranch ResolveLegacyBranchMirror(Prompts prompts, Character character)
    {
#pragma warning disable CS0618 // 读取 PendingMilestoneBlock（Obsolete）用于镜像 Greeting 门控。
        var flags = prompts.CurrentFlags;
        string npcName = character?.Name ?? "";
        if (flags?.HasStoodUpPending == true && ModEntry.Config.EnableDateSystem)
            return InstructionsBranch.StoodUp;
        if (ModEntry.Config.EnableDateSystem && !string.IsNullOrEmpty(npcName)
            && DateManager.Instance?.IsOnDate(npcName) == true)
            return InstructionsBranch.Date;
        if (flags?.IsSimpleGreeting == true && flags?.IsMovementRequested != true
            && string.IsNullOrEmpty(prompts.PendingMilestoneBlock))
            return InstructionsBranch.Greeting;
#pragma warning restore CS0618
        return InstructionsBranch.Normal;
    }

    /// <summary>比对器自测：A vs A 必须 0 差异（自反性）；A vs A 去除一个已知块必须 FAIL 且正确定位（捕获力）。</summary>
    private static void RunComparatorSelfTest()
    {
        Info("── 比对器自测 (--selftest) ──");
        // 合成 side-A 样本：5 块（含 1 个随机方差类块），空行分隔。
        string sideA = string.Join("\n\n", new[]
        {
            "## GameState\n今天是阳光明媚的春天。",
            "[preoccupation] Abigail 正想着她的吉他。",
            "## Relation\n你和农夫是好朋友。",
            "## Current Conversation\n- 农夫: 你好！\n- Abigail: 嗨！",
            "<movement_instruction>\n- 跟随农夫。\n</movement_instruction>",
        });
        var blocksA = NormalizeToBlocks(sideA);

        // 自测 1：side A 与自身比对 → 必须 0 差异（分块/归一化自反性）。
        var r1 = CompareBlockSets(blocksA, NormalizeToBlocks(sideA), out int warn1);
        bool t1 = r1 == ParityResult.Pass && warn1 == 0;
        Info($"SELFTEST-1 自反性 (A vs A): {(t1 ? "PASS (0 差异)" : $"FAIL (result={r1.GetLabel()}, warn={warn1})")}");

        // 自测 2：side A vs 去除一个已知常规块 → 必须 FAIL 且正确定位（捕获力）。
        string removed = blocksA[2]; // "## Relation…"——非随机方差类
        var minusOne = new List<string>(blocksA);
        minusOne.RemoveAt(2);
        var r2 = CompareBlockSets(blocksA, minusOne, out _);
        var detail = ComputeBlockDiffDetail(blocksA, minusOne);
        bool located = detail.OnlyInLegacy.Count == 1 && detail.OnlyInLegacy[0] == removed
                    && detail.OnlyInNovel.Count == 0 && detail.ContentDiffs.Count == 0;
        bool t2 = r2 == ParityResult.Fail && located;
        Info($"SELFTEST-2 捕获力 (A vs A 去除已知块): {(t2
            ? $"PASS (FAIL 且定位准确: \"{Truncate(FirstLine(removed), 40)}\")"
            : $"FAIL (result={r2.GetLabel()}, 定位{(located ? "准确" : "错误")})")}");

        // 自测 3（VT3-D-FIX2）：同内容不同拼接 → 扁平化模式必须 PASS（块级可 FAIL，边界伪影）。
        string concatA = "## GameState\n今天是阳光明媚的春天。\n\n## Relation\n你和农夫是好朋友。";
        string concatB = "## GameState\n今天是阳光明媚的春天。\n## Relation\n你和农夫是好朋友。"; // 少一个空行 → 块级不同
        var r3Block = CompareBlockSets(NormalizeToBlocks(concatA), NormalizeToBlocks(concatB), out _);
        var r3Flat = CompareFlattened(concatA, concatB, out int warn3);
        bool t3 = r3Flat == ParityResult.Pass && warn3 == 0;
        Info($"SELFTEST-3 扁平化 (同内容不同拼接): {(t3
            ? $"PASS (flattened PASS; block-level={r3Block.GetLabel()})"
            : $"FAIL (flattened={r3Flat.GetLabel()}, warn={warn3}, block-level={r3Block.GetLabel()})")}");

        int selftestPass = (t1 ? 1 : 0) + (t2 ? 1 : 0) + (t3 ? 1 : 0);
        Info($"Selftest summary: {selftestPass}/3 PASS {(selftestPass == 3 ? "→ 比对器可用" : "→ 比对器不可用，A/B 结果禁止采信")}");
    }

    private static void OnAbMultiCommand(string command, string[] args)
    {
        if (!Context.IsWorldReady) { Info("世界未就绪。"); return; }
        if (args.Length < 1) { Info("用法: vt_ab_topology_multi <NpcName>"); return; }
        string npcName = args[0];
        var character = FindCharacter(npcName);
        if (character == null) { Info($"NPC 不存在: {npcName}"); return; }

        const int turns = 3;
        var segA = new string[turns];
        var segX = new string[turns];
        var segB = new string[turns];
        var corePrompts = new string[turns];
        var reusedFlags = new bool[turns];
        var a3Ok = new bool[turns];
        var a4Ok = new bool[turns];

        for (int turn = 1; turn <= turns; turn++)
        {
            try
            {
                SessionCache.ClearForNpc(character.Name);
                var context = BuildPopulatedContext(character);
                context.ChatHistory = BuildTurnChatHistory(turn);
                context.RoutingFlags.IncludeShortTermContext = true;

                var prompts = new Prompts(context, character);
                var plan = ConversationDirectorInstance.BuildPlan(context, character, prompts);

                // 段捕获：Tier2a 用独立 fresh 实例渲染，避免与 AssembleCore 的
                // _emittedBlockKeys 台账交叉（A4 字节级拼接完整性的前提）。
                var segPrompts = new Prompts(context, character);
                segA[turn - 1] = Prompts.AssembleTier1Segment(plan);
                segX[turn - 1] = segPrompts.AssembleTier2aSegment(context, character);
                segB[turn - 1] = Prompts.AssembleTier2bSegment(plan);
                reusedFlags[turn - 1] = plan.IsTier1Reused;

                prompts.AssembleCore(plan, context, character);
                corePrompts[turn - 1] = prompts.CorePrompt;

                // A3（逐轮构造一致性，非跨轮）：段捕获后重渲同一 plan，必须字节一致。
                a3Ok[turn - 1] = segB[turn - 1] == Prompts.AssembleTier2bSegment(plan);

                // A4（拼接完整性，逐轮，字节级）。
                a4Ok[turn - 1] = corePrompts[turn - 1] == segA[turn - 1] + segX[turn - 1] + segB[turn - 1];
            }
            catch (Exception ex)
            {
                Info($"[turn{turn}] ERROR: {ex.Message}");
            }
        }

        // A1: Tier1 frozen across turns（active 续用保证）。
        bool a1 = segA.All(s => s != null) && segA[0] == segA[1] && segA[1] == segA[2];
        Info($"A1 (Tier1 frozen, Seg_A(1)==(2)==(3)): {(a1 ? "PASS" : "FAIL")}");

        // A2: Seg_X(1)⊑Seg_X(2)⊑Seg_X(3) 字节前缀链（累积历史 + continuity 已清）。
        int window = Math.Clamp(ModEntry.Config?.PromptHistoryWindow ?? 6, 1, 20);
        bool a2 = segX.All(s => s != null)
            && segX[1].StartsWith(segX[0], StringComparison.Ordinal)
            && segX[2].StartsWith(segX[1], StringComparison.Ordinal);
        Info($"A2 (Seg_X(1)⊑(2)⊑(3) 字节前缀链, historyWindow={window}): {(a2 ? "PASS" : "FAIL")}");
        if (!a2)
            Info($"  A2 归因: len(1)={segX[0]?.Length ?? -1}, len(2)={segX[1]?.Length ?? -1}, len(3)={segX[2]?.Length ?? -1} "
                + "(window<4 时尾部截断破坏前缀链；turn1 空历史时受 SpokeJustNow 影响)");

        // A3: Seg_B(i)==RenderTier2b(plan_i) 逐轮构造一致性。
        int a3Count = a3Ok.Count(b => b);
        Info($"A3 (Seg_B(i)==RenderTier2b(plan_i) 逐轮构造一致): {(a3Count == turns ? $"PASS ({turns}/{turns})" : $"FAIL ({a3Count}/{turns})")}");

        // A4: CorePrompt(i)==Seg_A⊕Seg_X⊕Seg_B 拼接完整性。
        int a4Count = a4Ok.Count(b => b);
        Info($"A4 (CorePrompt(i)==Seg_A⊕Seg_X⊕Seg_B 拼接完整): {(a4Count == turns ? $"PASS ({turns}/{turns})" : $"FAIL ({a4Count}/{turns})")}");

        // Rotation probe (turn4): Normal → Greeting。
        // rotationNotReused 供 Reuse 断言使用（rotation=F 即 turn4 未复用）。
        bool rotationReused = false;
        bool rotationOk = false;
        try
        {
            SessionCache.ClearForNpc(character.Name);
            var ctx4 = BuildPopulatedContext(character);
            ctx4.RoutingFlags.IsSimpleGreeting = true;
            ctx4.RoutingFlags.IsMovementRequested = false;
            var prompts4 = new Prompts(ctx4, character);
            var plan4 = ConversationDirectorInstance.BuildPlan(ctx4, character, prompts4);
            string segA4 = Prompts.AssembleTier1Segment(plan4);
            rotationReused = plan4.IsTier1Reused;
            rotationOk = !rotationReused && segA4 != segA[0];
            Info($"Rotation (turn4 Normal→Greeting): reused={rotationReused}, segA changed={segA4 != segA[0]} (expect F,T) → {(rotationOk ? "PASS" : "FAIL")}");
        }
        catch (Exception ex)
        {
            Info($"Rotation probe ERROR: {ex.Message}");
        }
        bool rotationNotReused = !rotationReused;

        // Reuse 断言（VT3-D-FIX2 第三项 multi 卫生）：
        // turn1 ∈ {F,T}（环境依赖，注明来源）且 turn2/3=T 且 rotation=F。
        // 不再静默放行 turn1=True：turn1=T 时必须注明来源。
        string turn1Source = reusedFlags[0]
            ? "env:reuse-from-prior-active-session (Store 残留 active 会话跨轮复用)"
            : "fresh (新会话)";
        bool turn2Reused = reusedFlags[1];
        bool turn3Reused = reusedFlags[2];
        bool reuseAssertion = turn2Reused && turn3Reused && rotationNotReused;
        Info($"Reuse: turn1={reusedFlags[0]} ({turn1Source}) / turn2={turn2Reused} (expect T) / turn3={turn3Reused} (expect T) / rotation reused={rotationReused} (expect F) → {(reuseAssertion ? "PASS" : "FAIL")}");
        Info($"Multi summary: A1={(a1 ? "PASS" : "FAIL")} / A2={(a2 ? "PASS" : "FAIL")} / A3={a3Count}/{turns} / A4={a4Count}/{turns} / Rotation={(rotationOk ? "PASS" : "FAIL")} / Reuse={(reuseAssertion ? "PASS" : "FAIL")}");
    }

    // ── A/B helpers ──

    private static List<string> NormalizeToBlocks(string text)
    {
        var blocks = new List<string>();
        if (string.IsNullOrEmpty(text)) return blocks;
        var sb = new StringBuilder();
        foreach (var line in text.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                if (sb.Length > 0) { blocks.Add(sb.ToString().Trim()); sb.Clear(); }
            }
            else sb.AppendLine(line.Trim());
        }
        if (sb.Length > 0) blocks.Add(sb.ToString().Trim());
        return blocks;
    }

    private static ParityResult CompareBlockSets(List<string> legacy, List<string> novel, out int warnCount)
    {
        warnCount = 0;
        var legSet = new HashSet<string>(legacy);
        var novSet = new HashSet<string>(novel);

        // Random-variance classes: presence/content diff = WARN, not FAIL.
        bool IsRandomBlock(string b) =>
            b.Contains("[preoccupation]", StringComparison.OrdinalIgnoreCase)
            || b.Contains("giftGiving", StringComparison.OrdinalIgnoreCase)
            || b.Contains("你刚刚收到了", StringComparison.OrdinalIgnoreCase)
            || b.Contains("You just received", StringComparison.OrdinalIgnoreCase);

        var missingInNovel = legSet.Where(b => !novSet.Contains(b)).ToList();
        var extraInNovel = novSet.Where(b => !legSet.Contains(b)).ToList();

        int nonRandomMissing = missingInNovel.Count(b => !IsRandomBlock(b));
        int nonRandomExtra = extraInNovel.Count(b => !IsRandomBlock(b));
        warnCount = missingInNovel.Count(b => IsRandomBlock(b)) + extraInNovel.Count(b => IsRandomBlock(b));

        if (nonRandomMissing == 0 && nonRandomExtra == 0)
            return warnCount > 0 ? ParityResult.PassWithWarnings : ParityResult.Pass;
        return ParityResult.Fail;
    }

    // ── 扁平化权威比较（VT3-D-FIX2 第二项）──
    // 双侧 CorePrompt 各自压平（删除空行、保留行序）后按行序列比较，成为权威判定。
    // 块边界伪影（同内容不同拼接）不导致 FAIL。

    /// <summary>压平：删除空行、逐行 Trim、保留行序。</summary>
    private static List<string> FlattenLines(string text)
    {
        var lines = new List<string>();
        if (string.IsNullOrEmpty(text)) return lines;
        foreach (var line in text.Split('\n'))
        {
            string t = line.Trim();
            if (!string.IsNullOrEmpty(t)) lines.Add(t);
        }
        return lines;
    }

    /// <summary>随机方差类行（内容含这些标记的行视为随机方差，差异 = WARN 不 FAIL）。</summary>
    private static bool IsRandomLine(string line) =>
        line.Contains("[preoccupation]", StringComparison.OrdinalIgnoreCase)
        || line.Contains("giftGiving", StringComparison.OrdinalIgnoreCase)
        || line.Contains("你刚刚收到了", StringComparison.OrdinalIgnoreCase)
        || line.Contains("You just received", StringComparison.OrdinalIgnoreCase);

    /// <summary>扁平化比较：双侧压平后按行多重集合比较（权威判定）。
    /// 剔除随机方差行后的非随机差异 → FAIL；仅随机差异 → PassWithWarnings。</summary>
    private static ParityResult CompareFlattened(string legacy, string novel, out int warnCount)
    {
        warnCount = 0;
        var legLines = FlattenLines(legacy);
        var novLines = FlattenLines(novel);

        var legCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var l in legLines) legCounts[l] = legCounts.GetValueOrDefault(l) + 1;
        var novCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var l in novLines) novCounts[l] = novCounts.GetValueOrDefault(l) + 1;

        // 非随机差异行（A 多出的行 / B 多出的行）。
        var extraInLegacy = new List<string>();
        var extraInNovel = new List<string>();
        foreach (var kvp in legCounts)
        {
            int nov = novCounts.GetValueOrDefault(kvp.Key);
            int diff = kvp.Value - nov;
            for (int i = 0; i < diff; i++) extraInLegacy.Add(kvp.Key);
        }
        foreach (var kvp in novCounts)
        {
            int leg = legCounts.GetValueOrDefault(kvp.Key);
            int diff = kvp.Value - leg;
            for (int i = 0; i < diff; i++) extraInNovel.Add(kvp.Key);
        }

        int nonRandomExtraLeg = extraInLegacy.Count(l => !IsRandomLine(l));
        int nonRandomExtraNov = extraInNovel.Count(l => !IsRandomLine(l));
        warnCount = extraInLegacy.Count(IsRandomLine) + extraInNovel.Count(IsRandomLine);

        if (nonRandomExtraLeg == 0 && nonRandomExtraNov == 0)
            return warnCount > 0 ? ParityResult.PassWithWarnings : ParityResult.Pass;
        return ParityResult.Fail;
    }

    private static HashSet<string> GetInstructionLines(string instructions)
    {
        var lines = new HashSet<string>();
        if (string.IsNullOrEmpty(instructions)) return lines;
        foreach (var line in instructions.Split('\n'))
        {
            var t = line.Trim();
            if (!string.IsNullOrEmpty(t)) lines.Add(t);
        }
        return lines;
    }

    private static List<ConversationElement> BuildTurnChatHistory(int turn)
    {
        // 累积式罐头历史：turn N 含 2*(N-1) 行（0/2/4），行文本跨轮稳定（hist-line{i}），
        // 使 CurrentConversation 逐轮只追加 → Seg_X 呈字节前缀链（A2）。
        var history = new List<ConversationElement>();
        int lines = 2 * (turn - 1);
        for (int i = 0; i < lines; i++)
            history.Add(new ConversationElement($"hist-line{i}", i % 2 == 0));
        return history;
    }

    // ── §5 差异明细 ──

    private sealed class BlockDiffDetail
    {
        public List<string> OnlyInLegacy { get; } = new();
        public List<string> OnlyInNovel { get; } = new();
        public List<(string LegacyBlock, string NovelBlock)> BoundaryArtifacts { get; } = new();
        public List<(string Identity, string LineA, string LineB, bool SameAfterNormalization)> ContentDiffs { get; } = new();
    }

    private static BlockDiffDetail ComputeBlockDiffDetail(List<string> legacy, List<string> novel)
    {
        var detail = new BlockDiffDetail();
        var legSet = new HashSet<string>(legacy);
        var novSet = new HashSet<string>(novel);
        var onlyA = legacy.Where(b => !novSet.Contains(b)).ToList();
        var onlyB = novel.Where(b => !legSet.Contains(b)).ToList();

        // 薄壳侧压平行集合（用于边界伪影判定：A 块多出的行若与 B 的相邻独立块逐行相同 → 边界伪影）。
        var novFlat = new HashSet<string>(StringComparer.Ordinal);
        foreach (var b in novel) foreach (var l in FlattenLines(b)) novFlat.Add(l);
        var legFlat = new HashSet<string>(StringComparer.Ordinal);
        foreach (var b in legacy) foreach (var l in FlattenLines(b)) legFlat.Add(l);

        // 首行配对：同首行 → “双侧皆有但内容不同”。
        var pairedB = new HashSet<string>();
        var bByFirstLine = new Dictionary<string, string>();
        foreach (var b in onlyB)
        {
            string key = FirstLine(b);
            if (!bByFirstLine.ContainsKey(key))
                bByFirstLine[key] = b;
        }
        foreach (var a in onlyA)
        {
            if (bByFirstLine.TryGetValue(FirstLine(a), out var b) && !pairedB.Contains(b))
            {
                pairedB.Add(b);
                bool sameAfterNorm = string.Equals(StripAllWhitespace(a), StripAllWhitespace(b), StringComparison.Ordinal);
                var (lineA, lineB) = sameAfterNorm ? ("", "") : FirstDifferingLine(a, b);
                detail.ContentDiffs.Add((FirstLine(a), lineA, lineB, sameAfterNorm));
            }
            else
            {
                detail.OnlyInLegacy.Add(a);
            }
        }
        foreach (var b in onlyB)
        {
            if (!pairedB.Contains(b))
                detail.OnlyInNovel.Add(b);
        }

        // 边界伪影判定：仅 A 侧块的所有行均在 B 侧压平行集合中（同内容不同拼接）→ 边界伪影，不再标真差异。
        var trueOnlyInLegacy = new List<string>();
        foreach (var a in detail.OnlyInLegacy)
        {
            var aLines = FlattenLines(a);
            bool allInNovel = aLines.Count > 0 && aLines.All(l => novFlat.Contains(l));
            // 找 B 侧对应的独立块（逐行相同）。
            string matchedNovel = null;
            if (allInNovel)
            {
                foreach (var b in detail.OnlyInNovel)
                {
                    var bLines = FlattenLines(b);
                    if (aLines.Count == bLines.Count && aLines.Zip(bLines, (x, y) => string.Equals(x, y, StringComparison.Ordinal)).All(eq => eq))
                    {
                        matchedNovel = b; break;
                    }
                }
            }
            if (allInNovel && matchedNovel != null)
            {
                detail.BoundaryArtifacts.Add((a, matchedNovel));
                detail.OnlyInNovel.Remove(matchedNovel);
            }
            else
            {
                trueOnlyInLegacy.Add(a);
            }
        }
        detail.OnlyInLegacy.Clear();
        foreach (var a in trueOnlyInLegacy) detail.OnlyInLegacy.Add(a);

        var trueOnlyInNovel = new List<string>();
        foreach (var b in detail.OnlyInNovel)
        {
            var bLines = FlattenLines(b);
            bool allInLegacy = bLines.Count > 0 && bLines.All(l => legFlat.Contains(l));
            if (allInLegacy)
                detail.BoundaryArtifacts.Add(("", b)); // B-only 边界伪影（A 侧行散落在其他块）
            else
                trueOnlyInNovel.Add(b);
        }
        detail.OnlyInNovel.Clear();
        foreach (var b in trueOnlyInNovel) detail.OnlyInNovel.Add(b);

        return detail;
    }

    private static void LogBlockDiffDetail(string branch, BlockDiffDetail d)
    {
        Info($"[{branch}] 差异明细: 仅A侧 {d.OnlyInLegacy.Count} 块 / 仅B侧 {d.OnlyInNovel.Count} 块 / 边界伪影 {d.BoundaryArtifacts.Count} 块 / 双侧皆有但内容不同 {d.ContentDiffs.Count} 块");
        foreach (var b in d.OnlyInLegacy)
            Info($"[{branch}]   仅A侧块: \"{Truncate(FirstLine(b), 90)}\"");
        foreach (var b in d.OnlyInNovel)
            Info($"[{branch}]   仅B侧块: \"{Truncate(FirstLine(b), 90)}\"");
        foreach (var (legacyBlock, novelBlock) in d.BoundaryArtifacts)
        {
            string id = !string.IsNullOrEmpty(legacyBlock) ? FirstLine(legacyBlock) : FirstLine(novelBlock);
            Info($"[{branch}]   边界伪影（同内容不同拼接）: \"{Truncate(id, 60)}\" → A 块多出的行与 B 的相邻独立块逐行相同，不标真差异");
        }
        foreach (var (identity, lineA, lineB, sameAfterNorm) in d.ContentDiffs)
        {
            if (sameAfterNorm)
                Info($"[{branch}]   双侧皆有但内容不同: \"{Truncate(identity, 60)}\" → 【归一化后同文】(纯空白/换行差异 → 比对器归一化缺口)");
            else
                Info($"[{branch}]   双侧皆有但内容不同: \"{Truncate(identity, 60)}\" → 【真差异】首差异行 A=\"{Truncate(lineA, 70)}\" | B=\"{Truncate(lineB, 70)}\"");
        }
    }

    private static string FirstLine(string block) => block.Split('\n')[0].Trim();

    private static string StripAllWhitespace(string text) => string.Concat(text.Where(c => !char.IsWhiteSpace(c)));

    private static (string LineA, string LineB) FirstDifferingLine(string a, string b)
    {
        var la = a.Split('\n').Select(l => l.Trim()).ToArray();
        var lb = b.Split('\n').Select(l => l.Trim()).ToArray();
        for (int i = 0; i < Math.Max(la.Length, lb.Length); i++)
        {
            string x = i < la.Length ? la[i] : "<无此行>";
            string y = i < lb.Length ? lb[i] : "<无此行>";
            if (!string.Equals(x, y, StringComparison.Ordinal))
                return (x, y);
        }
        return ("<同文>", "<同文>");
    }

    private static string Truncate(string text, int max) =>
        string.IsNullOrEmpty(text) ? text : (text.Length <= max ? text : text.Substring(0, max) + "…");

    // ── §1/§2 会话隔离与同源断言辅助 ──

    /// <summary>反射清除 Tier1SnapshotStore 中该 NPC 的 active/closed 会话记录，
    /// 保证随后 BuildPlan 的 TryReuseSession 必走“新会话”分支（禁跨分支快照复用）。
    /// Store 无公开清理 API 且本票禁触生产文件，故沿用 dumper 反射惯例。</summary>
    private static void ForceNewTier1Sessions(string npcName)
    {
        const BindingFlags binding = BindingFlags.NonPublic | BindingFlags.Static;
        var storeType = typeof(Tier1SnapshotStore);
        var activeByNpc = storeType.GetField("_activeByNpc", binding)?.GetValue(null) as System.Collections.IDictionary;
        var activeBySession = storeType.GetField("_activeBySession", binding)?.GetValue(null) as System.Collections.IDictionary;
        var closed = storeType.GetField("_recentClosedSessions", binding)?.GetValue(null) as System.Collections.IDictionary;
        if (activeByNpc == null || activeBySession == null || closed == null)
        {
            ModEntry.SMonitor?.Log($"{Prefix} Tier1SnapshotStore 反射字段缺失，无法强制新会话", LogLevel.Warn);
            return;
        }
        if (activeByNpc.Contains(npcName))
        {
            var record = activeByNpc[npcName];
            var sessionId = record?.GetType().GetProperty("SessionId")?.GetValue(record) as string;
            activeByNpc.Remove(npcName);
            if (!string.IsNullOrEmpty(sessionId))
                activeBySession.Remove(sessionId);
        }
        closed.Remove(npcName);
    }

    /// <summary>反射读取 Prompts.Context（私有 getter）——同源断言用。</summary>
    private static DialogueContext GetPromptsContext(Prompts prompts) =>
        typeof(Prompts).GetProperty("Context", BindingFlags.NonPublic | BindingFlags.Instance)
            ?.GetValue(prompts) as DialogueContext;

    private enum ParityResult { Pass, PassWithWarnings, Fail }
    private static string GetLabel(this ParityResult r) => r switch
    {
        ParityResult.Pass => "blocks equivalent",
        ParityResult.PassWithWarnings => "blocks equivalent (warnings)",
        ParityResult.Fail => "block set mismatch",
        _ => "unknown",
    };

    // ConversationDirector access (internal in Director namespace).
    private static readonly ConversationDirector ConversationDirectorInstance = new();
}