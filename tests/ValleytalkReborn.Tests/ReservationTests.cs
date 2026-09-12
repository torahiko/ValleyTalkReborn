using System;
using Xunit;

namespace ValleytalkReborn.Tests;

/// <summary>
/// Tests for NpcReservationService.
/// </summary>
public class ReservationTests
{
    [Fact]
    public void Reservation_ShouldBeExclusive()
    {
        var service = new NpcReservationService();

        Assert.True(service.TryReserve("Abigail", "A2A:1"));
        Assert.False(service.TryReserve("Abigail", "Bark"));
        Assert.True(service.IsReservedBy("Abigail", "A2A:1"));

        service.Release("Abigail", "A2A:1");

        Assert.False(service.IsReserved("Abigail"));
    }

    [Fact]
    public void Reservation_SameOwner_CanReacquire()
    {
        var service = new NpcReservationService();

        Assert.True(service.TryReserve("Abigail", "A2A:1"));
        Assert.True(service.TryReserve("Abigail", "A2A:1"));
        Assert.True(service.IsReservedBy("Abigail", "A2A:1"));
    }

    [Fact]
    public void Reservation_ReleaseWrongOwner_ShouldNotRelease()
    {
        var service = new NpcReservationService();

        Assert.True(service.TryReserve("Abigail", "A2A:1"));
        service.Release("Abigail", "Bark");
        Assert.True(service.IsReserved("Abigail"));
    }

    [Fact]
    public void Reservation_Clear_ShouldRemoveAll()
    {
        var service = new NpcReservationService();

        Assert.True(service.TryReserve("Abigail", "A2A:1"));
        Assert.True(service.TryReserve("Sebastian", "Bark"));

        service.Clear();

        Assert.False(service.IsReserved("Abigail"));
        Assert.False(service.IsReserved("Sebastian"));
    }
}
