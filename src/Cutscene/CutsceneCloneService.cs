using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using ValleytalkReborn.Cutscene.Storage;

namespace ValleytalkReborn.Cutscene
{
    /// <summary>
    /// 回放克隆演员服务：依据归档站位以真人 NPC 为原型构造临时克隆演员入场景落位，
    /// 真人本体零接触（不摆位、不动日程、不入快照）。
    /// SceneEnded 幕后摘除全部克隆；DayStarted/SaveLoaded 扫描清除意外残留
    /// （克隆经 modData 标记键识别，标记键是存档污染的唯一恢复锚点）。
    /// </summary>
    internal static class CutsceneCloneService
    {
        /// <summary>克隆演员标记键：写入 modData 即视为临时克隆，绝不落业务键</summary>
        internal const string CloneMarkerKey = "ValleytalkReborn.EphemeralCutsceneActor";

        /// <summary>当前存活克隆注册表（Memory 瞬态，仅"回放受理 → SceneEnded"窗口内有条目）</summary>
        private static readonly List<NPC> _activeClones = new();

        static CutsceneCloneService()
        {
            // 订阅关系在静态构造器建立：场景终止即幕后摘除全部克隆（单例语义，与导演同生命周期）
            VirtualDirector.Instance.SceneEnded += () => DisposeAll();
        }

        /// <summary>
        /// 判定 NPC 是否为本服务的临时克隆演员（modData 标记键存在即真）
        /// </summary>
        internal static bool IsClone(NPC npc)
            => npc != null && npc.modData.ContainsKey(CloneMarkerKey);

        /// <summary>
        /// 依据归档站位在录制现场构造克隆演员并落位（调用点保证已身处录制地图）。
        /// 单个克隆构造失败仅跳过该演员（RECOVERABLE）；全部失败时调用方以
        /// "无法召集任何录制演员" 拒绝开演。
        /// </summary>
        internal static List<NPC> StageClones(ArchivedCutscene cutscene, GameLocation stage)
        {
            var clones = new List<NPC>();
            if (cutscene == null || stage == null)
                return clones;

            // 站位来源：归档站位（按名字去重，首个为准）
            var stances = cutscene.ActorStances?
                .Where(s => s != null && !string.IsNullOrWhiteSpace(s.Name))
                .GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList() ?? new List<ArchivedActorStance>();

            foreach (var stance in stances)
            {
                try
                {
                    NPC prototype = Game1.getCharacterFromName(stance.Name);
                    if (prototype == null)
                    {
                        ModEntry.SMonitor?.Log(
                            $"[CutsceneClone] Prototype '{stance.Name}' not found in world, skipped.",
                            LogLevel.Warn);
                        continue;
                    }

                    // 红线：强制换算到可通行格子（探针用原型：pathfinding 语义下角色互撞豁免，
                    // 探针仅影响地形/家具判定，且 null 探针会被 isCollidingPosition 判为碰撞）
                    var stanceTile = new Vector2(stance.TileX, stance.TileY);
                    Vector2 safeTile = MovementPathfinding.FindNearestWalkableTile(stage, stanceTile, prototype, radius: 5);

                    // 克隆须持独立 AnimatedSprite 实例（禁止共享），按原型纹理名重建精灵
                    var sprite = new AnimatedSprite(prototype.Sprite.textureName.Value);
                    var clone = new NPC(sprite, safeTile * 64f, Math.Clamp(stance.Facing, 0, 3), stance.Name, Game1.content);
                    clone.displayName = prototype.displayName;
                    clone.modData[CloneMarkerKey] = "1";

                    stage.addCharacter(clone);
                    clone.currentLocation = stage;

                    clones.Add(clone);
                }
                catch (Exception ex)
                {
                    // RECOVERABLE：单个克隆构造失败仅跳过该演员，其余继续
                    ModEntry.SMonitor?.Log(
                        $"[CutsceneClone] Failed to stage clone for '{stance.Name}': {ex.Message}",
                        LogLevel.Warn);
                }
            }

            _activeClones.AddRange(clones);

            ModEntry.SMonitor?.Log(
                $"[CutsceneClone] Staged {clones.Count} clone actors in {stage.NameOrUniqueName}.",
                LogLevel.Info);
            return clones;
        }

        /// <summary>
        /// 幕后摘除全部克隆演员（SceneEnded 触发）：移出场景、断开位置引用并清空注册表。
        /// 无存活克隆时静默返回（真人偶遇场景同样会触发 SceneEnded，不得刷日志）。
        /// </summary>
        internal static void DisposeAll()
        {
            if (_activeClones.Count == 0)
                return;

            int count = _activeClones.Count;
            foreach (var clone in _activeClones)
            {
                try
                {
                    clone?.currentLocation?.characters.Remove(clone);
                    if (clone != null)
                        clone.currentLocation = null;
                }
                catch (Exception ex)
                {
                    // BUG 路径：逐个记录并继续清除其余克隆（可观测、可继续）
                    ModEntry.SMonitor?.Log(
                        $"[CutsceneClone] Failed to dispose clone '{clone?.Name}': {ex}",
                        LogLevel.Error);
                }
            }
            _activeClones.Clear();

            ModEntry.SMonitor?.Log($"[CutsceneClone] Cutscene clones disposed ({count}).", LogLevel.Info);
        }

        /// <summary>
        /// 存档污染恢复（DayStarted/SaveLoaded 调用）：遍历全部场景摘除带标记键的克隆残留
        /// （克隆若曾存活到存档写入，读档后凭标记键确定性清除）。
        /// </summary>
        internal static void SweepAll()
        {
            int swept = 0;
            foreach (var location in Game1.locations)
            {
                for (int i = location.characters.Count - 1; i >= 0; i--)
                {
                    NPC npc = location.characters[i];
                    if (npc != null && IsClone(npc))
                    {
                        location.characters.RemoveAt(i);
                        swept++;
                    }
                }
            }
            _activeClones.Clear();

            if (swept > 0)
            {
                ModEntry.SMonitor?.Log(
                    $"[CutsceneClone] Swept {swept} leftover clone actor(s) from world (save contamination recovery).",
                    LogLevel.Warn);
            }
        }
    }
}
