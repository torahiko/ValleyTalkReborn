using System;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;

namespace ValleytalkReborn
{
    /// <summary>
    /// 负责 NPC 与玩家之间的直接交互行为（亲吻、表情等）。
    /// 不持有任何移动状态，移动需求通过 MovementManager 完成后回调触发。
    /// </summary>
    public static class NpcInteractionService
    {
        /// <summary>
        /// 尝试执行亲吻交互。
        /// 若 NPC 站位不对（正上方/正下方），会先通过 MovementManager 移动到侧位，
        /// 到位后再执行交互。
        /// </summary>
        /// <returns>false 表示距离或状态不满足，true 表示已触发（可能是立即执行，也可能是移动后执行）。</returns>
        public static bool TryKiss(NPC npc)
        {
            if (npc == null || Game1.player == null)
                return false;

            if (npc.IsInvisible || npc.currentLocation == null || Game1.player.currentLocation == null)
                return false;

            // 约会跟随期间不允许亲吻（由 MovementManager 的约会状态保护）
            if (MovementManager.Instance.HasActiveDateFollow &&
                MovementManager.Instance.CurrentFollowingNpc == npc)
                return false;

            var loc = npc.currentLocation;
            if (loc != Game1.player.currentLocation)
                return false;

            var playerTile = Game1.player.Tile;
            var npcTile    = npc.Tile;
            float dist     = Vector2.Distance(npcTile, playerTile);

            if (dist > 2.5f)
            {
                ModEntry.SMonitor?.Log(
                    $"[NpcInteractionService] {npc.Name} kiss rejected: too far ({dist:F2}).",
                    LogLevel.Debug);
                return false;
            }

            // 正上方/正下方：先移到侧位，再亲吻
            bool isDirectlyAboveOrBelow =
                (int)npcTile.X == (int)playerTile.X &&
                Math.Abs(npcTile.Y - playerTile.Y) <= 1;

            if (isDirectlyAboveOrBelow)
            {
                var leftTile  = new Vector2(playerTile.X - 1, playerTile.Y);
                var rightTile = new Vector2(playerTile.X + 1, playerTile.Y);

                Vector2? targetTile = null;

                if (MovementPathfinding.IsTileWalkable(loc, leftTile, npc))
                    targetTile = leftTile;
                else if (MovementPathfinding.IsTileWalkable(loc, rightTile, npc))
                    targetTile = rightTile;

                if (targetTile.HasValue)
                {
                    MovementManager.Instance.MoveToTile(
                        npc,
                        targetTile.Value,
                        onComplete: () => ExecuteKiss(npc),
                        onFail:     null);

                    return true;
                }
            }

            return ExecuteKiss(npc);
        }

        /// <summary>
        /// 直接执行亲吻交互（不做移动，假设站位已就绪）。
        /// 包含关系判断、朝向设置、音效、好感度加成、Bark 触发。
        /// </summary>
        public static bool ExecuteKiss(NPC npc)
        {
            if (npc == null || Game1.player == null)
                return false;

            var friendship = Game1.player.friendshipData.TryGetValue(npc.Name, out var f) ? f : null;

            bool isSpouse =
                friendship != null &&
                (friendship.IsMarried() || friendship.IsEngaged() || PolyBridge.IsOfficialSpouse(npc));

            bool isDatingWithHighHearts =
                friendship != null &&
                (friendship.IsDating() || PolyBridge.IsUnofficialSpouse(npc)) &&
                friendship.Points >= 2000;

            bool isSuccess = isSpouse || isDatingWithHighHearts;

            if (!isSuccess)
            {
                npc.faceGeneralDirection(Game1.player.getStandingPosition(), 0, false, false);
                npc.doEmote(8); // 问号

                DynamicBarkManager.TriggerKissReaction(npc, isSuccess: false);

                ModEntry.SMonitor?.Log(
                    $"[NpcInteractionService] {npc.Name} rejected kiss: relationship condition not met.",
                    LogLevel.Debug);

                return false;
            }

            // 双方朝向对齐
            Vector2 delta = Game1.player.Tile - npc.Tile;

            if (Math.Abs(delta.X) >= Math.Abs(delta.Y))
            {
                if (delta.X >= 0) { npc.faceDirection(1); Game1.player.faceDirection(3); }
                else              { npc.faceDirection(3); Game1.player.faceDirection(1); }
            }
            else
            {
                if (delta.Y >= 0) { npc.faceDirection(2); Game1.player.faceDirection(0); }
                else              { npc.faceDirection(0); Game1.player.faceDirection(2); }
            }

            Game1.playSound("dwop");
            npc.doEmote(20); // 爱心

            if (friendship != null)
                friendship.Points = Math.Min(2500, friendship.Points + 10);

            DynamicBarkManager.TriggerKissReaction(npc, isSuccess: true);

            ModEntry.SMonitor?.Log(
                $"[NpcInteractionService] Kiss executed successfully between {npc.Name} and player.",
                LogLevel.Info);

            return true;
        }
    }
}