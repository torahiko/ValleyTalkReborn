// FarmStateScannerFruitTreeTests.cs
// VT-FARMSTATE-FRUITTREE-01: 果园状态品种解析的单测锚点。
//
// 无头事实（同 AiStreamingDialogueBoxTests 头注，全部实测验证）：
//   1) LocalizedContentManager.CurrentLanguageCode 的公开 setter 会写 Game1.log 并触发
//      OnLanguageChange；测试直接改私有静态字段 _currentLangCode（默认 GetDefaultLanguageCode()
//      恒返回 en），finally 恢复。程序集级 DisableTestParallelization 已保证串行。
//   2) StardewValley.GameData.dll 测试工程不引用（CharacterData 先例）：ObjectData 数据模型
//      反射装配，路径相对 AppDomain.BaseDirectory 上溯四层到游戏根目录。
//   3) ItemRegistry 类型定义表由内部 RegisterItemTypes() 注册，测试通过 "(O)" 探测保证幂等；
//      Game1.objectData 是公共静态可写字典，直接注入含单个 ObjectData 条目的字典。
//   4) 1.6 数据模型（ObjectData 等）成员是公共字段而非属性（VT-NPC-NAME-LOCALIZE-01 教训），
//      GetProperty 会静默返回 null，必须 GetField。
//   5) TokenParser.RegisterParser 是公开 API 但无法注销：测试令牌用唯一键 + 有状态委托
//      （首次调用故意失败让原版 ObjectDataDefinition.GetData 保留原始令牌串，第二次调用在
//      SafeGetDisplayName 的 TokenParser 增强分支中解析出果名），测试结束后该键残留但无副作用。

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using StardewValley;
using StardewValley.TokenizableStrings;
using Xunit;

namespace ValleytalkReborn.Tests;

public class FarmStateScannerFruitTreeTests
{
    private static object InvokePrivate(string method, params object[] args)
    {
        MethodInfo m = typeof(FarmStateScanner).GetMethod(method,
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(m);
        return m.Invoke(null, args);
    }

    /// <summary>临时改写 LocalizedContentManager 私有静态语言字段，finally 恢复。</summary>
    private static void WithLanguage(string code, Action action)
    {
        FieldInfo field = typeof(LocalizedContentManager).GetField("_currentLangCode",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);

        object original = field.GetValue(null);
        try
        {
            field.SetValue(null, Enum.Parse(field.FieldType, code));
            action();
        }
        finally
        {
            field.SetValue(null, original);
        }
    }

    /// <summary>反射调用 TryGetVanillaFruitName 并解开 out 参数。</summary>
    private static (bool matched, string name) TryVanillaFruit(string id)
    {
        object[] args = { id, null };
        bool matched = (bool)InvokePrivate("TryGetVanillaFruitName", args);
        return (matched, (string)args[1]);
    }

    [Fact]
    public void ExtractFruitNameFromTreeName_ChineseAndEnglish_StripsSuffixCorrectly()
    {
        WithLanguage("zh", () =>
        {
            Assert.Equal("苹果", InvokePrivate("ExtractFruitNameFromTreeName", "苹果树"));
            Assert.Equal("石榴", InvokePrivate("ExtractFruitNameFromTreeName", "石榴树苗"));
        });

        WithLanguage("en", () =>
        {
            Assert.Equal("Apple", InvokePrivate("ExtractFruitNameFromTreeName", "Apple Tree"));
            Assert.Equal("Pomegranate", InvokePrivate("ExtractFruitNameFromTreeName", "Pomegranate Sapling"));
        });
    }

    [Fact]
    public void TryGetVanillaFruitName_ResolvesStandardSaplingsAndFruits()
    {
        WithLanguage("zh", () =>
        {
            Assert.Equal(("苹果", true), (TryVanillaFruit("633").name, TryVanillaFruit("633").matched));
            Assert.Equal("樱桃", TryVanillaFruit("613").name);
            Assert.Equal("石榴", TryVanillaFruit("632").name);
            Assert.Equal("石榴", TryVanillaFruit("637").name);
            Assert.Equal("香蕉", TryVanillaFruit("69").name);
            Assert.Equal("芒果", TryVanillaFruit("835").name);
        });

        WithLanguage("en", () =>
        {
            Assert.Equal(("Apple", true), (TryVanillaFruit("633").name, TryVanillaFruit("633").matched));
            Assert.Equal("Cherry", TryVanillaFruit("613").name);
            Assert.Equal("Pomegranate", TryVanillaFruit("632").name);
            Assert.Equal("Pomegranate", TryVanillaFruit("637").name);
            Assert.Equal("Banana", TryVanillaFruit("69").name);
            Assert.Equal("Mango", TryVanillaFruit("835").name);
        });

        // 未知 modded ID 不命中常数映射，交由上层优雅省略品种括号
        Assert.False(TryVanillaFruit("999999").matched);
    }

    [Fact]
    public void SafeGetDisplayName_WithTokenizedString_ParsesLocalizedText()
    {
        const string tokenKey = "VT_FARMSTATE_TestFruit";
        bool firstCall = true;
        TokenParser.RegisterParser(tokenKey, (string[] query, out string replacement, Random random, Farmer player) =>
        {
            if (firstCall)
            {
                firstCall = false;
                replacement = null;
                return false; // 原版 ObjectDataDefinition.GetData 阶段不识别，令牌串原样保留
            }
            replacement = "石榴";
            return true; // SafeGetDisplayName 的 TokenParser 增强分支解析出果名
        });

        // StardewValley.GameData.dll 测试工程未引用：反射装配 ObjectData 并构造字典
        string gameDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
            "..", "..", "..", "..", "Stardew Valley");
        Assembly gameData = Assembly.LoadFrom(Path.Combine(gameDir, "StardewValley.GameData.dll"));
        Type objectDataType = gameData.GetType("StardewValley.GameData.Objects.ObjectData");
        Assert.NotNull(objectDataType);

        object entry = Activator.CreateInstance(objectDataType);
        objectDataType.GetField("DisplayName").SetValue(entry, "[" + tokenKey + "]");

        IDictionary seeded = (IDictionary)Activator.CreateInstance(
            typeof(Dictionary<,>).MakeGenericType(typeof(string), objectDataType));
        seeded["638"] = entry;

        FieldInfo objectDataField = typeof(Game1).GetField("objectData");
        Assert.NotNull(objectDataField);
        object originalData = objectDataField.GetValue(null);

        try
        {
            EnsureItemTypesRegistered();
            objectDataField.SetValue(null, seeded);

            string result = (string)InvokePrivate("SafeGetDisplayName", "638");
            Assert.Equal("石榴", result);
        }
        finally
        {
            objectDataField.SetValue(null, originalData);
            CleanupItemRegistry();
        }
    }

    /// <summary>
    /// 恢复 ItemRegistry 进程级静态状态：本测试注册的 "(O)" 定义与其缓存若残留，
    /// 会改变后续测试中未注册 ID 的解析结果（实测 TownIncident 谣言组 因此翻车）。
    /// </summary>
    private static void CleanupItemRegistry()
    {
        FieldInfo itemTypes = typeof(ItemRegistry).GetField("ItemTypes",
            BindingFlags.NonPublic | BindingFlags.Static);
        FieldInfo lookup = typeof(ItemRegistry).GetField("IdentifierLookup",
            BindingFlags.NonPublic | BindingFlags.Static);
        if (itemTypes?.GetValue(null) is IList types && types.Count > 0)
        {
            types.Clear();
        }
        if (lookup?.GetValue(null) is IDictionary ids && ids.Count > 0)
        {
            ids.Clear();
        }
        ItemRegistry.ResetCache();
    }

    /// <summary>
    /// 幂等注册物品类型表。只注册 ObjectDataDefinition：原版 RegisterItemTypes 还包含
    /// 壁纸/地板等依赖 Game1.content 的定义，无头必炸；RebuildCache 只遍历已注册类型，
    /// 因此注册单个 (O) 定义即可让 ItemRegistry.GetData("638") 走通 objectData 数据源。
    /// </summary>
    private static void EnsureItemTypesRegistered()
    {
        if (ItemRegistry.GetTypeDefinition("(O)") != null)
        {
            return;
        }

        ItemRegistry.AddTypeDefinition(new StardewValley.ItemTypeDefinitions.ObjectDataDefinition());
    }

    private class MockTestFruitItem : StardewValley.Object
    {
        private readonly string _displayName;

        public MockTestFruitItem(string name, string displayName)
        {
            this.Name = name;
            _displayName = displayName;
        }

        public override string DisplayName => _displayName;
    }

    [Theory]
    [InlineData("Error Item", true)]
    [InlineData("Error Item (Cornucopia_Yuzu)", true)]
    [InlineData("错误物品", true)]
    [InlineData("错误物品 (Cornucopia_Yuzu)", true)]
    [InlineData("错误物品 (DomTSVG.PlentifulHarvestCP_Frostfruit)", true)]
    [InlineData("未命名的物品", true)]
    [InlineData("Unnamed Item", true)]
    [InlineData("???", true)]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData(null, true)]
    [InlineData("苹果", false)]
    [InlineData("Apple", false)]
    [InlineData("香蕉", false)]
    [InlineData("Yuzu", false)]
    public void IsErrorDisplayName_DetectsChineseAndEnglishErrors(string displayName, bool expected)
    {
        Assert.Equal(expected, FarmStateScanner.IsErrorDisplayName(displayName));
    }

    [Fact]
    public void IsErrorItem_ValidatesNullAndErrorObjects()
    {
        Assert.True(FarmStateScanner.IsErrorItem(null));

        var errorObj = new StardewValley.Object { Name = "ErrorItem" };
        Assert.True(FarmStateScanner.IsErrorItem(errorObj));

        var mockErrorFruit = new MockTestFruitItem("Cornucopia_Yuzu", "错误物品 (Cornucopia_Yuzu)");
        Assert.True(FarmStateScanner.IsErrorItem(mockErrorFruit));

        var normalFruit = new MockTestFruitItem("Apple", "苹果");
        Assert.False(FarmStateScanner.IsErrorItem(normalFruit));
    }

    [Fact]
    public void ResolveFruitTreeName_WithCorruptedFruit_DoesNotReturnErrorString()
    {
        var tree = new StardewValley.TerrainFeatures.FruitTree();
        tree.fruit.Add(new MockTestFruitItem("Cornucopia_Yuzu", "错误物品 (Cornucopia_Yuzu)"));
        tree.fruit.Add(new MockTestFruitItem("DomTSVG.PlentifulHarvestCP_Frostfruit", "错误物品 (DomTSVG.PlentifulHarvestCP_Frostfruit)"));

        string result = (string)InvokePrivate("ResolveFruitTreeName", tree);
        Assert.Null(result);

        // 当挂果中混有正常果实实体时，能够正确跳过错误果实并提取正常果名
        tree.fruit.Add(new MockTestFruitItem("Apple", "苹果"));
        string mixedResult = (string)InvokePrivate("ResolveFruitTreeName", tree);
        Assert.Equal("苹果", mixedResult);
    }
}
