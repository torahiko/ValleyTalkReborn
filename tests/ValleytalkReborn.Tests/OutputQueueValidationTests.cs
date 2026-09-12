using System;
using System.Collections.Concurrent;
using Xunit;
using ValleytalkReborn;

namespace ValleyTalk.Tests
{
    /// <summary>
    /// Mock IA2AOutputValidator for testing MainThreadOutputQueue.
    /// </summary>
    internal class MockA2AOutputValidator : IA2AOutputValidator
    {
        private bool _result = true;

        public void SetResult(bool result) => _result = result;
        public int CallCount { get; private set; }

        public bool Validate(string sessionId, int generation, string npcName)
        {
            CallCount++;
            return _result;
        }
    }

    /// <summary>
    /// Unit tests for MainThreadOutputQueue A2A validation.
    /// Verifies that invalid A2A output is discarded.
    /// </summary>
    public class OutputQueueValidationTests
    {
        [Fact]
        public void InvalidA2AOutputIsDiscarded()
        {
            var validator = new MockA2AOutputValidator();
            validator.SetResult(false);

            var queue = new MainThreadOutputQueue(validator);
            queue.EnqueueA2A("session1", 1, "Abigail", "Test line", 3500);

            // Process should discard without calling showTextAboveHead
            queue.Process(20);

            Assert.Equal(1, validator.CallCount);
            Assert.True(queue.IsEmpty());
        }

        [Fact]
        public void ValidA2AOutputPassesValidation()
        {
            var validator = new MockA2AOutputValidator();
            validator.SetResult(true);

            var queue = new MainThreadOutputQueue(validator);
            queue.EnqueueA2A("session1", 1, "Abigail", "Test line", 3500);

            // Process would try to show, but without game state it skips the NPC lookup
            queue.Process(20);

            Assert.Equal(1, validator.CallCount);
        }

        [Fact]
        public void ClearRemovesAllItems()
        {
            var validator = new MockA2AOutputValidator();
            var queue = new MainThreadOutputQueue(validator);

            queue.EnqueueA2A("session1", 1, "Abigail", "Line 1", 3500);
            queue.EnqueueA2A("session1", 1, "Sebastian", "Line 2", 3500);
            queue.Enqueue("Abigail", "Bark text", 3500, "Bark");

            queue.Clear();
            Assert.True(queue.IsEmpty());

            queue.Process(20);
            Assert.Equal(0, validator.CallCount);
        }

        [Fact]
        public void MultipleInvalidOutputsAreAllDiscarded()
        {
            var validator = new MockA2AOutputValidator();
            validator.SetResult(false);

            var queue = new MainThreadOutputQueue(validator);

            for (int i = 0; i < 5; i++)
            {
                queue.EnqueueA2A("session1", 1, $"Npc{i}", $"Line {i}", 3500);
            }

            queue.Process(20);

            Assert.Equal(5, validator.CallCount);
            Assert.True(queue.IsEmpty());
        }

        [Fact]
        public void EnqueueA2AWithNullOrEmptyValuesDoesNotEnqueue()
        {
            var validator = new MockA2AOutputValidator();
            var queue = new MainThreadOutputQueue(validator);

            queue.EnqueueA2A(null, 1, "Abigail", "text", 3500);
            queue.EnqueueA2A("session1", 1, null, "text", 3500);
            queue.EnqueueA2A("session1", 1, "Abigail", null, 3500);

            queue.Process(20);

            Assert.Equal(0, validator.CallCount);
            Assert.True(queue.IsEmpty());
        }
    }

    /// <summary>
    /// Extension to expose queue count for testing.
    /// </summary>
    internal static class MainThreadOutputQueueExtensions
    {
        public static bool IsEmpty(this MainThreadOutputQueue queue)
        {
            var field = typeof(MainThreadOutputQueue).GetField(
                "_queue",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var q = (ConcurrentQueue<MainThreadOutputQueue.Item>)field.GetValue(queue);
            return q.IsEmpty;
        }
    }
}
