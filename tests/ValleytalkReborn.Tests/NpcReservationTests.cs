using System;
using Xunit;
using ValleytalkReborn;

namespace ValleyTalk.Tests
{
    /// <summary>
    /// Unit tests for NpcReservationService.
    /// Verifies owner-based reservation semantics.
    /// </summary>
    public class NpcReservationTests
    {
        [Fact]
        public void SameOwnerCanReserveAgain()
        {
            var service = new NpcReservationService();

            Assert.True(service.TryReserve("Abigail", "A2A:1"));
            Assert.True(service.TryReserve("Abigail", "A2A:1"));
            Assert.False(service.TryReserve("Abigail", "A2A:2"));
        }

        [Fact]
        public void ReleaseOnlyReleasesMatchingOwner()
        {
            var service = new NpcReservationService();

            service.TryReserve("Abigail", "A2A:1");
            service.Release("Abigail", "A2A:2");

            Assert.True(service.IsReserved("Abigail"));

            service.Release("Abigail", "A2A:1");

            Assert.False(service.IsReserved("Abigail"));
        }

        [Fact]
        public void DifferentOwnersCannotReserveSameNpc()
        {
            var service = new NpcReservationService();

            Assert.True(service.TryReserve("Abigail", "A2A:session1"));
            Assert.False(service.TryReserve("Abigail", "A2A:session2"));
            Assert.False(service.TryReserve("Abigail", "AmbientBark"));
        }

        [Fact]
        public void ReleaseOwnerReleasesAllNpcs()
        {
            var service = new NpcReservationService();

            service.TryReserve("Abigail", "A2A:session1");
            service.TryReserve("Sebastian", "A2A:session1");
            service.TryReserve("Haley", "AmbientBark");

            service.ReleaseOwner("A2A:session1");

            Assert.False(service.IsReserved("Abigail"));
            Assert.False(service.IsReserved("Sebastian"));
            Assert.True(service.IsReserved("Haley"));
        }

        [Fact]
        public void ClearRemovesAllReservations()
        {
            var service = new NpcReservationService();

            service.TryReserve("Abigail", "A2A:1");
            service.TryReserve("Sebastian", "AmbientBark");

            service.Clear();

            Assert.False(service.IsReserved("Abigail"));
            Assert.False(service.IsReserved("Sebastian"));
        }

        [Fact]
        public void IsReservedByReturnsCorrectResult()
        {
            var service = new NpcReservationService();

            service.TryReserve("Abigail", "A2A:1");

            Assert.True(service.IsReservedBy("Abigail", "A2A:1"));
            Assert.False(service.IsReservedBy("Abigail", "A2A:2"));
            Assert.False(service.IsReservedBy("Sebastian", "A2A:1"));
        }

        [Fact]
        public void TryReserveWithNullOrEmptyReturnsFalse()
        {
            var service = new NpcReservationService();

            Assert.False(service.TryReserve(null, "A2A:1"));
            Assert.False(service.TryReserve("", "A2A:1"));
            Assert.False(service.TryReserve("Abigail", null));
            Assert.False(service.TryReserve("Abigail", ""));
        }
    }
}
