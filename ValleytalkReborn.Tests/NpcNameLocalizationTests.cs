// NpcNameLocalizationTests.cs
// ═══════════════════════════════════════════════════════════════════════════
// VT-NPC-NAME-LOCALIZE-01 — NPC 内部标识符与本地化显示名混用导致的英文泄漏回归。
//
// 覆盖四条验收线：
//   1. GetLocalizedName 中文环境解析链（简中字典兜底 / 1.6 资产元数据自定义名 /
//      "[LocalizedText" 原始 token 拒收 / 未知 NPC 原名返回）。
//   2. BuildSpouse 与配偶对话：内部名比较必须命中 talkingToSpouse 分支，
//      其余配偶经 GetLocalizedName 渲染，绝不出现对话对象本人的内部名。
//   3. BuildSpouse 与第三方村民对话：输出全量本地化配偶名单。
//   4. DailyHeadlinedGenerator 婚礼/离婚头条使用本地化显示名（快照比较仍用内部名）。
//
// 无头环境事实（沿用 GiftPipelineHistoryTests 头注）：
//   - Game1.content == null：PromptCache 走 Game1.content.Load 必炸并停用 Mod，
//     因此直接反射预置 PromptCache._promptCache（并钉住 locale/gender 缓存避免
//     刷新回退），Util.GetString 即可命中确定性中文模板；
//   - friendshipData（NetStringDictionary）不承 NetFieldBase，需显式实例化；
//   - farmer.getChildren 依赖 homeLocation + Game1._locationLookup 的 FarmHouse
//     注册与 game1 实例垫片（instanceGameLocation 保持 null）；
//   - CharacterData 位于 StardewValley.GameData.dll（测试工程不引用），全程反射。
// ═══════════════════════════════════════════════════════════════════════════

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Runtime.Serialization;
using Netcode;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Locations;
using StardewValley.Network;
using ValleytalkReborn;
using ValleytalkReborn.Dialogue.Coordination;
using Xunit;

namespace ValleytalkReborn.Tests;

[Collection("EngineStaticStateCollection")]
public class NpcNameLocalizationTests : IDisposable
{
    private const string FarmerName = "虎彦";

    private static readonly FieldInfo CharacterDataField =
        typeof(Game1).GetField("characterData", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

    private static readonly FieldInfo Game1InstanceField =
        typeof(Game1).GetField("game1", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

    private static readonly FieldInfo NetWorldStateField =
        typeof(Game1).GetField("netWorldState", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

    private static readonly FieldInfo LocationLookupField =
        typeof(Game1).GetField("_locationLookup", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

    private static readonly FieldInfo PrevSpouseField =
        typeof(DailyHeadlinedGenerator).GetField("_prevSpouse", BindingFlags.Static | BindingFlags.NonPublic);

    private static readonly FieldInfo NpcBioDataField =
        typeof(Character).GetField("_bioData", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    static NpcNameLocalizationTests()
    {
        // SMAPI Context / MonoGame LocalizedContentManager 垫片的卫星程序集回退加载
        // （与 GiftPipelineHistoryTests 相同的钩子，幂等）。
        string gameDir = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Stardew Valley"));
        string smapiInternalDir = Path.Combine(gameDir, "smapi-internal");
        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            if (name.Name == null)
            {
                return null;
            }

            string candidate = name.Name.StartsWith("SMAPI.", StringComparison.Ordinal)
                ? Path.Combine(smapiInternalDir, name.Name + ".dll")
                : Path.Combine(gameDir, name.Name + ".dll");
            return File.Exists(candidate) ? context.LoadFromAssemblyPath(candidate) : null;
        };
    }

    private readonly LocalizedContentManager.LanguageCode _originalLanguageCode;
    private readonly ModConfig _originalConfig;
    private readonly object _originalCharacterData;
    private readonly object _originalGame1;
    private readonly object _originalNetWorldState;
    private readonly object _originalLocationLookup;
    private readonly object _originalPromptCache;
    private readonly object _originalPromptLocaleCache;
    private readonly object _originalPromptGenderCache;
    private readonly IDisposable _playerScope;
    private readonly IDisposable _localeScope;

    public NpcNameLocalizationTests()
    {
        TestEnvironment.InstallHeadlessContext();

        _originalLanguageCode = LocalizedContentManager.CurrentLanguageCode;
        _originalConfig = ModEntry.Config;
        _originalCharacterData = CharacterDataField?.GetValue(null);
        _originalGame1 = Game1InstanceField?.GetValue(null);
        _originalNetWorldState = NetWorldStateField?.GetValue(null);
        _originalLocationLookup = LocationLookupField?.GetValue(null);

        _localeScope = TestEnv.UseIsolatedLocale("zh");
        // PerceptionManager 静态构造订阅 SHelper.Events.GameLoop——FakeModEvents 会抛
        // NotImplementedException，必须换全静默事件桩（同 GiftPipelineHistoryTests）。
        TestEnv.SetSHelper(new QuietModHelper());
        ModEntry.Config = new ModConfig { LanguageOverride = "zh" };
        LocalizedContentManager.CurrentLanguageCode = LocalizedContentManager.LanguageCode.zh;
        // 空 characterData：GetLocalizedName 的实体层/元数据层在无头环境下确定性走空，
        // 断言聚焦在简中字典兜底层。
        CharacterDataField?.SetValue(null, CreateEmptyStringKeyedDictionary(CharacterDataField.FieldType));

        // 预置 PromptCache：绕开 Game1.content 依赖，钉住刷新条件（locale/gender）。
        var promptCache = PromptCache.Instance;
        _originalPromptCache = typeof(PromptCache)
            .GetField("_promptCache", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(promptCache);
        _originalPromptLocaleCache = typeof(PromptCache)
            .GetField("_promptLocaleCache", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(promptCache);
        _originalPromptGenderCache = typeof(PromptCache)
            .GetField("_promptGenderCache", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(promptCache);

        _playerScope = FakePlayer.Install(FarmerName);
        SeedPromptCache();

        // 供 DailyHeadlinedGenerator（game1.currentLocation / player.getChildren 路径）。
        InstallGameShim();
    }

    public void Dispose()
    {
        var promptCacheType = typeof(PromptCache);
        promptCacheType.GetField("_promptCache", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.SetValue(PromptCache.Instance, _originalPromptCache);
        promptCacheType.GetField("_promptLocaleCache", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.SetValue(PromptCache.Instance, _originalPromptLocaleCache);
        promptCacheType.GetField("_promptGenderCache", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.SetValue(PromptCache.Instance, _originalPromptGenderCache);

        CharacterDataField?.SetValue(null, _originalCharacterData);
        LocationLookupField?.SetValue(null, _originalLocationLookup);
        NetWorldStateField?.SetValue(null, _originalNetWorldState);
        Game1InstanceField?.SetValue(null, _originalGame1);
        LocalizedContentManager.CurrentLanguageCode = _originalLanguageCode;
        _playerScope?.Dispose();
        ModEntry.Config = _originalConfig;
        _localeScope?.Dispose();
    }

    // ── 1. GetLocalizedName 解析链 ──

    [Fact]
    public void NpcNameLocalizer_ChineseLanguage_ResolvesFallbackAndCustomNames()
    {
        // 字典兜底层
        Assert.Equal("亚历克斯", NpcNameLocalizer.GetLocalizedName("Alex"));
        Assert.Equal("山姆", NpcNameLocalizer.GetLocalizedName("Sam"));
        // GetZhName 兼容别名与统一流水线同源
        Assert.Equal("亚历克斯", NpcNameLocalizer.GetZhName("Alex"));
        // 未知 NPC：各层均未命中，原样返回内部名
        Assert.Equal("SomeUnknownNpc", NpcNameLocalizer.GetLocalizedName("SomeUnknownNpc"));

        // 1.6 资产元数据层：自定义 DisplayName（无 token 的纯文本，ParseText 原样返回）
        var characterData = (IDictionary)CharacterDataField.GetValue(null);
        characterData["ModNpc"] = CreateCharacterData("星尘");
        Assert.Equal("星尘", NpcNameLocalizer.GetLocalizedName("ModNpc"));

        // 元数据层产物为原始 "[LocalizedText" token 时必须拒收并回退
        characterData["RawTokenNpc"] = CreateCharacterData("[LocalizedText Strings\\NPCNames:RawTokenNpc]");
        Assert.Equal("RawTokenNpc", NpcNameLocalizer.GetLocalizedName("RawTokenNpc"));

        // 非中文环境：字典兜底层不生效，返回内部名（无实体/无元数据时）
        LocalizedContentManager.CurrentLanguageCode = LocalizedContentManager.LanguageCode.es;
        Assert.Equal("Alex", NpcNameLocalizer.GetLocalizedName("Alex"));
    }

    // ── 2. BuildSpouse：与配偶对话 ──

    [Fact]
    public void BuildSpouse_TalkingToSpouse_FiltersSelfAndLocalizesOtherSpouses()
    {
        using var world = InstallMarriedFarmer("Alex", "Sam", "Shane");
        var character = MakeCharacter("Alex");

        // name 为当前会话正在使用的本地化显示名；内部名比较必须命中配偶分支
        string result = Prompts.PromptsBlocks.BuildSpouse(character, "亚历克斯");

        // talkingToSpouse 分支：其余配偶本地化渲染，自我（Alex/亚历克斯）绝不进入名单
        Assert.Contains("农夫还与其他配偶同住", result);
        Assert.Contains("山姆", result);
        Assert.Contains("谢恩", result);
        Assert.DoesNotContain("Alex", result);
        Assert.DoesNotContain("亚历克斯", result);
        // 多配偶 + 与配偶对话 → spousePolyView（配偶视角），而非 spousePoly（吃瓜视角）
        Assert.Contains("你自愿参与到与农夫及其他伴侣的多人恋爱关系中", result);
        Assert.DoesNotContain("你知道并接受农夫的多人恋爱关系", result);
    }

    // ── 3. BuildSpouse：与第三方村民对话 ──

    [Fact]
    public void BuildSpouse_TalkingToNonSpouse_OutputsAllLocalizedSpouses()
    {
        using var world = InstallMarriedFarmer("Alex", "Sam");
        var character = MakeCharacter("Haley");

        string result = Prompts.PromptsBlocks.BuildSpouse(character, "海莉");

        // 局外人视角：全量本地化名单 + spousePoly
        // （FieldDict 为插入序字典，Alex 先于 Sam 加入，输出按插入序渲染）
        Assert.Contains("农夫同时与 2 个人结婚", result);
        Assert.Contains("亚历克斯、山姆", result);
        Assert.DoesNotContain("Alex", result);
        Assert.Contains("你知道并接受农夫的多人恋爱关系", result);
    }

    // ── 4. DailyHeadlinedGenerator：婚礼/离婚头条 ──

    [Fact]
    public void DailyHeadline_MarriageAndDivorce_UsesLocalizedNames()
    {
        using var world = InstallMarriedFarmer("Alex");
        ModEntry.Config.EnablePerceptionSystem = true;

        var farmer = Game1.player;
        FindField(typeof(Farmer), "netSpouse")?.SetValue(farmer, new NetString("Alex"));
        PrevSpouseField.SetValue(null, string.Empty);

        InvokeTryInjectLifeEvent();
        var weddingEntry = PerceptionManager.Instance.GetGossipSnapshots()
            .Single(e => string.Equals(e.Key, "LifeEvent", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("亚历克斯", weddingEntry.Template);
        Assert.Contains(FarmerName, weddingEntry.Template);
        Assert.DoesNotContain("Alex", weddingEntry.Template);

        // 离婚：快照比较使用内部名 _prevSpouse，但文案渲染必须本地化；
        // EnqueueGossip 以 Key 去重替换（同键仅保留一条），故与婚礼模板互异即可。
        FindField(typeof(Farmer), "netSpouse")?.SetValue(farmer, new NetString(string.Empty));
        PrevSpouseField.SetValue(null, "Alex");

        InvokeTryInjectLifeEvent();
        var divorceEntry = PerceptionManager.Instance.GetGossipSnapshots()
            .Single(e => string.Equals(e.Key, "LifeEvent", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("亚历克斯", divorceEntry.Template);
        Assert.Contains(FarmerName, divorceEntry.Template);
        Assert.DoesNotContain("Alex", divorceEntry.Template);
        Assert.NotEqual(weddingEntry.Template, divorceEntry.Template);
    }

    // ── 无头垫片 ──

    /// <summary>
    /// 与配偶/村民对话共用的最小世界垫片：friendshipData（NetStringDictionary
    /// 不承 NetFieldBase，需显式实例化）+ 任一配偶置为已结婚状态。
    /// </summary>
    private IDisposable InstallMarriedFarmer(params string[] spouseKeys)
    {
        var farmer = Game1.player;
        // friendshipData 为 readonly 字段且不承 NetFieldBase（FakePlayer 的标量补齐
        // 不会初始化），反射替换为全新实例后直填 FieldDict。
        FindField(typeof(Farmer), "friendshipData")?.SetValue(
            farmer, new NetStringDictionary<Friendship, NetRef<Friendship>>());
        foreach (var key in spouseKeys)
        {
            farmer.friendshipData.FieldDict[key] = new NetRef<Friendship>(
                new Friendship { Status = FriendshipStatus.Married });
        }
        return new ActionScope(() =>
        {
            FindField(typeof(Farmer), "friendshipData")?.SetValue(farmer, null);
        });
    }

    /// <summary>
    /// DailyHeadline 路径所需：game1 实例（currentLocation 读取）、netWorldState、
    /// FarmHouse 位置注册（player.getChildren → Utility.getHomeOfFarmer 取址）、
    /// Farmer 深度 Net 补齐。instanceGameLocation 保持 null（天气判定 null 短路）。
    /// </summary>
    private void InstallGameShim()
    {
        if (Game1InstanceField?.GetValue(null) == null)
            Game1InstanceField?.SetValue(null, FormatterServices.GetUninitializedObject(typeof(Game1)));

        if (NetWorldStateField?.GetValue(null) == null)
            NetWorldStateField.SetValue(null,
                Activator.CreateInstance(NetWorldStateField.FieldType, new NetWorldState()));

        var farmer = Game1.player;
        InitializeNetFields(farmer);
        FindField(typeof(Farmer), "homeLocation")?.SetValue(farmer, new NetString("FarmHouse"));

        if (LocationLookupField?.GetValue(null) == null)
        {
            var lookup = new Dictionary<string, GameLocation>(StringComparer.OrdinalIgnoreCase);
            var farmHouse = (FarmHouse)FormatterServices.GetUninitializedObject(typeof(FarmHouse));
            InitializeNetFields(farmHouse);
            lookup["FarmHouse"] = farmHouse;
            LocationLookupField?.SetValue(null, lookup);
        }
        else
        {
            var existing = (Dictionary<string, GameLocation>)LocationLookupField.GetValue(null);
            if (existing != null && !existing.ContainsKey("FarmHouse"))
            {
                var farmHouse = (FarmHouse)FormatterServices.GetUninitializedObject(typeof(FarmHouse));
                InitializeNetFields(farmHouse);
                existing["FarmHouse"] = farmHouse;
            }
        }
    }

    private static void InvokeTryInjectLifeEvent()
    {
        typeof(DailyHeadlinedGenerator)
            .GetMethod("TryInjectLifeEvent", BindingFlags.Static | BindingFlags.NonPublic)
            ?.Invoke(null, Array.Empty<object>());
    }

    private static void SeedPromptCache()
    {
        // 模板取自 ContentPack/i18n/zh.json，确定性（无随机分支）；
        // 钉住刷新条件字段后，Cache 直接返回该字典而不再触碰 Game1.content。
        var cache = new Dictionary<string, string>
        {
            { "spousesMarriedToMany", "农夫同时与 {{nSpouses}} 个人结婚: {{spouseList}}。" },
            { "spousesMarriedToOne", "农夫已与 {{spouseList}} 结婚。" },
            { "spousesMarriedToOthers", "农夫还与其他配偶同住: {{otherSpousesList}}（{{otherSpousesReference}}）。" },
            { "spousesNOtherPeople", "{{nSpouses}} 个人:" },
            { "spousesAllTheOthers", "所有其他配偶" },
            { "spousePoly", "你知道并接受农夫的多人恋爱关系。" },
            { "spousePolyView", "你自愿参与到与农夫及其他伴侣的多人恋爱关系中。" },
            { "spouseEngaged", "农夫已与 {{engagedTo}} 订婚（婚礼将在 {{weddingDays}} 天后举办）。" },
            { "spouseRoommateWithOne", "农夫与 {{roommateList}} 是室友关系。" },
            { "spouseRoommateWithMany", "农夫与 {{roommateList}} 是室友关系。" },
            { "spouseRoommatesWithOthers", "农夫还与室友 {{roommateList}} 同住（{{roommateReference}}）。" },
            { "spouseRoommatesAllTheOthers", "所有其他室友" },
        };
        var promptCache = PromptCache.Instance;
        typeof(PromptCache).GetField("_promptCache", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.SetValue(promptCache, cache);
        typeof(PromptCache).GetField("_promptLocaleCache", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.SetValue(promptCache, ModEntry.Language);
        // Gender 枚举位于 StardewValley.GameData.dll（测试工程不引用），反射取值避免
        // 编译期引用（装箱触发 CS0012）。
        var farmerForGender = Game1.getPlayerOrEventFarmer();
        object gender = farmerForGender?.GetType().GetProperty("Gender")?.GetValue(farmerForGender);
        typeof(PromptCache).GetField("_promptGenderCache", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.SetValue(promptCache, gender);
    }

    private static Character MakeCharacter(string name)
    {
        var c = (Character)FormatterServices.GetUninitializedObject(typeof(Character));
        typeof(Character).GetField("<Name>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.SetValue(c, name);
        // 直接注入有效 Bio：无头环境 CheckBio 经 Game1.content 必产出 Missing，
        // 注入后 Util.GetString 的 PromptOverrides / IsMale 读取确定性安全。
        // 注意：不设置 BioData.Name——其 setter 会经 Game1.getCharacterFromName 遍历地图。
        NpcBioDataField?.SetValue(c, new BioData { Biography = "Alex 是星露谷镇的运动员，热爱健身与早餐。" });
        return c;
    }

    private static object CreateCharacterData(string displayName)
    {
        var data = Activator.CreateInstance(CharacterDataField.FieldType.GetGenericArguments()[1]);
        // CharacterData（StardewValley.GameData.dll）的 DisplayName 是公共字段而非属性
        data.GetType().GetField("DisplayName", BindingFlags.Public | BindingFlags.Instance)
            ?.SetValue(data, displayName);
        return data;
    }

    private static IDictionary CreateEmptyStringKeyedDictionary(Type interfaceType)
    {
        var elementType = interfaceType.GetGenericArguments()[1];
        var dictType = typeof(Dictionary<,>).MakeGenericType(typeof(string), elementType);
        return (IDictionary)Activator.CreateInstance(dictType);
    }

    private static void InitializeNetFields(object target)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        for (Type t = target.GetType(); t != null && t != typeof(object); t = t.BaseType)
        {
            foreach (var field in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (!visited.Add(field.Name)) continue;
                if (field.GetValue(target) != null) continue;
                try
                {
                    if (field.FieldType == typeof(NetInt)) field.SetValue(target, new NetInt(0));
                    else if (field.FieldType == typeof(NetBool)) field.SetValue(target, new NetBool(false));
                    else if (field.FieldType == typeof(NetLong)) field.SetValue(target, new NetLong(0L));
                    else if (field.FieldType == typeof(NetFloat)) field.SetValue(target, new NetFloat(0f));
                    else if (field.FieldType == typeof(NetDouble)) field.SetValue(target, new NetDouble(0d));
                    else if (field.FieldType == typeof(NetString)) field.SetValue(target, new NetString(string.Empty));
                    else if (IsNetStateField(field.FieldType) && !field.FieldType.IsAbstract)
                    {
                        var ctor = field.FieldType.GetConstructors(BindingFlags.Instance | BindingFlags.Public)
                            .Where(c => c.GetParameters().Length <= 1)
                            .OrderBy(c => c.GetParameters().Length)
                            .FirstOrDefault();
                        if (ctor == null) continue;
                        var args = ctor.GetParameters()
                            .Select(p => p.HasDefaultValue
                                ? p.DefaultValue
                                : (p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null))
                            .ToArray();
                        field.SetValue(target, ctor.Invoke(args));
                    }
                }
                catch (Exception)
                {
                    // 测试用桩：无法初始化的字段保持原样，调用方若真的触碰会以显式异常暴露。
                }
            }
        }
    }

    private static bool IsNetStateField(Type type)
    {
        return type.GetInterfaces().Any(i =>
            i.IsGenericType && i.GetGenericTypeDefinition().FullName == "Netcode.INetObject`1");
    }

    private static FieldInfo FindField(Type type, string name)
    {
        for (var t = type; t != null && t != typeof(object); t = t.BaseType)
        {
            var f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (f != null) return f;
        }
        return null;
    }

    private sealed class ActionScope : IDisposable
    {
        private readonly Action _onDispose;
        private bool _disposed;

        public ActionScope(Action onDispose) => _onDispose = onDispose;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _onDispose();
        }
    }

    /// <summary>接口事件/属性的静默桩：事件订阅为无操作，接口属性返回嵌套静默桩。</summary>
    private class NoopInterfaceProxy : DispatchProxy
    {
        protected override object Invoke(MethodInfo targetMethod, object[] args)
        {
            if (targetMethod.IsSpecialName && targetMethod.Name.StartsWith("get_", StringComparison.Ordinal)
                && targetMethod.ReturnType.IsInterface)
            {
                return CreateNoop(targetMethod.ReturnType);
            }

            return null;
        }

        public static object CreateNoop(Type interfaceType)
        {
            var create = typeof(DispatchProxy)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .First(m => m.IsGenericMethodDefinition
                            && m.GetGenericArguments().Length == 2
                            && m.GetParameters().Length == 0)
                .MakeGenericMethod(interfaceType, typeof(NoopInterfaceProxy));
            return create.Invoke(null, Array.Empty<object>());
        }
    }

    /// <summary>
    /// 全事件静默的 IModHelper：Events 整棵树静默（PerceptionManager 静态构造订阅
    /// GameLoop 而不触雷），Translation 走 FakeTranslationHelper。
    /// </summary>
    private sealed class QuietModHelper : IModHelper
    {
        private readonly ITranslationHelper _translation = new FakeTranslationHelper("en");
        private readonly IModEvents _events =
            (IModEvents)NoopInterfaceProxy.CreateNoop(typeof(IModEvents));

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
}
