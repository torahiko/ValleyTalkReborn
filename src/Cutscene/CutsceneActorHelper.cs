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
        // 全仓 Cutscene 域唯一反射点：StardewValley.Character.freezeMotion（protected bool，1.6 API 契约）。
        // 必须全限定：本仓 ValleytalkReborn.Character（Core/Character.cs）会遮蔽 using 导入的 StardewValley.Character
        internal static readonly System.Reflection.FieldInfo FreezeMotionField =
            typeof(StardewValley.Character).GetField("freezeMotion",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);

        /// <summary>
        /// 穿透重置 protected freezeMotion 字段；反射契约失效时记录 Error 并放弃（演员可能保持冻结，可观测），不抛异常。
        /// </summary>
        internal static void SetFreezeMotion(StardewValley.Character character, bool value)
        {
            if (FreezeMotionField == null)
            {
                ModEntry.SMonitor?.Log(
                    "[CutsceneActorHelper] Character.freezeMotion reflection failed — API contract broken",
                    LogLevel.Error);
                return;
            }

            FreezeMotionField.SetValue(character, value);
        }

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
                try
                {
                    npc.EndActivityRouteEndBehavior();
                }
                catch { }
                npc.doingEndOfRouteAnimation.Value = false;
                npc.goingToDoEndOfRouteAnimation.Value = false;
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
                SetFreezeMotion(npc, false);
            }
            catch (Exception ex)
            {
                ModEntry.SMonitor?.Log($"[CutsceneActorHelper] Failed to wakeup {npc.Name}: {ex.Message}", LogLevel.Trace);
            }
        }
    }
}
