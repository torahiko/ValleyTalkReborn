using System;
using System.Collections.Generic;
using ValleytalkReborn.Cutscene.Serendipity;
using Xunit;

namespace ValleytalkReborn.Tests
{
    public class CutscenePhase4Tests
    {
        [Fact]
        public void SerendipityCooldownStore_CanTriggerToday_EnforcesMaxDailyLimit()
        {
            var store = SerendipityCooldownStore.Instance;
            store.ResetDay();

            Assert.True(store.CanTriggerToday(1));
            Assert.True(store.CanTriggerToday(2));
            Assert.False(store.CanTriggerToday(0));

            store.RecordTrigger(new[] { "Shane", "Emily" }, currentTotalDays: 15, timeOfDay: 1900);

            Assert.False(store.CanTriggerToday(1));
            Assert.True(store.CanTriggerToday(2));
            Assert.Equal(1, store.DailyTriggeredCount);
            Assert.Equal(1900, store.LastTriggerTimeOfDay);

            store.ResetDay();
            Assert.True(store.CanTriggerToday(1));
            Assert.Equal(0, store.DailyTriggeredCount);
        }

        [Fact]
        public void SerendipityCooldownStore_IsNpcAvailable_RespectsCooldownDays()
        {
            var store = SerendipityCooldownStore.Instance;
            store.Clear();

            Assert.True(store.IsNpcAvailable("Shane", currentTotalDays: 20));
            Assert.True(store.IsNpcAvailable("Emily", currentTotalDays: 20));

            store.RecordTrigger(new[] { "Shane", "Emily" }, currentTotalDays: 20, timeOfDay: 1830);

            // Same day: unavailable
            Assert.False(store.IsNpcAvailable("Shane", currentTotalDays: 20));
            Assert.False(store.IsNpcAvailable("Emily", currentTotalDays: 20));

            // Another NPC: available
            Assert.True(store.IsNpcAvailable("Clint", currentTotalDays: 20));

            // Next day (day 21): available again with default cooldownDays = 1
            Assert.True(store.IsNpcAvailable("Shane", currentTotalDays: 21));
            Assert.True(store.IsNpcAvailable("Emily", currentTotalDays: 21));
        }

        [Fact]
        public void SerendipityCooldownStore_BeaconDismissal_SuppressesWithinWindow()
        {
            var store = SerendipityCooldownStore.Instance;
            store.ResetDay();

            store.RecordBeaconDismissal("雪山偶遇", "Farm", currentTotalDays: 100, timeOfDay: 1240);

            // 记录后 10 游戏分钟：抑制
            Assert.True(store.IsBeaconSuppressed("雪山偶遇", "Farm", currentTotalDays: 100, timeOfDay: 1250));
            // 跨小时进位（1240 → 1305 实际为 25 分钟）：仍抑制
            Assert.True(store.IsBeaconSuppressed("雪山偶遇", "Farm", currentTotalDays: 100, timeOfDay: 1305));
        }

        [Fact]
        public void SerendipityCooldownStore_BeaconDismissal_ReleasesAfterSuppressWindow()
        {
            var store = SerendipityCooldownStore.Instance;
            store.ResetDay();

            store.RecordBeaconDismissal("雪山偶遇", "Farm", currentTotalDays: 100, timeOfDay: 1240);

            // 满 30 游戏分钟（1240 → 1310）：放行（窗口判定为差值 < 30）
            Assert.False(store.IsBeaconSuppressed("雪山偶遇", "Farm", currentTotalDays: 100, timeOfDay: 1310));

            // 不同剧本或不同地点：互不影响
            Assert.False(store.IsBeaconSuppressed("酒馆夜晚", "Farm", currentTotalDays: 100, timeOfDay: 1250));
            Assert.False(store.IsBeaconSuppressed("雪山偶遇", "Town", currentTotalDays: 100, timeOfDay: 1250));
        }

        [Fact]
        public void SerendipityCooldownStore_BeaconDismissal_ReleasesAcrossDays()
        {
            var store = SerendipityCooldownStore.Instance;
            store.ResetDay();

            store.RecordBeaconDismissal("雪山偶遇", "Farm", currentTotalDays: 100, timeOfDay: 2550);

            // 跨天（次日清晨，timeOfDay 早于自毁时刻）：Day 键校验自然失效
            Assert.False(store.IsBeaconSuppressed("雪山偶遇", "Farm", currentTotalDays: 101, timeOfDay: 610));
        }

        [Fact]
        public void SituationMatcher_MatchesFridaySaloon_Accurately()
        {
            var situation = SituationMatcher.MatchSituation(
                locationName: "Saloon",
                timeOfDay: 1930,
                isRaining: false,
                dayOfWeek: "Friday",
                candidateNpcNames: new List<string> { "Shane", "Emily" }
            );

            Assert.NotNull(situation);
            Assert.Equal("SaloonFridayNight", situation.Id);
            Assert.Equal("周五晚星果沙龙", situation.Title);
            Assert.Contains("吧台", situation.IntentPrompt);
        }

        [Fact]
        public void SituationMatcher_MatchesRainyLake_Accurately()
        {
            var situation = SituationMatcher.MatchSituation(
                locationName: "Mountain",
                timeOfDay: 1400,
                isRaining: true,
                dayOfWeek: "Tuesday",
                candidateNpcNames: new List<string> { "Abigail", "Sebastian" }
            );

            Assert.NotNull(situation);
            Assert.Equal("MountainRainyLake", situation.Id);
            Assert.Equal("雨天湖畔偶遇", situation.Title);
            Assert.Contains("细雨", situation.IntentPrompt);
        }

        [Fact]
        public void SituationMatcher_FallbackToGenericEncounter_WhenNoPresetMatches()
        {
            var situation = SituationMatcher.MatchSituation(
                locationName: "Forest",
                timeOfDay: 1100,
                isRaining: false,
                dayOfWeek: "Wednesday",
                candidateNpcNames: new List<string> { "Jas", "Vincent" }
            );

            Assert.NotNull(situation);
            Assert.Equal("GenericEncounter", situation.Id);
            Assert.Equal("街头偶遇闲谈", situation.Title);
            Assert.Contains("Jas", situation.IntentPrompt);
            Assert.Contains("Vincent", situation.IntentPrompt);
        }

        [Fact]
        public void SituationDefinition_Constructor_GuardsNullArguments()
        {
            Assert.Throws<ArgumentNullException>(() => new SituationDefinition(null, "Title", "Prompt"));
            Assert.Throws<ArgumentNullException>(() => new SituationDefinition("Id", null, "Prompt"));
            Assert.Throws<ArgumentNullException>(() => new SituationDefinition("Id", "Title", null));
        }

        [Fact]
        public void SerendipityBeacon_Dismiss_ClearsActiveState()
        {
            var beacon = SerendipityBeacon.Instance;
            beacon.Dismiss();

            Assert.False(beacon.IsActive);
            Assert.Empty(beacon.TargetActors);
            Assert.Null(beacon.Situation);
        }

        [Fact]
        public void SerendipityBeacon_Dismiss_DoesNotMutatePreviouslyCapturedReference()
        {
            var beacon = SerendipityBeacon.Instance;
            beacon.Dismiss();

            // 预置非空演员表：借非泛型 IList 注入 null 占位演员（无头测试无法构造真实 NPC）
            var seeded = (System.Collections.IList)beacon.TargetActors;
            seeded.Add(null);
            seeded.Add(null);
            Assert.Equal(2, beacon.TargetActors.Count);

            // 模拟 ModEntry 按键时序：先快照截获引用，再 Dismiss
            var captured = beacon.TargetActors;

            beacon.Dismiss("player triggered encounter");

            // 旧实现 TargetActors.Clear() 会就地清空同一引用使快照失真；
            // 新实现整体换新列表，外部捕获的引用保持原样
            Assert.NotNull(captured);
            Assert.Equal(2, captured.Count);
            Assert.False(beacon.IsActive);
            Assert.Empty(beacon.TargetActors);
            Assert.Null(beacon.Situation);
        }

        [Fact]
        public void SerendipityBeacon_IsPlayerInRange_FalseWhenInactive()
        {
            var beacon = SerendipityBeacon.Instance;
            beacon.Dismiss();

            Assert.False(beacon.IsActive);
            Assert.False(beacon.IsPlayerInRange());
        }

        [Fact]
        public void SerendipityBeacon_Update_InactiveBeaconRemainsInactive()
        {
            var beacon = SerendipityBeacon.Instance;
            beacon.Dismiss();

            // Calling Update when inactive should be a clean no-op
            beacon.Update(5.0f);
            Assert.False(beacon.IsActive);
            Assert.Empty(beacon.TargetActors);
        }
    }
}
