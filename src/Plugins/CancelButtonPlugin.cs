using System;
using System.Threading;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;
using StardewValley.BellsAndWhistles;

namespace ValleytalkReborn.Plugins
{
    public class CancelButtonPlugin : IDisposable
    {
        private readonly IModHelper _helper;
        private readonly IMonitor _monitor;
        private CancellationTokenSource _currentCts;
        private Character _activeCharacter;
        private Rectangle _closeButtonRect;
        private bool _isHoveringOverClose;
        private float _hoverScale = 1.0f;
        private double _generationStartTime = -1.0;
        private const double OverlayDelaySeconds = 0.25;

        public CancelButtonPlugin(IModHelper helper, IMonitor monitor)
        {
            _helper = helper;
            _monitor = monitor;
            helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
            helper.Events.Display.RenderedActiveMenu += OnRenderedActiveMenu;
            helper.Events.GameLoop.ReturnedToTitle += OnReturnedToTitle;
            helper.Events.Input.ButtonPressed += OnButtonPressed;
        }

        private void OnButtonPressed(object sender, ButtonPressedEventArgs e)
        {
            if (!Context.IsWorldReady ||
                !AsyncBuilder.Instance.IsGeneratingDialogue ||
                Game1.activeClickableMenu is not DialogueBox)
                return;

            // Esc 或手柄 B → 取消并吃掉
            if (e.Button == SButton.Escape || e.Button == SButton.ControllerB)
            {
                CancelCurrentDialogue();
                _helper.Input.Suppress(e.Button);
                return;
            }

            // 拦截所有可能导致 DialogueBox 翻页或关闭的按键（已移除 MouseRight）
            if (e.Button == SButton.MouseLeft || 
                e.Button == SButton.Space    || e.Button == SButton.Enter ||
                e.Button == SButton.ControllerA || e.Button.IsActionButton())
            {
                // 鼠标左键精确点在关闭按钮上 → 触发取消
                if (e.Button == SButton.MouseLeft &&
                    _closeButtonRect.Contains((int)e.Cursor.ScreenPixels.X, (int)e.Cursor.ScreenPixels.Y))
                {
                    CancelCurrentDialogue();
                }
                // 正在生成时，所有这些键一律吃掉
                _helper.Input.Suppress(e.Button);
            }
        }

        private void OnUpdateTicked(object sender, UpdateTickedEventArgs e)
        {
            SyncCancellationToken();
            if (!Context.IsWorldReady || !AsyncBuilder.Instance.IsGeneratingDialogue){
                // 生成结束或未开始，重置计时
                _generationStartTime = -1.0;
        
                if (!Context.IsWorldReady ||
                    !AsyncBuilder.Instance.IsGeneratingDialogue ||
                    _currentCts == null || _currentCts.IsCancellationRequested)
                    return;
            }
            // 记录开始时间（只记一次）
            if (_generationStartTime < 0)
                _generationStartTime = Game1.currentGameTime.TotalGameTime.TotalSeconds;
            _isHoveringOverClose = _closeButtonRect.Contains(Game1.getMouseX(), Game1.getMouseY());
            _hoverScale += ((_isHoveringOverClose ? 1.15f : 1.0f) - _hoverScale) * 0.2f;
        }
        
        private void OnRenderedActiveMenu(object sender, RenderedActiveMenuEventArgs e)
        {
            if (!Context.IsWorldReady ||
                !AsyncBuilder.Instance.IsGeneratingDialogue ||
                Game1.activeClickableMenu is not DialogueBox dialogueBox)
                return;

            // 延迟 0.5 秒再显示，等对话框缩放动画结束
            if (_generationStartTime < 0) return;
            double elapsed = Game1.currentGameTime.TotalGameTime.TotalSeconds - _generationStartTime;
            if (elapsed < OverlayDelaySeconds) return;

            DrawOverlay(e.SpriteBatch, dialogueBox);
        }

        public void SetActiveCharacter(Character character)
        {
            _activeCharacter = character;
            _hoverScale = 1.0f;
            _isHoveringOverClose = false;
            SyncCancellationToken();
        }

        private void SyncCancellationToken()
        {
            var cts = _activeCharacter?.CurrentDialogueCts;
            if (_currentCts != cts)
                _currentCts = cts;
        }

        private void OnReturnedToTitle(object sender, ReturnedToTitleEventArgs e)
        {
            _currentCts = null;
            _activeCharacter = null;
        }

        private void DrawOverlay(SpriteBatch b, DialogueBox dialogueBox)
        {
            double time = Game1.currentGameTime.TotalGameTime.TotalSeconds;
            bool isChinese = LocalizedContentManager.CurrentLanguageCode == LocalizedContentManager.LanguageCode.zh;

            // DialogueBox.xPositionOnScreen/yPositionOnScreen 不可信（可能为0）
            // 用视口尺寸手算实际位置
            int vpWidth  = Game1.uiViewport.Width;
            int vpHeight = Game1.uiViewport.Height;
            int boxW     = dialogueBox.width  > 0 ? dialogueBox.width  : 1200;
            int boxH     = dialogueBox.height > 0 ? dialogueBox.height : 384;

            int boxLeft   = (vpWidth - boxW) / 2;
            int boxBottom = vpHeight - 32;
            int boxRight  = boxLeft + boxW;
            int boxTop    = boxBottom - boxH;

            // ==========================================
            // 1. "思考中" 波浪文字 —— 对话框顶部左侧
            // ==========================================
            var translation = _helper.Translation.Get("ui.thinking");
            string message = translation.HasValue()
                ? translation.ToString()
                : (isChinese ? "思考中" : "thinking");

            int dotCount = (int)(Game1.currentGameTime.TotalGameTime.TotalMilliseconds / 500.0) % 4;
            string animatedMessage = message + new string('.', dotCount);

            float startX         = boxLeft + 16f;
            float baseY          = boxTop  - 20f;
            float amplitude      = 3f;
            float speed          = 5f;
            float charWaveOffset = 0.5f;
            float currentX       = startX;

            for (int i = 0; i < animatedMessage.Length; i++)
            {
                string charStr = animatedMessage[i].ToString();
                float yOffset  = (float)Math.Sin(time * speed + i * charWaveOffset) * amplitude;

                if (isChinese)
                {
                    SpriteText.drawString(
                        b, charStr, (int)currentX, (int)(baseY + yOffset),
                        characterPosition: 999999, width: -1, height: 999999,
                        alpha: 1f, layerDepth: 1f);
                    currentX += SpriteText.getWidthOfString(charStr);
                }
                else
                {
                    b.DrawString(Game1.dialogueFont, charStr,
                        new Vector2(currentX, baseY + yOffset), Game1.textColor);
                    currentX += Game1.dialogueFont.MeasureString(charStr).X;
                }
            }

            // ==========================================
            // 2. 关闭按钮 —— 对话框内右下角
            // ==========================================
            const int spriteSize  = 12;
            float     baseScale   = 5f;
            int       displaySize = (int)(spriteSize * baseScale);
            const int margin      = 16;

            float btnBounceY = (float)Math.Sin(time * 3f) * 4f;
            
            //此处修改cancelbutton的按钮位置
            int   btnX       = boxRight  - displaySize - 485;
            int   baseBtnY   = boxBottom - displaySize - margin;
            int   btnY       = (int)(baseBtnY + btnBounceY);
            
            _closeButtonRect = new Rectangle(btnX, btnY, displaySize, displaySize);

            float drawScale   = baseScale * _hoverScale;
            float visualSize  = spriteSize * drawScale;
            float drawOffsetX = (visualSize - displaySize) / 2f;
            float drawOffsetY = (visualSize - displaySize) / 2f;

            b.Draw(
                Game1.mouseCursors,
                new Vector2(btnX - drawOffsetX, btnY - drawOffsetY),
                new Rectangle(337, 494, spriteSize, spriteSize),
                Color.White, 0f, Vector2.Zero, drawScale,
                SpriteEffects.None, 0.99f);

            // ==========================================
            // 3. 鼠标指针（置于最顶层）
            // ==========================================
            if (!Game1.options.hardwareCursor)
            {
                b.Draw(
                    Game1.mouseCursors,
                    new Vector2(Game1.getMouseX(), Game1.getMouseY()),
                    Game1.getSourceRectForStandardTileSheet(Game1.mouseCursors, 0, 16, 16),
                    Color.White, 0f, Vector2.Zero, 4f, SpriteEffects.None, 1f);
            }
        }

        private void CancelCurrentDialogue()
        {
            if (_currentCts == null || _currentCts.IsCancellationRequested ||
                _activeCharacter == null || _activeCharacter.IsUserCancelled)
                return;

            _activeCharacter.IsUserCancelled = true;

            try
            {
                NPC currentNpc = AsyncBuilder.Instance.SpeakingNpc;
                _currentCts.Cancel();
                Game1.playSound("cancel");
                AsyncBuilder.Instance.Cleanup();
                
                var translation = _helper.Translation.Get("ui.cancelled");
                bool isChinese = LocalizedContentManager.CurrentLanguageCode == LocalizedContentManager.LanguageCode.zh;
                string fallback = isChinese ? "请求已取消。" : "Request cancelled.";
                string cancelMsg = translation.HasValue() ? translation.ToString() : fallback;

                if (currentNpc != null)
                    Game1.activeClickableMenu = new DialogueBox(new Dialogue(currentNpc, "", $"$s {cancelMsg}"));
                else
                    Game1.activeClickableMenu = new DialogueBox(cancelMsg);
            }
            catch (ObjectDisposedException) { }
            catch (Exception ex)
            {
                _monitor.Log($"[CancelButtonPlugin] Error during cancel: {ex.Message}", LogLevel.Warn);
            }
        }

        public void Dispose()
        {
            _helper.Events.GameLoop.UpdateTicked -= OnUpdateTicked;
            _helper.Events.Display.RenderedActiveMenu -= OnRenderedActiveMenu;
            _helper.Events.GameLoop.ReturnedToTitle -= OnReturnedToTitle;
            _helper.Events.Input.ButtonPressed -= OnButtonPressed;
            _currentCts = null;
            _activeCharacter = null;
        }
    }
}