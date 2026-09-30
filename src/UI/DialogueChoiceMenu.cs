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
    /// VT-UI-005 Stage 2：纯选择卡片面板。
    /// 原版对话框只播完纯台词，关闭瞬间由 OnMenuChanged 换壳到本面板，
    /// 统一分发建议卡 / 自定义回复 / 保持沉默 / 约会四路出口。
    /// </summary>
    internal class DialogueChoiceMenu : IClickableMenu
    {
        private const int MaxPanelWidth = 640;
        private const int SideMargin = 64;
        private const int BottomMargin = 48;
        private const int RowHeight = 56;
        private const int RowGap = 8;
        private const int PanelPadding = 16;
        private const int InputRowHeight = 48;
        private const int ActionButtonWidth = 96;

        private readonly NPC _speaker;
        private readonly string _npcLineSanitized;
        private readonly List<string> _suggestions;
        private readonly bool _showDateOption;

        private readonly DialogueTextInputBox _inputBox;

        private readonly List<Rectangle> _suggestionRects = new List<Rectangle>();
        private readonly List<float> _suggestionHoverScales = new List<float>();

        private Rectangle _dateRect;
        private Rectangle _sendRect;
        private Rectangle _silenceRect;

        private float _dateHoverScale = 1f;
        private float _sendHoverScale = 1f;
        private float _silenceHoverScale = 1f;

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
            _dateLabel = Util.GetString("choiceMenuDate", returnNull: true)
                         ?? (isZh ? "【敲定约会地点...】" : "【Choose Date Location...】");
            _sendLabel = Util.GetString("choiceMenuSend", returnNull: true)
                         ?? (isZh ? "发送" : "Send");
            _silenceLabel = Util.GetString("choiceMenuSilence", returnNull: true)
                            ?? (isZh ? "【保持沉默】" : "【Stay silent】");

            _inputBox = new DialogueTextInputBox(200)
            {
                AllowNewlines = false,
                UseCustomFont = true,
                CustomFontSize = 18f,
                DrawFrame = true,
                ShowCharacterCount = false,
                Selected = true,
                PlaceholderText = Util.GetString("choiceMenuInputPlaceholder", returnNull: true)
                                  ?? (isZh ? "在此输入你想说的话…（回车发送）" : "Type your reply... (Enter to send)"),
                PlaceholderColor = new Color(175, 145, 115),
                TextColor = BioEditorMenu.TextPrimary
            };

            Layout();

            _openLocation = Game1.currentLocation;

            _inputBox.Selected = true;
            Game1.keyboardDispatcher.Subscriber = _inputBox;
        }

        private void Layout()
        {
            // 行结构自上而下：[约会卡(若有)] → [建议卡 0~3 张] → [输入行：输入条 + 发送钮 + 沉默钮]
            width = Math.Min(MaxPanelWidth, Game1.uiViewport.Width - SideMargin);
            height = (_showDateOption ? RowHeight + RowGap : 0)
                     + _suggestions.Count * (RowHeight + RowGap)
                     + InputRowHeight
                     + PanelPadding * 2;
            xPositionOnScreen = (Game1.uiViewport.Width - width) / 2;
            yPositionOnScreen = Math.Max(0, Game1.uiViewport.Height - height - BottomMargin);

            int rowX = xPositionOnScreen + PanelPadding;
            int rowW = width - PanelPadding * 2;
            int rowY = yPositionOnScreen + PanelPadding;

            if (_showDateOption)
            {
                _dateRect = new Rectangle(rowX, rowY, rowW, RowHeight);
                rowY += RowHeight + RowGap;
            }

            _suggestionRects.Clear();
            _suggestionHoverScales.Clear();
            for (int i = 0; i < _suggestions.Count; i++)
            {
                _suggestionRects.Add(new Rectangle(rowX, rowY, rowW, RowHeight));
                _suggestionHoverScales.Add(1f);
                rowY += RowHeight + RowGap;
            }

            int inputW = rowW - ActionButtonWidth * 2 - RowGap * 2;
            _inputBox.Position = new Vector2(rowX, rowY);
            _inputBox.Extent = new Vector2(inputW, InputRowHeight);
            _inputBox.InvalidateLayout();

            _sendRect = new Rectangle(rowX + inputW + RowGap, rowY, ActionButtonWidth, InputRowHeight);
            _silenceRect = new Rectangle(_sendRect.Right + RowGap, rowY, ActionButtonWidth, InputRowHeight);
        }

        public override void update(GameTime time)
        {
            base.update(time);

            _inputBox.Update(time);

            // 面板存活期间钉住 NPC：不游走、面向玩家。
            _speaker.Halt();
            _speaker.movementPause = 20;
            _speaker.facePlayer(Game1.player);

            // 事件打断或切图：自毁且不落沉默记录，玩家可重新搭话。
            if (Game1.eventUp || Game1.currentLocation != _openLocation)
            {
                SelfDestruct("world state changed (event started or location switched)");
            }
        }

        public override void receiveKeyPress(Keys key)
        {
            // (i) Esc 最高优先：Esc 沉默语义不受 menuButton 过滤影响。
            if (key == Keys.Escape)
            {
                Silence();
                return;
            }

            // (ii) 裸 Enter 提交收敛到菜单层（对齐 DialogueTextInputMenu 范式）；空文本 no-op，坞保持打开。
            if (key == Keys.Enter)
            {
                if (!string.IsNullOrWhiteSpace(_inputBox.Text))
                {
                    Game1.playSound("coin");
                    Submit(_inputBox.Text);
                }
                return;
            }

            // (iii) menuButton（含 Esc 键位）显式拦截，防止穿透关菜单导致坞状态悬空。
            if (Game1.options.doesInputListContain(Game1.options.menuButton, key))
                return;

            // (iv) 剪贴板与光标编辑族透传给盒内。
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

            // (v) 其余忽略。
        }

        public override void receiveLeftClick(int x, int y, bool playSound = true)
        {
            for (int i = 0; i < _suggestionRects.Count; i++)
            {
                if (_suggestionRects[i].Contains(x, y))
                {
                    Submit(_suggestions[i]);
                    return;
                }
            }

            if (_dateRect.Contains(x, y))
            {
                OpenDateMenu();
                return;
            }

            if (_sendRect.Contains(x, y))
            {
                Submit(_inputBox.Text);
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

            // 面板内非控件区 → 收回焦点；面板外 → 沉默关闭。
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

            IClickableMenu.drawTextureBox(b, xPositionOnScreen - 8, yPositionOnScreen - 8, width + 16, height + 16, Color.White);
            b.Draw(Game1.staminaRect, new Rectangle(xPositionOnScreen, yPositionOnScreen, width, height), new Color(245, 230, 205));

            if (_showDateOption)
            {
                DialogueTextInputMenu.DrawAnimatedActionButton(b, _dateRect, _dateLabel, ref _dateHoverScale, mx, my);
            }

            for (int i = 0; i < _suggestionRects.Count; i++)
            {
                float hoverScale = _suggestionHoverScales[i];
                DialogueTextInputMenu.DrawAnimatedActionButton(b, _suggestionRects[i], _suggestions[i], ref hoverScale, mx, my);
                _suggestionHoverScales[i] = hoverScale;
            }

            _inputBox.Draw(b);
            DialogueTextInputMenu.DrawAnimatedActionButton(b, _sendRect, _sendLabel, ref _sendHoverScale, mx, my, isPrimary: true);
            DialogueTextInputMenu.DrawAnimatedActionButton(b, _silenceRect, _silenceLabel, ref _silenceHoverScale, mx, my);

            // 卡片排版可能截断长建议文本，悬停时以气泡回显完整原文
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

        private void Submit(string text)
        {
            Game1.exitActiveMenu();
            _closed = true;

            // 用户主动选择选项，清零冷却防止请求被静默丢弃
            AsyncBuilder.Instance.ClearCooldown();

            string farmerResponse = text ?? string.Empty;
            if (farmerResponse.Contains('@') && Game1.player != null)
            {
                farmerResponse = farmerResponse.Replace("@", Game1.player.Name);
            }

            // TIE-005：观察性钩子——玩家选项文本交由镇事件引擎按关键词组记录运行时标志。
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

        /// <summary>
        /// 沉默 = 会话边界：对齐 6b48bf3a Vanilla Silent 语义，只落统一 i18n 会话结束标记，
        /// 不再写入玩家沉默文本（该语义已由 6b48bf3a 废弃）。
        /// </summary>
        private void Silence()
        {
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

        /// <summary>
        /// 以当前会话历史为基底重建本轮上下文；NPC 本轮台词被去重过滤时补插一次。
        /// </summary>
        private List<ConversationElement> ReconstructHistory()
        {
            var context = DialogueBuilder.Instance.GetContext(_speaker.Name);
            var history = context?.ChatHistory != null
                ? new List<ConversationElement>(context.ChatHistory)
                : new List<ConversationElement>();

            // 空白台词不入库（与 AsyncBuilder 占位框的 IsNullOrWhiteSpace 保护同语义）
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
