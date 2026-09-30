using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewValley;
using StardewValley.BellsAndWhistles;
using StardewValley.Menus;

namespace ValleytalkReborn
{
    /// <summary>
    /// 原版对白框皮肤复刻：drawBox 十段绘制序列、纯文本路径 SpriteText 签名、关闭图标
    /// TemporaryAnimatedSprite 参数均逐字照抄 Stardew 1.6 DialogueBox，纹理坐标为原版常量，禁止微调。
    /// 相对原版的四处刻意偏离（产品语义）：无过渡动画、3 秒自动消散、无 safetyTimer、无头像。
    /// </summary>
    internal sealed class ReplicaDialogueBox : IClickableMenu
    {
        private int _x;
        private int _y;
        private int _width;
        private int _height;
        private int _ticksLeft;
        private float _alpha;
        private TemporaryAnimatedSprite _dialogueIcon;
        private readonly string _message;
        private bool _closed;

        public ReplicaDialogueBox(string message) : base()
        {
            _message = message;
            _width = Math.Min(1240, SpriteText.getWidthOfString(message, 999999) + 64);
            _height = SpriteText.getHeightOfString(message, _width - 20) + 4;
            _x = (int)Utility.getTopLeftPositionForCenteringOnScreen(_width, _height, 0, 0).X;
            _y = Game1.uiViewport.Height - _height - 64;
            _ticksLeft = 180;
            _alpha = 1f;
            _dialogueIcon = new TemporaryAnimatedSprite("LooseSprites\\Cursors",
                new Rectangle(289, 342, 11, 12), 80f, 11, 999999,
                new Vector2(_x + _width - 40, _y + _height - 44),
                false, false, 0.89f, 0f, Color.White, 4f, 0f, 0f, 0f, true);
        }

        public override void update(GameTime time)
        {
            base.update(time);

            _dialogueIcon?.update(time);

            if (--_ticksLeft <= 0)
            {
                Close();
                return;
            }

            if (_ticksLeft < 30)
                _alpha = _ticksLeft / 30f;
        }

        public override void draw(SpriteBatch b)
        {
            // 原版 DialogueBox.drawBox 十段皮肤逐字复刻（transitionInitialized 门控省略）
            b.Draw(Game1.mouseCursors, new Rectangle(_x, _y, _width, _height), new Rectangle(306, 320, 16, 16), Color.White * _alpha);
            b.Draw(Game1.mouseCursors, new Rectangle(_x, _y - 20, _width, 24), new Rectangle(275, 313, 1, 6), Color.White * _alpha);
            b.Draw(Game1.mouseCursors, new Rectangle(_x + 12, _y + _height, _width - 20, 32), new Rectangle(275, 328, 1, 8), Color.White * _alpha);
            b.Draw(Game1.mouseCursors, new Rectangle(_x - 32, _y + 24, 32, _height - 28), new Rectangle(264, 325, 8, 1), Color.White * _alpha);
            b.Draw(Game1.mouseCursors, new Rectangle(_x + _width, _y, 28, _height), new Rectangle(293, 324, 7, 1), Color.White * _alpha);
            b.Draw(Game1.mouseCursors, new Vector2(_x - 44, _y - 28), new Rectangle(261, 311, 14, 13), Color.White * _alpha, 0f, Vector2.Zero, 4f, SpriteEffects.None, 0.87f);
            b.Draw(Game1.mouseCursors, new Vector2(_x + _width - 8, _y - 28), new Rectangle(291, 311, 12, 11), Color.White * _alpha, 0f, Vector2.Zero, 4f, SpriteEffects.None, 0.87f);
            b.Draw(Game1.mouseCursors, new Vector2(_x + _width - 8, _y + _height - 8), new Rectangle(291, 326, 12, 12), Color.White * _alpha, 0f, Vector2.Zero, 4f, SpriteEffects.None, 0.87f);
            b.Draw(Game1.mouseCursors, new Vector2(_x - 44, _y + _height - 4), new Rectangle(261, 327, 14, 11), Color.White * _alpha, 0f, Vector2.Zero, 4f, SpriteEffects.None, 0.87f);

            // 原版纯文本路径：characterPosition=999999 即全量绘制，alpha 注入消散渐隐
            SpriteText.drawString(b, _message, _x + 8, _y + 8, 999999, _width, 999999, _alpha, 0.88f, false, -1, "", null, SpriteText.ScrollTextAlignment.Left);

            _dialogueIcon?.draw(b, true, 0, 0, _alpha);
            base.drawMouse(b, false, -1);
        }

        public override void receiveLeftClick(int x, int y, bool playSound = true)
        {
            Close();
        }

        public override void receiveKeyPress(Keys key)
        {
            if (key == Keys.Escape)
                Close();
        }

        private void Close()
        {
            if (_closed) return;
            _closed = true;

            Game1.exitActiveMenu();
            Game1.player.forceCanMove();
        }
    }
}
