// AiStreamingDialogueBox.cs
// VT-STREAM-01-CoreDialogueBox: AI 流式对白框。
//
// 继承自原版 DialogueBox 并完全接管 update/draw：打字机推进、标点阻尼、
// 流式追加与游标吸附均由本类驱动，不调用 base.draw(b)（避免基类单体逻辑
// 造成双重绘制与空对白崩溃）。NPC 对白文本一律走 SpriteText.drawString，
// 不使用 Game1.dialogueFont。

using System;
using System.Collections.Generic;
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

        /// <summary>
        /// 单页允许的最大文字高度（像素）。约 4 行 SpriteText 高度，
        /// 用于避让对白框底边与金黄色翻页小箭头。
        /// </summary>
        public const int MaxPageHeight = 200;

        /// <summary>
        /// 单次输入允许的最大页数（含已翻页、当前页与 backlog）。
        /// 超出即熔断，防止 LLM 异常无限吐字撑爆内存。
        /// </summary>
        private const int MaxPageCount = 10;

        /// <summary>页数熔断时追加在末页的截断标记。</summary>
        private const string TruncationMarker = "...";

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
        private string _displayedPageText;
        private int _characterIndex;
        private bool _isStreamComplete;
        private bool _isFastForwardActive;
        private int _typeTimerMs;
        private int _thinkingClockMs;
        private int _thinkingDotsIndex;
        private string _errorMessage;

        /// <summary>已封口、等待玩家翻页才能继续显示的后续页面。</summary>
        private readonly Queue<string> _backlogPages = new();

        /// <summary>已翻过的页面归档，仅供 GetFullDialogueText 复原全文本。</summary>
        private readonly List<string> _pageHistory = new();

        /// <summary>当前页是否已封口（显式 '#' 或高度溢出触发），不再接受新文本。</summary>
        private bool _isCurrentPageSealed = false;

        /// <summary>页数预算是否已耗尽；为 true 时后续文本一律丢弃，避免重复追加截断标记。</summary>
        private bool _isPageBudgetExhausted = false;

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

        /// <summary>当前页是否已封口等待翻页。</summary>
        public bool IsCurrentPageSealed => _isCurrentPageSealed;

        /// <summary>尚未显示的后续页数。</summary>
        public int PendingPageCount => _backlogPages.Count;

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
            _displayedPageText = string.Empty;
            _backlogPages.Clear();
            _pageHistory.Clear();
            _isCurrentPageSealed = false;
            _isPageBudgetExhausted = false;
            _characterIndex = 0;
            _isFastForwardActive = false;
            _typeTimerMs = BaseTypeDelayMs;
            _isStreamComplete = isComplete;

            IngestText(Sanitize(fullText));

            // 空首屏只有两种情形：完全无文本 -> 思考态；首字符即 '#' 导致
            // 全量落在 backlog -> 仍须进入 Typing，否则玩家永远等不到第一次翻页。
            if (_displayedPageText.Length == 0 && _backlogPages.Count == 0)
            {
                _state = StreamingDialogueState.Thinking;
                return;
            }

            _state = StreamingDialogueState.Typing;

            TryEnterWaitingForPageTurnState();
            TryEnterCompleteState();
        }

        /// <summary>
        /// 追加一段流式增量文本。增量经分页管线写入当前页或 backlog；
        /// 快进模式下游标立即吸附到当前页末端。
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
                    TryEnterWaitingForPageTurnState();
                    TryEnterCompleteState();
                }
                return;
            }

            if (_state == StreamingDialogueState.Faulted)
                return;

            IngestText(addition);

            // 首批有效字符到达：脱离思考态。
            if (_state == StreamingDialogueState.Thinking)
                _state = StreamingDialogueState.Typing;

            // 快进模式下游标保持吸附到当前页末端，等待后续增量。
            if (_isFastForwardActive)
                _characterIndex = _displayedPageText.Length;
            else if (_characterIndex > _displayedPageText.Length)
                _characterIndex = _displayedPageText.Length;

            _isStreamComplete = isComplete;

            TryEnterWaitingForPageTurnState();
            TryEnterCompleteState();
        }

        /// <summary>
        /// 设置立绘表情码。未知/非法码降级为默认表情，绝不阻断渲染。
        /// </summary>
        /// <param name="emotionCode">情绪码，可带或不带 '$' 前缀。</param>
        public void SetEmotion(string emotionCode)
        {
            if (string.IsNullOrWhiteSpace(emotionCode) || this.characterDialogue == null)
                return;

            this.characterDialogue.CurrentEmotion = NormalizeEmotionCode(emotionCode);
        }

        /// <summary>
        /// 复原跨页全文本：已翻页归档 + 当前页 + 尚未显示的 backlog 页。
        /// 供历史记录入库与 NPC 偷听广播使用，避免分页导致前页丢失。
        /// </summary>
        public string GetFullDialogueText()
        {
            var sb = new System.Text.StringBuilder();
            foreach (var page in _pageHistory) sb.Append(page).Append(' ');
            if (!string.IsNullOrEmpty(_displayedPageText)) sb.Append(_displayedPageText).Append(' ');
            foreach (var page in _backlogPages) sb.Append(page).Append(' ');
            return sb.ToString().Trim();
        }

        /// <summary>标记流异常并展示错误信息。</summary>
        /// <param name="errorMessage">面向玩家的错误描述。</param>
        public void SetFaulted(string errorMessage)
        {
            _errorMessage = errorMessage;
            _displayedPageText = string.Empty;
            _backlogPages.Clear();
            _pageHistory.Clear();
            _isCurrentPageSealed = false;
            _characterIndex = 0;
            _isStreamComplete = true;
            _isFastForwardActive = false;
            _state = StreamingDialogueState.Faulted;
        }

        #endregion

        #region 分页

        /// <summary>文字区可用宽度：立绘布局扣 PortraitReserve，宽布局扣左右内边距。</summary>
        private int GetTextWidth()
            => this.isPortraitBox() ? (this.width - PortraitReserve) : (this.width - TextPadding * 2);

        /// <summary>
        /// 把一段文本灌入分页管线：显式 '#' 封口 + 高度溢出封口，
        /// 溢出部分压入 backlog 队尾。纯字符串处理，不触碰任何游戏态。
        /// </summary>
        private void IngestText(string text)
        {
            int cursor = 0;
            // 首段延续 backlog 尾页（流式增量语义）；'#' 之后的每段都另起新页。
            bool startNewPage = false;

            while (cursor < text.Length)
            {
                int hash = text.IndexOf('#', cursor);
                string segment = hash < 0
                    ? text.Substring(cursor)
                    : text.Substring(cursor, hash - cursor);

                if (_isCurrentPageSealed)
                {
                    EnqueueToBacklog(segment, startNewPage);
                }
                else
                {
                    // 高度溢出封口后，溢出段必须先于 '#' 之后的内容入页。
                    string overflow = TryFillCurrentPage(segment);
                    if (overflow.Length > 0)
                        EnqueueToBacklog(overflow, startNewPage);
                }

                if (hash < 0)
                    return;

                // 显式 '#' 强制封口当前页。
                _isCurrentPageSealed = true;
                cursor = hash + 1;
                startNewPage = true;
            }
        }

        /// <summary>
        /// 尝试把 <paramref name="text"/> 全部并入当前页；
        /// 返回未能容纳的剩余部分（空串表示全部容纳）。高度溢出时在安全位置截断并封口。
        /// </summary>
        private string TryFillCurrentPage(string text)
        {
            if (text.Length == 0)
                return string.Empty;

            if (_isCurrentPageSealed)
                return text;

            string candidate = _displayedPageText + text;
            int textWidth = GetTextWidth();

            if (SpriteText.getHeightOfString(candidate, textWidth) <= MaxPageHeight)
            {
                _displayedPageText = candidate;
                return string.Empty;
            }

            // 二分定位可容纳前缀：高度对前缀长度单调不减。
            int low = 1;
            int high = candidate.Length - 1;
            while (low < high)
            {
                int mid = low + (high - low + 1) / 2;
                if (SpriteText.getHeightOfString(candidate.Substring(0, mid), textWidth) <= MaxPageHeight)
                    low = mid;
                else
                    high = mid - 1;
            }

            int cut = ComputeSafeBreakIndex(candidate, low);
            _displayedPageText = candidate.Substring(0, cut);
            _isCurrentPageSealed = true;
            return candidate.Substring(cut).TrimStart(' ', '\t', '\r', '\n');
        }

        /// <summary>
        /// 安全断点：严禁从 ASCII 单词中间断开。逐字回退出词尾，使断行落在词间空格处
        /// （该空格留在本页行尾，符合「在空格处断行」语义）；中文等无词边界字符
        /// 允许就近断行。无解时至少保留 1 字符以保证推进。
        /// </summary>
        private static int ComputeSafeBreakIndex(string text, int upperBound)
        {
            int cut = Math.Max(1, Math.Min(upperBound, text.Length));
            while (cut > 1 && IsAsciiWordChar(text[cut - 1]))
                cut--;
            return cut;
        }

        /// <summary>是否为需要保持在单词内部的 ASCII 字母/数字。</summary>
        private static bool IsAsciiWordChar(char c)
            => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9');

        /// <summary>
        /// 封口后的增量写入 backlog。流式续写并入队尾页；'#' 分页另起新页。
        /// 触发 BOUNDARY 熔断时强行在末页截断并追加标记。
        /// </summary>
        /// <param name="text">待入页文本。</param>
        /// <param name="startNewPage">是否另起新页（'#' 之后为 true）。</param>
        private void EnqueueToBacklog(string text, bool startNewPage)
        {
            if (text.Length == 0 || _isPageBudgetExhausted)
                return;

            if (_backlogPages.Count > 0 && !startNewPage)
            {
                _backlogPages.Enqueue(_backlogPages.Dequeue() + text);
                return;
            }

            // 新建一页会使总页数越过上限：熔断并在末页追加标记。
            if (_pageHistory.Count + 1 + _backlogPages.Count + 1 > MaxPageCount)
            {
                _isPageBudgetExhausted = true;
                MarkLastPageTruncated();
                ModEntry.SMonitor?.Log(
                    $"[AiStreamingDialogueBox] Page budget ({MaxPageCount}) exhausted; remaining text dropped with truncation marker.",
                    LogLevel.Warn);
                return;
            }

            _backlogPages.Enqueue(text);
        }

        /// <summary>在最后一张页面（backlog 尾页，无尾页时为当前页）末尾追加截断标记。</summary>
        private void MarkLastPageTruncated()
        {
            if (_backlogPages.Count > 0)
            {
                // Queue 无索引器：必须出队后重入队，否则会在队尾追加出第二份副本。
                string tail = _backlogPages.Dequeue();
                _backlogPages.Enqueue(tail.EndsWith(TruncationMarker, StringComparison.Ordinal)
                    ? tail
                    : tail + TruncationMarker);
                return;
            }

            if (!_displayedPageText.EndsWith(TruncationMarker, StringComparison.Ordinal))
                _displayedPageText += TruncationMarker;
        }

        /// <summary>
        /// 规范化情绪码：补 '$' 前缀；非法 token 降级为默认表情 "$0"，不阻断渲染。
        /// </summary>
        private static string NormalizeEmotionCode(string emotionCode)
        {
            string code = emotionCode.Trim();
            if (code.StartsWith("$"))
                code = code.Substring(1);

            if (code.Length == 0)
                return "$0";

            foreach (char c in code)
            {
                if (!IsAsciiWordChar(c))
                    return "$0";
            }

            return "$" + code;
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

            TryEnterWaitingForPageTurnState();
            TryEnterCompleteState();
        }

        /// <summary>当前页已打完且存在后续页时切换到等待翻页态。</summary>
        private void TryEnterWaitingForPageTurnState()
        {
            if (_state != StreamingDialogueState.Typing)
                return;
            if (!_isCurrentPageSealed && _backlogPages.Count == 0)
                return;
            if (_characterIndex < _displayedPageText.Length)
                return;

            _state = StreamingDialogueState.WaitingForPageTurn;
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
            int textWidth = GetTextWidth();

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
                    SpriteText.drawString(b, _errorMessage ?? "...", textX, textY, width: textWidth);
                    break;
            }

            // Faulted 与完成态同样点亮翻页箭头，作为「点击此处安全退出」的视觉凭据。
            if (_state == StreamingDialogueState.WaitingForPageTurn
                || _state == StreamingDialogueState.Complete
                || _state == StreamingDialogueState.Faulted)
                this.dialogueIcon?.draw(b, true, 0, 0, 1f);

            base.drawMouse(b);
        }

        #endregion

        #region 输入

        /// <summary>Typing 拉满当前页；WaitingForPageTurn 翻页；Complete/Faulted 关闭。</summary>
        /// <param name="x">点击 X。</param>
        /// <param name="y">点击 Y。</param>
        /// <param name="playSound">是否播放音效。</param>
        public override void receiveLeftClick(int x, int y, bool playSound = true)
        {
            if (_state == StreamingDialogueState.Typing)
            {
                _isFastForwardActive = true;
                _characterIndex = _displayedPageText.Length;
                TryEnterWaitingForPageTurnState();
                TryEnterCompleteState();
                return;
            }

            if (_state == StreamingDialogueState.WaitingForPageTurn)
            {
                if (_backlogPages.Count == 0)
                {
                    // RECOVERABLE：空 backlog 却触发了翻页，直接收束为 Complete 防止死锁停滞。
                    ModEntry.SMonitor?.Log(
                        "[AiStreamingDialogueBox] Page turn requested with empty backlog; collapsing to Complete.",
                        LogLevel.Warn);
                    _state = StreamingDialogueState.Complete;
                    return;
                }

                Game1.playSound("smallSelect");

                _pageHistory.Add(_displayedPageText);
                _displayedPageText = _backlogPages.Dequeue();

                _characterIndex = 0;
                _typeTimerMs = BaseTypeDelayMs;
                _isFastForwardActive = false;
                _isCurrentPageSealed = false;
                _state = StreamingDialogueState.Typing;
                return;
            }

            if (_state == StreamingDialogueState.Complete || _state == StreamingDialogueState.Faulted)
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
