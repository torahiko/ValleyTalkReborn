using System;
using Xunit;
using ValleytalkReborn;

namespace ValleyTalk.Tests
{
    /// <summary>
    /// Unit tests for AmbientBarkStateStore.State expiry and request invalidation.
    /// Verifies that old LLM results cannot be applied after state expiry.
    /// </summary>
    public class BarkExpiredRequestTests
    {
        [Fact]
        public void ReplaceCtsIncrementsRequestId()
        {
            var state = new AmbientBarkStateStore.State();

            int oldRequestId = state.RequestId;

            state.ReplaceCts();

            Assert.NotEqual(oldRequestId, state.RequestId);
        }

        [Fact]
        public void ReplaceCtsTwiceProducesDifferentIds()
        {
            var state = new AmbientBarkStateStore.State();

            state.ReplaceCts();
            int firstId = state.RequestId;

            state.ReplaceCts();
            int secondId = state.RequestId;

            Assert.NotEqual(firstId, secondId);
        }

        [Fact]
        public void ExpiredRequestCannotApplyOldResult()
        {
            var state = new AmbientBarkStateStore.State();

            int originalRequestId = state.RequestId;

            // Simulate expiry
            state.ReplaceCts();

            // Old request ID should no longer match
            Assert.NotEqual(originalRequestId, state.RequestId);
        }

        [Fact]
        public void StateStartsWithValidDefaults()
        {
            var state = new AmbientBarkStateStore.State();

            Assert.Equal(0, state.LifeTicks);
            Assert.Equal(0, state.CooldownTicks);
            Assert.Equal(0, state.DisplayCountdown);
            Assert.False(state.IsRequesting);
            Assert.False(state.HasPlayedFirst);
            Assert.NotNull(state.BarkQueue);
            Assert.NotNull(state.BackgroundCts);
            Assert.NotNull(state.RecentBarks);
        }

        [Fact]
        public void HardResetClearsAllState()
        {
            var store = new AmbientBarkStateStore();
            var state = store.GetOrCreate("Abigail");

            state.LifeTicks = 100;
            state.IsRequesting = true;
            state.BarkQueue.Enqueue("test");

            store.HardReset("Abigail");

            Assert.Equal(0, state.LifeTicks);
            Assert.Equal(0, state.CooldownTicks);
            Assert.False(state.IsRequesting);
            Assert.Empty(state.BarkQueue);
        }

        [Fact]
        public void TryRemoveRemovesState()
        {
            var store = new AmbientBarkStateStore();
            store.GetOrCreate("Abigail");

            Assert.True(store.TryGet("Abigail", out _));

            store.TryRemove("Abigail", out _);

            Assert.False(store.TryGet("Abigail", out _));
        }
    }
}
