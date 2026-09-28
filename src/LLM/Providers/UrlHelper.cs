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

        // LOCAL-006：本地 / 私网 IPv4 默认 http，其余仍 https；显式 scheme 在上面已原样返回。
        if (IsLocalHost(host) || IsPrivateIpv4Host(host))
        {
            return "http://" + url;
        }

        return "https://" + url;
    }

    /// <summary>
    /// 判定主机名是否为 RFC1918 私网 IPv4（点分四段）。纯字符串解析，禁 DNS / 网络 / 文件 I/O；
    /// 非法输入返回 false，不抛异常。与 URL vs Host 无关，仅供 EnsureScheme / IsPrivateNetworkUrl 复用。
    /// </summary>
    internal static bool IsPrivateIpv4Host(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        string[] parts = host.Split('.');
        if (parts.Length != 4)
        {
            return false;
        }

        byte[] octets = new byte[4];
        for (int i = 0; i < 4; i++)
        {
            if (!byte.TryParse(parts[i], out octets[i]))
            {
                return false;
            }
        }

        if (octets[0] == 10) return true;
        if (octets[0] == 172 && octets[1] >= 16 && octets[1] <= 31) return true;
        if (octets[0] == 192 && octets[1] == 168) return true;

        return false;
    }

    internal static bool IsLocalHost(string host)
    {
        return string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "[::1]", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsLoopbackUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        if (Uri.TryCreate(EnsureScheme(url.Trim()), UriKind.Absolute, out Uri uri))
        {
            return IsLocalHost(uri.Host);
        }

        return false;
    }

    /// <summary>
    /// 判定 URL 是否指向本地/私网端点：Host 为 localhost / 127.0.0.1 / [::1]，
    /// 或 IPv4 落在 10.0.0.0/8、172.16.0.0/12、192.168.0.0/16。
    /// 纯字符串与 URI 解析，禁止 DNS、网络与文件 I/O；解析失败返回 false（不因解析失败判为本地）。
    /// </summary>
    internal static bool IsPrivateNetworkUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        if (!Uri.TryCreate(EnsureScheme(url.Trim()), UriKind.Absolute, out Uri uri))
        {
            return false;
        }

        if (IsLocalHost(uri.Host))
        {
            return true;
        }

        if (uri.HostNameType != UriHostNameType.IPv4)
        {
            return false;
        }

        return IsPrivateIpv4Host(uri.Host);
    }
}
