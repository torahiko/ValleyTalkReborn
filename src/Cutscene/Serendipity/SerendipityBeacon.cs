using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;

namespace ValleytalkReborn.Cutscene.Serendipity
{
    /// <summary>
    /// 驻足观摩微标：当雷达在【驻足观摩模式】下发现合规偶遇剧情时，
    /// 在角色头顶轻柔呈现半透明浮动微标，提示玩家走近按 E 欣赏剧情，绝不强行打扰赶路。
    /// </summary>
    public sealed class SerendipityBeacon
    {
        public static SerendipityBeacon Instance { get; } = new();

        public bool IsActive { get; private set; }
        public Vector2 WorldPosition { get; private set; }
        public IReadOnlyList<NPC> TargetActors { get; private set; } = new List<NPC>();
        public SituationDefinition Situation { get; private set; }

        private float _remainingSeconds = 0f;
        private const float DefaultTimeoutSeconds = 30f;
        private const float InteractionRadiusPixels = 4.0f * 64f; // 约 4.0 格内可按键激活

        private SerendipityBeacon() { }

        /// <summary>
        /// 激活浮标
        /// </summary>
        public void Activate(List<NPC> actors, SituationDefinition situation)
        {
            if (actors == null || actors.Count == 0 || situation == null) return;

            TargetActors = new List<NPC>(actors);
            Situation = situation;
            _remainingSeconds = DefaultTimeoutSeconds;

            // 计算演员群体的中心物理坐标
            Vector2 sumPos = Vector2.Zero;
            foreach (var actor in TargetActors)
            {
                sumPos += actor.Position;
            }
            Vector2 center = sumPos / TargetActors.Count;
            WorldPosition = center + new Vector2(32f, -48f); // 居中略微向上悬浮

            IsActive = true;
        }

        /// <summary>
        /// 关闭浮标（可选附带关闭原因用于日志观测）
        /// </summary>
        public void Dismiss(string reason = null)
        {
            if (IsActive && !string.IsNullOrEmpty(reason))
            {
                ModEntry.SMonitor?.Log($"[SerendipityBeacon] Beacon dismissed: {reason}", LogLevel.Debug);
            }

            IsActive = false;
            // 整体换新列表而非 Clear()：外部此前捕获的引用（如 ModEntry 的按键快照）不受破坏性就地清空影响
            TargetActors = new List<NPC>();
            Situation = null;
            _remainingSeconds = 0f;
        }

        /// <summary>
        /// 玩家当前是否处于可按键交互的有效半径内（支持双重锚点：微标中心或任意参演NPC）
        /// </summary>
        public bool IsPlayerInRange()
        {
            if (!IsActive || Game1.player == null) return false;

            // 1. 距离浮标物理中心 4 格以内
            if (Vector2.Distance(Game1.player.Position, WorldPosition) <= InteractionRadiusPixels)
                return true;

            // 2. 双重锚点防护：距离任意一名参演角色 4 格以内均判定在交互范围内
            if (TargetActors != null)
            {
                for (int i = 0; i < TargetActors.Count; i++)
                {
                    var actor = TargetActors[i];
                    if (actor != null && Vector2.Distance(Game1.player.Tile, actor.Tile) <= 4.0f)
                        return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 帧更新：倒计时、动态跟随移动与离散/换图自毁
        /// </summary>
        public void Update(float dt)
        {
            if (!IsActive) return;

            _remainingSeconds -= dt;
            if (_remainingSeconds <= 0f)
            {
                RecordSelfDismissal();
                Dismiss("wait timeout elapsed");
                return;
            }

            // 1. 参演角色合法性与换图检测：若有角色离开当前场景，微标自然隐退
            if (TargetActors == null || TargetActors.Count < 2 || Game1.currentLocation == null)
            {
                RecordSelfDismissal();
                Dismiss("target actors invalid or current location null");
                return;
            }

            for (int i = 0; i < TargetActors.Count; i++)
            {
                var actor = TargetActors[i];
                if (actor == null || actor.currentLocation != Game1.currentLocation)
                {
                    RecordSelfDismissal();
                    Dismiss($"actor '{actor?.Name ?? "null"}' left the current location");
                    return;
                }
            }

            // 2. 演员离散检测：若 NPC 之间走散（距离 > 7 格），说明偶遇已结束，微标静默退场
            for (int i = 0; i < TargetActors.Count; i++)
            {
                for (int j = i + 1; j < TargetActors.Count; j++)
                {
                    if (Vector2.Distance(TargetActors[i].Tile, TargetActors[j].Tile) > 7.0f)
                    {
                        RecordSelfDismissal();
                        Dismiss($"actors '{TargetActors[i].Name}' and '{TargetActors[j].Name}' drifted beyond 7 tiles");
                        return;
                    }
                }
            }

            // 3. 动态跟随演员群体中心物理坐标（NPC 边走微标边动）
            Vector2 sumPos = Vector2.Zero;
            foreach (var actor in TargetActors)
            {
                sumPos += actor.Position;
            }
            Vector2 center = sumPos / TargetActors.Count;
            WorldPosition = center + new Vector2(32f, -48f);

            // 4. 距离超限守护：若玩家走远超过 20 格，浮标自然静默取消
            if (Game1.player != null && Vector2.Distance(Game1.player.Position, WorldPosition) > 20f * 64f)
            {
                RecordSelfDismissal();
                Dismiss("player moved beyond 20 tiles");
            }
        }

        /// <summary>
        /// 自毁类 Dismiss 的统一退避记录（交互与门禁类 Dismiss 不记录）：
        /// Situation/currentLocation 缺一即跳过（RECOVERABLE：退避不生效一秒，无碍）。
        /// </summary>
        private void RecordSelfDismissal()
        {
            var situation = Situation;
            var location = Game1.currentLocation;
            if (situation == null || location == null)
                return;

            SerendipityCooldownStore.Instance.RecordBeaconDismissal(
                situation.Title, location.Name, Game1.Date.TotalDays, Game1.timeOfDay);
        }

        /// <summary>
        /// 渲染在世界图层（RenderedWorld）中
        /// </summary>
        public void Draw(SpriteBatch b)
        {
            if (!IsActive || Situation == null) return;

            // 视口坐标换算
            Vector2 screenPos = Game1.GlobalToLocal(WorldPosition);

            // 屏幕范围剔除
            var vp = Game1.graphics.GraphicsDevice.Viewport;
            if (screenPos.X < -150 || screenPos.X > vp.Width + 150 ||
                screenPos.Y < -150 || screenPos.Y > vp.Height + 150)
            {
                return;
            }

            bool inRange = IsPlayerInRange();

            // 呼吸悬浮与透明度渐变
            float totalSec = (float)Game1.currentGameTime.TotalGameTime.TotalSeconds;
            float bobOffset = (float)Math.Sin(totalSec * 3.5) * 5f;
            float pulse = 0.85f + (float)Math.Sin(totalSec * 4.5) * 0.15f;

            string mainText = inRange
                ? "🌟 [ 右键 / E 驻足观摩 ]"
                : "💬 附近有故事正在发生 [靠近即可观摩]";
            string subText = $"《{Situation.Title}》";

            var mainSize = Game1.smallFont.MeasureString(mainText);
            var subSize = Game1.tinyFont.MeasureString(subText);

            int pillWidth = (int)Math.Max(mainSize.X, subSize.X) + 28;
            int pillHeight = (int)(mainSize.Y + subSize.Y) + 16;

            int pillX = (int)screenPos.X - pillWidth / 2;
            int pillY = (int)(screenPos.Y + bobOffset) - pillHeight;

            var pillRect = new Rectangle(pillX, pillY, pillWidth, pillHeight);

            // 1. 半透明典雅底板
            Color bgColor = inRange
                ? new Color(15, 20, 30, (int)(230 * pulse))
                : new Color(20, 24, 32, (int)(180 * pulse));
            b.Draw(Game1.staminaRect, pillRect, bgColor);

            // 2. 边框渲染
            Color borderColor = inRange
                ? new Color(255, 215, 0, (int)(240 * pulse)) // 亮金
                : new Color(180, 160, 120, (int)(160 * pulse)); // 柔和金铜
            DrawBorder(b, pillRect, borderColor, inRange ? 2 : 1);

            // 3. 绘制文字
            Vector2 mainTextPos = new Vector2(pillX + (pillWidth - mainSize.X) / 2f, pillY + 6);
            Color textColor = inRange ? Color.Gold : Color.White * 0.95f;

            // 阴影
            b.DrawString(Game1.smallFont, mainText, mainTextPos + new Vector2(1, 1), Color.Black * 0.8f);
            b.DrawString(Game1.smallFont, mainText, mainTextPos, textColor);

            Vector2 subTextPos = new Vector2(pillX + (pillWidth - subSize.X) / 2f, pillY + mainSize.Y + 6);
            b.DrawString(Game1.tinyFont, subText, subTextPos + new Vector2(1, 1), Color.Black * 0.7f);
            b.DrawString(Game1.tinyFont, subText, subTextPos, Color.LightGray * 0.85f);
        }

        private static void DrawBorder(SpriteBatch b, Rectangle rect, Color color, int thickness)
        {
            b.Draw(Game1.staminaRect, new Rectangle(rect.X, rect.Y, rect.Width, thickness), color);
            b.Draw(Game1.staminaRect, new Rectangle(rect.X, rect.Bottom - thickness, rect.Width, thickness), color);
            b.Draw(Game1.staminaRect, new Rectangle(rect.X, rect.Y, thickness, rect.Height), color);
            b.Draw(Game1.staminaRect, new Rectangle(rect.Right - thickness, rect.Y, thickness, rect.Height), color);
        }
    }
}
