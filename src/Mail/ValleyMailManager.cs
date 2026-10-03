using System;
using System.Collections.Generic;
using System.Linq;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace ValleytalkReborn;

/// <summary>
/// VM-001R: ValleyMail core manager engine. Owns the independent SaveData key
/// "valleytalk.mailbox" holding the outbox (scheduled deliveries), pending inbox
/// (delivered, unopened) and archive (read) lists. Single-player only: in
/// multiplayer no mail is read, written or delivered. All handlers run on the
/// SMAPI main thread; the due-day settlement happens only from DayStarted, where
/// the engine reads the game date on the main thread. No mailbox/UI/Harmony or
/// vanilla mailReceived/mailbox access lives here (later tickets' scope).
/// </summary>
internal static class ValleyMailManager
{
    private const string SaveDataKey = "valleytalk.mailbox";
    private const int CurrentSchemaVersion = 1;

    private static IModHelper _helper;
    private static IMonitor _monitor;
    private static ValleyMailSaveData _data;
    private static bool _isSaveLoaded;
    private static bool _isDirty;
    private static bool _mpDisabledLogged;

    internal static void Initialize(IModHelper helper, IMonitor monitor)
    {
        _helper = helper;
        _monitor = monitor;

        Unsubscribe();
        helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
        helper.Events.GameLoop.DayStarted += OnDayStarted;
        helper.Events.GameLoop.Saving += OnSaving;
        helper.Events.GameLoop.ReturnedToTitle += OnReturnedToTitle;
    }

    internal static void Cleanup()
    {
        Unsubscribe();
        ResetMemoryState();
    }

    /// <summary>
    /// Enqueues one mail. deliverTomorrow=true schedules delivery for the next day
    /// through the persistent outbox; false puts it straight into the pending inbox.
    /// Invalid or duplicate MailId is rejected with a warning (callers are content
    /// pipelines, so this never throws).
    /// </summary>
    public static void SendMail(ValleyMailMessage mail, bool deliverTomorrow = true)
    {
        if (mail == null || string.IsNullOrWhiteSpace(mail.MailId))
        {
            _monitor?.Log("[ValleyMail] SendMail rejected: mail is null or MailId is empty.", LogLevel.Warn);
            return;
        }

        if (CheckMultiplayerDisabled())
            return;

        if (_data == null)
        {
            _monitor?.Log("[ValleyMail] SendMail rejected: SaveLoaded has not occurred.", LogLevel.Trace);
            return;
        }

        if (!TryEnqueue(_data, mail, (int)Game1.Date.TotalDays, deliverTomorrow))
        {
            _monitor?.Log($"[ValleyMail] SendMail skipped: duplicate MailId '{mail.MailId}'.", LogLevel.Warn);
            return;
        }

        _isDirty = true;
    }

    public static bool HasPendingMail()
    {
        return _data != null && _data.PendingInbox.Count > 0;
    }

    /// <summary>Next unopened mail (PendingInbox[0]), or null when empty.</summary>
    public static ValleyMailMessage PeekNextPendingMail()
    {
        return _data != null && _data.PendingInbox.Count > 0 ? _data.PendingInbox[0] : null;
    }

    /// <summary>
    /// Moves one mail from the pending inbox to the archive and marks it read.
    /// A mail that is not in the pending inbox is a caller-contract violation and
    /// is rejected with a warning, leaving all state untouched.
    /// </summary>
    public static void MarkCurrentMailAsRead(ValleyMailMessage mail)
    {
        if (CheckMultiplayerDisabled())
            return;

        if (_data == null)
        {
            _monitor?.Log("[ValleyMail] MarkCurrentMailAsRead rejected: SaveLoaded has not occurred.", LogLevel.Trace);
            return;
        }

        if (TryMarkAsRead(_data, mail))
        {
            _isDirty = true;
        }
        else
        {
            _monitor?.Log($"[ValleyMail] MarkCurrentMailAsRead ignored: mail '{mail?.MailId}' is not in the pending inbox.", LogLevel.Warn);
        }
    }

    public static IReadOnlyList<ValleyMailMessage> GetArchivedMails()
    {
        if (_data == null)
            return Array.Empty<ValleyMailMessage>();

        return _data.Archive;
    }

    // ── Headless-test pure seams: no Game1 access, day injected by parameter ──

    /// <summary>
    /// Enqueues into a plain save-data model. Returns false for invalid input or a
    /// MailId already present in the outbox or pending inbox. Does not touch the
    /// manager's dirty flag (the SendMail wrapper owns that).
    /// </summary>
    internal static bool TryEnqueue(ValleyMailSaveData data, ValleyMailMessage mail, int currentDay, bool deliverTomorrow)
    {
        if (mail == null || string.IsNullOrWhiteSpace(mail.MailId))
            return false;

        if (ContainsMailId(data.Outbox, mail.MailId) || ContainsMailId(data.PendingInbox, mail.MailId))
            return false;

        if (deliverTomorrow)
        {
            mail.DeliveryDay = currentDay + 1;
            data.Outbox.Add(mail);
        }
        else
        {
            data.PendingInbox.Add(mail);
        }

        return true;
    }

    /// <summary>
    /// Migrates every outbox mail with DeliveryDay &lt;= currentDay into the pending
    /// inbox (<=, not ==, so a crash/reload that skipped a DayStarted still
    /// delivers). Replaying on the same model is a no-op, hence idempotent.
    /// </summary>
    internal static void DeliverDueMail(ValleyMailSaveData data, int currentDay)
    {
        for (int i = data.Outbox.Count - 1; i >= 0; i--)
        {
            var mail = data.Outbox[i];
            if (mail.DeliveryDay > currentDay)
                continue;

            data.Outbox.RemoveAt(i);
            data.PendingInbox.Add(mail);
        }
    }

    /// <summary>
    /// Pure mark-read seam: removes the mail from the pending inbox (reference
    /// match), flags IsRead and files it into the archive. Returns false when the
    /// mail is not in the pending inbox.
    /// </summary>
    internal static bool TryMarkAsRead(ValleyMailSaveData data, ValleyMailMessage mail)
    {
        int index = data.PendingInbox.IndexOf(mail);
        if (index < 0)
            return false;

        data.PendingInbox.RemoveAt(index);
        mail.IsRead = true;
        data.Archive.Add(mail);
        return true;
    }

    // ── GameLoop handlers ──

    private static void OnSaveLoaded(object sender, SaveLoadedEventArgs e)
    {
        if (_isSaveLoaded)
            return;

        if (CheckMultiplayerDisabled())
        {
            _isSaveLoaded = true;
            return;
        }

        try
        {
            _data = _helper.Data.ReadSaveData<ValleyMailSaveData>(SaveDataKey);
        }
        catch (Exception ex)
        {
            // Declared failure path: fall back to an empty model, main flow continues.
            _monitor?.Log($"[ValleyMail] Failed to read save data '{SaveDataKey}': {ex}", LogLevel.Error);
            _data = null;
        }

        if (_data == null)
        {
            _data = new ValleyMailSaveData();
        }
        else if (_data.SchemaVersion != CurrentSchemaVersion)
        {
            // v1 has no migration branch: an unknown schema version is treated as empty.
            _monitor?.Log($"[ValleyMail] Save data '{SaveDataKey}' has schema version {_data.SchemaVersion} (expected {CurrentSchemaVersion}); replaced with an empty model.", LogLevel.Warn);
            _data = new ValleyMailSaveData();
        }

        _isSaveLoaded = true;
        _monitor?.Log($"[ValleyMail] Save data loaded ({_data.PendingInbox.Count} inbox / {_data.Archive.Count} archive / {_data.Outbox.Count} outbox).", LogLevel.Info);
    }

    private static void OnDayStarted(object sender, DayStartedEventArgs e)
    {
        if (CheckMultiplayerDisabled())
            return;

        if (_data == null)
        {
            _monitor?.Log("[ValleyMail] DayStarted settlement skipped: save data is null (event-order violation).", LogLevel.Error);
            return;
        }

        int inboxCountBefore = _data.PendingInbox.Count;
        DeliverDueMail(_data, (int)Game1.Date.TotalDays);
        if (_data.PendingInbox.Count != inboxCountBefore)
            _isDirty = true;
    }

    private static void OnSaving(object sender, SavingEventArgs e)
    {
        if (!_isDirty)
            return;

        if (CheckMultiplayerDisabled())
            return;

        try
        {
            _helper.Data.WriteSaveData(SaveDataKey, _data);
            _isDirty = false;
        }
        catch (Exception ex)
        {
            // Declared failure path: keep _isDirty so the next Saving retries.
            _monitor?.Log($"[ValleyMail] Failed to write save data '{SaveDataKey}': {ex}", LogLevel.Error);
        }
    }

    private static void OnReturnedToTitle(object sender, ReturnedToTitleEventArgs e)
    {
        ResetMemoryState();
    }

    // ── State helpers ──

    private static bool ContainsMailId(List<ValleyMailMessage> mails, string mailId)
    {
        return mails.Any(mail => string.Equals(mail.MailId, mailId, StringComparison.Ordinal));
    }

    private static bool CheckMultiplayerDisabled()
    {
        if (!Context.IsMultiplayer)
            return false;

        if (!_mpDisabledLogged)
        {
            _monitor?.Log("[ValleyMail] ValleyMail is disabled in multiplayer: no mail is read, written or delivered.", LogLevel.Info);
            _mpDisabledLogged = true;
        }

        return true;
    }

    private static void ResetMemoryState()
    {
        _data = null;
        _isSaveLoaded = false;
        _isDirty = false;
    }

    private static void Unsubscribe()
    {
        _helper?.Events.GameLoop.SaveLoaded -= OnSaveLoaded;
        _helper?.Events.GameLoop.DayStarted -= OnDayStarted;
        _helper?.Events.GameLoop.Saving -= OnSaving;
        _helper?.Events.GameLoop.ReturnedToTitle -= OnReturnedToTitle;
    }
}
