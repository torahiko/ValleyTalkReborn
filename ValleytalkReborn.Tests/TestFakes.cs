// TestFakes.cs
// Minimal SMAPI fakes so tests can drive ModEntry.SHelper.Translation.Locale,
// ModEntry.SMonitor, and construct Character instances without a real game.
// Only the members touched by IsZh() and Character's constructor/static ctor are
// functional; everything else throws NotImplementedException.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewModdingAPI.Framework.Logging;
using StardewValley;

namespace ValleytalkReborn.Tests;

internal static class TestEnv
{
    private static readonly FieldInfo LocaleField =
        typeof(ModEntry).GetField("_locale", BindingFlags.Static | BindingFlags.NonPublic);
    private static readonly FieldInfo LocaleCacheField =
        typeof(ModEntry).GetField("_localeCache", BindingFlags.Static | BindingFlags.NonPublic);
    private static readonly MethodInfo SHelperSetter =
        typeof(ModEntry).GetProperty("SHelper").GetSetMethod(nonPublic: true);

    public static IModHelper GetSHelper() => ModEntry.SHelper;
    public static void SetSHelper(IModHelper value) => SHelperSetter?.Invoke(null, new object[] { value });

    public static CultureInfo GetLocaleField() => (CultureInfo)LocaleField?.GetValue(null);
    public static string GetLocaleCacheField() => (string)LocaleCacheField?.GetValue(null);
    public static void SetLocaleField(CultureInfo value) => LocaleField?.SetValue(null, value);
    public static void SetLocaleCacheField(string value) => LocaleCacheField?.SetValue(null, value);

    /// <summary>
    /// Snapshots current ModEntry static state, applies the requested locale, and returns
    /// an IDisposable that restores the original state on Dispose. Use in a using statement
    /// to guarantee cleanup even if the test throws.
    /// </summary>
    public static IDisposable UseIsolatedLocale(string locale)
    {
        return new LocaleIsolation(locale);
    }

    private class LocaleIsolation : IDisposable
    {
        private readonly IModHelper _originalSHelper;
        private readonly IMonitor _originalSMonitor;
        private readonly ModConfig _originalConfig;
        private readonly CultureInfo _originalLocale;
        private readonly string _originalLocaleCache;
        private bool _disposed;

        public LocaleIsolation(string locale)
        {
            // Snapshot BEFORE any mutation.
            _originalSHelper = ModEntry.SHelper;
            _originalSMonitor = ModEntry.SMonitor;
            _originalConfig = ModEntry.Config;
            _originalLocale = GetLocaleField();
            _originalLocaleCache = GetLocaleCacheField();

            ApplyLocale(locale);
        }

        private static void ApplyLocale(string locale)
        {
            // Force English UI culture so CultureInfo.DisplayName returns "Chinese (Simplified)"
            // rather than the native name "中文", which lets ResolveIsChinese() match "chinese".
            Thread.CurrentThread.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");

            // Provide a no-op monitor so PromptCache's catch-block logging doesn't throw.
            ModEntry.SMonitor = new FakeMonitor();
            // Provide a Config so PromptCache's catch-block can set EnableMod = false without NRE.
            ModEntry.Config = new ModConfig();

            var fake = new FakeModHelper(locale);
            SetSHelper(fake);
            SetLocaleField(null);
            SetLocaleCacheField(string.Empty);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // Unconditionally restore to pre-test values.
            SetSHelper(_originalSHelper);
            ModEntry.SMonitor = _originalSMonitor;
            ModEntry.Config = _originalConfig;
            SetLocaleField(_originalLocale);
            SetLocaleCacheField(_originalLocaleCache);
        }
    }
}

internal class FakeMonitor : IMonitor
{
    public bool IsVerbose => false;
    public void Log(string message, LogLevel level) { }
    public void LogOnce(string message, LogLevel level) { }
    public void VerboseLog(string message) { }
    public void VerboseLog(ref VerboseLogStringHandler handler) { }
}

internal class FakeContentEvents : IContentEvents
{
    public event EventHandler<AssetRequestedEventArgs> AssetRequested { add { } remove { } }
    public event EventHandler<AssetsInvalidatedEventArgs> AssetsInvalidated { add { } remove { } }
    public event EventHandler<AssetReadyEventArgs> AssetReady { add { } remove { } }
    public event EventHandler<LocaleChangedEventArgs> LocaleChanged { add { } remove { } }
}

internal class FakeModEvents : IModEvents
{
    private readonly FakeContentEvents _content = new FakeContentEvents();
    public IContentEvents Content => _content;
    public IDisplayEvents Display => throw new NotImplementedException();
    public IGameLoopEvents GameLoop => throw new NotImplementedException();
    public IInputEvents Input => throw new NotImplementedException();
    public IMultiplayerEvents Multiplayer => throw new NotImplementedException();
    public IPlayerEvents Player => throw new NotImplementedException();
    public IWorldEvents World => throw new NotImplementedException();
    public ISpecializedEvents Specialized => throw new NotImplementedException();
}

internal class FakeTranslationHelper : ITranslationHelper
{
    private readonly string _locale;
    public FakeTranslationHelper(string locale) { _locale = locale; }
    public string Locale => _locale;
    public LocalizedContentManager.LanguageCode LocaleEnum =>
        Enum.TryParse<LocalizedContentManager.LanguageCode>(_locale, out var c) ? c : LocalizedContentManager.LanguageCode.en;
    public string ModID => "TestMod";
    public bool ContainsKey(string key) => false;
    public IEnumerable<string> GetKeys() => Array.Empty<string>();
    public IEnumerable<Translation> GetTranslations() => Array.Empty<Translation>();
    public Translation Get(string key) => EmptyTranslation;
    public Translation Get(string key, object tokens) => EmptyTranslation;
    public IDictionary<string, Translation> GetInAllLocales(string key, bool returnNull) => new Dictionary<string, Translation>();

    // A default-valued Translation whose HasValue() returns false, so Util.GetString
    // falls through to I18n without NRE. Created once via FormatterServices since
    // the type has no public constructors.
    private static readonly Translation EmptyTranslation = (Translation)FormatterServices.GetUninitializedObject(typeof(Translation));
}

internal class FakeModHelper : IModHelper
{
    private readonly FakeTranslationHelper _translation;
    private readonly FakeModEvents _events = new FakeModEvents();
    public FakeModHelper(string locale) { _translation = new FakeTranslationHelper(locale); }
    public string DirectoryPath => ".";
    public IModEvents Events => _events;
    public ICommandHelper ConsoleCommands => throw new NotImplementedException();
    public IGameContentHelper GameContent => throw new NotImplementedException();
    public IModContentHelper ModContent => throw new NotImplementedException();
    public IContentPackHelper ContentPacks => throw new NotImplementedException();
    public IDataHelper Data => throw new NotImplementedException();
    public IInputHelper Input => throw new NotImplementedException();
    public IReflectionHelper Reflection => throw new NotImplementedException();
    public IModRegistry ModRegistry => throw new NotImplementedException();
    public IMultiplayerHelper Multiplayer => throw new NotImplementedException();
    public ITranslationHelper Translation => _translation;
    public TConfig ReadConfig<TConfig>() where TConfig : class, new() => throw new NotImplementedException();
    public void WriteConfig<TConfig>(TConfig config) where TConfig : class, new() => throw new NotImplementedException();
}
