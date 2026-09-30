// StreamTokenPipeline.cs
// ═══════════════════════════════════════════════════════════════════════════
// STREAM TOKEN PIPELINE（票 VT-STREAM-02-StreamPipeline）
// ═══════════════════════════════════════════════════════════════════════════
//
// 逐字符状态机，把 LLM 流式返回的原始字符流切分为 StreamSegment：
//   · 台词正文            -> Text
//   · [ACTION:...] [UI:]  -> Action
//   · [MOOD:...] $h $5    -> Portrait
//   · '%' 选项区之后内容  -> 不产出任何片段，改由 Flush() 汇入候选项
//
// 设计要点：
//   1. 纯 C# 算法层：无 Game1 / MonoGame / 存档依赖，可在无头环境以最高性能运行。
//   2. 逐字符驱动，跨 Feed 保持状态：标签与情绪码允许被任意切碎在多个 chunk 之间。
//   3. 热路径使用 StringBuilder + char 遍历，不做高频正则全量 Replace。
//   4. 未闭合方括号有 64 字符熔断，防止模型异常截断导致对白永久挂起。
//
// 与既有 StreamLineTracker 的关系：后者是**非流式**回退基线（整块字符串按行处理），
// 本类面向逐 token 增量场景，二者并存，互不替代。
// ═══════════════════════════════════════════════════════════════════════════

#nullable disable

using System;
using System.Collections.Generic;
using System.Text;
using StardewModdingAPI;

namespace ValleytalkReborn.Dialogue.Coordination;

/// <summary>
/// 流式 token 分流器。实例状态与单次流绑定，<see cref="Reset"/> 后可复用于下一轮流。
/// </summary>
public sealed class StreamTokenPipeline
{
    /// <summary>未闭合方括号标签的熔断阈值：超过则判定为普通文本并回退释放。</summary>
    private const int MaxTagBufferLength = 64;

    /// <summary>候选项（玩家回复选项）最多保留条数。</summary>
    private const int MaxSuggestions = 3;

    private const string ActionTagPrefix = "[ACTION:";
    private const string UiTagPrefix = "[UI:";
    private const string MoodTagPrefix = "[MOOD:";

    /// <summary>星露谷原版情绪码字母（对应 Dialogue.checkEmotions 的 $h/$s/$u/$l/$a）。</summary>
    private const string MoodLetters = "hsula";

    private readonly StringBuilder _textBuffer = new StringBuilder();
    private readonly StringBuilder _tagBuffer = new StringBuilder();
    private readonly StringBuilder _optionBuffer = new StringBuilder();
    private readonly List<string> _collectedSuggestions = new List<string>();

    /// <summary>是否已切入 '%' 选项区；切入后不再产出任何片段。</summary>
    private bool _inOptionSection;

    /// <summary>是否仍为首个 chunk；用于清洗开头的 "- " 结构前缀。</summary>
    private bool _isFirstChunk = true;

    /// <summary>上一个已处理字符，用于判定 '%' 是否处于行首（跨 chunk 保持）。</summary>
    private char _lastProcessedChar;

    /// <summary>上一个已处理字符之前是否位于行首（全流第一个字符的情形）。</summary>
    private bool _atLineStart = true;

    /// <summary>
    /// 换行延迟判定：读到 '\n' / '\r' 时只置位不释放，等下一个字符到来后再决定去留。
    /// 若该字符是行首 '%'，则本换行是选项区的前导符，予以丢弃；否则作为正文字符释放。
    /// 由此既保留跨行对白的折行与打字机 450ms 换行顿挫，又不让选项区边界漏出换行符。
    /// </summary>
    private bool _hasPendingNewline;

    /// <summary>已产出的片段数（诊断用）。</summary>
    public int EmittedSegmentCount { get; private set; }

    /// <summary>
    /// 送入一段增量字符流，返回本次可立即消费的片段。
    /// 允许 chunk 为 null / 空（无动作）。
    /// </summary>
    /// <param name="chunk">增量文本。</param>
    public IEnumerable<StreamSegment> Feed(string chunk)
    {
        if (string.IsNullOrEmpty(chunk))
            yield break;

        if (_isFirstChunk)
        {
            // 清洗首 chunk 开头的结构前缀 "- "（Provider 常见的对白行前缀）
            if (chunk.StartsWith("- ", StringComparison.Ordinal))
                chunk = chunk.Substring(2);

            _isFirstChunk = false;
        }

        for (int i = 0; i < chunk.Length; i++)
        {
            char c = chunk[i];

            // ── 分支 1：选项区模式。此后不再产出任何片段。 ──
            if (_inOptionSection)
            {
                _optionBuffer.Append(c);
                AdvanceLineState(c);
                continue;
            }

            // ── 分支 2：选项区进入判定 ──
            // '%' 出现在行首（全流首字或紧跟换行）时切入选项区，并吞掉该字符。
            if (c == '%' && _atLineStart)
            {
                _inOptionSection = true;

                // 紧邻 '%' 的换行是选项区前导符，不再作为正文字符释放。
                _hasPendingNewline = false;

                AdvanceLineState(c);
                continue;
            }

            // ── 分支 2.5：延迟换行释放 ──
            // 上一字符是换行且本字符既不是选项区起始、也不是另一个换行符时，
            // 先把换行作为独立正文字符释放，保留 SpriteText 折行与打字机 450ms 换行停顿。
            // 排除换行符本身是必要的：'\r\n' 连续到达时若在此处释放，
            // 第二个 '\n' 会先被消费为片段、随后又把待定标记置回，导致折行重复一次。
            if (_hasPendingNewline && c != '\n' && c != '\r')
            {
                _hasPendingNewline = false;
                yield return new StreamSegment(StreamSegmentType.Text, "\n");
                EmittedSegmentCount++;
            }

            // ── 分支 3：标签缓冲模式 ──
            if (_tagBuffer.Length > 0)
            {
                _tagBuffer.Append(c);
                AdvanceLineState(c);

                if (c == ']')
                {
                    string tag = _tagBuffer.ToString();
                    _tagBuffer.Clear();

                    foreach (StreamSegment segment in TranslateTag(tag))
                        yield return segment;
                }
                else if (_tagBuffer.Length > MaxTagBufferLength)
                {
                    // ── 熔断：未闭合方括号超长，判定为普通文本回退释放 ──
                    ModEntry.SMonitor?.Log(
                        "[StreamTokenPipeline] Tag buffer exceeded 64 chars, flushing as text",
                        LogLevel.Trace);

                    _textBuffer.Append(_tagBuffer);
                    _tagBuffer.Clear();

                    // 熔断后本字符已并入文本，不再走常规文本分支
                    continue;
                }

                continue;
            }

            // ── 分支 4：常规台词文本模式 ──
            if (c == '[')
            {
                // 先释放已累积的正文，避免标签前后正文被合并成一段
                if (_textBuffer.Length > 0)
                {
                    yield return new StreamSegment(StreamSegmentType.Text, _textBuffer.ToString());
                    _textBuffer.Clear();
                    EmittedSegmentCount++;
                }

                _tagBuffer.Append(c);
                AdvanceLineState(c);
                continue;
            }

            if (c == '$')
            {
                // 2 字符窗口捕获：'$' 之后若为情绪码，则整体转为 Portrait 片段
                if (i + 1 < chunk.Length && TryReadMoodCode(chunk, i + 1, out string moodCode, out int consumed))
                {
                    yield return new StreamSegment(StreamSegmentType.Portrait, moodCode);
                    EmittedSegmentCount++;

                    for (int k = 0; k < consumed; k++)
                        AdvanceLineState(chunk[i + 1 + k]);

                    i += consumed;
                    continue;
                }

                // 窗口不足（'$' 落在 chunk 末尾）：作为普通文本处理并继续
                _textBuffer.Append(c);
                AdvanceLineState(c);
                continue;
            }

            // 换行符延迟判定：先释放已累积正文，再置位待定标记。
            // 本字符本身不立即释放——若下一字符是行首 '%'，它只是选项区前导符，应被丢弃；
            // 否则作为正文字符保留（见分支 2.5）。'\r\n' 连续出现时因标记幂等只产生一个换行。
            if (c == '\n' || c == '\r')
            {
                if (_textBuffer.Length > 0)
                {
                    yield return new StreamSegment(StreamSegmentType.Text, _textBuffer.ToString());
                    _textBuffer.Clear();
                    EmittedSegmentCount++;
                }

                _hasPendingNewline = true;
                AdvanceLineState(c);
                continue;
            }

            _textBuffer.Append(c);
            AdvanceLineState(c);
        }

        // 遍历结束：释放本 chunk 累积的正文
        if (_textBuffer.Length > 0)
        {
            yield return new StreamSegment(StreamSegmentType.Text, _textBuffer.ToString());
            _textBuffer.Clear();
            EmittedSegmentCount++;
        }
    }

    /// <summary>
    /// 流结束处理：释放残缺缓冲并解析候选项。
    /// </summary>
    public IEnumerable<StreamSegment> Flush()
    {
        // 未闭合标签：作为正文回退，避免丢字
        if (_tagBuffer.Length > 0)
        {
            _textBuffer.Append(_tagBuffer);
            _tagBuffer.Clear();
        }

        // 流以换行结尾：待定换行没有后继字符可供判定，按正文字符释放，不得丢弃。
        if (_hasPendingNewline)
        {
            _hasPendingNewline = false;
            yield return new StreamSegment(StreamSegmentType.Text, "\n");
            EmittedSegmentCount++;
        }

        if (_textBuffer.Length > 0)
        {
            yield return new StreamSegment(StreamSegmentType.Text, _textBuffer.ToString());
            _textBuffer.Clear();
            EmittedSegmentCount++;
        }

        ParseSuggestions();
    }

    /// <summary>返回已解析出的候选项（只读快照）。</summary>
    public IReadOnlyList<string> GetCollectedSuggestions() => _collectedSuggestions.AsReadOnly();

    /// <summary>复位全部缓冲与状态，可用于下一轮流。</summary>
    public void Reset()
    {
        _textBuffer.Clear();
        _tagBuffer.Clear();
        _optionBuffer.Clear();
        _collectedSuggestions.Clear();
        _inOptionSection = false;
        _isFirstChunk = true;
        _lastProcessedChar = '\0';
        _atLineStart = true;
        _hasPendingNewline = false;
        EmittedSegmentCount = 0;
    }

    #region 标签与情绪码翻译

    /// <summary>把一个已闭合的方括号标签翻译为对应片段；无对应类型时产出空序列。</summary>
    private static IEnumerable<StreamSegment> TranslateTag(string tag)
    {
        if (StartsWith(tag, ActionTagPrefix))
        {
            // 载荷保留 "ACTION:" 语义前缀（与 [UI:...] 分支一致）：下游以 "[" + Payload + "]"
            // 复原标签后交给 EmbodiedActionParser，而其正则只识别完整的 [ACTION:...] 形式，
            // 若在此剥离前缀将导致 FOLLOW / GOTO / STAY_HOME 等实体动作被静默丢弃。
            yield return new StreamSegment(
                StreamSegmentType.Action,
                tag.Substring(1, tag.Length - 2));
            yield break;
        }

        if (StartsWith(tag, UiTagPrefix))
        {
            // 载荷保留 "UI:" 前缀语义，由下游按既有 StreamLineTracker 约定消费
            yield return new StreamSegment(
                StreamSegmentType.Action,
                tag.Substring(1, tag.Length - 2));
            yield break;
        }

        if (StartsWith(tag, MoodTagPrefix))
        {
            yield return new StreamSegment(
                StreamSegmentType.Portrait,
                tag.Substring(MoodTagPrefix.Length, tag.Length - MoodTagPrefix.Length - 1));
            yield break;
        }

        // 其余方括号标签（如 [241] 动画帧）：作为 Action 载荷吞吐，保留内部内容
        if (tag.Length > 2)
            yield return new StreamSegment(StreamSegmentType.Action, tag.Substring(1, tag.Length - 2));
    }

    private static bool StartsWith(string value, string prefix)
        => value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 在 <paramref name="start"/> 处尝试读取原版情绪码。
    /// 支持 $h/$s/$u/$l/$a 单字母、$neutral 与 $0-$9 数字序列。
    /// </summary>
    /// <param name="chunk">当前 chunk。</param>
    /// <param name="start">'$' 之后的首字符下标。</param>
    /// <param name="moodCode">输出的情绪码（不含 '$'）。</param>
    /// <param name="consumed">消费的字符数（从 <paramref name="start"/> 起算）。</param>
    private static bool TryReadMoodCode(string chunk, int start, out string moodCode, out int consumed)
    {
        moodCode = null;
        consumed = 0;

        if (start >= chunk.Length)
            return false;

        char c = chunk[start];

        if (MoodLetters.IndexOf(c) >= 0)
        {
            moodCode = c.ToString();
            consumed = 1;
            return true;
        }

        if (c == 'n' && chunk.Length - start >= 6
            && string.CompareOrdinal(chunk, start, "neutral", 0, 6) == 0)
        {
            moodCode = "neutral";
            consumed = 6;
            return true;
        }

        if (c >= '0' && c <= '9')
        {
            int digits = 0;
            while (start + digits < chunk.Length
                && chunk[start + digits] >= '0'
                && chunk[start + digits] <= '9')
            {
                digits++;
            }

            // 单个数字可能是普通金额/序号的一部分，仅在长度 >= 2 或紧跟非数字时按情绪码处理
            moodCode = chunk.Substring(start, digits);
            consumed = digits;
            return true;
        }

        return false;
    }

    #endregion

    #region 选项区解析

    /// <summary>
    /// 解析选项区文本为候选项。按行拆分、剔除前导 '-'/'%'/空白、过滤空行，最多取 3 条。
    /// </summary>
    private void ParseSuggestions()
    {
        if (_optionBuffer.Length == 0)
            return;

        string raw = _optionBuffer.ToString();
        _optionBuffer.Clear();

        int lineStart = 0;
        while (lineStart <= raw.Length && _collectedSuggestions.Count < MaxSuggestions)
        {
            int newlineIndex = raw.IndexOf('\n', lineStart);
            int lineEnd = newlineIndex < 0 ? raw.Length : newlineIndex;
            int lineLength = lineEnd - lineStart;

            if (lineLength > 0)
            {
                string line = raw.Substring(lineStart, lineLength).Replace("\r", "").Trim();
                line = line.TrimStart('-', '%', ' ').Trim();

                // 格式异常（未以 '-' 引导）时尽力提取，保留原始文本
                if (line.Length > 0)
                    _collectedSuggestions.Add(line);
            }

            if (newlineIndex < 0)
                break;

            lineStart = newlineIndex + 1;
        }
    }

    #endregion

    #region 行首状态

    /// <summary>更新行首跟踪状态，支撑跨 chunk 的 '%' 行首判定。</summary>
    private void AdvanceLineState(char c)
    {
        _lastProcessedChar = c;
        _atLineStart = c == '\n' || c == '\r';
    }

    #endregion
}
