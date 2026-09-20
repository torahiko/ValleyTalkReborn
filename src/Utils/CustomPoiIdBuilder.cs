using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ValleytalkReborn;

/// <summary>自定义 POI 标识符生成：Custom_ + 消毒名，冲突 _2/_3 递增。纯函数。</summary>
internal static class CustomPoiIdBuilder
{
    public static string BuildCustomPoiId(string baseName, ICollection<string> existingIds)
    {
        string sanitized = Sanitize(baseName);
        string id = "Custom_" + sanitized;
        if (!existingIds.Contains(id))
            return id;
        int suffix = 2;
        while (existingIds.Contains($"Custom_{sanitized}_{suffix}"))
            suffix++;
        return $"Custom_{sanitized}_{suffix}";
    }

    private static string Sanitize(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "Custom";
        char[] invalid = Path.GetInvalidFileNameChars();
        string s = name.Trim();
        foreach (char c in invalid)
            s = s.Replace(c, '_');
        return string.IsNullOrWhiteSpace(s) ? "Custom" : s;
    }
}
