using System;
using StardewModdingAPI;
using StardewValley;

namespace ValleytalkReborn.Cutscene
{
    /// <summary>
    /// 演员状态唤醒辅助器：打断原版日程待机与动作，解除 freezeMotion 封锁，确保物理运动正常
    /// </summary>
    internal static class CutsceneActorHelper
    {
        /// <summary>
        /// 全面唤醒 NPC：重置待机动画、清空暂停、穿透解除 freezeMotion 封锁
        /// </summary>
        public static void WakeupActor(NPC npc)
        {
            if (npc == null) return;

            try
            {
                npc.Halt();
                npc.controller = null;
                npc.temporaryController = null;
                npc.isCharging = false;
                npc.addedSpeed = 0;
                npc.movementPause = 0;
                npc.doingEndOfRouteAnimation.Value = false;
                npc.Sprite?.StopAnimation();

                // 还原可能的精灵图拉伸尺寸（如待机动作改变了宽高）
                var data = npc.GetData();
                if (data != null && npc.Sprite != null)
                {
                    npc.Sprite.SpriteWidth = data.Size.X;
                    npc.Sprite.SpriteHeight = data.Size.Y;
                    npc.Sprite.UpdateSourceRect();
                }

                // 核心关键：穿透重置 protected freezeMotion 字段，防止引擎跳过 controller.update
                if (ModEntry.SHelper != null)
                {
                    ModEntry.SHelper.Reflection.GetField<bool>(npc, "freezeMotion", required: false)?.SetValue(false);
                }
                else
                {
                    typeof(Character).GetField("freezeMotion", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)
                        ?.SetValue(npc, false);
                }
            }
            catch (Exception ex)
            {
                ModEntry.SMonitor?.Log($"[CutsceneActorHelper] Failed to wakeup {npc.Name}: {ex.Message}", LogLevel.Trace);
            }
        }
    }
}
