// AiStreamingDialogueBox.cs
// VT-STREAM-01-CoreDialogueBox: AI 流式对白框。
//
// 继承自原版 DialogueBox 并完全接管 update/draw：打字机推进、标点阻尼、
// 流式追加与游标吸附均由本类驱动，不调用 base.draw(b)（避免基类单体逻辑
// 造成双重绘制与空对白崩溃）。NPC 对白文本一律走 SpriteText.drawString，
// 不使用 Game1.dialogueFont。

using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewModdingAPI;
using StardewValley;
using StardewValley.BellsAndWhistles;
using StardewValley.Menus;

namespace ValleytalkReborn.UI
{
    /// <summary>流式对白框的内部状态。</summary>
    public enum StreamingDialogueState
    {
        /// <summary>等待首批 token，尚无任何文本可显示。</summary>
        Thinking,

        /// <summary>正在逐字揭示文本。</summary>
        Typing,

        /// <summary>本页已打完，等待玩家翻页。</summary>
        WaitingForPageTurn,

        /// <summary>流已结束且全文揭示完毕。</summary>
        Complete,

        /// <summary>流异常中断，显示错误信息。</summary>
        Faulted
    }

    /// <summary>
    /// AI 流式对白框：以打字机方式逐字揭示 LLM 流式返回的文本，
    /// 并在标点处注入可变停顿以获得自然的朗读节奏。
    /// </summary>
    public class AiStreamingDialogueBox : DialogueBox
    {
        #region 常量

        private const int DialogueWidth = 1200;
        private const int DialogueHeight = 384;

        /// <summary>无头/未初始化视口时使用的标称视口高度。</summary>
        private const int FallbackViewportHeight = 720;

        /// <summary>文字区相对对白框的左/上内边距。</summary>
        private const int TextPadding = 32;

        /// <summary>有头像布局时右侧预留的头像区宽度。</summary>
        private const int PortraitReserve = 448;

        /// <summary>基础打字间隔（毫秒）。</summary>
        private const int BaseTypeDelayMs = 35;

        private const int LightPunctuationDelayMs = 140;
        private const int LongPunctuationDelayMs = 380;
        private const int DashPunctuationDelayMs = 320;
        private const int NewlineDelayMs = 450;

        /// <summary>思考态小圆点切换间隔（毫秒）。</summary>
        private const int ThinkingDotsIntervalMs = 400;

        #endregion

        #region 静态数据（热路径零分配）

        /// <summary>思考态三段小圆点，预分配以避免 update 内字符串拼接。</summary>
        private static readonly string[] ThinkingDotsTexts = { ".", "..", "..." };

        private const string LightPunctuation = ",，、;；";
        private const string LongPunctuation = ".。!！?？";
        private const string DashPunctuation = "…—-";

        #endregion

        #region 瞬态状态字段

        private StreamingDialogueState _state;
        private string _fullText;
        private string _displayedPageText;
        private int _characterIndex;
        private bool _isStreamComplete;
        private bool _isFastForwardActive;
        private int _typeTimerMs;
        private int _thinkingClockMs;
        private int _thinkingDotsIndex;
        private string _errorMessage;

        #endregion

        #region 属性

        /// <summary>当前流式状态。</summary>
        public StreamingDialogueState State => _state;

        /// <summary>已揭示的字符数（等价于 SpriteText 的 characterPosition）。</summary>
        public int CharacterIndex => _characterIndex;

        /// <summary>当前页已缓冲的完整文本。</summary>
        public string DisplayedPageText => _displayedPageText;

        /// <summary>是否处于快速跳过（游标吸附）模式。</summary>
        public bool IsFastForwardActive => _isFastForwardActive;

        #endregion

        #region 构造

        /// <summary>
        /// 无头安全视口高度：uiViewport 是 xTile.Dimensions.Rectangle 结构体，
        /// 永不为 null；无图形上下文时其 Height 为 0，此时降级到 720p 标称高度。
        /// </summary>
        private static int ComputeViewportHeight()
            => Game1.uiViewport.Height > 0 ? Game1.uiViewport.Height : FallbackViewportHeight;

        /// <summary>对白框水平居中坐标。</summary>
        private static int ComputeBoxX()
            => (int)Utility.getTopLeftPositionForCenteringOnScreen(DialogueWidth, DialogueHeight, 0, 0).X;

        /// <summary>对白框底部锚定坐标。</summary>
        private static int ComputeBoxY()
            => ComputeViewportHeight() - DialogueHeight - 64;

        /// <summary>
        /// 构造流式对白框。显式调用 base(x, y, width, height)，
        /// 绝不调用 base(Dialogue) 以免误触 DialogueBox_Ctor_Patch。
        /// </summary>
        /// <param name="speaker">对白说话者；为 null 时降级为无头像宽布局。</param>
        /// <param name="initialText">初始文本；为空则先进入思考态。</param>
        public AiStreamingDialogueBox(NPC speaker, string initialText = "")
            : base(ComputeBoxX(), ComputeBoxY(), DialogueWidth, DialogueHeight)
        {
            // 装配说话者。Game1.content 为 null 时（无头环境）原版 Dialogue 构造函数
            // 会在 TranslateArraysOfStrings 内部空引用，改用无初始化垫片以保持可构造性。
            if (speaker != null)
            {
                if (Game1.content != null)
                {
                    try
                    {
                        this.characterDialogue = new StardewValley.Dialogue(speaker, "", "");
                    }
                    catch (Exception ex)
                    {
                        ModEntry.SMonitor?.Log(
                            $"[AiStreamingDialogueBox] Failed to construct Dialogue for '{speaker.Name}'; using uninitialized shim. ({ex.GetType().Name}: {ex.Message})",
                            LogLevel.Warn);
                        this.characterDialogue = null;
                    }
                }

                if (this.characterDialogue == null)
                {
                    this.characterDialogue = (StardewValley.Dialogue)
                        System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(StardewValley.Dialogue));
                    this.characterDialogue.speaker = speaker;
                }
            }

            this.friendshipJewel = new Rectangle(this.x + this.width - 64, this.y + 256, 44, 44);
            this.transitioning = false;
            this.transitionInitialized = true;

            // 翻页小箭头沿用基类 public dialogueIcon 字段；无头环境无内容管线，跳过纹理载入。
            if (Game1.content != null)
            {
                this.dialogueIcon = new TemporaryAnimatedSprite("LooseSprites\\Cursors",
                    new Rectangle(289, 342, 11, 12), 80f, 11, 999999,
                    new Vector2(this.x + this.width - 40, this.y + this.height - 44),
                    false, false, 0.89f, 0f, Color.White, 4f, 0f, 0f, 0f, true);
            }

            _fullText = string.Empty;
            _displayedPageText = string.Empty;
            _typeTimerMs = BaseTypeDelayMs;
            _state = string.IsNullOrEmpty(initialText)
                ? StreamingDialogueState.Thinking
                : StreamingDialogueState.Typing;

            if (_state == StreamingDialogueState.Typing)
                SetContent(initialText, false);
        }

        #endregion

        #region 内容写入

        /// <summary>整体替换对白内容并重置游标。</summary>
        /// <param name="fullText">完整文本。</param>
        /// <param name="isComplete">流是否已结束。</param>
        public void SetContent(string fullText, bool isComplete = false)
        {
            _fullText = Sanitize(fullText);
            _displayedPageText = _fullText;
            _characterIndex = 0;
            _isStreamComplete = isComplete;
            _isFastForwardActive = false;
            _typeTimerMs = BaseTypeDelayMs;

            if (_displayedPageText.Length == 0)
            {
                _state = StreamingDialogueState.Thinking;
                return;
            }

            _state = StreamingDialogueState.Typing;

            if (isComplete)
                TryEnterCompleteState();
        }

        /// <summary>
        /// 追加一段流式增量文本。快进模式下游标立即吸附到新文本末端。
        /// </summary>
        /// <param name="chunk">增量文本。</param>
        /// <param name="isComplete">追加后流是否已结束。</param>
        public void AppendContent(string chunk, bool isComplete = false)
        {
            string addition = Sanitize(chunk);
            if (addition.Length == 0)
            {
                if (isComplete)
                {
                    _isStreamComplete = true;
                    TryEnterCompleteState();
                }
                return;
            }

            if (_state == StreamingDialogueState.Faulted)
                return;

            _fullText += addition;
            _displayedPageText = _fullText;

            // 首批有效字符到达：脱离思考态。
            if (_state == StreamingDialogueState.Thinking)
                _state = StreamingDialogueState.Typing;

            // 快进模式下游标保持吸附到最新末尾，等待后续增量。
            if (_isFastForwardActive)
                _characterIndex = _displayedPageText.Length;
            else if (_characterIndex > _displayedPageText.Length)
                _characterIndex = _displayedPageText.Length;

            _isStreamComplete = isComplete;

            if (isComplete)
                TryEnterCompleteState();
        }

        /// <summary>标记流异常并展示错误信息。</summary>
        /// <param name="errorMessage">面向玩家的错误描述。</param>
        public void SetFaulted(string errorMessage)
        {
            _errorMessage = errorMessage;
            _fullText = string.Empty;
            _displayedPageText = string.Empty;
            _characterIndex = 0;
            _isStreamComplete = true;
            _isFastForwardActive = false;
            _state = StreamingDialogueState.Faulted;
        }

        #endregion

        #region 标点阻尼

        /// <summary>
        /// 计算揭示第 <paramref name="revealedCount"/> 个字符之后，到揭示下一个字符
        /// 之前应当等待的毫秒数。连续同类标点折叠：若后一字符仍为标点，
        /// 当前标点不注入额外停顿，仅在标点组末字触发长停顿。
        /// </summary>
        /// <param name="text">当前页文本。</param>
        /// <param name="revealedCount">已揭示的字符数（刚揭示的字符下标为 revealedCount - 1）。</param>
        internal static int ComputeDelayMs(string text, int revealedCount)
        {
            if (text == null || revealedCount <= 0 || revealedCount > text.Length)
                return BaseTypeDelayMs;

            char current = text[revealedCount - 1];

            if (current == '\n')
                return NewlineDelayMs;

            int punctuationDelay = GetPunctuationDelayMs(current);
            if (punctuationDelay == 0)
                return BaseTypeDelayMs;

            // 折叠：下一个字符仍是标点时，本字符不注入停顿。
            if (revealedCount < text.Length && GetPunctuationDelayMs(text[revealedCount]) != 0)
                return BaseTypeDelayMs;

            return BaseTypeDelayMs + punctuationDelay;
        }

        /// <summary>返回单字符的标点停顿毫秒数；非标点返回 0。</summary>
        private static int GetPunctuationDelayMs(char c)
        {
            if (LightPunctuation.IndexOf(c) >= 0)
                return LightPunctuationDelayMs;
            if (LongPunctuation.IndexOf(c) >= 0)
                return LongPunctuationDelayMs;
            if (DashPunctuation.IndexOf(c) >= 0)
                return DashPunctuationDelayMs;
            return 0;
        }

        #endregion

        #region 生命周期

        /// <summary>推进打字机、思考动画与翻页箭头。</summary>
        /// <param name="time">帧时间。</param>
        public override void update(GameTime time)
        {
            int elapsed = time.ElapsedGameTime.Milliseconds;

            dialogueIcon?.update(time);

            if (_state == StreamingDialogueState.Thinking)
            {
                _thinkingClockMs += elapsed;
                if (_thinkingClockMs >= ThinkingDotsIntervalMs)
                {
                    _thinkingClockMs = 0;
                    _thinkingDotsIndex = (_thinkingDotsIndex + 1) % ThinkingDotsTexts.Length;
                }
                return;
            }

            if (_state == StreamingDialogueState.Typing && !_isFastForwardActive)
            {
                _typeTimerMs -= elapsed;
                while (_typeTimerMs <= 0 && _characterIndex < _displayedPageText.Length)
                {
                    _typeTimerMs += ComputeDelayMs(_displayedPageText, _characterIndex + 1);
                    _characterIndex++;

                    if (Game1.options.dialogueTyping
                        && _characterIndex > 1
                        && _characterIndex < _displayedPageText.Length)
                        Game1.playSound("dialogueCharacter");
                }
            }

            TryEnterCompleteState();
        }

        /// <summary>流已结束且全文揭示完毕时切换到 Complete 态。</summary>
        private void TryEnterCompleteState()
        {
            if (_state != StreamingDialogueState.Typing)
                return;
            if (!_isStreamComplete || _characterIndex < _displayedPageText.Length)
                return;

            _state = StreamingDialogueState.Complete;
            Game1.playSound("dialogueCharacterClose");
        }

        #endregion

        #region 绘制

        /// <summary>绘制对白框、头像、文本与翻页箭头。绝不调用 base.draw(b)。</summary>
        /// <param name="b">精灵批次。</param>
        public override void draw(SpriteBatch b)
        {
            this.drawBox(b, this.x, this.y, this.width, this.height);

            bool portrait = this.isPortraitBox();
            if (portrait)
                this.drawPortrait(b);

            int textX = this.x + TextPadding;
            int textY = this.y + TextPadding;
            int textWidth = portrait ? (this.width - PortraitReserve) : (this.width - TextPadding * 2);

            switch (_state)
            {
                case StreamingDialogueState.Thinking:
                    SpriteText.drawString(b, ThinkingDotsTexts[_thinkingDotsIndex], textX, textY, width: textWidth);
                    break;

                case StreamingDialogueState.Typing:
                case StreamingDialogueState.WaitingForPageTurn:
                case StreamingDialogueState.Complete:
                    SpriteText.drawString(b, _displayedPageText, textX, textY,
                        characterPosition: _characterIndex, width: textWidth);
                    break;

                case StreamingDialogueState.Faulted:
                    SpriteText.drawString(b, _errorMessage, textX, textY, width: textWidth);
                    break;
            }

            if (_state == StreamingDialogueState.WaitingForPageTurn || _state == StreamingDialogueState.Complete)
                this.dialogueIcon?.draw(b, true, 0, 0, 1f);

            base.drawMouse(b);
        }

        #endregion

        #region 输入

        /// <summary>首击拉满当前页进度；Complete 态则关闭。</summary>
        /// <param name="x">点击 X。</param>
        /// <param name="y">点击 Y。</param>
        /// <param name="playSound">是否播放音效。</param>
        public override void receiveLeftClick(int x, int y, bool playSound = true)
        {
            if (_state == StreamingDialogueState.Typing)
            {
                _isFastForwardActive = true;
                _characterIndex = _displayedPageText.Length;
                TryEnterCompleteState();
                return;
            }

            if (_state == StreamingDialogueState.Complete)
            {
                Game1.playSound("smallSelect");
                Close();
            }
        }

        /// <summary>Escape 关闭；Space 或 Action 键等同左键。</summary>
        /// <param name="key">按下的按键。</param>
        public override void receiveKeyPress(Keys key)
        {
            if (key == Keys.Escape)
            {
                Close();
                return;
            }

            if (key == Keys.Space || Game1.options.doesInputListContain(Game1.options.actionButton, key))
                receiveLeftClick(0, 0);
        }

        /// <summary>原子关闭：退出菜单并复原对白序列与玩家移动状态。</summary>
        public void Close()
        {
            Game1.exitActiveMenu();

            Game1.dialogueUp = false;
            Game1.currentSpeaker = null;
            if (!Game1.eventUp && !Game1.isWarping)
            {
                Game1.player.CanMove = true;
                Game1.player.movementDirections.Clear();
            }

            Game1.player.forceCanMove();
        }

        #endregion

        #region 工具

        /// <summary>
        /// 纯文本回退：剥离对白控制标签（花括号标记），
        /// 避免生成内容中的标签意外进入本组件导致渲染异常。
        /// </summary>
        private static string Sanitize(string text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;

            return text.IndexOf('{') < 0 && text.IndexOf('}') < 0
                ? text
                : text.Replace("{", "").Replace("}", "");
        }

        #endregion
    }
}
