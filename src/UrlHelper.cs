using System;

namespace ValleytalkReborn;

/// <summary>
/// 纯函数 URL 归一化工具。主线程与 Task.Run 后台均可调用，无静态可变状态。
/// </summary>
internal static class UrlHelper
{
    private static readonly string[] Suffixes = new[]
    {
        "/chat/completions",
        "/completions",
        "/embeddings",
        "/models",
        "/v1",
    };

    internal static string NormalizeBaseUrl(string rawUrl, string defaultBase = "https://api.openai.com/v1")
    {
        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            return defaultBase;
        }

        string url = EnsureScheme(rawUrl.Trim());

        // 专家逃生舱：含查询串则原样透传，不做任何补全/剥离。
        if (url.Contains('?'))
        {
            return url;
        }

        // 定点循环：剥离已知后缀直至不再变化。
        string prev;
        do
        {
            prev = url;
            url = url.TrimEnd('/');

            foreach (string suffix in Suffixes)
            {
                if (url.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    url = url.Substring(0, url.Length - suffix.Length);
                    break;
                }
            }
        }
        while (url != prev);

        bool hasV1 = url.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
                     || url.EndsWith("/v2", StringComparison.OrdinalIgnoreCase)
                     || url.Contains("/v1/")
                     || url.Contains("/v2/");
        if (!hasV1)
        {
            url = url + "/v1";
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out Uri uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            return url;
        }

        return defaultBase;
    }

    internal static string EnsureScheme(string rawUrl)
    {
        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            return rawUrl;
        }

        string url = rawUrl.Trim();

        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }

        string authority = url;
        int slashIndex = url.IndexOf('/');
        if (slashIndex >= 0)
        {
            authority = url.Substring(0, slashIndex);
        }

        string host;
        if (authority.StartsWith("["))
        {
            int close = authority.IndexOf(']');
            host = close >= 0 ? authority.Substring(0, close + 1) : authority;
        }
        else
        {
            int colon = authority.IndexOf(':');
            host = colon >= 0 ? authority.Substring(0, colon) : authority;
        }

        if (IsLocalHost(host))
        {
            return "http://" + url;
        }

        return "https://" + url;
    }

    private static bool IsLocalHost(string host)
    {
        return string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "[::1]", StringComparison.OrdinalIgnoreCase);
    }
}
