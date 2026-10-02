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
    }
}
