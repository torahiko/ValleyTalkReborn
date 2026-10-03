// ValleyMailManagerTests.cs
// VM-001R: headless contract tests for the ValleyMail pure-function seams.
// Exercises TryEnqueue / DeliverDueMail / TryMarkAsRead directly with
// parameter-injected days — no Game1, no MonoGame, no manager static state.

using System.Collections.Generic;
using ValleytalkReborn;
using Xunit;

namespace ValleytalkReborn.Tests;

public class ValleyMailManagerTests
{
    private static ValleyMailMessage MakeMail(string mailId)
    {
        return new ValleyMailMessage
        {
            MailId = mailId,
            SenderNpcName = "Robin",
            Title = "test",
            Pages = new List<string> { "page" },
        };
    }

    [Fact]
    public void DeliverDueMail_MovesDueMailAndEmptiesOutbox()
    {
        var data = new ValleyMailSaveData();
        Assert.True(ValleyMailManager.TryEnqueue(data, MakeMail("due"), currentDay: 10, deliverTomorrow: true));

        ValleyMailManager.DeliverDueMail(data, currentDay: 11);

        Assert.Empty(data.Outbox);
        Assert.Single(data.PendingInbox);
        Assert.Equal("due", data.PendingInbox[0].MailId);
        Assert.Equal(11, data.PendingInbox[0].DeliveryDay);
    }

    [Fact]
    public void DeliverDueMail_KeepsFutureMailInOutbox()
    {
        var data = new ValleyMailSaveData();
        Assert.True(ValleyMailManager.TryEnqueue(data, MakeMail("future"), currentDay: 10, deliverTomorrow: true));

        ValleyMailManager.DeliverDueMail(data, currentDay: 10);

        Assert.Single(data.Outbox);
        Assert.Empty(data.PendingInbox);
    }

    [Fact]
    public void TryEnqueue_RejectsDuplicateMailIdAcrossBothQueues()
    {
        var data = new ValleyMailSaveData();
        Assert.True(ValleyMailManager.TryEnqueue(data, MakeMail("m1"), currentDay: 5, deliverTomorrow: true));

        // Same MailId again (outbox target) and a cross-list attempt (direct-to-inbox
        // target) are both rejected.
        Assert.False(ValleyMailManager.TryEnqueue(data, MakeMail("m1"), currentDay: 6, deliverTomorrow: true));
        Assert.False(ValleyMailManager.TryEnqueue(data, MakeMail("m1"), currentDay: 6, deliverTomorrow: false));

        Assert.Single(data.Outbox);
        Assert.Empty(data.PendingInbox);
    }

    [Fact]
    public void TryEnqueue_RejectsNullOrBlankMailId()
    {
        var data = new ValleyMailSaveData();

        Assert.False(ValleyMailManager.TryEnqueue(data, null, currentDay: 5, deliverTomorrow: true));
        Assert.False(ValleyMailManager.TryEnqueue(data, MakeMail(null), currentDay: 5, deliverTomorrow: true));
        Assert.False(ValleyMailManager.TryEnqueue(data, MakeMail("   "), currentDay: 5, deliverTomorrow: true));

        Assert.Empty(data.Outbox);
        Assert.Empty(data.PendingInbox);
    }

    [Fact]
    public void DeliverDueMail_IsIdempotentAcrossRepeatedRuns()
    {
        var data = new ValleyMailSaveData();
        Assert.True(ValleyMailManager.TryEnqueue(data, MakeMail("due"), currentDay: 5, deliverTomorrow: true));
        Assert.True(ValleyMailManager.TryEnqueue(data, MakeMail("later"), currentDay: 5, deliverTomorrow: true));
        data.Outbox[1].DeliveryDay = 99; // not due yet

        ValleyMailManager.DeliverDueMail(data, currentDay: 6);
        int firstInbox = data.PendingInbox.Count;
        int firstOutbox = data.Outbox.Count;

        ValleyMailManager.DeliverDueMail(data, currentDay: 6);

        Assert.Equal(1, firstInbox);
        Assert.Equal(1, firstOutbox);
        Assert.Equal(firstInbox, data.PendingInbox.Count);
        Assert.Equal(firstOutbox, data.Outbox.Count);
    }

    [Fact]
    public void TryMarkAsRead_MovesMailToArchiveAndFlagsRead()
    {
        var data = new ValleyMailSaveData();
        Assert.True(ValleyMailManager.TryEnqueue(data, MakeMail("m1"), currentDay: 5, deliverTomorrow: false));

        Assert.True(ValleyMailManager.TryMarkAsRead(data, data.PendingInbox[0]));

        Assert.Empty(data.PendingInbox);
        Assert.Single(data.Archive);
        Assert.True(data.Archive[0].IsRead);

        // Second call finds nothing left to mark.
        Assert.False(ValleyMailManager.TryMarkAsRead(data, data.Archive[0]));
    }
}
