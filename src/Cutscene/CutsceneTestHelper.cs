using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using ValleytalkReborn.Cutscene.Actions;

namespace ValleytalkReborn.Cutscene
{
    /// <summary>
    /// Phase 0 测试辅助器：提供硬编码的验证剧本
    /// </summary>
    public static class CutsceneTestHelper
    {
        /// <summary>
        /// 运行 Phase 0 验证剧本："15 秒概念验证"
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
        /// 对指定 NPC 执行测试剧本
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

            // 构建测试剧本
            var script = new List<IDirectorAction>
            {
                // 第 1 步：相机缓慢对焦该 NPC
                new CameraAction(actor),

                // 第 2 步：NPC 头顶冒惊叹号
                new EmoteAction(actor, "SURPRISE"),

                // 第 3 步：NPC 自主走位至目标格子（带 6 秒超时瞬移兜底）
                new MoveToTileAction(actor, targetTile, timeoutSeconds: 6f),

                // 第 4 步：转身看向农夫
                new LookAtAction(actor, (StardewValley.Character)Game1.player),

                // 第 5 步：说一句测试台词
                new SpeakAction(actor, GetTestDialogue(actor.Name)),

                // 第 6 步：开心地冒个爱心
                new EmoteAction(actor, "HEART"),

                // 第 7 步：再说一句
                new SpeakAction(actor, "按 ESC 可随时退出过场演出！")
            };

            ModEntry.SMonitor?.Log(
                $"[CutsceneTest] Starting test cutscene with {actor.Name} at ({startTile.X},{startTile.Y}) -> ({targetTile.X},{targetTile.Y})",
                LogLevel.Info);

            VirtualDirector.Instance.Play(script, new List<NPC> { actor });
        }

        private static string GetTestDialogue(string npcName)
        {
            // 根据语言返回测试台词
            bool isChinese = LocalizedContentManager.CurrentLanguageCode == LocalizedContentManager.LanguageCode.zh;

            if (isChinese)
            {
                return $"你好！我是 {npcName}，这是虚拟导演系统的测试演出！";
            }
            else
            {
                return $"Hey! I'm {npcName}, and this is a Virtual Director test cutscene!";
            }
        }

        /// <summary>
        /// 多 NPC 协作测试剧本（Phase 0 扩展验证）
        /// </summary>
        public static void RunMultiActorTest()
        {
            if (!Context.IsWorldReady || Game1.player?.currentLocation == null)
            {
                ModEntry.SMonitor?.Log("[CutsceneTest] World not ready.", LogLevel.Warn);
                return;
            }

            var currentLoc = Game1.player.currentLocation;
            var nearbyNpcs = currentLoc.characters
                .Where(n => n != null && n.IsVillager && Vector2.Distance(n.Tile, Game1.player.Tile) < 12f)
                .Take(2)
                .ToList();

            if (nearbyNpcs.Count < 2)
            {
                ModEntry.SMonitor?.Log("[CutsceneTest] Need at least 2 NPCs nearby for multi-actor test.", LogLevel.Warn);
                Game1.addHUDMessage(new HUDMessage("Need 2+ NPCs nearby!", HUDMessage.error_type));
                return;
            }

            var npc1 = nearbyNpcs[0];
            var npc2 = nearbyNpcs[1];

            // 让两个 NPC 互相看向对方并交流
            var script = new List<IDirectorAction>
            {
                new CameraAction(npc1),
                new EmoteAction(npc1, "SURPRISE"),
                new LookAtAction(npc1, (StardewValley.Character)npc2),
                new SpeakAction(npc1, $"嘿，{npc2.Name}！"),
                
                new CameraAction(npc2),
                new LookAtAction(npc2, (StardewValley.Character)npc1),
                new EmoteAction(npc2, "QUESTION"),
                new SpeakAction(npc2, $"怎么了，{npc1.Name}？"),
                
                new CameraAction(npc1),
                new EmoteAction(npc1, "HEART"),
                new SpeakAction(npc1, "虚拟导演系统运行正常！"),
            };

            ModEntry.SMonitor?.Log(
                $"[CutsceneTest] Starting multi-actor test with {npc1.Name} and {npc2.Name}",
                LogLevel.Info);

            VirtualDirector.Instance.Play(script, new List<NPC> { npc1, npc2 });
        }
    }
}
