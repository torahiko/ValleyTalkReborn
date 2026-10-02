using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Newtonsoft.Json;
using StardewModdingAPI;
using StardewValley;
using ValleytalkReborn.Cutscene.Actions;

namespace ValleytalkReborn.Cutscene
{
    /// <summary>
    /// Phase 1 测试辅助器：提供基于 JSON IR 声明式协议与并发引擎驱动的验证剧本
    /// </summary>
    public static class CutsceneTestHelper
    {
        /// <summary>
        /// 运行 Phase 1 验证剧本："声明式 JSON 编译器与并发动作验证"
        /// 在当前地图找一个 NPC 执行测试序列
        /// </summary>
        public static void RunTestCutscene()
        {
            if (!Context.IsWorldReady || Game1.player?.currentLocation == null)
            {
                ModEntry.SMonitor?.Log("[CutsceneTest] World not ready.", LogLevel.Warn);
                return;
            }

            var currentLoc = Game1.player.currentLocation;
            
            // 找到最近的一个 NPC
            NPC targetNpc = null;
            float closestDist = float.MaxValue;

            foreach (var npc in currentLoc.characters)
            {
                if (npc == null || !npc.IsVillager) continue;
                
                float dist = Vector2.Distance(npc.Tile, Game1.player.Tile);
                if (dist < closestDist && dist < 15f) // 15 格内
                {
                    closestDist = dist;
                    targetNpc = npc;
                }
            }

            if (targetNpc == null)
            {
                ModEntry.SMonitor?.Log("[CutsceneTest] No NPC found nearby (within 15 tiles).", LogLevel.Warn);
                Game1.addHUDMessage(new HUDMessage("No NPC nearby to test cutscene!", HUDMessage.error_type));
                return;
            }

            RunTestCutsceneWithActor(targetNpc);
        }

        /// <summary>
        /// 对指定 NPC 执行测试剧本（基于 JSON IR 协议与并发驱动）
        /// </summary>
        public static void RunTestCutsceneWithActor(NPC actor)
        {
            if (actor?.currentLocation == null)
            {
                ModEntry.SMonitor?.Log("[CutsceneTest] Invalid actor.", LogLevel.Warn);
                return;
            }

            var currentLoc = actor.currentLocation;
            var startTile = actor.Tile;

            // 计算目标点：NPC 前方 3 格（根据朝向）
            Vector2 targetTile = startTile;
            switch (actor.FacingDirection)
            {
                case 0: targetTile += new Vector2(0, -3); break; // 上
                case 1: targetTile += new Vector2(3, 0); break;  // 右
                case 2: targetTile += new Vector2(0, 3); break;  // 下
                case 3: targetTile += new Vector2(-3, 0); break; // 左
            }

            // 确保目标点可通行
            targetTile = MovementPathfinding.FindNearestWalkableTile(currentLoc, targetTile, actor, radius: 5);

            bool isChinese = LocalizedContentManager.CurrentLanguageCode == LocalizedContentManager.LanguageCode.zh;
            string speak1 = isChinese
                ? $"你好！我是 {actor.Name}，现在已经完全由 JSON 编译器和并发引擎驱动了！"
                : $"Hey! I'm {actor.Name}, now fully driven by the JSON IR compiler and parallel action engine!";
            string speak2 = isChinese
                ? "按下 ESC 可以随时退出，而且动作完全不会卡死！"
                : "Press ESC anytime to exit smoothly — zero deadlocks!";

            // 构造真实的声明式 JSON 剧本（带 Markdown 围栏，验证编译器的容错剔除能力）
            string jsonScript = $@"
            ```json
            {{
                ""title"": ""{actor.Name} 的并发演练"",
                ""actors"": [""{actor.Name}""],
                ""actions"": [
                    {{ ""type"": ""camera"", ""target"": ""{actor.Name}"", ""waitForCompletion"": false }},
                    {{ ""type"": ""sound"", ""soundName"": ""dwop"", ""waitForCompletion"": false }},
                    {{ ""type"": ""emote"", ""actor"": ""{actor.Name}"", ""emote"": ""SURPRISE"", ""waitForCompletion"": false }},
                    {{ ""type"": ""move"", ""actor"": ""{actor.Name}"", ""targetTile"": [{(int)targetTile.X}, {(int)targetTile.Y}], ""timeout"": 6.0, ""waitForCompletion"": true }},
                    {{ ""type"": ""lookAt"", ""actor"": ""{actor.Name}"", ""target"": ""farmer"", ""waitForCompletion"": true }},
                    {{ ""type"": ""wait"", ""duration"": 0.5, ""waitForCompletion"": true }},
                    {{ ""type"": ""speak"", ""actor"": ""{actor.Name}"", ""text"": ""{speak1}"", ""waitForCompletion"": true }},
                    {{ ""type"": ""sound"", ""soundName"": ""coin"", ""waitForCompletion"": false }},
                    {{ ""type"": ""emote"", ""actor"": ""{actor.Name}"", ""emote"": ""HEART"", ""waitForCompletion"": false }},
                    {{ ""type"": ""speak"", ""actor"": ""{actor.Name}"", ""text"": ""{speak2}"", ""waitForCompletion"": true }}
                ]
            }}
            ```
            ";

            ModEntry.SMonitor?.Log(
                $"[CutsceneTest] Starting JSON-compiled cutscene with {actor.Name} at ({startTile.X},{startTile.Y}) -> ({targetTile.X},{targetTile.Y})",
                LogLevel.Info);

            bool success = VirtualDirector.Instance.PlayScript(jsonScript, out string error);
            if (!success)
            {
                ModEntry.SMonitor?.Log($"[CutsceneTest] PlayScript failed: {error}", LogLevel.Error);
            }
        }

        /// <summary>
        /// 运行 Phase 2 动态编剧剧本：由大语言模型根据当前场景与 NPC 人设即兴创作
        /// </summary>
        public static void RunDynamicCutscene(string userIntent = null)
        {
            if (!Context.IsWorldReady || Game1.player?.currentLocation == null)
            {
                ModEntry.SMonitor?.Log("[CutsceneTest] World not ready.", LogLevel.Warn);
                return;
            }

            var currentLoc = Game1.player.currentLocation;

            // 搜索玩家附近的村民（最多 2 名）
            var nearbyNpcs = currentLoc.characters
                .Where(n => n != null && n.IsVillager && Vector2.Distance(n.Tile, Game1.player.Tile) < 15f)
                .OrderBy(n => Vector2.Distance(n.Tile, Game1.player.Tile))
                .Take(2)
                .ToList();

            if (nearbyNpcs.Count == 0)
            {
                ModEntry.SMonitor?.Log("[CutsceneTest] No NPC found nearby (within 15 tiles).", LogLevel.Warn);
                Game1.addHUDMessage(new HUDMessage("附近 15 格内没有 NPC 可参演即兴剧目！", HUDMessage.error_type));
                return;
            }

            ModEntry.SMonitor?.Log(
                $"[CutsceneTest] Initiating dynamic cutscene with {string.Join(", ", nearbyNpcs.Select(n => n.Name))}, intent: {userIntent ?? "(default)"}",
                LogLevel.Info);

            _ = Generation.CutsceneGeneratorService.GenerateAndPlayAsync(nearbyNpcs, userIntent);
        }
    }
}
