using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using StardewModdingAPI;
using StardewValley;

namespace ValleytalkReborn;

/// <summary>
/// 情绪系统调试命令（VT-EMO-05）。
/// SMAPI 控制台命令在非主线程触发——所有游戏态读写经 MainThreadDispatcher
/// 封送至主线程执行；纯 Memory 操作，不写 modData、不写存档、不同步联机。
/// </summary>
internal static class EmotionDebugCommands
{
    private const string Prefix = "[vt_emotion]";

    // MoodShockStore 无公开枚举 API（01 契约未含，且本单禁改该文件），
    // 调试层以反射只读，与存储层既有锁对象同锁；仅主线程调用（经封送）。
    private static readonly FieldInfo ShocksField =
        typeof(MoodShockStore).GetField("_shocks", BindingFlags.NonPublic | BindingFlags.Static);

    private static readonly FieldInfo LockField =
        typeof(MoodShockStore).GetField("_lock", BindingFlags.NonPublic | BindingFlags.Static);

    public static void Register(ICommandHelper console)
    {
        console.Add("vt_emotion",
            "情绪系统调试。用法: vt_emotion <dump|force_scene|add_shock|clear> <npc> [...]",
            OnCommand);
    }

    private static void OnCommand(string command, string[] args)
    {
        if (!Context.IsWorldReady)
        {
            Info("需先进入存档后再使用。");
            return;
        }

        if (args.Length < 2)
        {
            Info("用法: vt_emotion <dump|force_scene|add_shock|clear> <npc> [参数...]");
            return;
        }

        switch (args[0].ToLowerInvariant())
        {
            case "dump":
                Dump(args[1]);
                break;
            case "force_scene":
                ForceScene(args);
                break;
            case "add_shock":
                AddShock(args);
                break;
            case "clear":
                Clear(args[1]);
                break;
            default:
                Info($"未知子命令: {args[0]}。用法: vt_emotion <dump|force_scene|add_shock|clear> <npc> [参数...]");
                break;
        }
    }

    private static async void Dump(string npcName)
    {
        try
        {
            await MainThreadDispatcher.RunOnMainThreadAsync(() =>
            {
                var character = FindCharacter(npcName);
                if (character == null)
                {
                    Info($"{npcName} not found（未加载或不存在）。");
                    return;
                }

                int now = MoodShockStore.NowProvider();
                Info($"=== {character.Name} 情绪状态 ===");
                Info($"DailyInitStamp: {character.DailyInitStamp}（今日: {Game1.Date.TotalDays}）");
                Info($"CurrentTodayScene: {character.CurrentTodayScene?.Id ?? "null"}");
                Info($"NeglectDampenedToday: {character.NeglectDampenedToday}");
                Info($"FeedbackTriggeredToday: {character.FeedbackTriggeredToday}");
                Info($"EmotionNarrationDoneToday: {character.EmotionNarrationDoneToday}");

                Info($"CountShocks: {MoodShockStore.CountShocks(character.Name)}");
                foreach (var (sourceId, ratio) in EnumerateShocks(character.Name, now))
                    Info($"  Shock: {sourceId} | 当前 Ratio: {ratio:F3}");

                var snapshot = EmotionalStateResolver.PrepareSnapshot(character, character.StardewNpc);
                Info(
                    $"Snapshot: V={snapshot.Valence:F2} A={snapshot.Arousal:F2} " +
                    $"O={snapshot.Openness:F2} BaselineO={snapshot.BaselineOpenness:F2}");
            });
        }
        catch (Exception ex)
        {
            Info($"dump 执行失败: {ex.Message}");
        }
    }

    private static async void ForceScene(string[] args)
    {
        if (args.Length < 3)
        {
            Info("用法: vt_emotion force_scene <npc> <sceneId>");
            return;
        }
        string npcName = args[1];
        string sceneId = args[2];

        try
        {
            await MainThreadDispatcher.RunOnMainThreadAsync(() =>
            {
                var character = FindCharacter(npcName);
                if (character == null)
                {
                    Info($"{npcName} not found（未加载或不存在）。");
                    return;
                }

                var scenes = character.Bio?.TodayScenes;
                if (scenes == null || scenes.Count == 0)
                {
                    Info($"{character.Name} 无可用 TodayScenes（Bio 未配置场景）。");
                    return;
                }

                var target = scenes.FirstOrDefault(
                    s => string.Equals(s.Id, sceneId, StringComparison.OrdinalIgnoreCase));
                if (target == null)
                {
                    Info($"sceneId '{sceneId}' 不存在。可用 Id: {string.Join(", ", scenes.Select(s => s.Id))}");
                    return;
                }

                character.CurrentTodayScene = target;
                character.DailyInitStamp = Game1.Date.TotalDays; // 防止 EnsureDaily 同日覆盖
                Info($"已强制场景: {target.Id}（Tag: {target.Tag}），DailyInitStamp 已置为今日。");
            });
        }
        catch (Exception ex)
        {
            Info($"force_scene 执行失败: {ex.Message}");
        }
    }

    private static async void AddShock(string[] args)
    {
        if (args.Length < 6
            || !TryParseFloat(args[2], out float dv)
            || !TryParseFloat(args[3], out float da)
            || !TryParseFloat(args[4], out float dOpen)
            || !int.TryParse(args[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out int minutes))
        {
            Info("用法: vt_emotion add_shock <npc> <dv> <da> <dOpen> <minutes>  例: vt_emotion add_shock Abigail -0.5 0 0 60");
            return;
        }
        string npcName = args[1];

        try
        {
            await MainThreadDispatcher.RunOnMainThreadAsync(() =>
            {
                var character = FindCharacter(npcName);
                if (character == null)
                {
                    Info($"{npcName} not found（未加载或不存在）。");
                    return;
                }

                MoodShockStore.AddShock(
                    character.Name, EmotionShockIds.Debug(character.Name), dv, da, dOpen,
                    durationMinutes: minutes, persistAcrossDays: false);
                Info($"已注入 Debug Shock（{character.Name}）: dv={dv} da={da} dOpen={dOpen} minutes={minutes}");
            });
        }
        catch (Exception ex)
        {
            Info($"add_shock 执行失败: {ex.Message}");
        }
    }

    private static async void Clear(string npcName)
    {
        try
        {
            await MainThreadDispatcher.RunOnMainThreadAsync(() =>
            {
                var character = FindCharacter(npcName);
                if (character == null)
                {
                    Info($"{npcName} not found（未加载或不存在）。");
                    return;
                }

                MoodShockStore.ClearNpc(character.Name);
                Info($"已清除 {character.Name} 的全部 Shock。");
            });
        }
        catch (Exception ex)
        {
            Info($"clear 执行失败: {ex.Message}");
        }
    }

    private static Character FindCharacter(string npcName)
    {
        var exact = DialogueBuilder.Instance.GetCharacterByName(npcName);
        if (exact != null) return exact;
        return DialogueBuilder.Instance.GetAllLoadedCharacters()
            .FirstOrDefault(c => string.Equals(c.Name, npcName, StringComparison.OrdinalIgnoreCase));
    }

    private static List<(string SourceId, float Ratio)> EnumerateShocks(string npcName, int nowMinutes)
    {
        var result = new List<(string, float)>();
        try
        {
            var dict = ShocksField?.GetValue(null) as Dictionary<string, List<MoodShock>>;
            var lockObj = LockField?.GetValue(null);
            if (dict == null || lockObj == null)
            {
                ModEntry.SMonitor?.Log($"{Prefix} Shock 枚举反射目标缺失，跳过。", LogLevel.Warn);
                return result;
            }

            lock (lockObj)
            {
                if (dict.TryGetValue(npcName, out var list))
                {
                    foreach (var shock in list)
                        result.Add((shock.SourceId, shock.GetCurrentRatio(nowMinutes)));
                }
            }
        }
        catch (Exception ex)
        {
            ModEntry.SMonitor?.Log($"{Prefix} Shock 枚举失败: {ex.Message}", LogLevel.Warn);
        }
        return result;
    }

    private static bool TryParseFloat(string s, out float value) =>
        float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static void Info(string message) =>
        ModEntry.SMonitor?.Log($"{Prefix} {message}", LogLevel.Info);
}
