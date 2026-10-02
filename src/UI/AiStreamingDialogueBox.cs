// AiStreamingDialogueBox.cs
// VT-STREAM-01-CoreDialogueBox: AI 流式对白框。
//
// 继承自原版 DialogueBox 并完全接管 update/draw：打字机推进、标点阻尼、
// 流式追加与游标吸附均由本类驱动，不调用 base.draw(b)（避免基类单体逻辑
// 造成双重绘制与空对白崩溃）。NPC 对白文本一律走 SpriteText.drawString，
// 不使用 Game1.dialogueFont。

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
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
    /// 情绪 Cue（票 VT-STREAM-08）：一个情绪标记在净文本页内的字符偏移量
    /// 及其情绪码。结构体零分配，随页面存入 <see cref="BacklogPage.Cues"/>
    /// 或当前页的 _currentCues 时间轴。
    /// </summary>
    internal readonly struct EmotionCue
    {
        public readonly int CharIndex;
        public readonly string EmotionCode;

        public EmotionCue(int charIndex, string emotionCode)
        {
            CharIndex = charIndex;
            EmotionCode = emotionCode;
        }
    }

    /// <summary>
    /// 已封口、等待玩家翻页的 backlog 页（票 VT-STREAM-08）：页文本连同其
    /// 情绪 Cue 时间轴一起归档，翻页时装载为当前页并重建打字机时间轴。
    /// </summary>
    internal sealed class BacklogPage
    {
        public string Text;
        public readonly List<EmotionCue> Cues = new();
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

        /// <summary>文字区相对对白框的左/上内边距（原版 DialogueBox 文本路径 x+8 / y+8）。</summary>
        private const int TextPadding = 8;

        /// <summary>
        /// 有立绘布局时右侧预留的相框总宽（原版 448 + 12 装饰）。
        /// 立绘文字区宽度 = width - 460 - 24 = 716，与原版 draw 完全一致。
        /// </summary>
        private const int PortraitTextReserve = 460 + 24;

        /// <summary>无立绘布局时右侧扣除的内边距（原版纯文本路径 width - 16）。</summary>
        private const int WideTextGutter = 16;

        /// <summary>
        /// 立绘布局下翻页小箭头在 <see cref="WideIconOffset"/> 之外的额外内缩
        /// （票 VT-STREAM-09）。取自原版 <c>DialogueBox.setUpCloseDialogueIcon</c>
        /// 的 <c>position.X -= 492f</c>：该减法作用在 <c>x + width - 40</c> 之上，
        /// 故立绘布局的总内缩为 40 + 492 = 532。
        /// 11x12 贴图按 4x 绘制占 44px 宽，右缘落在 x + width - 488，恰在立绘相框
        /// 装饰左缘（<c>drawPortrait</c> 的 num = x + width - 448 + 4，再左移 40
        /// 即 x + width - 484）外侧 4px，不压框。
        /// </summary>
        private const int PortraitIconExtraInset = 492;

        /// <summary>无立绘布局下翻页小箭头的右侧内缩偏移（原版 x + width - 40）。</summary>
        private const int WideIconOffset = 40;

        /// <summary>翻页小箭头的底边内缩偏移（原版 y + height - 44）。</summary>
        private const int IconVerticalOffset = 44;

        /// <summary>好感度宝石命中区相对对白框的右/上偏移与尺寸（原版推导）。</summary>
        private const int FriendshipJewelRightOffset = 64;
        private const int FriendshipJewelTopOffset = 256;
        private const int FriendshipJewelSize = 44;

        /// <summary>基础打字间隔（毫秒）。</summary>
        private const int BaseTypeDelayMs = 35;

        private const int LightPunctuationDelayMs = 140;
        private const int LongPunctuationDelayMs = 380;
        private const int DashPunctuationDelayMs = 320;
        private const int NewlineDelayMs = 450;

        /// <summary>
        /// 思考态波浪文字的点号追加节奏：每 500ms 递增一点，0~3 循环。
        /// 与 CancelButtonPlugin 的原版复刻保持同一时间基。
        /// </summary>
        private const int ThinkingDotIntervalMs = 500;

        /// <summary>思考态波浪文字的正弦振幅（像素）。</summary>
        private const float ThinkingWaveAmplitude = 3f;

        /// <summary>思考态波浪文字的正弦角速度。</summary>
        private const float ThinkingWaveSpeed = 5f;

        /// <summary>思考态波浪文字相邻字符的相位差。</summary>
        private const float ThinkingWaveCharPhase = 0.5f;

        /// <summary>取消按钮贴图区域（原版 Cursors 红叉）。</summary>
        private static readonly Rectangle CancelButtonSource = new Rectangle(337, 494, 12, 12);

        /// <summary>取消按钮基准缩放（与原版立绘装饰同量级的 4x）。</summary>
        private const float CancelButtonBaseScale = 4f;

        /// <summary>取消按钮悬浮缩放目标值。</summary>
        private const float CancelButtonHoverScale = 1.15f;

        /// <summary>悬浮缩放插值系数（每帧向目标靠拢的比例）。</summary>
        private const float HoverScaleLerp = 0.2f;

        /// <summary>取消按钮锚点相对文字区的右/下偏移（票 VT-STREAM-05）。</summary>
        private const int CancelButtonAnchorX = 64;
        private const int CancelButtonAnchorY = 68;

        /// <summary>设置立绘表情时触发的原版立绘晃动时长（毫秒）。</summary>
        private const int PortraitShakeDurationMs = 250;

        /// <summary>
        /// 单页允许的最大文字高度（像素）。精确容纳 6 行 SpriteText（单行 48px），
        /// 留出 44px 避让对白框底边与金黄色翻页小箭头（票 VT-STREAM-07 容量翻倍）。
        /// </summary>
        public const int MaxPageHeight = 300;

        /// <summary>
        /// 单次输入允许的最大页数（含已翻页、当前页与 backlog）。
        /// 超出即熔断，防止 LLM 异常无限吐字撑爆内存。
        /// </summary>
        private const int MaxPageCount = 10;

        /// <summary>页数熔断时追加在末页的截断标记。</summary>
        private const string TruncationMarker = "...";

        #endregion

        #region 静态数据（热路径零分配）

        /// <summary>
        /// 情绪码 / [MOOD:...] 标签的纵深清洗正则（票 VT-STREAM-05）。
        /// 预编译并常驻，避免每帧或每个 chunk 的高频正则构造。
        /// </summary>
        private static readonly Regex EmotionRegex =
            new(@"(\$([a-zA-Z0-9]+)|\[MOOD:([^\]]+)\])", RegexOptions.Compiled);

        /// <summary>
        /// 通用分屏标记（票 VT-STREAM-07 大模型友好断页）：兼容原版
        /// "#$b#" 全串、"#$b"、"$b#" 残缺形态、"[PAGE]" 显式标记与单 '#'。
        /// 匹配项整体吞掉，绝不让 "$b" 控制符碎片残留在页面上。
        /// </summary>
        private static readonly Regex PageBreakRegex =
            new(@"(#\$[a-zA-Z0-9]+#|#\$[a-zA-Z0-9]+|\$[a-zA-Z0-9]+#|\[PAGE\]|#)", RegexOptions.Compiled);

        /// <summary>句末软断行回溯窗口宽度（字符数，票 VT-STREAM-07）。</summary>
        private const int SentenceBreakLookback = 24;

        /// <summary>句末标点集：句末软断行的截断点紧随其标点之后。</summary>
        private const string SentenceEnders = "。！？.!?\n";

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
        private string _errorMessage;

        /// <summary>鼠标是否悬浮于对白框内建的取消按钮上。</summary>
        private bool _isHoveringOverClose;

        /// <summary>取消按钮的悬浮缩放插值（1.0 = 静止）。</summary>
        private float _hoverScale = 1.0f;

        /// <summary>
        /// 好感度宝石悬浮文本（原版 DialogueBox.hoverText 为 private 字段，
        /// 派生类不可写，故本类持有等价副本并据此驱动 drawStringWithScrollBackground）。
        /// </summary>
        private string _friendshipHoverText = string.Empty;

        /// <summary>已封口、等待玩家翻页才能继续显示的后续页面。</summary>
        private readonly Queue<BacklogPage> _backlogPages = new();

        /// <summary>
        /// 当前页的情绪 Cue 时间轴（票 VT-STREAM-08）：按净文本字符偏移存放，
        /// 打字机游标越过即生效；List 预分配容量并随页面复位复用。
        /// </summary>
        private readonly List<EmotionCue> _currentCues = new(8);

        /// <summary>已生效的最后一个 Cue 在 _currentCues 中的下标；-1 表示尚未生效任何 Cue。</summary>
        private int _appliedCueIndex = -1;

        /// <summary>网络断粮代偿预算（毫秒）：游标追平流末尾后累积，标点停顿处扣减。</summary>
        private int _starvationMs = 0;

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

        /// <summary>错误或取消提示文本。</summary>
        public string ErrorMessage => _errorMessage;

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

                // 原版 Dialogue.prepareCurrentDialogueForDisplay 会在解析对白时置位 showPortrait；
                // 本类绕过整段解析，故在此显式复刻，使 isPortraitBox() 与相框绘制得以生效。
                this.characterDialogue.showPortrait = true;
            }

            this.transitioning = false;
            this.transitionInitialized = true;

            // 翻页小箭头沿用基类 public dialogueIcon 字段；无头环境无内容管线，跳过纹理载入。
            // X 坐标按立绘布局动态判定，让开右侧相框，与原版 setUpCloseDialogueIcon 对齐。
            if (Game1.content != null)
            {
                this.dialogueIcon = new TemporaryAnimatedSprite("LooseSprites\\Cursors",
                    new Rectangle(289, 342, 11, 12), 80f, 11, 999999,
                    new Vector2(ComputeIconX(), this.y + this.height - IconVerticalOffset),
                    false, false, 0.89f, 0f, Color.White, 4f, 0f, 0f, 0f, true);
            }

            // 统一应用初始布局：对白框坐标、好感度宝石命中区与小箭头位置。
            // 必须在 characterDialogue.showPortrait 置位之后调用，isPortraitBox 才判得准。
            this.UpdatePosition();

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
            _currentCues.Clear();
            _appliedCueIndex = -1;
            _starvationMs = 0;

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
        /// 设置立绘表情码（票 VT-STREAM-08）：外部下发的标记进入当前页情绪
        /// 时间轴，生效点为打字机游标当前位置——游标已停驻/越过该位置时
        /// 经时间轴立即切换立绘。未知/非法码降级为默认表情，绝不阻断渲染。
        /// </summary>
        /// <param name="emotionCode">情绪码，可带或不带 '$' 前缀，可为语义词。</param>
        public void SetEmotion(string emotionCode)
        {
            if (string.IsNullOrWhiteSpace(emotionCode))
                return;

            _currentCues.Add(new EmotionCue(_characterIndex, emotionCode));
            ApplyPendingEmotionCues();
        }

        /// <summary>
        /// 应用情绪码到立绘（票 VT-STREAM-08）：全部情绪生效路径的唯一出口，
        /// 主线程专属。空码降级 $neutral，未映射码由 NormalizeEmotionCode 降级。
        /// </summary>
        private void ApplyEmotionInternal(string emotionCode)
        {
            string normalized = string.IsNullOrWhiteSpace(emotionCode)
                ? "$neutral"
                : NormalizeEmotionCode(emotionCode);

            if (this.characterDialogue != null)
                this.characterDialogue.CurrentEmotion = normalized;

            // BOUNDARY：无头环境（Game1.content == null）没有立绘可晃，跳过。
            if (Game1.content != null)
                this.newPortaitShakeTimer = PortraitShakeDurationMs;
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
            foreach (BacklogPage page in _backlogPages) sb.Append(page.Text).Append(' ');
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
            _currentCues.Clear();
            _appliedCueIndex = -1;
            _starvationMs = 0;
            _isCurrentPageSealed = false;
            _characterIndex = 0;
            _isStreamComplete = true;
            _isFastForwardActive = false;
            _state = StreamingDialogueState.Faulted;
        }

        #endregion

        #region 分页

        /// <summary>
        /// 文字区可用宽度（原版 DialogueBox.draw 的 1:1 对齐，票 VT-STREAM-05）：
        /// 立绘布局扣 460 + 24（716px），无立绘布局扣 16。
        /// 分页高度熔断与实际绘制共用本值，保证折行位置与渲染一致。
        /// </summary>
        private int GetTextWidth()
            => this.isPortraitBox() ? (this.width - PortraitTextReserve) : (this.width - WideTextGutter);

        /// <summary>文字区左上角坐标（原版 x + 8 / y + 8）。</summary>
        private void GetTextOrigin(out int textX, out int textY)
        {
            textX = this.x + TextPadding;
            textY = this.y + TextPadding;
        }

        /// <summary>取消按钮命中矩形：锚在文字区右下角，尺寸随基准缩放固定。</summary>
        private Rectangle GetCancelButtonRect()
        {
            int size = (int)(CancelButtonSource.Width * CancelButtonBaseScale);
            return new Rectangle(
                this.x + GetTextWidth() - CancelButtonAnchorX,
                this.y + this.height - CancelButtonAnchorY,
                size,
                size);
        }

        /// <summary>
        /// 把一段文本灌入分页管线（票 VT-STREAM-07 大模型友好断页）：
        /// 以 <see cref="PageBreakRegex"/> 定位分屏标记（"#$b#" 全串、"#$b"、"$b#"、
        /// "[PAGE]" 与单 '#'），命中时把匹配前的文本并入当前页并封口，整个匹配项
        /// 被彻底吃掉（绝不留存 "$b" 控制符碎片），后续内容进入下一页。
        /// 段内再做情绪标记纵深清洗与首屏引导线净化。纯字符串处理，不触碰任何游戏态。
        /// </summary>
        private void IngestText(string text)
        {
            int cursor = 0;
            // 首段延续 backlog 尾页（流式增量语义）；每个分屏标记之后另起新页。
            bool startNewPage = false;

            while (cursor <= text.Length)
            {
                Match match = PageBreakRegex.Match(text, cursor);
                string segment = match.Success
                    ? text.Substring(cursor, match.Index - cursor)
                    : text.Substring(cursor);

                string body = ExtractAndStripEmotions(segment, out List<EmotionCue> segmentCues);

                if (_displayedPageText.Length == 0)
                {
                    int beforeStrip = body.Length;
                    body = StripLeadingDialogueDash(body);
                    if (body.Length != beforeStrip)
                        ShiftEmotionCues(segmentCues, beforeStrip - body.Length);
                }

                if (body.Length > 0)
                {
                    if (_isCurrentPageSealed)
                    {
                        EnqueueToBacklog(body, startNewPage, segmentCues);
                    }
                    else
                    {
                        // 高度溢出封口后，溢出段必须先于分屏标记之后的内容入页；
                        // 情绪 Cue 按截断点拆分归属两页。
                        List<EmotionCue> overflowCues;
                        string overflow = TryFillCurrentPage(body, segmentCues, out overflowCues);
                        if (overflow.Length > 0)
                            EnqueueToBacklog(overflow, startNewPage, overflowCues);
                    }
                }
                else if (segmentCues.Count > 0)
                {
                    // 纯情绪 chunk（正文洗空）：Cue 仍须进入对应页面的时间轴。
                    RouteCuesWithoutBody(segmentCues);
                }

                if (!match.Success)
                    return;

                // 分屏标记强制封口当前页；游标跳过整个匹配项（含 "#$b#" 全串）。
                _isCurrentPageSealed = true;
                cursor = match.Index + match.Length;
                startNewPage = true;
            }
        }

        /// <summary>
        /// 尝试把 <paramref name="text"/> 全部并入当前页；
        /// 返回未能容纳的剩余部分（空串表示全部容纳）。高度溢出时在安全位置截断并封口。
        /// 情绪 Cue 按截断点拆分：截断点之前挂当前页时间轴，其余重定基后随溢出段输出。
        /// </summary>
        private string TryFillCurrentPage(string text, List<EmotionCue> cues, out List<EmotionCue> overflowCues)
        {
            overflowCues = new List<EmotionCue>();
            if (text.Length == 0)
                return string.Empty;

            if (_isCurrentPageSealed)
            {
                overflowCues.AddRange(cues);
                return text;
            }

            int pageStart = _displayedPageText.Length;
            string candidate = _displayedPageText + text;
            int textWidth = GetTextWidth();

            if (SpriteText.getHeightOfString(candidate, textWidth) <= MaxPageHeight)
            {
                _displayedPageText = candidate;
                RegisterCurrentPageCues(cues, pageStart);
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

            string rawOverflow = candidate.Substring(cut);
            string overflow = rawOverflow.TrimStart(' ', '\t', '\r', '\n');
            int trimCount = rawOverflow.Length - overflow.Length;

            foreach (EmotionCue cue in cues)
            {
                int absolute = pageStart + cue.CharIndex;
                if (absolute < cut)
                    RegisterCurrentPageCue(absolute, cue.EmotionCode);
                else
                    overflowCues.Add(new EmotionCue(Math.Max(0, absolute - cut - trimCount), cue.EmotionCode));
            }

            return overflow;
        }

        /// <summary>
        /// 安全断点（票 VT-STREAM-07 语义句末优先软断行）：
        /// 1) 优先在 [upperBound - 24, upperBound] 窗口内反向查找句末标点
        ///    （。！？.!?\n），截断点选在标点紧随的后一位——标点留在当前页，
        ///    不留悬挂残句；2) 窗口内无句末标点时，回退到 ASCII 词边界回退，
        ///    严禁从 ASCII 单词中间断开（中文等无词边界字符允许就近断行）。
        /// 无解时至少保留 1 字符以保证推进。
        /// </summary>
        private static int ComputeSafeBreakIndex(string text, int upperBound)
        {
            int limit = Math.Min(upperBound, text.Length);

            int windowStart = Math.Max(0, limit - SentenceBreakLookback);
            for (int i = limit - 1; i >= windowStart; i--)
            {
                if (SentenceEnders.IndexOf(text[i]) >= 0)
                    return Math.Max(1, i + 1);
            }

            int cut = Math.Max(1, limit);
            while (cut > 1 && IsAsciiWordChar(text[cut - 1]))
                cut--;
            return cut;
        }

        /// <summary>是否为需要保持在单词内部的 ASCII 字母/数字。</summary>
        private static bool IsAsciiWordChar(char c)
            => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9');

        /// <summary>
        /// 封口后的增量写入 backlog。流式续写并入队尾页；'#' 分页另起新页。
        /// 情绪 Cue 重定基到目标页文本内并随页归档。
        /// 触发 BOUNDARY 熔断时强行在末页截断并追加标记。
        /// </summary>
        /// <param name="text">待入页文本。</param>
        /// <param name="startNewPage">是否另起新页（'#' 之后为 true）。</param>
        /// <param name="cues">随文本入页的情绪 Cue（页内相对偏移）。</param>
        private void EnqueueToBacklog(string text, bool startNewPage, List<EmotionCue> cues)
        {
            if (text.Length == 0 || _isPageBudgetExhausted)
                return;

            if (_backlogPages.Count > 0 && !startNewPage)
            {
                // Queue 无索引器：出队尾页拼接后重入队。
                BacklogPage tail = _backlogPages.Dequeue();
                AppendCuesToPage(tail, cues, tail.Text.Length);
                tail.Text += text;
                _backlogPages.Enqueue(tail);
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

            BacklogPage page = new BacklogPage { Text = text };
            AppendCuesToPage(page, cues, 0);
            _backlogPages.Enqueue(page);
        }

        /// <summary>把情绪 Cue 重定基到 backlog 页文本内（offset 为并入点在页内的偏移）。</summary>
        private static void AppendCuesToPage(BacklogPage page, List<EmotionCue> cues, int offset)
        {
            for (int i = 0; i < cues.Count; i++)
                page.Cues.Add(new EmotionCue(Math.Max(0, offset + cues[i].CharIndex), cues[i].EmotionCode));
        }

        /// <summary>在最后一张页面（backlog 尾页，无尾页时为当前页）末尾追加截断标记。</summary>
        private void MarkLastPageTruncated()
        {
            if (_backlogPages.Count > 0)
            {
                // Queue 无索引器：必须出队后重入队，否则会在队尾追加出第二份副本。
                BacklogPage tail = _backlogPages.Dequeue();
                if (!tail.Text.EndsWith(TruncationMarker, StringComparison.Ordinal))
                    tail.Text += TruncationMarker;
                _backlogPages.Enqueue(tail);
                return;
            }

            if (!_displayedPageText.EndsWith(TruncationMarker, StringComparison.Ordinal))
                _displayedPageText += TruncationMarker;
        }

        /// <summary>
        /// 提取并剔除文本中的情绪标记（票 VT-STREAM-05）。作为分流器之上的纵深防御：
        /// 即使上游漏切，正文也绝不残留 '$h' / '$s' / '[MOOD:xxx]'。
        /// </summary>
        /// <param name="text">待清洗文本。</param>
        /// <param name="extractedEmotions">
        /// 按出现顺序输出的情绪 Cue（票 VT-STREAM-08）：CharIndex 为剥离后的
        /// 净文本内字符偏移，EmotionCode 为不含 '$' 与标签括号的情绪码。
        /// </param>
        /// <returns>剔除全部情绪标记后的正文。</returns>
        private static string ExtractAndStripEmotions(string text, out List<EmotionCue> extractedEmotions)
        {
            extractedEmotions = new List<EmotionCue>();
            if (string.IsNullOrEmpty(text))
                return text;

            MatchCollection matches = EmotionRegex.Matches(text);
            if (matches.Count == 0)
                return text;

            var stripped = new StringBuilder(text.Length);
            int cursor = 0;

            foreach (Match match in matches)
            {
                stripped.Append(text, cursor, match.Index - cursor);

                // 组 2 = '$xxx' 载荷；组 3 = '[MOOD:xxx]' 载荷。
                // Cue 偏移 = 净文本当前长度（即该标记在剥离后正文中的落点）。
                extractedEmotions.Add(new EmotionCue(
                    stripped.Length,
                    match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value));

                cursor = match.Index + match.Length;
            }

            stripped.Append(text, cursor, text.Length - cursor);
            return stripped.ToString();
        }

        /// <summary>
        /// 前导引导线剥离后正文整体左移，情绪 Cue 偏移同步左移并钳制在 0
        /// （落在被剥离前缀内的 Cue 收敛到页首，保证开口仍带表情）。
        /// </summary>
        private static void ShiftEmotionCues(List<EmotionCue> cues, int removedCount)
        {
            for (int i = 0; i < cues.Count; i++)
            {
                int shifted = Math.Max(0, cues[i].CharIndex - removedCount);
                if (shifted != cues[i].CharIndex)
                    cues[i] = new EmotionCue(shifted, cues[i].EmotionCode);
            }
        }

        /// <summary>
        /// 剥离首屏正文的前导对白引导线（票 VT-STREAM-07）：文本以空白字符加上
        /// "- " / "— " / "– " 开头，或仅由 '-' 与空白组成时予以剔除；
        /// 引导线后直接跟实质性字符（如 "-你好"）时按正文保留，与管道层语义一致。
        /// </summary>
        /// <param name="text">待净化文本。</param>
        /// <returns>剔除引导线后的正文；整段仅为引导线与空白时返回空串。</returns>
        private static string StripLeadingDialogueDash(string text)
        {
            if (string.IsNullOrEmpty(text))
                return text;

            int index = 0;
            while (index < text.Length && char.IsWhiteSpace(text[index]))
                index++;

            // 纯空白或首个非空白字符不是引导线：交回原样
            if (index >= text.Length || !IsDialogueLeadDash(text[index]))
                return text;

            // 引导线后必须紧跟空白才构成结构前缀；后无任何字符（孤立 "-"）时
            // 整段剥空，避免发出残缺对白；后直接跟实质性字符（如 "-你好"）
            // 则按正文保留，与管道层语义一致。
            int cursor = index + 1;
            if (cursor >= text.Length)
                return string.Empty;

            if (!char.IsWhiteSpace(text[cursor]))
                return text;

            while (cursor < text.Length && char.IsWhiteSpace(text[cursor]))
                cursor++;

            return text.Substring(cursor);
        }

        /// <summary>是否为对白引导线字符：半角 '-'、全角破折号 “—” 与短破折号 “–”。</summary>
        private static bool IsDialogueLeadDash(char c)
            => c == '-' || c == '—' || c == '–';

        /// <summary>
        /// 规范化情绪码（票 VT-STREAM-05）：把 LLM 语义词归一到原版立绘表情。
        /// happy/smile/joy/h -> $h，sad/sorrow/cry/s -> $s，surprised/shocked/unique/u -> $u，
        /// love/blush/heart/l -> $l，angry/annoyed/rage/a -> $a，neutral/default/0 -> $neutral，
        /// 纯数字串 -> "$" + 数字；其余一律降级为 $neutral，绝不阻断渲染。
        /// </summary>
        private static string NormalizeEmotionCode(string emotionCode)
        {
            string code = emotionCode.Trim();
            if (code.StartsWith("$"))
                code = code.Substring(1);

            if (code.Length == 0)
                return "$neutral";

            // 数字串：原版立绘帧索引，保留原值。'0' 是原版的 neutral 帧，别名优先于数字规则。
            if (code == "0")
                return "$neutral";

            bool allDigits = true;
            foreach (char c in code)
            {
                if (c < '0' || c > '9')
                {
                    allDigits = false;
                    break;
                }
            }

            if (allDigits)
                return "$" + code;

            switch (code.ToLowerInvariant())
            {
                case "happy":
                case "smile":
                case "joy":
                case "h":
                    return "$h";

                case "sad":
                case "sorrow":
                case "cry":
                case "s":
                    return "$s";

                case "surprised":
                case "shocked":
                case "unique":
                case "football":
                case "gridball":
                case "u":
                    return "$u";

                case "love":
                case "blush":
                case "heart":
                case "l":
                    return "$l";

                case "angry":
                case "annoyed":
                case "rage":
                case "a":
                    return "$a";

                case "neutral":
                case "default":
                    return "$neutral";

                default:
                    // RECOVERABLE：未映射的 token 降级为默认表情，保证渲染链路不中断。
                    ModEntry.SMonitor?.Log(
                        $"[AiStreamingDialogueBox] Unmapped emotion '{emotionCode}'; falling back to $neutral.",
                        LogLevel.Trace);
                    return "$neutral";
            }
        }

        #endregion

        #region 情绪时间轴（票 VT-STREAM-08）

        /// <summary>把一批页内相对偏移的情绪 Cue 挂到当前页时间轴上。</summary>
        private void RegisterCurrentPageCues(List<EmotionCue> cues, int pageStartOffset)
        {
            for (int i = 0; i < cues.Count; i++)
                RegisterCurrentPageCue(pageStartOffset + cues[i].CharIndex, cues[i].EmotionCode);
        }

        /// <summary>
        /// 把单个情绪 Cue 挂到当前页时间轴。若处于 Thinking 态或第 0 字符阶段
        /// （首字尚未打印）且 Cue 位置为 0，则立即生效，确保开口即带表情。
        /// </summary>
        private void RegisterCurrentPageCue(int charIndex, string emotionCode)
        {
            if (charIndex < 0)
                charIndex = 0;

            _currentCues.Add(new EmotionCue(charIndex, emotionCode));

            if (charIndex != 0)
                return;
            if (_state != StreamingDialogueState.Thinking && _characterIndex != 0)
                return;

            // 从队列前沿顺推：连续的位置 0 Cue（可能含本条）全部立即生效。
            for (int i = _appliedCueIndex + 1;
                 i < _currentCues.Count && _currentCues[i].CharIndex == 0;
                 i++)
            {
                _appliedCueIndex = i;
                ApplyEmotionInternal(_currentCues[i].EmotionCode);
            }
        }

        /// <summary>纯情绪 chunk（正文洗空）的情绪路由：未封口挂当前页末端；已封口并入 backlog 尾页末端。</summary>
        private void RouteCuesWithoutBody(List<EmotionCue> cues)
        {
            if (!_isCurrentPageSealed || _backlogPages.Count == 0)
            {
                // 已封口且无尾页时下一页尚未创建：同样挂在当前页末端，
                // 游标已停驻页尾，经时间轴立即生效。
                RegisterCurrentPageCues(cues, _displayedPageText.Length);
                return;
            }

            BacklogPage tail = _backlogPages.Dequeue();
            AppendCuesToPage(tail, cues, tail.Text.Length);
            _backlogPages.Enqueue(tail);
        }

        /// <summary>
        /// 推进情绪时间轴：顺序生效所有 CharIndex &lt;= 游标且尚未生效的 Cue。
        /// 顺序扫描（不提前折断）以容忍外部 SetEmotion 造成的乱序插入。
        /// </summary>
        private void ApplyPendingEmotionCues()
        {
            for (int i = _appliedCueIndex + 1; i < _currentCues.Count; i++)
            {
                if (_currentCues[i].CharIndex > _characterIndex)
                    continue;

                _appliedCueIndex = i;
                ApplyEmotionInternal(_currentCues[i].EmotionCode);
            }
        }

        /// <summary>快进激活：时间轴直接推进至最后一个 Cue 并生效，立绘与最终台词一致。</summary>
        private void FastForwardEmotionCues()
        {
            if (_currentCues.Count == 0)
                return;

            SanitizeCurrentCues();
            _appliedCueIndex = _currentCues.Count - 1;
            ApplyEmotionInternal(_currentCues[_appliedCueIndex].EmotionCode);
        }

        /// <summary>
        /// 失败路径守卫（票 VT-STREAM-08）：Cue 越界或乱序时钳制到
        /// [0, 当前页长度] 并按 CharIndex 排序，记 Trace 日志。仅在
        /// _appliedCueIndex 语义允许重排的时机调用（翻页装载、快进跳转）。
        /// </summary>
        private void SanitizeCurrentCues()
        {
            if (_currentCues.Count == 0)
                return;

            int limit = _displayedPageText?.Length ?? 0;
            bool clamped = false;
            for (int i = 0; i < _currentCues.Count; i++)
            {
                int index = _currentCues[i].CharIndex;
                if (index >= 0 && index <= limit)
                    continue;

                clamped = true;
                _currentCues[i] = new EmotionCue(Math.Clamp(index, 0, limit), _currentCues[i].EmotionCode);
            }

            for (int i = 1; i < _currentCues.Count; i++)
            {
                if (_currentCues[i].CharIndex >= _currentCues[i - 1].CharIndex)
                    continue;

                _currentCues.Sort((a, b) => a.CharIndex.CompareTo(b.CharIndex));
                clamped = true;
                break;
            }

            if (clamped)
                ModEntry.SMonitor?.Log(
                    "[AiStreamingDialogueBox] Emotion cues out of range/order; clamped and sorted by CharIndex.",
                    LogLevel.Trace);
        }

        #endregion

        #region 标点阻尼

        /// <summary>
        /// 旧签名兼容重载：以真实 runway、流已完成（不触发 runway 自适应与
        /// 断粮代偿）的语义求值，供既有节奏断言与外部纯函数测试使用。
        /// </summary>
        internal static int ComputeDelayMs(string text, int revealedCount)
        {
            int starvationMs = 0;
            return ComputeDelayMs(text, revealedCount, (text?.Length ?? 0) - revealedCount, true, ref starvationMs);
        }

        /// <summary>
        /// 计算揭示第 <paramref name="revealedCount"/> 个字符之后，到揭示下一个字符
        /// 之前应当等待的毫秒数（票 VT-STREAM-08）。连续同类标点折叠：若后一字符
        /// 仍为标点，当前标点不注入额外停顿，仅在标点组末字触发长停顿。
        /// 在此之上叠加：流式 runway 自适应基准（runway &lt;= 1 时 1.5x 阻尼防骤停、
        /// runway &gt;= 16 时 0.8x 轻微追赶、流已结束时恒定原速），以及断粮代偿——
        /// 网络断粮期间积累的 <paramref name="starvationMs"/> 预算优先抵扣本字符的
        /// 标点停顿，预算与停顿至少一方归零，彻底消灭延迟叠加冲突。
        /// </summary>
        /// <param name="text">当前页文本。</param>
        /// <param name="revealedCount">已揭示的字符数（刚揭示的字符下标为 revealedCount - 1）。</param>
        /// <param name="runway">未揭示的剩余缓冲字符数（text.Length - revealedCount）。</param>
        /// <param name="isStreamComplete">流是否已结束。</param>
        /// <param name="starvationMs">断粮代偿预算（毫秒），按实际抵扣量递减。</param>
        internal static int ComputeDelayMs(string text, int revealedCount, int runway, bool isStreamComplete, ref int starvationMs)
        {
            // 玩家关闭节奏开关时无视标点与换行，一律恒定原版等间隔打字速度。
            // Config 为 null（无头测试环境）时按开启处理，保持既有节奏行为。
            if (ModEntry.Config != null && !ModEntry.Config.EnableRhythmicTyping)
                return BaseTypeDelayMs;

            if (text == null || revealedCount <= 0 || revealedCount > text.Length)
                return BaseTypeDelayMs;

            char current = text[revealedCount - 1];

            if (current == '\n')
                return NewlineDelayMs;

            int punctuationDelay = GetPunctuationDelayMs(current);
            if (punctuationDelay == 0)
                return ScaleBaseDelayMs(runway, isStreamComplete);

            // 折叠：下一个字符仍是标点时，本字符不注入停顿。
            if (revealedCount < text.Length && GetPunctuationDelayMs(text[revealedCount]) != 0)
                return ScaleBaseDelayMs(runway, isStreamComplete);

            // 断粮代偿：标点停顿先被断粮预算抵扣（扣减后归零或扣至 0）。
            int effectivePunctuation = punctuationDelay;
            if (starvationMs > 0)
            {
                int consumed = Math.Min(punctuationDelay, starvationMs);
                effectivePunctuation = punctuationDelay - consumed;
                starvationMs -= consumed;
            }

            return ScaleBaseDelayMs(runway, isStreamComplete) + effectivePunctuation;
        }

        /// <summary>流式 runway 自适应基准延迟（票 VT-STREAM-08）。</summary>
        private static int ScaleBaseDelayMs(int runway, bool isStreamComplete)
        {
            if (isStreamComplete)
                return BaseTypeDelayMs;
            if (runway <= 1)
                return BaseTypeDelayMs * 3 / 2;   // 52：阻尼平滑防骤停
            if (runway >= 16)
                return BaseTypeDelayMs * 4 / 5;   // 28：轻微追赶
            return BaseTypeDelayMs;
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

            // 原版同款立绘晃动计时器递减（票 VT-STREAM-07）：SetEmotion 触发的
            // 250ms 晃动耗尽后必须归零，否则 newPortaitShakeTimer 恒大于 0
            // 导致立绘无休止抖动。仅在游戏主线程 Update 中安全递减。
            if (this.newPortaitShakeTimer > 0)
            {
                this.newPortaitShakeTimer -= elapsed;
                if (this.newPortaitShakeTimer < 0)
                    this.newPortaitShakeTimer = 0;
            }

            dialogueIcon?.update(time);

            if (_state == StreamingDialogueState.Thinking)
            {
                // 思考态只推进时钟，波浪文字在 draw 中按同一时钟求正弦偏移。
                _thinkingClockMs += elapsed;
                return;
            }

            if (_state == StreamingDialogueState.Typing && !_isFastForwardActive)
            {
                if (_characterIndex < _displayedPageText.Length)
                {
                    _typeTimerMs -= elapsed;
                    while (_typeTimerMs <= 0 && _characterIndex < _displayedPageText.Length)
                    {
                        _characterIndex++;
                        int runway = _displayedPageText.Length - _characterIndex;
                        _typeTimerMs += ComputeDelayMs(
                            _displayedPageText, _characterIndex, runway, _isStreamComplete, ref _starvationMs);

                        if (Game1.options.dialogueTyping)
                        {
                            if (!_isStreamComplete || _characterIndex < _displayedPageText.Length)
                            {
                                Game1.playSound("dialogueCharacter");
                            }
                        }
                    }
                }
                else if (!_isStreamComplete)
                {
                    // 网络断粮（票 VT-STREAM-08）：游标已追平流缓冲末尾而流未结束，
                    // 累积断粮代偿预算供后续标点停顿抵扣；计时器归零，严禁累积
                    // 负时间，防止下一 chunk 到达时瞬间倾泻多个字符（一顿一顿）。
                    _starvationMs += elapsed;
                    _typeTimerMs = 0;
                }
                else
                {
                    _typeTimerMs = 0;
                }
            }

            // 情绪时间轴推进（票 VT-STREAM-08）：游标越过 Cue 位置即切换立绘；
            // 对 WaitingForPageTurn 态同样生效（外部 SetEmotion 可在等待翻页时下发）。
            ApplyPendingEmotionCues();

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

        #region 窗口与布局自适应

        /// <summary>
        /// 游戏视口分辨率 / 窗口尺寸变化时重新校准对白框及其子组件坐标（票 VT-STREAM-09）。
        /// </summary>
        /// <remarks>
        /// BOUNDARY：无头环境（Game1.content == null）必须跳过基类调用。基类
        /// gameWindowSizeChanged 末尾会走 setUpIcons() → setUpCloseDialogueIcon() →
        /// new TemporaryAnimatedSprite("LooseSprites\Cursors", …) → loadTexture() 内的
        /// Game1.content.Load&lt;Texture2D&gt;，content 为 null 时必抛 NullReferenceException。
        /// 跳过基类对本类无任何损失：width/height 恒为 DialogueWidth/DialogueHeight（与基类
        /// 重置的 1200/384 相同），friendshipJewel 由 UpdatePosition 统一重算，
        /// setUpIcons 的 gamepad/safetyTimer 副作用本类完全不消费。
        /// </remarks>
        public override void gameWindowSizeChanged(Rectangle oldBounds, Rectangle newBounds)
        {
            if (Game1.content != null)
                base.gameWindowSizeChanged(oldBounds, newBounds);

            this.UpdatePosition();
        }

        /// <summary>
        /// 重算对白框自身与内部各子组件（好感度宝石命中区、翻页小箭头）的屏幕坐标（票 VT-STREAM-09）。
        /// 纯赋值、无副作用，可在构造期与每次视口变动时安全重入。
        /// </summary>
        public void UpdatePosition()
        {
            this.x = ComputeBoxX();
            this.y = ComputeBoxY();

            // 好感度宝石命中矩形：与原版构造器 / gameWindowSizeChanged 的推导一致。
            this.friendshipJewel = new Rectangle(
                this.x + this.width - FriendshipJewelRightOffset,
                this.y + FriendshipJewelTopOffset,
                FriendshipJewelSize,
                FriendshipJewelSize);

            // BOUNDARY：无头环境未构造 dialogueIcon（无内容管线），跳过坐标回写。
            if (this.dialogueIcon != null)
            {
                this.dialogueIcon.position = new Vector2(
                    ComputeIconX(),
                    this.y + this.height - IconVerticalOffset);
            }
        }

        /// <summary>
        /// 翻页小箭头的屏幕 X 坐标：立绘布局按原版在 -40 基础上再内缩 492px（合计 532px）
        /// 让开相框，无立绘布局贴纯文本框右下角内缩 40px。
        /// </summary>
        private int ComputeIconX()
            => this.x + this.width
                - (this.isPortraitBox() ? WideIconOffset + PortraitIconExtraInset : WideIconOffset);

        #endregion

        #region 绘制

        /// <summary>绘制对白框、立绘、文本、思考波浪、取消按钮与好感度悬浮条。绝不调用 base.draw(b)。</summary>
        /// <param name="b">精灵批次。</param>
        public override void draw(SpriteBatch b)
        {
            this.drawBox(b, this.x, this.y, this.width, this.height);

            if (this.isPortraitBox())
                this.drawPortrait(b);

            GetTextOrigin(out int textX, out int textY);
            int textWidth = GetTextWidth();

            switch (_state)
            {
                case StreamingDialogueState.Thinking:
                    DrawNativeThinkingWave(b, textX, textY);
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

            // 取消按钮仅在思考态可见并响应点击。一旦进入打字态，按钮立即消失。
            if (_state == StreamingDialogueState.Thinking)
                DrawNativeCancelButton(b);

            // 原版好感度悬浮条（hoverText 为基类 private 字段，此处用等价副本驱动）。
            if (_friendshipHoverText.Length > 0)
            {
                SpriteText.drawStringWithScrollBackground(
                    b,
                    _friendshipHoverText,
                    this.friendshipJewel.Center.X - SpriteText.getWidthOfString(_friendshipHoverText) / 2,
                    this.friendshipJewel.Y - 64);
            }

            base.drawMouse(b);
        }

        /// <summary>
        /// 组装思考态波浪文字的最终字符串：先剥离本地化文案尾部的固定标点，
        /// 再按时钟追加 0~3 个点号，使文案在「思考中」到「思考中...」之间匀速循环。
        /// 剥离是必需的：i18n 的 "ui.thinking" 本身带尾点（"思考中..." / "Thinking..."），
        /// 不剥离会在动点周期里拼出 4 个以上连续点号。
        /// </summary>
        /// <param name="message">已本地化且已回退的思考中文案。</param>
        /// <param name="clockMs">思考态累计时钟（毫秒）。</param>
        private static string BuildThinkingWaveText(string message, int clockMs)
        {
            string baseText = message.TrimEnd('.', '。', '…');
            int dotCount = (int)(clockMs / (float)ThinkingDotIntervalMs) % 4;
            return baseText + new string('.', dotCount);
        }

        /// <summary>
        /// 原版复刻的思考态波浪文字：逐字符按正弦上下浮动，并在末尾按帧时间追加 0~3 个点号。
        /// 严格在主线程 draw 中执行，SpriteBatch 与 SpriteText 不跨线程。
        /// </summary>
        private void DrawNativeThinkingWave(SpriteBatch b, int textX, int textY)
        {
            float seconds = _thinkingClockMs / 1000f;
            string message = I18n.Get("ui.thinking");
            if (string.IsNullOrEmpty(message) || message == "ui.thinking")
                message = I18n.IsChinese ? "思考中" : "thinking";

            string animated = BuildThinkingWaveText(message, _thinkingClockMs);

            float currentX = textX;
            for (int i = 0; i < animated.Length; i++)
            {
                string character = animated[i].ToString();
                float yOffset = (float)Math.Sin(seconds * ThinkingWaveSpeed + i * ThinkingWaveCharPhase)
                    * ThinkingWaveAmplitude;

                SpriteText.drawString(
                    b, character, (int)currentX, textY + (int)yOffset,
                    characterPosition: 999999, width: -1, height: 999999,
                    alpha: 1f, layerDepth: 1f);

                currentX += SpriteText.getWidthOfString(character);
            }
        }

        /// <summary>
        /// 原版复刻的取消按钮：绘制 Cursors 红叉，叠加悬浮缩放与呼吸浮动。
        /// </summary>
        private void DrawNativeCancelButton(SpriteBatch b)
        {
            Rectangle slot = GetCancelButtonRect();

            float seconds = _thinkingClockMs / 1000f;
            float drawScale = CancelButtonBaseScale * _hoverScale;
            float visualSize = CancelButtonSource.Width * drawScale;
            float offset = (visualSize - slot.Width) / 2f;
            float bounceY = (float)Math.Sin(seconds * 3f) * 4f;

            b.Draw(
                Game1.mouseCursors,
                new Vector2(slot.X - offset, slot.Y - offset + bounceY),
                CancelButtonSource,
                Color.White, 0f, Vector2.Zero, drawScale,
                SpriteEffects.None, 0.99f);
        }

        /// <summary>
        /// 悬浮检测：好感度宝石（复刻原版表达式）+ 对白框内建的取消按钮。
        /// 原版 DialogueBox.hoverText 为 private 字段，派生类不可写，
        /// 故把同一表达式求值到本类的等价副本上。
        /// </summary>
        /// <param name="mouseX">鼠标 X。</param>
        /// <param name="mouseY">鼠标 Y。</param>
        public override void performHoverAction(int mouseX, int mouseY)
        {
            _isHoveringOverClose = IsCancelAvailable() && GetCancelButtonRect().Contains(mouseX, mouseY);
            _hoverScale += ((_isHoveringOverClose ? CancelButtonHoverScale : 1.0f) - _hoverScale) * HoverScaleLerp;

            _friendshipHoverText = string.Empty;
            if (this.shouldDrawFriendshipJewel() && this.friendshipJewel.Contains(mouseX, mouseY))
            {
                _friendshipHoverText = Game1.player.getFriendshipHeartLevelForNPC(this.characterDialogue.speaker.Name)
                    + "/" + Utility.GetMaximumHeartsForCharacter(this.characterDialogue.speaker) + "<";
            }
        }

        #endregion

        #region 输入

        /// <summary>Typing 拉满当前页；WaitingForPageTurn 翻页；Complete/Faulted 关闭。</summary>
        /// <param name="x">点击 X。</param>
        /// <param name="y">点击 Y。</param>
        /// <param name="playSound">是否播放音效。</param>
        public override void receiveLeftClick(int x, int y, bool playSound = true)
        {
            // 取消按钮优先于打字快进：生成未完成期间点它必须中止本轮，而不是跳过当前页。
            if (IsCancelAvailable() && GetCancelButtonRect().Contains(x, y))
            {
                CancelCurrentDialogue();
                return;
            }

            if (_state == StreamingDialogueState.Typing)
            {
                _isFastForwardActive = true;

                // 快进激活（票 VT-STREAM-08）：Cue 时间轴直接推进至最后一个并生效，
                // 确保跳过后立绘状态与最终台词一致。
                FastForwardEmotionCues();
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
                BacklogPage page = _backlogPages.Dequeue();
                _displayedPageText = page.Text;

                // 翻页装载（票 VT-STREAM-08）：连同情绪 Cue 时间轴一起重建，
                // 断粮代偿预算按页复位，避免跨页污染后续节奏。
                _currentCues.Clear();
                _currentCues.AddRange(page.Cues);
                _appliedCueIndex = -1;
                _starvationMs = 0;
                SanitizeCurrentCues();

                _characterIndex = 0;
                _typeTimerMs = BaseTypeDelayMs;
                _isFastForwardActive = false;
                _isCurrentPageSealed = false;
                _state = StreamingDialogueState.Typing;

                // 新页索引 0 存在 Cue：开口即带表情，立即触发立绘更新。
                ApplyPendingEmotionCues();
                return;
            }

            if (_state == StreamingDialogueState.Complete || _state == StreamingDialogueState.Faulted)
            {
                Game1.playSound("smallSelect");
                Close();
            }
        }

        /// <summary>Escape 关闭；Space 或 Action 键等同左键。生成未完成期间（思考中或流式打字中）Escape 与取消按钮同权，中止本轮生成。</summary>
        /// <param name="key">按下的按键。</param>
        public override void receiveKeyPress(Keys key)
        {
            if (key == Keys.Escape)
            {
                // 对白框还在消耗生成内容（思考中，或打字中且流未完成）时，
                // Escape 必须掐断后台 LLM 请求，否则框关了请求仍会挂到超时。
                // 流已完成/翻页/完成态走普通关闭。
                if (_state == StreamingDialogueState.Thinking
                    || (_state == StreamingDialogueState.Typing && !_isStreamComplete))
                {
                    CancelCurrentDialogue();
                    return;
                }

                Close();
                return;
            }

            if (key == Keys.Space || Game1.options.doesInputListContain(Game1.options.actionButton, key))
                receiveLeftClick(0, 0);
        }

        /// <summary>当前是否处于「思考态且取消按钮可见」的状态。</summary>
        private bool IsCancelAvailable()
            => _state == StreamingDialogueState.Thinking;

        /// <summary>
        /// 中止本轮流式对白：设置用户取消标记、取消 CTS、播取消音效、清空 AsyncBuilder 队列，
        /// 切换立绘至悲伤表情（$s），并就地进入 Faulted 状态显示取消提示，等待玩家点击退出。
        /// CTS 缺失或已取消时按 RECOVERABLE 记录 Trace 日志后照常进入取消态，绝不抛异常。
        /// </summary>
        public void CancelCurrentDialogue()
        {
            NPC speaker = AsyncBuilder.Instance.SpeakingNpc ?? this.characterDialogue?.speaker;
            Character character = !string.IsNullOrWhiteSpace(speaker?.Name)
                ? DialogueBuilder.Instance?.GetCharacter(speaker)
                : null;
            if (character != null)
            {
                character.IsUserCancelled = true;
            }

            CancellationTokenSource cts = character?.CurrentDialogueCts;

            if (cts == null || cts.IsCancellationRequested)
            {
                ModEntry.SMonitor?.Log(
                    "[AiStreamingDialogueBox] Cancel requested with no live CTS; transitioning to cancelled state in place.",
                    LogLevel.Trace);
            }
            else
            {
                cts.Cancel();
            }

            Game1.playSound("cancel");
            AsyncBuilder.Instance.Cleanup();

            ApplyEmotionInternal("$s");

            string cancelMsg = I18n.Get("ui.cancelled");
            if (string.IsNullOrEmpty(cancelMsg) || cancelMsg == "ui.cancelled")
            {
                cancelMsg = LocalizedContentManager.CurrentLanguageCode == LocalizedContentManager.LanguageCode.zh
                    ? "请求已取消。"
                    : "Request cancelled.";
            }

            SetFaulted(cancelMsg);
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
        /// 纯文本回退：替换原版农夫名字通配符 '@' 并剥离花括号标记，
        /// 避免生成内容中的标签意外进入本组件导致渲染异常。
        /// </summary>
        private static string Sanitize(string text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;

            if (text.Contains('@'))
            {
                string playerName = GetSafePlayerName();
                if (!string.IsNullOrEmpty(playerName))
                    text = text.Replace("@", playerName);
            }

            return text.IndexOf('{') < 0 && text.IndexOf('}') < 0
                ? text
                : text.Replace("{", "").Replace("}", "");
        }

        /// <summary>安全获取玩家姓名（防止无头测试环境未初始化 Farmer.name NetField 抛 NRE）。</summary>
        private static string GetSafePlayerName()
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

        #endregion
    }
}
