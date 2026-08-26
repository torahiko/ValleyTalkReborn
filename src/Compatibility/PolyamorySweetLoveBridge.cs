using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using StardewModdingAPI;
using StardewValley;

namespace ValleytalkReborn;

/// <summary>
/// PolyamorySweetLove 兼容桥接类。
/// 用于检测多角恋婚姻关系，为 A2A 和单人 Bark 提供关系判断。
/// </summary>
internal static class PolyamorySweetLoveBridge
{
    private static bool _initialized = false;
    private static bool _available = false;
    private static FieldInfo _currentSpousesField = null;
    private static FieldInfo _unofficialSpousesField = null;

    internal static void TryInitialize()
    {
        if (_initialized) return;

        _initialized = true;

        try
        {
            var polyAssembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "PolyamorySweetLove");

            if (polyAssembly == null)
            {
                ModEntry.SMonitor?.Log(
                    "[PolyBridge] PolyamorySweetLove 未检测到，关系判断将使用原版逻辑。",
                    LogLevel.Debug);
                return;
            }

            var modEntryType = polyAssembly.GetType("PolyamorySweetLove.ModEntry");
            if (modEntryType == null) return;

            _currentSpousesField = modEntryType.GetField(
                "currentSpouses",
                BindingFlags.Public | BindingFlags.Static);

            _unofficialSpousesField = modEntryType.GetField(
                "currentUnofficialSpouses",
                BindingFlags.Public | BindingFlags.Static);

            if (_currentSpousesField != null && _unofficialSpousesField != null)
            {
                _available = true;

                ModEntry.SMonitor?.Log(
                    "[PolyBridge] PolyamorySweetLove 已检测到，多角恋关系判断已启用。",
                    LogLevel.Debug);
            }
        }
        catch (Exception ex)
        {
            ModEntry.SMonitor?.Log(
                $"[PolyBridge] 初始化失败（已降级）：{ex.Message}",
                LogLevel.Warn);
        }
    }

    internal static bool IsOfficialSpouse(NPC npc)
    {
        if (!_available || npc == null || Game1.player == null)
            return false;

        try
        {
            var dict = _currentSpousesField.GetValue(null)
                as Dictionary<long, Dictionary<string, NPC>>;

            if (dict == null) return false;

            long farmerId = Game1.player.UniqueMultiplayerID;

            return dict.TryGetValue(farmerId, out var spouses) && spouses.ContainsKey(npc.Name);
        }
        catch
        {
            return false;
        }
    }

    internal static bool IsUnofficialSpouse(NPC npc)
    {
        if (!_available || npc == null || Game1.player == null)
            return false;

        try
        {
            var dict = _unofficialSpousesField.GetValue(null)
                as Dictionary<long, Dictionary<string, NPC>>;

            if (dict == null) return false;

            long farmerId = Game1.player.UniqueMultiplayerID;

            return dict.TryGetValue(farmerId, out var spouses) && spouses.ContainsKey(npc.Name);
        }
        catch
        {
            return false;
        }
    }
}
