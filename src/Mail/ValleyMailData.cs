using System.Collections.Generic;

namespace ValleytalkReborn;

/// <summary>
/// VM-001R: attachment payload of one ValleyMail message. Only the field matching
/// <see cref="Type"/> is meaningful. v1 intentionally has no Quest variant — dead
/// schema fields are forbidden; future kinds arrive with a SchemaVersion bump.
/// </summary>
public enum MailAttachmentType
{
    Item,
    Money,
    TriggerAction
}

/// <summary>
/// One attachment carried by a <see cref="ValleyMailMessage"/>.
/// </summary>
public sealed class MailAttachment
{
    public MailAttachmentType Type { get; set; }

    /// <summary>Qualified item id, e.g. "(O)128". Item attachments only.</summary>
    public string QualifiedItemId { get; set; }

    public int Stack { get; set; } = 1;
    public int Quality { get; set; }

    /// <summary>Money attachments only.</summary>
    public int MoneyAmount { get; set; }

    /// <summary>Raw TriggerAction string, executed on read. TriggerAction attachments only.</summary>
    public string ActionString { get; set; }

    /// <summary>Set by the collection flow (later ticket); persisted with the message.</summary>
    public bool IsCollected { get; set; }
}

/// <summary>
/// One ValleyMail message. <see cref="MailId"/> is the caller-supplied unique key
/// across the outbox and pending inbox. <see cref="DeliveryDay"/> uses the
/// Game1.Date.TotalDays system (0-based absolute day).
/// </summary>
public sealed class ValleyMailMessage
{
    public string MailId { get; set; }
    public string SenderNpcName { get; set; }
    public string Title { get; set; }

    public List<string> Pages { get; set; } = new();
    public List<MailAttachment> Attachments { get; set; } = new();

    /// <summary>Optional TriggerAction executed once when the mail is read.</summary>
    public string TriggerActionOnRead { get; set; }

    /// <summary>Absolute due day (0-based TotalDays); only meaningful in the outbox.</summary>
    public int DeliveryDay { get; set; }

    public bool IsRead { get; set; }

    /// <summary>Drawer badge tag (e.g. clue tag), may be null.</summary>
    public string Tag { get; set; }
}

/// <summary>
/// VM-001R: the persisted ValleyMail model. All three lists are written as one
/// SaveData blob under "valleytalk.mailbox" (save-scope persistence), so a
/// scheduled delivery survives a save/load round-trip.
/// </summary>
internal sealed class ValleyMailSaveData
{
    public int SchemaVersion { get; set; } = 1;

    /// <summary>Scheduled-delivery pool, settled on DayStarted.</summary>
    public List<ValleyMailMessage> Outbox { get; set; } = new();

    /// <summary>Delivered but not yet opened.</summary>
    public List<ValleyMailMessage> PendingInbox { get; set; } = new();

    /// <summary>Opened mails, kept for the drawer UI.</summary>
    public List<ValleyMailMessage> Archive { get; set; } = new();
}
