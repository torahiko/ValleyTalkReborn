using System;
using System.Collections.Generic;
using System.Linq;

namespace ValleytalkReborn;

/// <summary>
/// 时间窗 "HHmm-HHmm" 与 (start, end) 整数的双向转换。纯函数。
/// </summary>
internal static class TimeWindowConverter
{
    /// <summary>解析 "1800-2130" → (1800, 2130)。非法返回 false（start/end 置 0）。</summary>
    public static bool TryParse(string timeWindow, out int start, out int end)
    {
        start = 0;
        end = 0;
        if (string.IsNullOrEmpty(timeWindow))
            return false;

        int dash = timeWindow.IndexOf('-');
        if (dash <= 0 || dash >= timeWindow.Length - 1)
            return false;

        string startStr = timeWindow.Substring(0, dash);
        string endStr = timeWindow.Substring(dash + 1);

        if (startStr.Length != 4 || endStr.Length != 4)
            return false;
        if (!int.TryParse(startStr, out int s) || !int.TryParse(endStr, out int e))
            return false;
        if (!IsValidTime(s) || !IsValidTime(e) || s >= e)
            return false;

        start = s;
        end = e;
        return true;
    }

    /// <summary>格式化 (start, end) → "1800-2130"。越界由调用方保证，否则抛 ArgumentOutOfRangeException。</summary>
    public static string Format(int start, int end)
    {
        if (!IsValidTime(start))
            throw new ArgumentOutOfRangeException(nameof(start), start, "时间须为 30 分钟网格上的 600–2550");
        if (!IsValidTime(end))
            throw new ArgumentOutOfRangeException(nameof(end), end, "时间须为 30 分钟网格上的 600–2550");
        if (start >= end)
            throw new ArgumentOutOfRangeException(nameof(start), start, "起始时间须小于结束时间");
        return $"{start:D4}-{end:D4}";
    }

    /// <summary>
    /// 墙钟 30 分钟网格判定：HHmm 编码下"30 分钟网格"指分钟位 ∈ {00, 30}，
    /// 故取 (t % 100) % 30 == 0 且分钟位 &lt; 60（排除 660/690 等伪值）；禁止对整个整数取模（否则 1900/2000/2200 等合法时间被误杀）。
    /// </summary>
    private static bool IsValidTime(int t) => t >= 600 && t <= 2550 && (t % 100) < 60 && (t % 100) % 30 == 0;

    /// <summary>
    /// 600 起至 2550 的墙钟 30 分钟网格（小时 6..25 × {00,30}，共 40 项）。单一事实源。
    /// </summary>
    public static IReadOnlyList<int> EnumerateGrid()
    {
        if (_grid is not null)
            return _grid;

        var list = new List<int>(40);
        for (int hour = 6; hour <= 25; hour++)
        {
            list.Add(hour * 100);     // :00
            list.Add(hour * 100 + 30); // :30
        }
        _grid = list.AsReadOnly();
        return _grid;
    }

    private static IReadOnlyList<int>? _grid;
}
