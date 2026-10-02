using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;

namespace ValleytalkReborn.Cutscene
{
    /// <summary>
    /// 过场现场快照：记录玩家与 NPC 初始状态，保证零脏写优雅复原。
    /// </summary>
    internal sealed class CutsceneSnapshot
    {
        public struct ActorState
        {
            public NPC Npc;
            public GameLocation OriginalLocation;
            public Vector2 OriginalTile;
            public int OriginalFacing;
        }

        public Vector2 PlayerTile;
        public int PlayerFacing;
        public bool PlayerCanMove;
        public List<ActorState> ActorStates = new();

        /// <summary>
        /// 捕获当前状态快照
        /// </summary>
        public static CutsceneSnapshot Capture(IEnumerable<NPC> actors)
        {
            var snapshot = new CutsceneSnapshot
            {
                PlayerTile = Game1.player.Tile,
                PlayerFacing = Game1.player.FacingDirection,
                PlayerCanMove = Game1.player.CanMove
            };

            foreach (var npc in actors)
            {
                if (npc?.currentLocation == null) continue;
                
                snapshot.ActorStates.Add(new ActorState
                {
                    Npc = npc,
                    OriginalLocation = npc.currentLocation,
                    OriginalTile = npc.Tile,
                    OriginalFacing = npc.FacingDirection
                });
            }
            return snapshot;
        }

        /// <summary>
        /// 优雅复原：恢复玩家控制权，NPC 归位并恢复日程
        /// </summary>
        public void Restore()
        {
            try
            {
                // 1. 恢复玩家
                if (Game1.player != null)
                {
                    Game1.player.Position = PlayerTile * 64f;
                    Game1.player.faceDirection(PlayerFacing);
                    Game1.player.CanMove = PlayerCanMove; // 快照保真：还原接管前的真实值
                    Game1.player.freezePause = 0;
                }

                // 2. 恢复演员状态与原版日程
                foreach (var state in ActorStates)
                {
                    var npc = state.Npc;
                    if (npc == null) continue;

                    npc.Halt();
                    npc.controller = null;
                    npc.addedSpeed = 0;
                    npc.forceUpdateTimer = 0;

                    // 归位
                    if (npc.currentLocation != state.OriginalLocation && state.OriginalLocation != null)
                    {
                        Game1.warpCharacter(npc, state.OriginalLocation.NameOrUniqueName, state.OriginalTile);
                    }
                    else
                    {
                        npc.setTilePosition(new Point((int)state.OriginalTile.X, (int)state.OriginalTile.Y));
                    }
                    npc.faceDirection(state.OriginalFacing);

                    // 复用现有 ScheduleRestorer 唤醒 NPC 后续日常
                    try
                    {
                        Movement.ScheduleRestorer restorer = new Movement.ScheduleRestorer(
                            (n, tile, onSuccess, onFail) => { },  // 空实现，仅用于恢复日程
                            n => false
                        );
                        restorer.TryRestoreSchedule(npc);
                    }
                    catch (Exception ex)
                    {
                        ModEntry.SMonitor?.Log(
                            $"[CutsceneSnapshot] Failed to restore schedule for {npc.Name}: {ex.Message}",
                            LogLevel.Warn);
                    }
                }
            }
            catch (Exception ex)
            {
                ModEntry.SMonitor?.Log(
                    $"[CutsceneSnapshot] Restore failed: {ex.Message}",
                    LogLevel.Error);
            }
        }
    }
}
