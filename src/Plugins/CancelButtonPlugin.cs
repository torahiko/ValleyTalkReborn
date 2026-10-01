using System;
using System.Threading;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;
using StardewValley.BellsAndWhistles;
using ValleytalkReborn.UI;

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
            if (!Context.IsWorldReady || Game1.activeClickableMenu is not DialogueBox)
                return;

            // 🌟【Panic Mode 应急逃生键】：按下 F8 强制关闭屏幕上残留的任何卡顿对话框
            if (e.Button == SButton.F8)
            {
                ForceEmergencyClose();
                _helper.Input.Suppress(e.Button);
                return;
            }

            // 🌟【活动菜单让位守卫】：流式对白框活动时输入层彻底移交。
            // 取消按钮点击、快进、翻页与退出全部由 AiStreamingDialogueBox 的
            // receiveLeftClick / receiveKeyPress 原生响应，SMAPI 不得压制任何按键。
            if (Game1.activeClickableMenu is AiStreamingDialogueBox)
                return;

            // 正在生成中的原有取消逻辑（保持原样）
            if (!AsyncBuilder.Instance.IsGeneratingDialogue)
                return;

            // Esc 或手柄 B → 取消并吃掉
            if (e.Button == SButton.Escape || e.Button == SButton.ControllerB)
            {
                CancelCurrentDialogue();
                _helper.Input.Suppress(e.Button);
                return;
            }

            // 拦截所有可能导致 DialogueBox 翻页或关闭的按键
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
            // 与 OnButtonPressed 同源的让位守卫：流式对白框自带取消按钮悬浮与
            // 呼吸动画，此处跳过旧关闭按钮的悬浮命中与 Lerp，避免两套状态互相污染。
            if (Game1.activeClickableMenu is AiStreamingDialogueBox)
                return;

            SyncCancellationToken();
            if (!Context.IsWorldReady || !AsyncBuilder.Instance.IsGeneratingDialogue)
            {
                if (!Context.IsWorldReady ||
                    !AsyncBuilder.Instance.IsGeneratingDialogue ||
                    _currentCts == null || _currentCts.IsCancellationRequested)
                    return;
            }

            _isHoveringOverClose = _closeButtonRect.Contains(Game1.getMouseX(), Game1.getMouseY());
            _hoverScale += ((_isHoveringOverClose ? 1.15f : 1.0f) - _hoverScale) * 0.2f;
        }
        
        private void OnRenderedActiveMenu(object sender, RenderedActiveMenuEventArgs e)
        {
            // AI 流式对白框自带原版复刻的思考中波浪文字与取消按钮。
            // 此处必须让位，否则两套绘制会叠在同一像素上产生撕裂。
            if (Game1.activeClickableMenu is AiStreamingDialogueBox)
                return;

            if (!Context.IsWorldReady ||
                !AsyncBuilder.Instance.IsGeneratingDialogue ||
                Game1.activeClickableMenu is not DialogueBox dialogueBox)
                return;

            // 监听对话框原生展开状态：若还在播放过渡动画（即使被 FastAnimations 加速也能精准捕捉），则先不渲染按钮
            if (dialogueBox.transitioning)
                return;

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

            int vpWidth  = Game1.uiViewport.Width;
            int vpHeight = Game1.uiViewport.Height;
            int boxW     = dialogueBox.width  > 0 ? dialogueBox.width  : 1200;
            int boxH     = dialogueBox.height > 0 ? dialogueBox.height : 384;

            int boxLeft   = (vpWidth - boxW) / 2;
            int boxBottom = vpHeight - 32;
            int boxRight  = boxLeft + boxW;
            int boxTop    = boxBottom - boxH;

            // ==========================================
            // 1. "思考中" 波浪文字 —— 对话框内左上角
            // ==========================================
            var translation = _helper.Translation.Get("ui.thinking");
            string message = translation.HasValue()
                ? translation.ToString()
                : (isChinese ? "思考中" : "thinking");

            int dotCount = (int)(Game1.currentGameTime.TotalGameTime.TotalMilliseconds / 500.0) % 4;
            string animatedMessage = message + new string('.', dotCount);

            float startX         = boxLeft + 16f;
            float baseY          = boxTop  - 15f;
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
            
            int   btnX       = boxRight  - displaySize - 485;
            int   baseBtnY   = boxBottom - displaySize - margin - 5;
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
                    Game1.activeClickableMenu = new DialogueBox(new StardewValley.Dialogue(currentNpc, "", $"$s {cancelMsg}"));
                else
                    Game1.activeClickableMenu = new DialogueBox(cancelMsg);
            }
            catch (ObjectDisposedException) { }
            catch (Exception ex)
            {
                _monitor.Log($"[CancelButtonPlugin] Error during cancel: {ex.Message}", LogLevel.Warn);
            }
        }

        /// <summary>
        /// 极端异常下的安全逃生：强制关闭任何卡死的对话菜单并解锁农夫
        /// </summary>
        private void ForceEmergencyClose()
        {
            try
            {
                _monitor.Log("[CancelButtonPlugin] Panic key triggered: forcing dialogue box closure.", LogLevel.Warn);
                AsyncBuilder.Instance.Cleanup();
                Game1.dialogueUp = false;
                Game1.activeClickableMenu = null;
                Game1.player?.forceCanMove();
                Game1.playSound("bigDeSelect");
            }
            catch (Exception ex)
            {
                _monitor.Log($"[CancelButtonPlugin] Error during emergency close: {ex.Message}", LogLevel.Error);
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