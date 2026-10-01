// GiftPipelineHistoryTests.cs
// ═══════════════════════════════════════════════════════════════════════════
// VT-GIFT-HISTORY-01：送礼/物品交付生成后 NPC 回复回填 ChatHistory 的因果历史。
//
// 本类真实驱动 DialogueBuilder.GenerateGift / GenerateHandover / GenerateResponse
// 全流程（含 LlmDialogueService 调用、StardewValley.Dialogue 构造与返回）：
//
//   1) LLM Provider 以确定桩（StubReplyLlm）替换——Llm.Instance 为 internal 静态
//      属性（IVT 已授权），经非公共 setter 安装/还原。默认 LlmDummy 输出随机，
//      不可断言；确定桩保证无网络、无随机分支。
//   2) StardewValley.Dialogue 构造：Game1.content == null 时静态翻译表
//      TranslateArraysOfStrings 会 NRE（见 AiStreamingDialogueBoxTests 头注）——
//      本类置 nameArraysTranslated=true 跳过翻译表初始化，纯文本台词可完整构造，
//      GenerateGift / GenerateHandover 得以跑通全程并返回真实 Dialogue。
//   3) 引擎垫片沿用并扩展既有模式：
//      - FakePlayer：Game1.player（Farmer + 标量 Net 字段补齐），另补
//        teamRoot 的 FarmerTeam 值（isMarriedOrRoommates → team.IsMarried 路径）
//        与 NetStringDictionary/NetCollection 类字段（friendshipData 等，
//        直承 AbstractNetSerializable，不经过 NetFieldBase）；
//      - 未初始化 NPC + 逐层 Net 字段补齐 + name/_displayName/dialogue 预置；
//      - FarmHouse 注册进 Game1._locationLookup（farmer.getChildren 经
//        Utility.getHomeOfFarmer → RequireLocation 的取址路径），game1 实例
//        垫片保持 instanceGameLocation == null（天气判定走 null 短路）；
//      - characterData 注册 Alex 的 CharacterData（1.6.15 起位于独立程序集
//        StardewValley.GameData.dll，测试工程未引用，全程反射构造；GetData
//        返回 null 时 Prompts 读 npcData.Gender 必炸）+ 空 NPCGiftTastes；
//      - netWorldState NetRoot（Game1.Date 读取路径）；
//      - 直接注入 Character._bioData（Game1.content 缺失时 CheckBio 只能产出
//        Missing Bio，必须经 IVT 反射注入有效 Biography 才能过 HasValidBio 门禁）；
//      - SMAPI Context 静态构造依赖 smapi-internal 卫星程序集，挂
//        AssemblyLoadContext.Resolving 钩子回退加载。
//   4) 断言设计：LlmDialogueService 的 Prompts 装配段自带 try/catch 兜底
//      （无头环境下微环境/内容资产触雷时确定性地降级为 "..."，不逃逸）。
//      因此 NPC 回复行只断言结构契约——IsPlayerLine == false 且文本非空白
//      （成功路径为确定桩台词，降级路径为 "..."，两者皆满足）；条目数量、
//      说话方标志、顺序与去重等回填核心语义则逐项精确断言。
// ═══════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Runtime.Serialization;
using System.Threading.Tasks;
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
public class GiftPipelineHistoryTests : IDisposable
{
    private const string NpcName = "Alex";
    private const string CannedNpcReply = "谢谢你送我的柠檬，朋友。";
    private const string FarmerFollowUp = "嘿，亚历克斯。";

    private static readonly FieldInfo NetWorldStateField =
        typeof(Game1).GetField("netWorldState", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

    private static readonly FieldInfo Game1InstanceField =
        typeof(Game1).GetField("game1", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

    private static readonly FieldInfo CharacterDataField =
        typeof(Game1).GetField("characterData", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

    private static readonly FieldInfo NpcGiftTastesField =
        typeof(Game1).GetField("NPCGiftTastes", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

    private static readonly FieldInfo LocationLookupField =
        typeof(Game1).GetField("_locationLookup", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

    private static readonly FieldInfo NameArraysTranslatedField =
        typeof(StardewValley.Dialogue).GetField("nameArraysTranslated", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

    private static readonly FieldInfo NpcBioDataField =
        typeof(Character).GetField("_bioData", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    /// <summary>
    /// SMAPI 的 Context 静态构造依赖 smapi-internal 下的 SMAPI.Toolkit 等卫星程序集，
    /// MonoGame.Framework 亦是 LocalizedContentManager 垫片的运行时依赖；默认探测路径
    /// （测试 bin）都没有——挂 Resolving 钩子按名回退到游戏目录加载。
    /// </summary>
    static GiftPipelineHistoryTests()
    {
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

    private readonly IDisposable _playerScope;
    private readonly object _previousGame1;
    private readonly object _previousNetWorldState;
    private readonly object _previousCharacterData;
    private readonly object _previousNpcGiftTastes;
    private readonly object _previousLocationLookup;
    private readonly object _previousNameArraysTranslated;
    private readonly Llm _previousLlm;
    private readonly EngineScope _scope;

    public GiftPipelineHistoryTests()
    {
        _scope = new EngineScope();
        _scope.UseIsolatedEngineDefaults();
        _scope.ResetNpcState(NpcName);

        _playerScope = FakePlayer.Install("虎彦");

        _previousNetWorldState = NetWorldStateField?.GetValue(null);
        _previousGame1 = Game1InstanceField?.GetValue(null);
        _previousCharacterData = CharacterDataField?.GetValue(null);
        _previousNpcGiftTastes = NpcGiftTastesField?.GetValue(null);
        _previousLocationLookup = LocationLookupField?.GetValue(null);
        _previousNameArraysTranslated = NameArraysTranslatedField?.GetValue(null);

        InstallHeadlessWorld();
        InstallValidBio();

        _previousLlm = LlmSession.Install(new StubReplyLlm(CannedNpcReply));
    }

    public void Dispose()
    {
        LlmSession.Install(_previousLlm);

        NetWorldStateField?.SetValue(null, _previousNetWorldState);
        Game1InstanceField?.SetValue(null, _previousGame1);
        CharacterDataField?.SetValue(null, _previousCharacterData);
        NpcGiftTastesField?.SetValue(null, _previousNpcGiftTastes);
        LocationLookupField?.SetValue(null, _previousLocationLookup);
        NameArraysTranslatedField?.SetValue(null, _previousNameArraysTranslated);

        _playerScope?.Dispose();
        _scope.ResetNpcState(NpcName);
        _scope.Dispose();
    }

    [Fact]
    public async Task GenerateGift_AppendsNpcReply_AfterPlayerGiftSeedLine()
    {
        var builder = DialogueBuilder.Instance;
        var npc = CreateHeadlessNpc(NpcName);

        var dialogue = await builder.GenerateGift(npc, new StubGift(), 0, onStreamingToken: null);

        Assert.NotNull(dialogue);

        var context = builder.GetContext(NpcName);
        Assert.NotNull(context);
        Assert.Equal(2, context.ChatHistory.Count);

        ConversationElement seed = context.ChatHistory[0];
        Assert.True(seed.IsPlayerLine);
        Assert.Contains("柠檬", seed.Text);
        Assert.Equal("Just now", seed.FuzzyTime);

        ConversationElement reply = context.ChatHistory[1];
        Assert.False(reply.IsPlayerLine);
        Assert.False(string.IsNullOrWhiteSpace(reply.Text));
        Assert.Equal("Just now", reply.FuzzyTime);
    }

    [Fact]
    public async Task GenerateResponse_AfterGift_MergesIntoThreeTurnSequence()
    {
        var builder = DialogueBuilder.Instance;
        var npc = CreateHeadlessNpc(NpcName);

        // 第一轮：送礼生成（种入 [农夫送礼] + [NPC 回礼] 两轮因果历史）
        await builder.GenerateGift(npc, new StubGift(), 0, onStreamingToken: null);
        var context = builder.GetContext(NpcName);
        Assert.Equal(2, context.ChatHistory.Count);

        // 第二轮：续聊输入——UI 以会话超集形态携带 [送礼, 回礼] 历史并追加新发言
        var conversation = new List<ConversationElement>(context.ChatHistory)
        {
            new ConversationElement(FarmerFollowUp, true)
        };
        string followUp = await builder.GenerateResponse(npc, conversation, onStreamingToken: null);

        Assert.NotNull(followUp);

        var merged = builder.GetContext(NpcName).ChatHistory;
        Assert.Equal(3, merged.Count);

        Assert.True(merged[0].IsPlayerLine);
        Assert.Contains("柠檬", merged[0].Text);

        Assert.False(merged[1].IsPlayerLine);
        Assert.False(string.IsNullOrWhiteSpace(merged[1].Text));

        Assert.True(merged[2].IsPlayerLine);
        Assert.Equal(FarmerFollowUp, merged[2].Text);

        // 无重复项：三轮 (方向, 文本) 两两互异
        Assert.Equal(3, merged.Select(e => (e.IsPlayerLine, e.Text)).Distinct().Count());
    }

    [Fact]
    public async Task GenerateHandover_AppendsNpcReply_AfterHandoverSeedLine()
    {
        var builder = DialogueBuilder.Instance;
        var npc = CreateHeadlessNpc(NpcName);

        var dialogue = await builder.GenerateHandover(
            npc, HandoverVerdict.Passthrough_Vanilla, new StubGift(), onStreamingToken: null);

        Assert.NotNull(dialogue);

        var context = builder.GetContext(NpcName);
        Assert.NotNull(context);
        Assert.Equal(2, context.ChatHistory.Count);

        ConversationElement seed = context.ChatHistory[0];
        Assert.True(seed.IsPlayerLine);
        Assert.Contains("柠檬", seed.Text);
        Assert.Equal("Just now", seed.FuzzyTime);

        ConversationElement reply = context.ChatHistory[1];
        Assert.False(reply.IsPlayerLine);
        Assert.False(string.IsNullOrWhiteSpace(reply.Text));
        Assert.Equal("Just now", reply.FuzzyTime);
    }

    // ── 无头世界垫片 ──

    /// <summary>
    /// 安装本类所需的 Game1 静态状态：netWorldState（Game1.Date 读取）、
    /// 空 characterData / NPCGiftTastes（GetData 与送礼偏好查询不触雷）、
    /// FarmHouse 位置（farmer.getChildren 取址路径）、Dialogue 静态翻译表开关。
    /// </summary>
    private static void InstallHeadlessWorld()
    {
        if (NetWorldStateField != null && NetWorldStateField.GetValue(null) == null)
        {
            NetWorldStateField.SetValue(
                null,
                Activator.CreateInstance(NetWorldStateField.FieldType, new NetWorldState()));
        }

        // 字段类型为 IDictionary<> 接口，必须以具体 Dictionary 实例填充。
        // 1.6.15 起 CharacterData 位于独立程序集 StardewValley.GameData.dll（测试工程
        // 未直接引用），故全程反射构造 Alex 条目：Prompts.InitializeInstanceFields 会读
        // npcData.Gender（GetData 返回 null 必炸）；BirthDay 置 -1 避免误入生日分支。
        var characterData = CreateEmptyStringKeyedDictionary(CharacterDataField.FieldType);
        var alexData = Activator.CreateInstance(characterData.GetType().GetGenericArguments()[1]);
        alexData.GetType().GetProperty("Gender")?.SetValue(alexData, 0); // Gender.Male
        alexData.GetType().GetProperty("BirthDay")?.SetValue(alexData, -1);
        characterData[NpcName] = alexData;
        CharacterDataField?.SetValue(null, characterData);
        NpcGiftTastesField?.SetValue(null, new Dictionary<string, string>());

        var locationLookup = new Dictionary<string, GameLocation>(StringComparer.OrdinalIgnoreCase);
        var farmHouse = (FarmHouse)FormatterServices.GetUninitializedObject(typeof(FarmHouse));
        InitializeNetFields(farmHouse);
        locationLookup["FarmHouse"] = farmHouse;
        LocationLookupField?.SetValue(null, locationLookup);

        // Game1.currentLocation → Game1.game1.instanceGameLocation（实例承载）：
        // game1 静态实例缺位时 currentLocation 读取即 NRE。垫一个未初始化 Game1，
        // instanceGameLocation 保持 null——currentLocation 返回 null（天气判定走
        // location == null 短路），farmer.getChildren 经 _locationLookup 直接命中 FarmHouse。
        var game1 = (Game1)FormatterServices.GetUninitializedObject(typeof(Game1));
        Game1InstanceField?.SetValue(null, game1);

        var farmer = Game1.player;
        FindField(typeof(Farmer), "homeLocation")?.SetValue(farmer, new NetString("FarmHouse"));
        // FakePlayer 的通用补齐只覆盖 NetFieldBase 系标量字段；farmer 上的
        // NetStringDictionary/NetCollection 类（friendshipData 等）不承 NetFieldBase，
        // 用本类扩展的 Net 补齐再过一遍（已初始化字段自动跳过）。
        InitializeNetFields(farmer);
        // FakePlayer 的通用 Net 补齐会给 teamRoot 一个无值 NetRoot；isMarriedOrRoommates
        // 经 team.IsMarried 读取 Value，必须补上真实 FarmerTeam 值。FarmerTeam 构造函数
        // 会加载玩家状态贴图（无头 NRE），同样以未初始化 + Net 补齐方式构造。
        var farmerTeam = (FarmerTeam)FormatterServices.GetUninitializedObject(typeof(FarmerTeam));
        InitializeNetFields(farmerTeam);
        FindField(typeof(Farmer), "teamRoot")?.SetValue(farmer, new NetRoot<FarmerTeam>(farmerTeam));

        // 置位后 Dialogue 构造跳过 TranslateArraysOfStrings（Game1.content 依赖），
        // 纯文本台词可完整解析；Dispose 恢复原值。
        NameArraysTranslatedField?.SetValue(null, true);
    }

    /// <summary>
    /// 为 NPC 注入有效 Bio：无头环境 Game1.content == null，CheckBio 只能产出
    /// Missing 标记，HasValidBio 门禁将拒绝生成；反射注入 _bioData 后门禁放行。
    /// </summary>
    private void InstallValidBio()
    {
        var npc = CreateHeadlessNpc(NpcName);
        var character = DialogueBuilder.Instance.GetCharacter(npc);
        // 注意：不设置 BioData.Name——其 setter 会经 Game1.getCharacterFromName 遍历地图
        // 位置解析性别，无头环境必炸；Name 非门禁字段，注入与否不影响 HasValidBio。
        NpcBioDataField?.SetValue(character, new BioData
        {
            Biography = "Alex 是星露谷镇的运动员，热爱健身与早餐，性格开朗直率，重视家人与朋友。"
        });
        character.ValidPortraits = new List<string> { "h", "s", "l", "a" };
    }

    private static System.Collections.IDictionary CreateEmptyStringKeyedDictionary(Type interfaceType)
    {
        var elementType = interfaceType.GetGenericArguments()[1];
        var dictType = typeof(Dictionary<,>).MakeGenericType(typeof(string), elementType);
        return (System.Collections.IDictionary)Activator.CreateInstance(dictType);
    }

    private static NPC CreateHeadlessNpc(string name)
    {
        var npc = (NPC)FormatterServices.GetUninitializedObject(typeof(NPC));
        InitializeNetFields(npc);
        FindField(typeof(NPC), "name")?.SetValue(npc, new NetString(name));
        // displayName 属性的兜底 translateName() 依赖内容资产；直接置后备字段跳过。
        FindField(typeof(Character), "_displayName")?.SetValue(npc, name);
        // NPC.Dialogue getter 对 null dialogue 字段会走 Game1.content 懒加载（无头 NRE）；
        // 预置空字典使其直接返回。
        FindField(typeof(NPC), "dialogue")?.SetValue(npc, new Dictionary<string, string>());
        return npc;
    }

    /// <summary>
    /// 为未初始化对象补齐标量/可构造 Net 字段（FakePlayer.InitializeScalarNetFields
    /// 的通用化版本）：所有 GetUninitializedObject 的引擎对象都需要，否则读取
    /// Net 属性（如 preserve/orderData/characters）会 NRE。
    /// 逐层遍历类型层次——继承的 protected Net 字段（如 Character.currentLocationRef）
    /// 不会出现在派生类型的 GetFields 结果里。
    /// </summary>
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

    /// <summary>
    /// 是否为网络状态容器字段。统一以 INetObject&lt;T&gt; 接口判定：
    /// NetCollection/NetDictionary 直承 AbstractNetSerializable，NetLocationRef
    /// 直接实现 INetObject，二者都不经过 NetFieldBase，单一基类检查会漏。
    /// </summary>
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

    /// <summary>
    /// 送礼桩：跳过 DisplayName 的数据表加载路径（loadDisplayName 依赖 ItemRegistry
    /// 内容数据，无头环境不可用），直接返回确定物品名。Name 保留基类行为（空串），
    /// 仅影响对话 key，不参与断言。
    /// </summary>
    private sealed class StubGift : StardewValley.Object
    {
        public override string DisplayName => "柠檬";
    }

    /// <summary>
    /// 确定性 LLM 桩：仅实现主对话非流式入口，返回固定台词。
    /// ProcessLines 会剥掉 "- " 前缀，落库文本即 _reply 本身。
    /// </summary>
    internal sealed class StubReplyLlm : Llm
    {
        private readonly string _reply;

        public StubReplyLlm(string reply)
        {
            _reply = reply;
        }

        public override bool IsHighlySensoredModel => false;

        public override string ExtraInstructions => string.Empty;

        internal override Task<LlmResponse> RunInference(
            string systemPromptString,
            string gameCacheString,
            string npcCacheString,
            string promptString,
            string responseStart = "",
            int n_predict = 2048,
            string cacheContext = "",
            bool allowRetry = true)
        {
            return Task.FromResult(new LlmResponse("- " + _reply));
        }

        internal override Dictionary<string, double>[] RunInferenceProbabilities(string fullPrompt, int n_predict = 1)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>Llm.Instance（internal 静态属性，IVT 授权）的安装/还原助手。</summary>
    private static class LlmSession
    {
        private static readonly PropertyInfo InstanceProperty =
            typeof(Llm).GetProperty("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

        public static Llm Install(Llm stub)
        {
            var previous = (Llm)InstanceProperty.GetValue(null);
            InstanceProperty.GetSetMethod(nonPublic: true).Invoke(null, new object[] { stub });
            return previous;
        }
    }

    /// <summary>SMAPI 侧状态隔离（Monitor/Config/SHelper + locale），复用 TestEnv 既有机制。</summary>
    private sealed class EngineScope : IDisposable
    {
        private readonly IDisposable _localeScope = TestEnv.UseIsolatedLocale("en");

        public void UseIsolatedEngineDefaults()
        {
            // 断言不受情绪系统日初始化副作用干扰；调试日志关闭保持输出干净。
            ModEntry.Config.Debug = false;
            ModEntry.Config.EnableEmotionSystem = false;

            // FakeModHelper.Events.GameLoop 等成员抛 NotImplementedException，而
            // DialogueHistoryManager 的静态构造会订阅 GameLoop 事件——换用全静默事件桩。
            TestEnv.SetSHelper(new QuietModHelper());
        }

        public void ResetNpcState(string npcName)
        {
            DialogueBuilder.Instance.ClearContext(npcName);
            SessionCache.Instance.Reset(npcName);
        }

        public void Dispose()
        {
            _localeScope.Dispose();
        }
    }

    /// <summary>接口事件/属性的静默桩：事件订阅（add/remove）为无操作，接口属性返回嵌套静默桩。</summary>
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
    /// 全事件静默的 IModHelper：Translation 走既有 FakeTranslationHelper（i18n 回退安全），
    /// Events 整棵事件树静默，供 DialogueHistoryManager 等静态构造订阅而不触雷。
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
