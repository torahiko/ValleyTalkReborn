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

        private readonly NPC _speaker;
        private readonly string _npcLineSanitized;
        private readonly List<string> _suggestions;
        private readonly bool _showDateOption;

        private readonly List<Rectangle> _suggestionRects = new List<Rectangle>();
        private readonly List<float> _suggestionHoverScales = new List<float>();

        private Rectangle _dateRect;
        private Rectangle _customRect;
        private Rectangle _silenceRect;

        private float _dateHoverScale = 1f;
        private float _customHoverScale = 1f;
        private float _silenceHoverScale = 1f;

        private readonly string _dateLabel;
        private readonly string _customLabel;
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
            _customLabel = Util.GetString("choiceMenuCustomReply", returnNull: true)
                           ?? (isZh ? "【自定义回复...】" : "【Type your own reply...】");
            _silenceLabel = Util.GetString("choiceMenuSilence", returnNull: true)
                            ?? (isZh ? "【保持沉默】" : "【Stay silent】");

            Layout();

            _openLocation = Game1.currentLocation;
        }

        private void Layout()
        {
            int rowCount = _suggestions.Count + 2 + (_showDateOption ? 1 : 0);

            width = Math.Min(MaxPanelWidth, Game1.uiViewport.Width - SideMargin);
            height = rowCount * RowHeight + (rowCount - 1) * RowGap + PanelPadding * 2;
            xPositionOnScreen = (Game1.uiViewport.Width - width) / 2;
            yPositionOnScreen = Game1.uiViewport.Height - height - BottomMargin;

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

            _customRect = new Rectangle(rowX, rowY, rowW, RowHeight);
            rowY += RowHeight + RowGap;
            _silenceRect = new Rectangle(rowX, rowY, rowW, RowHeight);
        }

        public override void update(GameTime time)
        {
            base.update(time);

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
            if (key == Keys.Escape)
            {
                Silence();
                return;
            }
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

            if (_customRect.Contains(x, y))
            {
                OpenFloatingInput();
                return;
            }

            if (_silenceRect.Contains(x, y))
            {
                Silence();
            }
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

            DialogueTextInputMenu.DrawAnimatedActionButton(b, _customRect, _customLabel, ref _customHoverScale, mx, my, isPrimary: true);
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

        private void OpenFloatingInput()
        {
            Game1.exitActiveMenu();
            _closed = true;

            TextInputManager.RequestTextInput(
                Util.GetString("uiYourResponse") ?? "你的回应",
                _speaker,
                _speaker.LoadedDialogueKey ?? "default",
                ReconstructHistory());
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
