using System.Text;
using System.Text.RegularExpressions;

namespace ValleytalkReborn;

internal sealed class StreamLineTracker
{
    /// <summary>匹配完整的 [UI:...] 标签，用于从输出中剔除。</summary>
    private static readonly Regex CompleteUiTagPattern = new Regex(@"\[UI:[^\]]+\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>匹配尾部未闭合的 [UI:... 残缺段，用于扣留防止逐字泄漏。</summary>
    private static readonly Regex IncompleteUiTagPattern = new Regex(@"\[UI:[^\]]*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// 合法 [UI:...] 标签的最大可能长度。超过此阈值视为非控制标签，强制熔断释放，
    /// 防止模型异常截断导致 _withheldFragment 无限扣留后续文本。
    /// </summary>
    private const int MaxWithheldLength = 64;

    private readonly StringBuilder _lineBuf = new StringBuilder();
    private string _withheldFragment;
    private bool _inOptionSection = false;

    public string Feed(string delta)
    {
        if (string.IsNullOrEmpty(delta)) return null;

        // 🔧 兜底熔断：残片超长说明并非合法 [UI:...] 标签，强制释放避免文本丢失
        if (!string.IsNullOrEmpty(_withheldFragment) && _withheldFragment.Length > MaxWithheldLength)
        {
            delta = _withheldFragment + delta;
            _withheldFragment = string.Empty;
        }

        // 将上次扣留的残片拼接到新 delta 之前（保持原始文本顺序）
        string text = (_withheldFragment ?? "") + delta;
        _withheldFragment = null;

        _lineBuf.Append(text);
        string full = _lineBuf.ToString();

        // 若尾部存在残缺的 [UI:... 标签，扣留残缺段不释放，防止 [、[U、[UI: 等碎片泄漏到屏幕
        var incMatch = IncompleteUiTagPattern.Match(full);
        if (incMatch.Success)
        {
            _withheldFragment = incMatch.Value;
            full = full.Substring(0, incMatch.Index);
            _lineBuf.Clear();
        }

        // 剔除已完整闭合的 [UI:...] 标签
        full = CompleteUiTagPattern.Replace(full, "");

        // ── 原有换行切分与选项段过滤逻辑 ──
        var display = new StringBuilder();

        int newlineIdx;
        while ((newlineIdx = full.IndexOf('\n')) >= 0)
        {
            var line = full.Substring(0, newlineIdx).Replace("\r", "").Trim();
            full = full.Substring(newlineIdx + 1);

            if (line.StartsWith("%"))
            {
                _inOptionSection = true;
                continue;
            }

            if (_inOptionSection) continue;

            if (line.StartsWith("-"))
            {
                var stripped = line.TrimStart('-', ' ');
                if (!string.IsNullOrWhiteSpace(stripped))
                    display.Append(stripped).Append(' ');
            }
            else if (!string.IsNullOrWhiteSpace(line))
            {
                display.Append(line).Append(' ');
            }
        }

        // 保留未闭合的尾部供下次拼接，同时立即显示（与原始行为一致）
        _lineBuf.Clear();
        _lineBuf.Append(full);

        if (!_inOptionSection && full.Length > 0)
            display.Append(full);

        return display.Length > 0 ? display.ToString() : null;
    }

    public void Reset()
    {
        _lineBuf.Clear();
        _withheldFragment = null;
        _inOptionSection = false;
    }
}
