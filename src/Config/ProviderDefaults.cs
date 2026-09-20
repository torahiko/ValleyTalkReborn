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
