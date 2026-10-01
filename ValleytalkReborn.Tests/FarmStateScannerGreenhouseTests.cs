// FarmStateScannerGreenhouseTests.cs
// VT-FARM-ZONES-02: 温室分区格式化（BuildGreenhouseSection）的无头单测锚点。
//
// 本文件验证的是格式化纯函数的输出契约，不是游戏内观察或模型台词验收：
// BuildGreenhouseSection 只格式化传入事实，不读取 Game1、不写状态；
// 计数与品种列表均由测试直接构造。
//
// 无头事实（沿用 FarmStateScannerFruitTreeTests 头注惯例）：
//   LocalizedContentManager.CurrentLanguageCode 的公开 setter 会写 Game1.log 并触发
//   OnLanguageChange；测试直接改私有静态字段 _currentLangCode（默认 GetDefaultLanguageCode()
//   恒返回 en），finally 恢复。程序集级 DisableTestParallelization 已保证串行。

using System;
using System.Collections.Generic;
using System.Reflection;
using StardewValley;
using Xunit;

namespace ValleytalkReborn.Tests;

public class FarmStateScannerGreenhouseTests
{
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

    private static List<string> NoVarieties() => new();

    /// <summary>非零计数 + 非空品种：证明未解锁/地点不可用路径不得泄露内部事实。</summary>
    private static (int readyCount, int growingCount, int deadCount,
        List<string> topReady, List<string> topGrowing, List<string> topDead) SeededCrops() =>
        (3, 5, 2, new List<string> { "苹果", "葡萄" }, new List<string> { "草莓" }, new List<string> { "枯萎品种" });

    private static (int producingCount, int growingCount, int restingCount,
        List<string> topProducing, List<string> topGrowing, List<string> topResting) SeededFruitTrees() =>
        (2, 4, 1, new List<string> { "苹果", "桃子" }, new List<string> { "樱桃" }, new List<string> { "橙子" });

    // ── 1. 未解锁：只输出破损描述，非零统计与品种均不得泄露 ──

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Locked_NonZeroFacts_ProduceBrokenDescriptionOnly(bool isZh)
    {
        WithLanguage(isZh ? "zh" : "en", () =>
        {
            string section = FarmStateScanner.BuildGreenhouseSection(
                isZh, isUnlocked: false, isLocationAvailable: false,
                SeededCrops(), SeededFruitTrees());

            Assert.Contains(isZh ? "破损废弃" : "Dilapidated and abandoned", section);

            Assert.DoesNotContain(isZh ? "空置" : "currently contains no crops", section);
            Assert.DoesNotContain(isZh ? "已成熟" : "ripe and ready", section);
            Assert.DoesNotContain(isZh ? "果实累累" : "bearing ripe fruit", section);
            Assert.DoesNotContain(isZh ? "苹果" : "Apple", section);
            Assert.DoesNotContain(isZh ? "温室果树" : "Greenhouse Fruit Trees", section);
        });
    }

    // ── 2. 已解锁但地点不可用：非零统计仍不得泄露内部状态或品种，也不得声称空置 ──

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Unlocked_LocationUnavailable_NonZeroFactsDoNotLeak(bool isZh)
    {
        WithLanguage(isZh ? "zh" : "en", () =>
        {
            string section = FarmStateScanner.BuildGreenhouseSection(
                isZh, isUnlocked: true, isLocationAvailable: false,
                SeededCrops(), SeededFruitTrees());

            Assert.Contains(isZh ? "温室已修复，当前无法确认内部种植情况" : "Repaired; its planting state is currently unavailable", section);

            Assert.DoesNotContain(isZh ? "空置" : "currently contains no crops", section);
            Assert.DoesNotContain(isZh ? "已成熟" : "ripe and ready", section);
            Assert.DoesNotContain(isZh ? "果实累累" : "bearing ripe fruit", section);
            Assert.DoesNotContain(isZh ? "苹果" : "Apple", section);
            Assert.DoesNotContain(isZh ? "葡萄" : "Grape", section);
            Assert.DoesNotContain(isZh ? "温室果树" : "Greenhouse Fruit Trees", section);
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Unlocked_LocationUnavailable_ZeroStats_StillUnavailableNotVacant(bool isZh)
    {
        WithLanguage(isZh ? "zh" : "en", () =>
        {
            string section = FarmStateScanner.BuildGreenhouseSection(
                isZh, isUnlocked: true, isLocationAvailable: false,
                (0, 0, 0, NoVarieties(), NoVarieties(), NoVarieties()),
                (0, 0, 0, NoVarieties(), NoVarieties(), NoVarieties()));

            Assert.Contains(isZh ? "无法确认内部种植情况" : "currently unavailable", section);
            Assert.DoesNotContain(isZh ? "目前没有作物或果树" : "currently contains no crops", section);
        });
    }

    // ── 3. 地点可用且六类计数全 0：仅输出空置行，且不再出现旧"什么也没有"话术 ──

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Unlocked_Available_AllZero_EmitsVacantLineOnly(bool isZh)
    {
        WithLanguage(isZh ? "zh" : "en", () =>
        {
            string section = FarmStateScanner.BuildGreenhouseSection(
                isZh, isUnlocked: true, isLocationAvailable: true,
                (0, 0, 0, NoVarieties(), NoVarieties(), NoVarieties()),
                (0, 0, 0, NoVarieties(), NoVarieties(), NoVarieties()));

            Assert.Contains(isZh ? "温室已修复，目前没有作物或果树" : "Repaired; currently contains no crops or fruit trees", section);
            Assert.DoesNotContain(isZh ? "什么也没有" : "nothing planted", section);
            Assert.DoesNotContain(isZh ? "果实累累" : "bearing ripe fruit", section);
            Assert.DoesNotContain(isZh ? "温室作物异常" : "Greenhouse Crop Warning", section);
        });
    }

    // ── 4. 仅挂果树：输出果树行，不得输出空置 ──

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Unlocked_Available_OnlyProducingTrees_NoVacantClaim(bool isZh)
    {
        WithLanguage(isZh ? "zh" : "en", () =>
        {
            string section = FarmStateScanner.BuildGreenhouseSection(
                isZh, isUnlocked: true, isLocationAvailable: true,
                (0, 0, 0, NoVarieties(), NoVarieties(), NoVarieties()),
                (2, 0, 0, new List<string> { isZh ? "苹果" : "Apple" }, NoVarieties(), NoVarieties()));

            Assert.Contains(isZh ? "温室果树" : "Greenhouse Fruit Trees", section);
            Assert.Contains(isZh ? "果实累累" : "bearing ripe fruit", section);
            Assert.Contains(isZh ? "苹果" : "Apple", section);
            Assert.DoesNotContain(isZh ? "目前没有作物或果树" : "currently contains no crops", section);
            Assert.DoesNotContain(isZh ? "温室作物异常" : "Greenhouse Crop Warning", section);
        });
    }

    // ── 5. 仅幼树：输出幼树生长行，不得输出空置 ──

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Unlocked_Available_OnlyGrowingTrees_NoVacantClaim(bool isZh)
    {
        WithLanguage(isZh ? "zh" : "en", () =>
        {
            string section = FarmStateScanner.BuildGreenhouseSection(
                isZh, isUnlocked: true, isLocationAvailable: true,
                (0, 0, 0, NoVarieties(), NoVarieties(), NoVarieties()),
                (0, 3, 0, NoVarieties(), new List<string> { isZh ? "桃子" : "Peach" }, NoVarieties()));

            Assert.Contains(isZh ? "温室果树" : "Greenhouse Fruit Trees", section);
            Assert.Contains(isZh ? "幼年果树正在生长中" : "young fruit trees are growing", section);
            Assert.Contains(isZh ? "桃子" : "Peach", section);
            Assert.DoesNotContain(isZh ? "目前没有作物或果树" : "currently contains no crops", section);
            Assert.DoesNotContain(isZh ? "果实累累" : "bearing ripe fruit", section);
        });
    }

    // ── 6. 仅未挂果成年树：只描述当前事实，不得推断"非挂果季" ──

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Unlocked_Available_OnlyRestingTrees_StatesFactWithoutSeasonInference(bool isZh)
    {
        WithLanguage(isZh ? "zh" : "en", () =>
        {
            string section = FarmStateScanner.BuildGreenhouseSection(
                isZh, isUnlocked: true, isLocationAvailable: true,
                (0, 0, 0, NoVarieties(), NoVarieties(), NoVarieties()),
                (0, 0, 4, NoVarieties(), NoVarieties(), new List<string> { isZh ? "橙子" : "Orange" }));

            Assert.Contains(isZh ? "温室果树" : "Greenhouse Fruit Trees", section);
            Assert.Contains(isZh ? "成年果树" : "mature fruit trees", section);
            Assert.Contains(isZh ? "目前没有挂果" : "currently bearing no fruit", section);
            Assert.Contains(isZh ? "橙子" : "Orange", section);

            // 不推断非产果季
            Assert.DoesNotContain(isZh ? "非挂果期" : "out of season", section);
            Assert.DoesNotContain(isZh ? "目前没有作物或果树" : "currently contains no crops", section);
        });
    }

    // ── 7. 仅枯萎作物：输出温室作物异常行，不得输出空置或成熟/生长描述 ──

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Unlocked_Available_OnlyDeadCrops_EmitWarningNoVacantClaim(bool isZh)
    {
        WithLanguage(isZh ? "zh" : "en", () =>
        {
            string section = FarmStateScanner.BuildGreenhouseSection(
                isZh, isUnlocked: true, isLocationAvailable: true,
                (0, 0, 3, NoVarieties(), NoVarieties(), new List<string> { isZh ? "枯萎品种" : "Withered Variety" }),
                (0, 0, 0, NoVarieties(), NoVarieties(), NoVarieties()));

            Assert.Contains(isZh ? "温室作物异常" : "Greenhouse Crop Warning", section);
            Assert.Contains(isZh ? "枯萎死去的作物" : "withered crops spotted", section);
            Assert.Contains(isZh ? "枯萎品种" : "Withered Variety", section);
            Assert.DoesNotContain(isZh ? "目前没有作物或果树" : "currently contains no crops", section);
            Assert.DoesNotContain(isZh ? "已成熟" : "ripe and ready", section);
            Assert.DoesNotContain(isZh ? "正在生长" : "crops growing", section);
        });
    }

    // ── 8. 作物与果树并存：两类描述均输出，不得输出空置 ──

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Unlocked_Available_CropsAndTreesCoexist_BothDescriptionsEmitted(bool isZh)
    {
        WithLanguage(isZh ? "zh" : "en", () =>
        {
            string section = FarmStateScanner.BuildGreenhouseSection(
                isZh, isUnlocked: true, isLocationAvailable: true,
                (5, 0, 0, new List<string> { isZh ? "草莓" : "Strawberry" }, NoVarieties(), NoVarieties()),
                (2, 0, 0, new List<string> { isZh ? "苹果" : "Apple" }, NoVarieties(), NoVarieties()));

            Assert.Contains(isZh ? "温室" : "Greenhouse", section);
            Assert.Contains(isZh ? "已成熟待收割" : "ripe and ready to harvest", section);
            Assert.Contains(isZh ? "温室果树" : "Greenhouse Fruit Trees", section);
            Assert.Contains(isZh ? "果实累累" : "bearing ripe fruit", section);
            Assert.DoesNotContain(isZh ? "目前没有作物或果树" : "currently contains no crops", section);
        });
    }

    // ── 9. 计数非零但品种无法解析：保留状态行，省略品种样例，不推断空置 ──

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Unlocked_Available_NonZeroCountWithUnknownVariety_OmitsSampleKeepsState(bool isZh)
    {
        WithLanguage(isZh ? "zh" : "en", () =>
        {
            string section = FarmStateScanner.BuildGreenhouseSection(
                isZh, isUnlocked: true, isLocationAvailable: true,
                (7, 0, 0, NoVarieties(), NoVarieties(), NoVarieties()),
                (0, 0, 0, NoVarieties(), NoVarieties(), NoVarieties()));

            Assert.Contains(isZh ? "温室" : "Greenhouse", section);
            Assert.Contains(isZh ? "已成熟待收割" : "ripe and ready to harvest", section);
            Assert.DoesNotContain(isZh ? "包含" : "including", section);
            Assert.DoesNotContain(isZh ? "目前没有作物或果树" : "currently contains no crops", section);
        });
    }
}
