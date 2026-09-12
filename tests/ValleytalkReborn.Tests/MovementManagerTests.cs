using Xunit;
using Microsoft.Xna.Framework;
using ValleyTalk;

namespace ValleyTalk.Tests
{
    public class GotoTimeoutCounterTests
    {
        [Fact]
        public void Tick_ReturnsFalse_BeforeExpiry()
        {
            var counter = new GotoTimeoutCounter(3);
            Assert.False(counter.Tick()); // 2 remaining
            Assert.False(counter.Tick()); // 1 remaining
        }

        [Fact]
        public void Tick_ReturnsTrue_OnExpiry()
        {
            var counter = new GotoTimeoutCounter(2);
            counter.Tick(); // 1 remaining
            Assert.True(counter.Tick()); // 0 — expired
        }

        [Fact]
        public void Reset_AllowsCounterToExpireAgain()
        {
            var counter = new GotoTimeoutCounter(2);
            counter.Tick();              // 1 remaining
            Assert.True(counter.Tick()); // expired

            counter.Reset();

            Assert.False(counter.Tick()); // 1 remaining after reset
            Assert.True(counter.Tick());  // expired again
        }
    }

    public class ResolveTargetAvoidingPlayerTests
    {
        private static bool AlwaysWalkable(Vector2 _) => true;
        private static bool NeverWalkable(Vector2 _) => false;

        [Fact]
        public void ReturnsTarget_WhenNotOccupiedByPlayer()
        {
            var target = new Vector2(5, 5);
            var player = new Vector2(10, 10);

            var result = MovementManager.ResolveTargetAvoidingPlayer(target, player, AlwaysWalkable);

            Assert.Equal(target, result);
        }

        [Fact]
        public void ReturnsAdjacentTile_WhenTargetOccupiedByPlayer()
        {
            var target = new Vector2(5, 5);
            var player = new Vector2(5, 5); // same tile

            var result = MovementManager.ResolveTargetAvoidingPlayer(target, player, AlwaysWalkable);

            // Must not be the player's tile
            Assert.NotEqual(player, result);
        }

        [Fact]
        public void ReturnsOriginalTarget_WhenNoAdjacentTileWalkable()
        {
            var target = new Vector2(5, 5);
            var player = new Vector2(5, 5);

            var result = MovementManager.ResolveTargetAvoidingPlayer(target, player, NeverWalkable);

            // Fallback: return original target even if blocked
            Assert.Equal(target, result);
        }

        [Fact]
        public void ReturnsTarget_WhenPlayerIsNull_LikeZeroVector()
        {
            // Player at Vector2.Zero is treated as "no player" in fallback logic.
            var target = new Vector2(3, 7);
            var player = Vector2.Zero;

            var result = MovementManager.ResolveTargetAvoidingPlayer(target, player, AlwaysWalkable);

            Assert.Equal(target, result);
        }
    }

    public class TryDetectGotoIntentTests
    {
        [Theory]
        [InlineData("Can you go to the pond?")]
        [InlineData("go to the tree over there")]
        [InlineData("walk to the bench")]
        [InlineData("move to the fountain")]
        [InlineData("head to the shop")]
        [InlineData("fetch my bag")]
        public void DetectsEnglishGotoIntent(string input)
        {
            bool detected = ContextRouterTestShim.TryDetectGotoIntent(input, out string text);
            Assert.True(detected);
            Assert.Equal(input, text);
        }

        [Theory]
        [InlineData("你可以去那棵树吗")]
        [InlineData("走到那个喷泉")]
        [InlineData("移动到那里")]
        [InlineData("过去看看")]
        public void DetectsChineseGotoIntent(string input)
        {
            bool detected = ContextRouterTestShim.TryDetectGotoIntent(input, out string text);
            Assert.True(detected);
            Assert.Equal(input, text);
        }

        [Theory]
        [InlineData("How are you today?")]
        [InlineData("What time is it?")]
        [InlineData("I like this weather.")]
        [InlineData("")]
        public void DoesNotDetectNonGotoInput(string input)
        {
            bool detected = ContextRouterTestShim.TryDetectGotoIntent(input, out string text);
            Assert.False(detected);
            Assert.Equal(string.Empty, text);
        }
    }
}
