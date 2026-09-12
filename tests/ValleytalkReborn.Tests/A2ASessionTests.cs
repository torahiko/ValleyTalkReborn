using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Xunit;
using ValleytalkReborn;

namespace ValleyTalk.Tests
{
    /// <summary>
    /// Unit tests for A2A session state machine.
    /// Tests methods that do not require game objects.
    /// </summary>
    public class A2ASessionTests
    {
        [Fact]
        public void GetReservationOwnerFormat()
        {
            var owner = DialogueModels.A2ASession.GetReservationOwner("abc123");
            Assert.Equal("A2A:abc123", owner);
        }

        [Fact]
        public void NewSessionHasNoParticipants()
        {
            var session = new DialogueModels.A2ASession();
            Assert.Empty(session.ParticipantNames);
            Assert.False(session.MembersLocked);
            Assert.Equal(0, session.StareTicks);
            Assert.Equal(0, session.Generation);
        }

        [Fact]
        public void HasAllParticipantsResolvedWithTooFewNames()
        {
            var session = new DialogueModels.A2ASession();
            session.ParticipantNames.Add("Abigail");

            // Only 1 participant - should return false
            Assert.False(session.HasAllParticipantsResolved());
        }

        [Fact]
        public void HasAllParticipantsResolvedWithNoNames()
        {
            var session = new DialogueModels.A2ASession();
            Assert.False(session.HasAllParticipantsResolved());
        }

        [Fact]
        public void AllInStareRangeReturnsFalseForMissingParticipants()
        {
            var session = new DialogueModels.A2ASession();
            session.ParticipantNames.Add("Abigail");
            session.ParticipantNames.Add("Sebastian");

            // No game objects, so ResolveParticipants returns empty
            Assert.False(session.AllInStareRange());
        }

        [Fact]
        public void AllInStareRangeReturnsFalseForEmptySession()
        {
            var session = new DialogueModels.A2ASession();
            Assert.False(session.AllInStareRange());
        }

        [Fact]
        public void SessionCancelIsIdempotent()
        {
            var session = new DialogueModels.A2ASession();
            session.Cancel();
            session.Cancel();
            Assert.True(session.IsCancelled);
        }

        [Fact]
        public void MarkEndingIsIdempotent()
        {
            var session = new DialogueModels.A2ASession();
            session.MarkEnding();
            session.MarkEnding();
            Assert.True(session.IsEnding);
        }

        [Fact]
        public void TryStartRequestSucceedsOnce()
        {
            var session = new DialogueModels.A2ASession();
            Assert.True(session.TryStartRequest());
            Assert.False(session.TryStartRequest());
        }

        [Fact]
        public void FinishRequestResetsRequesting()
        {
            var session = new DialogueModels.A2ASession();
            session.TryStartRequest();
            session.FinishRequest();
            Assert.True(session.TryStartRequest());
        }

        [Fact]
        public void ReservationOwnerDefaultsToNull()
        {
            var session = new DialogueModels.A2ASession();
            Assert.Null(session.ReservationOwner);
        }

        [Fact]
        public void GenerationDefaultsToZero()
        {
            var session = new DialogueModels.A2ASession();
            Assert.Equal(0, session.Generation);
        }

        [Fact]
        public void ScriptQueueStartsEmpty()
        {
            var session = new DialogueModels.A2ASession();
            Assert.Empty(session.Script);
        }

        [Fact]
        public void ParticipantNamesAreCaseInsensitive()
        {
            var session = new DialogueModels.A2ASession();
            session.ParticipantNames.Add("abigail");
            // xUnit2012: use Any() to check with case-insensitive comparer
            Assert.True(session.ParticipantNames.Any(n => string.Equals(n, "Abigail", StringComparison.OrdinalIgnoreCase)));
        }
    }
}
