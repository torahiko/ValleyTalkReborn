using System;

namespace ValleytalkReborn;

internal static class ProviderDefaults
{
    internal const string OllamaDefaultUrl = "http://localhost:11434/v1";
    internal const string LmStudioDefaultUrl = "http://localhost:1234/v1";

    internal static bool IsLocalProvider(string provider)
    {
        return string.Equals(provider, "Ollama", StringComparison.OrdinalIgnoreCase)
            || string.Equals(provider, "LMStudio", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 本地目标共享分类：Provider 名（Ollama / LMStudio / LlamaCpp）或 URL 指向回环/私网。
    /// 纯函数，无 ModEntry / 世界状态依赖。
    /// </summary>
    internal static bool IsLocalTarget(string provider, string configuredUrl)
    {
        if (IsLocalProvider(provider)
            || string.Equals(provider, "LlamaCpp", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return UrlHelper.IsLoopbackUrl(configuredUrl) || UrlHelper.IsPrivateNetworkUrl(configuredUrl);
    }

    internal static string ResolveServerAddress(string provider, string configuredUrl)
    {
        if (!string.IsNullOrWhiteSpace(configuredUrl))
        {
            return UrlHelper.EnsureScheme(configuredUrl.Trim());
        }

        if (string.Equals(provider, "Ollama", StringComparison.OrdinalIgnoreCase))
        {
            return OllamaDefaultUrl;
        }

        if (string.Equals(provider, "LMStudio", StringComparison.OrdinalIgnoreCase))
        {
            return LmStudioDefaultUrl;
        }

        return configuredUrl;
    }
}
