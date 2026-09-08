using System.Text;

namespace ValleytalkReborn;

internal sealed class StreamLineTracker
{
    private readonly StringBuilder _lineBuf = new StringBuilder();
    private bool _inOptionSection = false;

    public string Feed(string delta)
    {
        if (string.IsNullOrEmpty(delta)) return null;

        _lineBuf.Append(delta);
        var display = new StringBuilder();

        var content = _lineBuf.ToString();
        int newlineIdx;
        while ((newlineIdx = content.IndexOf('\n')) >= 0)
        {
            var line = content.Substring(0, newlineIdx).Replace("\r", "").Trim();
            content  = content.Substring(newlineIdx + 1);

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

        _lineBuf.Clear();
        _lineBuf.Append(content);

        if (!_inOptionSection && _lineBuf.Length > 0)
            display.Append(_lineBuf);

        return display.Length > 0 ? display.ToString() : null;
    }

    public void Reset()
    {
        _lineBuf.Clear();
        _inOptionSection = false;
    }
}
