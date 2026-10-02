using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
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
        public List<NPC> TargetActors { get; private set; } = new();
        public SituationDefinition Situation { get; private set; }

        private float _remainingSeconds = 0f;
        private const float DefaultTimeoutSeconds = 30f;
        private const float InteractionRadiusPixels = 3.5f * 64f; // 约 3.5 格内可按键激活

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
        /// 关闭浮标
        /// </summary>
        public void Dismiss()
        {
            IsActive = false;
            TargetActors.Clear();
            Situation = null;
            _remainingSeconds = 0f;
        }

        /// <summary>
        /// 玩家当前是否处于可按键交互的有效半径内
        /// </summary>
        public bool IsPlayerInRange()
        {
            if (!IsActive || Game1.player == null) return false;
            return Vector2.Distance(Game1.player.Position, WorldPosition) <= InteractionRadiusPixels;
        }

        /// <summary>
        /// 帧更新：倒计时与距离防护
        /// </summary>
        public void Update(float dt)
        {
            if (!IsActive) return;

            _remainingSeconds -= dt;
            if (_remainingSeconds <= 0f)
            {
                Dismiss();
                return;
            }

            // 距离超限守护：若玩家走远超过 22 格，浮标自然静默取消
            if (Game1.player != null && Vector2.Distance(Game1.player.Position, WorldPosition) > 22f * 64f)
            {
                Dismiss();
            }
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
                ? "🌟 [按 E 驻足观摩]"
                : "💬 附近有故事正在发生 [走近按 E]";
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
