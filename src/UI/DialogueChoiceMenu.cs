using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;

namespace ValleytalkReborn.UI
{
    /// <summary>
    /// VT-UI-005 Stage 2：精致交互响应坞（Response Dock）。
    /// 原版对话框播完纯台词后平滑接管，集成“NPC语境回显 + 建议卡片 + 坞内文本输入框”。
    /// 全局字号全面上调一阶，与大画幅排版完美匹配。
    /// </summary>
    internal class DialogueChoiceMenu : IClickableMenu
    {
        private const int MaxPanelWidth = 880;
        private const int SideMargin = 64;
        private const int BottomMargin = 28;

        private const int HeaderHeight = 56;
        private const int HeaderGap = 12;
        private const int RowHeight = 60;
        private const int RowGap = 10;
        private const int PanelPadding = 22;

        private const int InputRowHeight = 54;
        private const int SendButtonWidth = 96;
        private const int SilenceButtonWidth = 120;

        // ★ 统一字体字阶配置（全面放大一号）
        private const float TitleFontSize = 25f;    // 顶栏主标题（原 22f）
        private const float SubtitleFontSize = 17f; // 顶栏副状态（原 15f）
        private const float InputFontSize = 21f;    // 输入框与占位符（原 18f）

        private readonly NPC _speaker;
        private readonly string _npcLineSanitized;
        private readonly List<string> _suggestions;
        private readonly bool _showDateOption;

        private readonly DialogueTextInputBox _inputBox;

        // 肖像与头像框
        private Texture2D _npcPortrait;
        private Rectangle _portraitSourceRect;
        private Rectangle _portraitDestRect;

        private readonly List<Rectangle> _suggestionRects = new List<Rectangle>();
        private readonly List<float> _suggestionHoverScales = new List<float>();

        private Rectangle _dateRect;
        private Rectangle _sendRect;
        private Rectangle _silenceRect;

        private float _dateHoverScale = 1f;
        private float _sendHoverScale = 1f;
        private float _silenceHoverScale = 1f;

        private readonly string _titleText;
        private readonly string _subtitleText;
        private readonly string _dateLabel;
        private readonly string _sendLabel;
        private readonly string _silenceLabel;

        private readonly GameLocation _openLocation;
        private bool _closed;

        public DialogueChoiceMenu(PendingChoiceContext context)
        {
            _speaker = context.Speaker;
            _npcLineSanitized = context.NpcLineSanitized;
            _suggestions = context.Suggestions ?? new List<string>();
            _showDateOption = context.ShowDateOption;

            bool isZh = LocalizedContentManager.CurrentLanguageCode == LocalizedContentManager.LanguageCode.zh;
            string speakerName = _speaker?.displayName ?? _speaker?.Name ?? "NPC";
            var nameTokens = new { Name = speakerName };

            _titleText = Util.GetString("choiceMenuTitle", nameTokens, returnNull: true)
                         ?? (isZh ? $"回应：{speakerName}" : $"Replying to: {speakerName}");
            _subtitleText = Util.GetString("choiceMenuSubtitle", nameTokens, returnNull: true)
                            ?? (isZh ? $"{speakerName} 正在等待你的答复…" : $"{speakerName} is waiting for your reply...");

            _dateLabel = Util.GetString("choiceMenuDate", returnNull: true)
                         ?? (isZh ? "【敲定约会地点...】" : "【Choose Date Location...】");
            _sendLabel = Util.GetString("choiceMenuSend", returnNull: true)
                         ?? (isZh ? "发送" : "Send");
            _silenceLabel = Util.GetString("choiceMenuSilence", returnNull: true)
                            ?? (isZh ? "保持沉默" : "Stay silent");

            // 输入框字号升级为 21f
            _inputBox = new DialogueTextInputBox(200)
            {
                AllowNewlines = false,
                UseCustomFont = true,
                CustomFontSize = InputFontSize,
                DrawFrame = true,
                ShowCharacterCount = false,
                Selected = true,
                PlaceholderText = Util.GetString("choiceMenuInputPlaceholder", returnNull: true)
                                  ?? (isZh ? "在此输入你想说的话…（按 Enter 发送）" : "Type your reply... (Press Enter)"),
                PlaceholderColor = new Color(175, 145, 115),
                TextColor = BioEditorMenu.TextPrimary
            };

            LoadNpcPortrait();
            Layout();

            _openLocation = Game1.currentLocation;

            _inputBox.Selected = true;
            Game1.keyboardDispatcher.Subscriber = _inputBox;
        }

        private void LoadNpcPortrait()
        {
            try
            {
                if (_speaker?.Portrait != null && !_speaker.Portrait.IsDisposed)
                {
                    _npcPortrait = _speaker.Portrait;
                }
                else if (!string.IsNullOrEmpty(_speaker?.Name))
                {
                    _npcPortrait = Game1.content.Load<Texture2D>("Portraits\\" + _speaker.Name);
                }

                if (_npcPortrait != null)
                {
                    if (_npcPortrait.Width >= 128 && _npcPortrait.Height >= 64)
                        _portraitSourceRect = new Rectangle(64, 0, 64, 64);
                    else if (_npcPortrait.Width >= 64 && _npcPortrait.Height >= 128)
                        _portraitSourceRect = new Rectangle(0, 64, 64, 64);
                    else
                        _portraitSourceRect = new Rectangle(0, 0, Math.Min(64, _npcPortrait.Width), Math.Min(64, _npcPortrait.Height));
                }
            }
            catch
            {
                _npcPortrait = null;
                _portraitSourceRect = Rectangle.Empty;
            }
        }

        private void Layout()
        {
            width = Math.Min(MaxPanelWidth, Game1.uiViewport.Width - SideMargin);

            height = PanelPadding 
                     + HeaderHeight 
                     + HeaderGap
                     + (_showDateOption ? RowHeight + RowGap : 0)
                     + _suggestions.Count * (RowHeight + RowGap)
                     + InputRowHeight
                     + PanelPadding;

            xPositionOnScreen = (Game1.uiViewport.Width - width) / 2;
            yPositionOnScreen = Math.Max(16, Game1.uiViewport.Height - height - BottomMargin);

            int contentX = xPositionOnScreen + PanelPadding;
            int contentW = width - PanelPadding * 2;
            int currentY = yPositionOnScreen + PanelPadding;

            // 1. 顶栏区域（48x48 头像）
            _portraitDestRect = new Rectangle(contentX, currentY + 3, 48, 48);
            currentY += HeaderHeight + HeaderGap;

            // 2. 约会卡
            if (_showDateOption)
            {
                _dateRect = new Rectangle(contentX, currentY, contentW, RowHeight);
                currentY += RowHeight + RowGap;
            }

            // 3. 建议卡列表
            _suggestionRects.Clear();
            _suggestionHoverScales.Clear();
            for (int i = 0; i < _suggestions.Count; i++)
            {
                _suggestionRects.Add(new Rectangle(contentX, currentY, contentW, RowHeight));
                _suggestionHoverScales.Add(1f);
                currentY += RowHeight + RowGap;
            }

            // 4. 输入行
            int inputW = contentW - SendButtonWidth - SilenceButtonWidth - RowGap * 2;
            _inputBox.Position = new Vector2(contentX, currentY);
            _inputBox.Extent = new Vector2(inputW, InputRowHeight);
            _inputBox.InvalidateLayout();

            _sendRect = new Rectangle(contentX + inputW + RowGap, currentY, SendButtonWidth, InputRowHeight);
            _silenceRect = new Rectangle(_sendRect.Right + RowGap, currentY, SilenceButtonWidth, InputRowHeight);
        }

        public override void update(GameTime time)
        {
            base.update(time);

            _inputBox.Update(time);

            _speaker.Halt();
            _speaker.movementPause = 20;
            _speaker.facePlayer(Game1.player);

            if (Game1.eventUp || Game1.currentLocation != _openLocation)
            {
                SelfDestruct("world state changed (event started or location switched)");
            }
        }

        public override void receiveKeyPress(Keys key)
        {
            if (key == Keys.Escape)
            {
                Silence();
                return;
            }

            if (key == Keys.Enter)
            {
                if (!string.IsNullOrWhiteSpace(_inputBox.Text))
                {
                    Game1.playSound("coin");
                    Submit(_inputBox.Text);
                }
                return;
            }

            if (Game1.options.doesInputListContain(Game1.options.menuButton, key))
                return;

            if (DialogueTextInputBox.IsControlKeyDown())
            {
                if (key == Keys.A || key == Keys.C || key == Keys.X || key == Keys.Z || key == Keys.V)
                {
                    _inputBox.RecieveSpecialInput(key);
                    return;
                }
            }

            if (key == Keys.Left || key == Keys.Right || key == Keys.Up || key == Keys.Down ||
                key == Keys.Home || key == Keys.End || key == Keys.Delete || key == Keys.Back)
            {
                _inputBox.RecieveSpecialInput(key);
            }
        }

        public override void receiveLeftClick(int x, int y, bool playSound = true)
        {
            for (int i = 0; i < _suggestionRects.Count; i++)
            {
                if (_suggestionRects[i].Contains(x, y))
                {
                    Game1.playSound("coin");
                    Submit(_suggestions[i]);
                    return;
                }
            }

            if (_dateRect.Contains(x, y))
            {
                Game1.playSound("bigSelect");
                OpenDateMenu();
                return;
            }

            if (_sendRect.Contains(x, y))
            {
                if (!string.IsNullOrWhiteSpace(_inputBox.Text))
                {
                    Game1.playSound("coin");
                    Submit(_inputBox.Text);
                }
                return;
            }

            if (_silenceRect.Contains(x, y))
            {
                Silence();
                return;
            }

            if (_inputBox.ReceiveLeftClick(x, y))
            {
                RestoreFocus();
                return;
            }

            if (new Rectangle(xPositionOnScreen, yPositionOnScreen, width, height).Contains(x, y))
            {
                RestoreFocus();
                return;
            }

            Silence();
        }

        public override void leftClickHeld(int x, int y)
        {
            base.leftClickHeld(x, y);
            _inputBox.LeftClickHeld(x, y);
        }

        public override void releaseLeftClick(int x, int y)
        {
            base.releaseLeftClick(x, y);
            _inputBox.ReleaseLeftClick(x, y);
        }

        public override void receiveScrollWheelAction(int direction)
        {
            base.receiveScrollWheelAction(direction);
            _inputBox.ReceiveScrollWheel(direction);
        }

        public override void gameWindowSizeChanged(Rectangle oldBounds, Rectangle newBounds)
        {
            Layout();
        }

        private void RestoreFocus()
        {
            _inputBox.Selected = true;
            Game1.keyboardDispatcher.Subscriber = _inputBox;
        }

        protected override void cleanupBeforeExit()
        {
            if (Game1.keyboardDispatcher.Subscriber == _inputBox)
                Game1.keyboardDispatcher.Subscriber = null;

            base.cleanupBeforeExit();
        }

        public override void draw(SpriteBatch b)
        {
            int mx = Game1.getMouseX();
            int my = Game1.getMouseY();

            b.Draw(Game1.fadeToBlackRect, Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.Black * 0.25f);

            IClickableMenu.drawTextureBox(b, xPositionOnScreen - 8, yPositionOnScreen - 8, width + 16, height + 16, Color.White);
            b.Draw(Game1.staminaRect, new Rectangle(xPositionOnScreen, yPositionOnScreen, width, height), new Color(245, 230, 205));
            b.Draw(Game1.menuTexture,
                new Rectangle(xPositionOnScreen, yPositionOnScreen, width, height),
                new Rectangle(64, 128, 64, 64),
                new Color(245, 230, 205));

            DrawHeader(b);

            if (_showDateOption)
            {
                DrawStableActionButton(b, _dateRect, _dateLabel, ref _dateHoverScale, mx, my, isRomantic: true);
            }

            for (int i = 0; i < _suggestionRects.Count; i++)
            {
                float hoverScale = _suggestionHoverScales[i];
                DrawStableActionButton(b, _suggestionRects[i], _suggestions[i], ref hoverScale, mx, my);
                _suggestionHoverScales[i] = hoverScale;
            }

            b.Draw(Game1.staminaRect,
                new Rectangle((int)_inputBox.Position.X + 1, (int)_inputBox.Position.Y + 2, (int)_inputBox.Extent.X, (int)_inputBox.Extent.Y),
                Color.Black * 0.12f);

            _inputBox.Draw(b);

            DrawStableActionButton(b, _sendRect, _sendLabel, ref _sendHoverScale, mx, my, isPrimary: true);
            DrawStableActionButton(b, _silenceRect, _silenceLabel, ref _silenceHoverScale, mx, my, isDanger: true);

            for (int i = 0; i < _suggestionRects.Count; i++)
            {
                if (_suggestionRects[i].Contains(mx, my))
                {
                    DialogueTextInputMenu.DrawHoverTextCustom(b, _suggestions[i]);
                    break;
                }
            }

            drawMouse(b);
        }

        private void DrawHeader(SpriteBatch b)
        {
            b.Draw(Game1.staminaRect, new Rectangle(_portraitDestRect.X - 1, _portraitDestRect.Y - 1, _portraitDestRect.Width + 2, _portraitDestRect.Height + 2), new Color(225, 210, 185));
            IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(403, 383, 6, 6),
                _portraitDestRect.X - 2, _portraitDestRect.Y - 2, _portraitDestRect.Width + 4, _portraitDestRect.Height + 4,
                new Color(200, 175, 140), 2f, false);

            if (_npcPortrait != null && !_portraitSourceRect.IsEmpty)
            {
                b.Draw(_npcPortrait, _portraitDestRect, _portraitSourceRect, Color.White);
            }
            else
            {
                string fallback = _speaker?.displayName?.Substring(0, 1) ?? "?";
                CustomFontManager.DrawStringBold(b, fallback,
                    new Vector2(_portraitDestRect.X + 16, _portraitDestRect.Y + 12),
                    BioEditorMenu.TextMuted, TitleFontSize);
            }

            // 主标题放大至 25f Bold
            int textLeft = _portraitDestRect.Right + 16;
            CustomFontManager.DrawStringBold(b, _titleText, new Vector2(textLeft, _portraitDestRect.Y + 3), BioEditorMenu.TextPrimary, TitleFontSize);

            // 副标题放大至 17f Medium
            CustomFontManager.DrawString(b, _subtitleText, new Vector2(textLeft, _portraitDestRect.Y + 31), BioEditorMenu.TextMuted, SubtitleFontSize);

            int sepY = yPositionOnScreen + PanelPadding + HeaderHeight + 6;
            b.Draw(Game1.staminaRect, new Rectangle(xPositionOnScreen + PanelPadding, sepY, width - PanelPadding * 2, 1), Color.Gray * 0.35f);
        }

        private static void DrawStableActionButton(
            SpriteBatch b,
            Rectangle rect,
            string label,
            ref float hoverScale,
            int mx, int my,
            bool isPrimary = false,
            bool isRomantic = false,
            bool isDanger = false)
        {
            bool isHover = rect.Contains(mx, my);
            bool isPressed = isHover && Game1.input.GetMouseState().LeftButton == ButtonState.Pressed;

            float targetScale = (isHover && !isPressed) ? 1.025f : 1.0f;
            hoverScale += (targetScale - hoverScale) * 0.25f;

            int drawW = (int)MathF.Round(rect.Width * hoverScale);
            int drawH = (int)MathF.Round(rect.Height * hoverScale);
            int drawX = rect.X - (drawW - rect.Width) / 2;
            int drawY = rect.Y - (drawH - rect.Height) / 2;
            int pressOffset = isPressed ? 1 : 0;

            if (!isPressed)
            {
                int shadowY = isHover ? 3 : 2;
                b.Draw(Game1.staminaRect,
                    new Rectangle(drawX + 1, drawY + shadowY, drawW, drawH),
                    Color.Black * (isHover ? 0.20f : 0.12f));
            }

            Color bg;
            if (isDanger)
            {
                bg = isHover ? new Color(245, 130, 125) : new Color(225, 100, 95);
            }
            else if (isRomantic)
            {
                bg = isHover ? new Color(255, 225, 235) : new Color(245, 205, 215);
            }
            else if (isPrimary)
            {
                bg = isHover ? new Color(255, 232, 120) : new Color(255, 210, 115);
            }
            else
            {
                bg = isHover ? new Color(255, 248, 235) : new Color(236, 215, 185);
            }

            if (isPressed)
                bg = Color.Lerp(bg, Color.Black, 0.12f);

            var dynamicBox = new Rectangle(drawX + pressOffset, drawY + pressOffset, drawW, drawH);
            b.Draw(Game1.staminaRect,
                new Rectangle(dynamicBox.X + 1, dynamicBox.Y + 1, dynamicBox.Width - 2, dynamicBox.Height - 2),
                bg);

            Color borderCol;
            if (isDanger)
            {
                borderCol = isHover ? new Color(205, 65, 60) : new Color(175, 50, 45);
            }
            else if (isRomantic)
            {
                borderCol = isHover ? new Color(230, 110, 140) : new Color(200, 120, 140);
            }
            else if (isPrimary)
            {
                borderCol = isHover ? new Color(245, 160, 30) : new Color(205, 140, 45);
            }
            else
            {
                borderCol = isHover ? new Color(225, 150, 50) : new Color(190, 155, 115);
            }

            IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9),
                dynamicBox.X, dynamicBox.Y, dynamicBox.Width, dynamicBox.Height,
                borderCol, 3f, false);

            var stableTextBounds = new Rectangle(rect.X + pressOffset, rect.Y + pressOffset, rect.Width, rect.Height);
            ButtonTextRenderer.DrawButtonText(b, label, stableTextBounds, BioEditorMenu.TextPrimary, useBold: isPrimary || isRomantic);
        }

        private void Submit(string text)
        {
            Game1.exitActiveMenu();
            _closed = true;

            AsyncBuilder.Instance.ClearCooldown();

            string farmerResponse = text ?? string.Empty;
            if (farmerResponse.Contains('@') && Game1.player != null)
            {
                farmerResponse = farmerResponse.Replace("@", Game1.player.Name);
            }

            TownIncidentEngine.RecordChoice(_speaker.Name, farmerResponse);

            var history = ReconstructHistory();
            history.Add(new ConversationElement(farmerResponse, true));

            bool accepted = AsyncBuilder.Instance.RequestNpcResponse(_speaker, history.ToArray());
            if (accepted)
            {
                DialogueHistoryManager.Instance.RecordPlayerDialogue(_speaker.Name, farmerResponse);
            }
            else
            {
                ModEntry.SMonitor?.Log($"[DialogueChoiceMenu] Request rejected by AsyncBuilder for {_speaker.Name}.", LogLevel.Warn);
            }
        }

        private void OpenDateMenu()
        {
            Game1.exitActiveMenu();
            _closed = true;

            Game1.activeClickableMenu = new DateLocationPickerMenu(_speaker);
        }

        private void Silence()
        {
            Game1.playSound("bigDeSelect");

            Game1.exitActiveMenu();
            _closed = true;

            DialogueHistoryManager.Instance.RecordSessionEnd(_speaker.Name);
            Game1.player.forceCanMove();
        }

        private void SelfDestruct(string reason)
        {
            if (_closed) return;
            _closed = true;

            ModEntry.SMonitor?.Log($"[DialogueChoiceMenu] self-destruct: {reason}. No silence recorded.", LogLevel.Trace);

            Game1.exitActiveMenu();
            Game1.player.forceCanMove();
        }

        private List<ConversationElement> ReconstructHistory()
        {
            var context = DialogueBuilder.Instance.GetContext(_speaker.Name);
            var history = context?.ChatHistory != null
                ? new List<ConversationElement>(context.ChatHistory)
                : new List<ConversationElement>();

            if (!string.IsNullOrEmpty(_npcLineSanitized)
                && !history.Any(y => y.Text != null
                                     && y.Text.Length >= _npcLineSanitized.Length
                                     && y.Text.Contains(_npcLineSanitized)))
            {
                history.Add(new ConversationElement(_npcLineSanitized, false));
            }

            return history;
        }
    }
}