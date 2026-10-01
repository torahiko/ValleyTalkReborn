#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewModdingAPI;
using StardewValley;
using StardewValley.BellsAndWhistles;
using StardewValley.Menus;

namespace ValleytalkReborn.UI
{
    /// <summary>
    /// VT-UI-005 Stage 2：精致交互响应坞（Response Dock）。
    /// 外观与 DialogueTextInputMenu 保持 100% 原版原生统一：
    /// 右侧为原生立绘展台与好感度宝石，左侧集成大字号建议卡片、约会选项与坞内自由输入区。
    /// </summary>
    internal class DialogueChoiceMenu : IClickableMenu
    {
        // 像素级对齐原版 DialogueBox 规格
        private const int DialogueWidth = 1200;
        private const int DialogueHeight = 384;

        // 左侧文本排版常数
        private const int TextLeftPadding = 34;
        private const int HeaderHeight = 44;
        private const int InputRowHeight = 44;
        private const int SendButtonWidth = 92;
        private const int SilenceButtonWidth = 114;
        private const int RowGap = 8;

        // ★ 统一字阶配置（全面放大）
        private const float TitleFontSize = 22f;
        private const float SubtitleFontSize = 15f;
        private const float CardFontSize = 21f;     // ★ 建议选项卡与约会卡文字放大至 21f
        private const float InputFontSize = 21f;    // 输入框文字
        private const float ButtonFontSize = 18f;   // 发送/沉默等动作按钮放大至 18f
        private const float TipFontSize = CustomFontManager.SizeSmall;

        private readonly NPC _speaker = null!;
        private readonly string _npcLineSanitized;
        private readonly List<string> _suggestions;
        private readonly bool _showDateOption;

        private readonly DialogueTextInputBox _inputBox;

        // 右侧原版立绘与好感度数据
        private Texture2D? _npcPortrait;
        private Rectangle _portraitSourceRect;
        private Rectangle _friendshipJewel;
        private bool _hasFriendship;
        private int _friendshipHearts;
        private int _maxHearts = 10;

        // 交互卡片与按钮几何
        private readonly List<Rectangle> _suggestionRects = new();
        private readonly List<float> _suggestionHoverScales = new();

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
        private string? _hoverText;

        public DialogueChoiceMenu(PendingChoiceContext context)
            : base(0, 0, DialogueWidth, DialogueHeight, showUpperRightCloseButton: false)
        {
            _speaker = context.Speaker;
            _npcLineSanitized = context.NpcLineSanitized;
            _suggestions = context.Suggestions?
                .Select(s => (s.Contains('@') && GetSafePlayerName() is string pName)
                    ? s.Replace("@", pName)
                    : s)
                .ToList() ?? new List<string>();
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

            _inputBox = new DialogueTextInputBox(300)
            {
                AllowNewlines = false,
                UseCustomFont = true,
                CustomFontSize = InputFontSize,
                CounterFontSize = TipFontSize,
                DrawFrame = true,
                ShowCharacterCount = false,
                Selected = true,
                PlaceholderText = Util.GetString("choiceMenuInputPlaceholder", returnNull: true)
                                  ?? (isZh ? "在此输入你想说的话…（按 Enter 发送）" : "Type your reply... (Press Enter)"),
                PlaceholderColor = new Color(175, 145, 115),
                TextColor = BioEditorMenu.TextPrimary
            };

            LoadNpcPortrait();
            LoadFriendshipData();
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
                    var character = Game1.getCharacterFromName(_speaker.Name);
                    _npcPortrait = (character?.Portrait != null && !character.Portrait.IsDisposed)
                        ? character.Portrait
                        : Game1.content.Load<Texture2D>("Portraits/" + _speaker.Name);
                }

                if (_npcPortrait != null)
                {
                    _portraitSourceRect = Game1.getSourceRectForStandardTileSheet(_npcPortrait, 0, 64, 64);
                    if (!_npcPortrait.Bounds.Contains(_portraitSourceRect))
                    {
                        _portraitSourceRect = new Rectangle(0, 0, 64, 64);
                    }
                }
            }
            catch
            {
                _npcPortrait = null;
                _portraitSourceRect = Rectangle.Empty;
            }
        }

        private void LoadFriendshipData()
        {
            _hasFriendship = false;
            _friendshipHearts = 0;
            _maxHearts = 10;

            if (string.IsNullOrEmpty(_speaker?.Name)) return;

            if (Game1.player != null && Game1.player.friendshipData.ContainsKey(_speaker.Name))
            {
                _hasFriendship = true;
                _friendshipHearts = Game1.player.getFriendshipHeartLevelForNPC(_speaker.Name);
                _maxHearts = Utility.GetMaximumHeartsForCharacter(_speaker);
            }
        }

        private void Layout()
        {
            width = DialogueWidth;
            height = DialogueHeight;

            Vector2 centered = Utility.getTopLeftPositionForCenteringOnScreen(width, height, 0, 0);
            xPositionOnScreen = (int)centered.X;
            yPositionOnScreen = Game1.uiViewport.Height - height - 64;

            _friendshipJewel = new Rectangle(xPositionOnScreen + width - 64, yPositionOnScreen + 256, 44, 44);

            int xPositionOfPortraitArea = xPositionOnScreen + width - 448 + 4;
            int textLeft = xPositionOnScreen + TextLeftPadding;
            int textRightBound = xPositionOfPortraitArea - 40 - 20;
            int contentW = textRightBound - textLeft;

            // 1. 顶栏排版
            int topY = yPositionOnScreen + 20;
            int sepY = topY + HeaderHeight;

            // 2. 底部输入行与操作按钮
            int bottomBarY = yPositionOnScreen + height - InputRowHeight - 16;
            int inputW = contentW - SendButtonWidth - SilenceButtonWidth - RowGap * 2;

            _inputBox.Position = new Vector2(textLeft, bottomBarY);
            _inputBox.Extent = new Vector2(inputW, InputRowHeight);
            _inputBox.InvalidateLayout();

            _sendRect = new Rectangle(textLeft + inputW + RowGap, bottomBarY, SendButtonWidth, InputRowHeight);
            _silenceRect = new Rectangle(_sendRect.Right + RowGap, bottomBarY, SilenceButtonWidth, InputRowHeight);

            // 3. 中间选项卡区域自适应排布
            int availableMiddleH = bottomBarY - 14 - (sepY + 10);
            int totalCards = (_showDateOption ? 1 : 0) + _suggestions.Count;

            int cardH = 50; // 调大卡片高度以契合 21f 字阶
            if (totalCards > 0)
            {
                cardH = Math.Clamp((availableMiddleH - (totalCards - 1) * RowGap) / totalCards, 40, 56);
            }

            int currentY = sepY + 10;

            if (_showDateOption)
            {
                _dateRect = new Rectangle(textLeft, currentY, contentW, cardH);
                currentY += cardH + RowGap;
            }

            _suggestionRects.Clear();
            _suggestionHoverScales.Clear();
            for (int i = 0; i < _suggestions.Count; i++)
            {
                _suggestionRects.Add(new Rectangle(textLeft, currentY, contentW, cardH));
                _suggestionHoverScales.Add(1f);
                currentY += cardH + RowGap;
            }
        }

        public override void update(GameTime time)
        {
            base.update(time);

            _hoverText = null;
            _inputBox.Update(time);

            if (_speaker != null)
            {
                _speaker.Halt();
                _speaker.movementPause = 20;
                _speaker.facePlayer(Game1.player);
            }

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

            if (_showDateOption && _dateRect.Contains(x, y))
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

        // ── 渲染管线 ──────────────────────────────────────────

        public override void draw(SpriteBatch b)
        {
            int mx = Game1.getMouseX();
            int my = Game1.getMouseY();

            _hoverText = null;

            b.Draw(Game1.fadeToBlackRect, Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.Black * 0.25f);

            DrawNativeDialogueBox(b, xPositionOnScreen, yPositionOnScreen, width, height);
            DrawNativePortraitArea(b);
            DrawHeader(b);

            // 约会卡大字号绘制 (21f)
            if (_showDateOption)
            {
                ActionButtonRenderer.Draw(b, _dateRect, _dateLabel, ref _dateHoverScale, mx, my, style: ActionButtonStyle.Romantic, fontSize: CardFontSize);
            }

            // 建议卡大字号绘制 (21f)
            var scales = CollectionsMarshal.AsSpan(_suggestionHoverScales);
            for (int i = 0; i < _suggestionRects.Count; i++)
            {
                ActionButtonRenderer.Draw(b, _suggestionRects[i], _suggestions[i], ref scales[i], mx, my, fontSize: CardFontSize);
            }

            _inputBox.Draw(b);

            // 动作按钮字号绘制 (18f)
            ActionButtonRenderer.Draw(b, _sendRect, _sendLabel, ref _sendHoverScale, mx, my, style: ActionButtonStyle.Primary, fontSize: ButtonFontSize);
            ActionButtonRenderer.Draw(b, _silenceRect, _silenceLabel, ref _silenceHoverScale, mx, my, style: ActionButtonStyle.Danger, fontSize: ButtonFontSize);

            // 悬停气泡提示处理
            if (_hasFriendship && !_friendshipJewel.IsEmpty && _friendshipJewel.Contains(mx, my))
            {
                _hoverText = $"{_friendshipHearts}/{_maxHearts}<";
                SpriteText.drawStringWithScrollBackground(b, _hoverText, _friendshipJewel.Center.X - SpriteText.getWidthOfString(_hoverText, 999999) / 2, _friendshipJewel.Y - 64, "", 1f, null, SpriteText.ScrollTextAlignment.Left);
            }
            else
            {
                for (int i = 0; i < _suggestionRects.Count; i++)
                {
                    if (_suggestionRects[i].Contains(mx, my))
                    {
                        DialogueTextInputMenu.DrawHoverTextCustom(b, _suggestions[i]);
                        break;
                    }
                }
            }

            drawMouse(b);
        }

        private static void DrawNativeDialogueBox(SpriteBatch b, int xPos, int yPos, int boxWidth, int boxHeight)
        {
            b.Draw(Game1.mouseCursors, new Rectangle(xPos, yPos, boxWidth, boxHeight), new Rectangle(306, 320, 16, 16), Color.White);

            b.Draw(Game1.mouseCursors, new Rectangle(xPos, yPos - 20, boxWidth, 24), new Rectangle(275, 313, 1, 6), Color.White);
            b.Draw(Game1.mouseCursors, new Rectangle(xPos + 12, yPos + boxHeight, boxWidth - 20, 32), new Rectangle(275, 328, 1, 8), Color.White);
            b.Draw(Game1.mouseCursors, new Rectangle(xPos - 32, yPos + 24, 32, boxHeight - 28), new Rectangle(264, 325, 8, 1), Color.White);
            b.Draw(Game1.mouseCursors, new Rectangle(xPos + boxWidth, yPos, 28, boxHeight), new Rectangle(293, 324, 7, 1), Color.White);

            b.Draw(Game1.mouseCursors, new Vector2(xPos - 44, yPos - 28), new Rectangle(261, 311, 14, 13), Color.White, 0f, Vector2.Zero, 4f, SpriteEffects.None, 0.87f);
            b.Draw(Game1.mouseCursors, new Vector2(xPos + boxWidth - 8, yPos - 28), new Rectangle(291, 311, 12, 11), Color.White, 0f, Vector2.Zero, 4f, SpriteEffects.None, 0.87f);
            b.Draw(Game1.mouseCursors, new Vector2(xPos + boxWidth - 8, yPos + boxHeight - 8), new Rectangle(291, 326, 12, 12), Color.White, 0f, Vector2.Zero, 4f, SpriteEffects.None, 0.87f);
            b.Draw(Game1.mouseCursors, new Vector2(xPos - 44, yPos + boxHeight - 4), new Rectangle(261, 327, 14, 11), Color.White, 0f, Vector2.Zero, 4f, SpriteEffects.None, 0.87f);
        }

        private void DrawNativePortraitArea(SpriteBatch b)
        {
            if (width < 642) return;

            int xPositionOfPortraitArea = xPositionOnScreen + width - 448 + 4;
            int widthOfPortraitArea = xPositionOnScreen + width - xPositionOfPortraitArea;

            b.Draw(Game1.mouseCursors, new Rectangle(xPositionOfPortraitArea - 40, yPositionOnScreen, 36, height), new Rectangle(278, 324, 9, 1), Color.White);
            b.Draw(Game1.mouseCursors, new Vector2(xPositionOfPortraitArea - 40, yPositionOnScreen - 20), new Rectangle(278, 313, 10, 7), Color.White, 0f, Vector2.Zero, 4f, SpriteEffects.None, 0.88f);
            b.Draw(Game1.mouseCursors, new Vector2(xPositionOfPortraitArea - 40, yPositionOnScreen + height), new Rectangle(278, 328, 10, 8), Color.White, 0f, Vector2.Zero, 4f, SpriteEffects.None, 0.88f);

            int portraitBoxX = xPositionOfPortraitArea + 76;
            int portraitBoxY = yPositionOnScreen + height / 2 - 148 - 36;
            b.Draw(Game1.mouseCursors, new Vector2(xPositionOfPortraitArea - 8, yPositionOnScreen), new Rectangle(583, 411, 115, 97), Color.White, 0f, Vector2.Zero, 4f, SpriteEffects.None, 0.88f);

            if (_npcPortrait != null && !_portraitSourceRect.IsEmpty)
            {
                b.Draw(_npcPortrait, new Vector2(portraitBoxX + 16, portraitBoxY + 24), _portraitSourceRect, Color.White, 0f, Vector2.Zero, 4f, SpriteEffects.None, 0.88f);
            }

            string speakerName = _speaker?.displayName ?? _speaker?.Name ?? "";
            if (!string.IsNullOrEmpty(speakerName))
            {
                SpriteText.drawStringHorizontallyCenteredAt(b, speakerName, xPositionOfPortraitArea + widthOfPortraitArea / 2, portraitBoxY + 296 + 16, 999999, -1, 999999, 1f, 0.88f, false, null, 99999);
            }

            if (_hasFriendship && !_friendshipJewel.IsEmpty)
            {
                Rectangle jewelSource = (_friendshipHearts >= 10 || _friendshipHearts >= _maxHearts)
                    ? new Rectangle(269, 494, 11, 11)
                    : new Rectangle(
                        Math.Max(140, 140 + (int)(Game1.currentGameTime.TotalGameTime.TotalMilliseconds % 1000.0 / 250.0) * 11),
                        Math.Max(532, 532 + _friendshipHearts / 2 * 11),
                        11, 11);

                b.Draw(Game1.mouseCursors, new Vector2(_friendshipJewel.X, _friendshipJewel.Y), jewelSource, Color.White, 0f, Vector2.Zero, 4f, SpriteEffects.None, 0.88f);
            }
        }

        private void DrawHeader(SpriteBatch b)
        {
            int textLeft = xPositionOnScreen + TextLeftPadding;
            int topY = yPositionOnScreen + 20;

            CustomFontManager.DrawStringBold(b, _titleText, new Vector2(textLeft, topY), BioEditorMenu.TextPrimary, TitleFontSize);
            CustomFontManager.DrawString(b, _subtitleText, new Vector2(textLeft, topY + 26), BioEditorMenu.TextMuted, SubtitleFontSize);

            int sepY = topY + HeaderHeight;
            int xPositionOfPortraitArea = xPositionOnScreen + width - 448 + 4;
            int textWidth = (xPositionOfPortraitArea - 40 - 20) - textLeft;
            b.Draw(Game1.staminaRect, new Rectangle(textLeft, sepY, textWidth, 1), Color.Gray * 0.35f);
        }

        private void Submit(string text)
        {
            Game1.exitActiveMenu();
            _closed = true;

            AsyncBuilder.Instance.ClearCooldown();

            string farmerResponse = text ?? string.Empty;
            if (farmerResponse.Contains('@') && GetSafePlayerName() is string pName)
            {
                farmerResponse = farmerResponse.Replace("@", pName);
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

        /// <summary>安全获取玩家姓名（防止无头测试环境未初始化 Farmer.name NetField 抛 NRE）。</summary>
        private static string? GetSafePlayerName()
        {
            try
            {
                if (Game1.player != null && !string.IsNullOrEmpty(Game1.player.Name))
                    return Game1.player.Name;
            }
            catch (NullReferenceException)
            {
                // BOUNDARY：无头测试环境中未初始化 Name 字段的 Farmer 垫片
            }
            return null;
        }
    }
}