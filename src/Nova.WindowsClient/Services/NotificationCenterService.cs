using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Nova.WindowsClient.Services;

internal enum NotificationEntryKind
{
    SystemEvent,
    Reminder,
    TrainReminder
}

internal enum NotificationReminderUpdateResult
{
    Updated,
    NotFound,
    AlreadySent,
    DueTimePassed
}

internal sealed record NotificationCenterEntry(
    string Id,
    NotificationEntryKind Kind,
    string Title,
    string Body,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DueAt,
    bool IsRead,
    bool Enabled,
    bool Sent,
    string? RelatedId);

internal sealed class ToastNotificationDeliveryException(string message, Exception innerException)
    : IOException(message, innerException);

internal sealed class NotificationCenterStore
{
    private const string DocumentFormat = "NOVA-NOTIFICATION-CENTER";
    private const int CurrentVersion = 1;
    private const int MaximumEntries = 500;
    private readonly string _filePath;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public NotificationCenterStore(string stateDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        _filePath = Path.Combine(stateDirectory, "notification-center.json");
    }

    public IReadOnlyList<NotificationCenterEntry> Load()
    {
        if (!File.Exists(_filePath))
            return [];
        if (new FileInfo(_filePath).Length > 1_048_576)
            throw new InvalidDataException("Файл центра уведомлений превышает допустимый размер.");

        var document = JsonSerializer.Deserialize<NotificationCenterDocument>(File.ReadAllText(_filePath));
        Validate(document);
        return document!.Entries!.ToArray();
    }

    public void Save(IReadOnlyCollection<NotificationCenterEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var document = new NotificationCenterDocument(DocumentFormat, CurrentVersion, entries.ToList());
        Validate(document);
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        var temporaryPath = $"{_filePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, document, JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, _filePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static void Validate(NotificationCenterDocument? document)
    {
        if (document is null ||
            document.Format != DocumentFormat ||
            document.Version != CurrentVersion ||
            document.Entries is null ||
            document.Entries.Count > MaximumEntries)
            throw new InvalidDataException("Файл центра уведомлений имеет неизвестный формат.");

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in document.Entries)
        {
            if (entry is null ||
                string.IsNullOrWhiteSpace(entry.Id) ||
                entry.Id.Length > 96 ||
                !ids.Add(entry.Id) ||
                !Enum.IsDefined(entry.Kind) ||
                string.IsNullOrWhiteSpace(entry.Title) ||
                entry.Title.Length > 256 ||
                entry.Body is null ||
                entry.Body.Length > 2048 ||
                (entry.Kind == NotificationEntryKind.Reminder && entry.DueAt is null) ||
                (entry.Kind == NotificationEntryKind.SystemEvent && (entry.Enabled || !entry.Sent)))
                throw new InvalidDataException("Файл центра уведомлений содержит некорректную запись.");
        }
    }

    private sealed record NotificationCenterDocument(
        string Format,
        int Version,
        List<NotificationCenterEntry>? Entries);
}

internal sealed class NotificationCenterService
{
    private static readonly TimeSpan DeliveryGracePeriod = TimeSpan.FromMinutes(1);
    private readonly NotificationCenterStore _store;
    private List<NotificationCenterEntry> _entries = [];
    private bool _initialized;

    public NotificationCenterService(NotificationCenterStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public event EventHandler? Changed;

    public string? LastError { get; private set; }

    public bool IsAvailable => _initialized;

    public IReadOnlyList<NotificationCenterEntry> Entries =>
        _entries.OrderByDescending(entry => entry.CreatedAt).ToArray();

    public int UnreadCount => _entries.Count(entry => !entry.IsRead);

    public void Initialize()
    {
        try
        {
            _entries = _store.Load().ToList();
            _initialized = true;
            LastError = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _entries = [];
            _initialized = false;
            LastError = $"Не удалось загрузить центр уведомлений: {ex.Message}";
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public NotificationCenterEntry AddReminder(string title, string body, DateTimeOffset dueAt, DateTimeOffset nowUtc)
    {
        EnsureReady();
        title = NormalizeText(title, 256, nameof(title));
        body = NormalizeOptionalText(body, 2048);
        var now = nowUtc.ToUniversalTime();
        if (dueAt.ToUniversalTime() <= now)
            throw new ArgumentOutOfRangeException(nameof(dueAt), "Дата и время напоминания должны быть в будущем.");

        var entry = new NotificationCenterEntry(
            $"reminder-{Guid.NewGuid():N}",
            NotificationEntryKind.Reminder,
            title,
            body,
            now,
            dueAt,
            false,
            true,
            false,
            null);
        Persist(_entries.Append(entry).ToList());
        return entry;
    }

    public void AddSystemEvent(string title, string body, DateTimeOffset createdAt)
    {
        EnsureReady();
        var entry = new NotificationCenterEntry(
            $"event-{Guid.NewGuid():N}",
            NotificationEntryKind.SystemEvent,
            NormalizeText(title, 256, nameof(title)),
            NormalizeOptionalText(body, 2048),
            createdAt.ToUniversalTime(),
            null,
            false,
            false,
            true,
            null);
        Persist(_entries.Append(entry).ToList());
    }

    public void SyncTrainReminders(
        IReadOnlyCollection<TrainDepartureReminder> reminders,
        TrainReminderScheduler scheduler)
    {
        EnsureReady();
        var updated = _entries.ToDictionary(entry => entry.Id, StringComparer.Ordinal);
        var currentIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reminder in reminders)
        {
            var id = $"train-{reminder.Id}";
            currentIds.Add(id);
            updated.TryGetValue(id, out var existing);
            DateTimeOffset? dueAt = null;
            if (scheduler.IsTimeZoneAvailable)
                dueAt = scheduler.GetNotifyAtLocal(reminder);

            var status = reminder.NotificationSent
                ? "Уведомление отправлено."
                : reminder.Enabled ? "Ожидает отправки." : "Отключено.";
            updated[id] = new NotificationCenterEntry(
                id,
                NotificationEntryKind.TrainReminder,
                reminder.NotificationSent ? "Скоро отправление!" : "Напоминание о поезде",
                $"{reminder.Direction} · поезд {reminder.DepartureTime:HH:mm} · № {reminder.TrainNumber} · " +
                $"за {reminder.LeadMinutes} мин. {status}",
                existing?.CreatedAt ?? DateTimeOffset.UtcNow,
                dueAt,
                existing?.IsRead ?? false,
                reminder.Enabled && !reminder.NotificationSent,
                reminder.NotificationSent,
                reminder.Id);
        }

        foreach (var entry in _entries.Where(entry => entry.Kind == NotificationEntryKind.TrainReminder))
        {
            if (currentIds.Contains(entry.Id))
                continue;
            if (!entry.Sent)
                updated.Remove(entry.Id);
        }

        Persist(updated.Values.ToList());
    }

    public NotificationReminderUpdateResult SetReminderEnabled(string id, bool enabled, DateTimeOffset nowUtc)
    {
        EnsureReady();
        var existing = _entries.FirstOrDefault(entry => entry.Id == id && entry.Kind == NotificationEntryKind.Reminder);
        if (existing is null)
            return NotificationReminderUpdateResult.NotFound;
        if (existing.Sent)
            return NotificationReminderUpdateResult.AlreadySent;
        if (enabled && existing.DueAt is { } dueAt && dueAt <= nowUtc.ToUniversalTime())
            return NotificationReminderUpdateResult.DueTimePassed;
        Persist(_entries.Select(entry => entry.Id == id ? entry with { Enabled = enabled } : entry).ToList());
        return NotificationReminderUpdateResult.Updated;
    }

    public bool MarkRead(string id)
    {
        EnsureReady();
        var existing = _entries.FirstOrDefault(entry => entry.Id == id);
        if (existing is null || existing.IsRead)
            return false;
        Persist(_entries.Select(entry => entry.Id == id ? entry with { IsRead = true } : entry).ToList());
        return true;
    }

    public bool Delete(string id)
    {
        EnsureReady();
        var updated = _entries.Where(entry => entry.Id != id).ToList();
        if (updated.Count == _entries.Count)
            return false;
        Persist(updated);
        return true;
    }

    public int ProcessDue(DateTimeOffset nowUtc, Action<string, string, string> showNotification)
    {
        ArgumentNullException.ThrowIfNull(showNotification);
        if (!_initialized)
            return 0;
        var now = nowUtc.ToUniversalTime();
        var sentCount = 0;
        foreach (var entry in _entries.ToArray())
        {
            if (entry.Kind != NotificationEntryKind.Reminder || !entry.Enabled || entry.Sent || entry.DueAt is null)
                continue;

            var dueAt = entry.DueAt.Value.ToUniversalTime();
            if (dueAt > now)
                continue;
            if (now - dueAt > DeliveryGracePeriod)
            {
                if (!TryPersistDueState(_entries.Select(item =>
                        item.Id == entry.Id ? item with { Enabled = false } : item).ToList()))
                    return sentCount;
                continue;
            }

            var claimed = entry with { Enabled = false, Sent = true };
            if (!TryPersistDueState(_entries.Select(item => item.Id == entry.Id ? claimed : item).ToList()))
                return sentCount;
            try
            {
                showNotification(claimed.Title, claimed.Body, claimed.Id);
                sentCount++;
                LastError = null;
                Changed?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex) when (ex is ToastNotificationDeliveryException or COMException or InvalidOperationException)
            {
                if (!TryPersistDueState(_entries.Select(item => item.Id == entry.Id
                    ? entry with { Enabled = dueAt > now - DeliveryGracePeriod }
                    : item).ToList()))
                    return sentCount;
                LastError = $"Не удалось отправить уведомление «{entry.Title}»: {ex.Message}";
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
        return sentCount;
    }

    private void Persist(List<NotificationCenterEntry> updated)
    {
        try
        {
            _store.Save(updated
                .OrderByDescending(entry => entry.CreatedAt)
                .Take(500)
                .ToArray());
            _entries = updated
                .OrderByDescending(entry => entry.CreatedAt)
                .Take(500)
                .ToList();
            LastError = null;
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            LastError = $"Не удалось сохранить центр уведомлений: {ex.Message}";
            Changed?.Invoke(this, EventArgs.Empty);
            throw;
        }
    }

    private bool TryPersistDueState(List<NotificationCenterEntry> updated)
    {
        try
        {
            Persist(updated);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    private void EnsureReady()
    {
        if (!_initialized)
            throw new InvalidOperationException(LastError ?? "Центр уведомлений не загружен.");
    }

    private static string NormalizeText(string value, int maximumLength, string parameter)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Поле не может быть пустым.", parameter);
        var normalized = value.Trim();
        if (normalized.Length > maximumLength)
            throw new ArgumentOutOfRangeException(parameter, $"Максимальная длина — {maximumLength} символов.");
        return normalized;
    }

    private static string NormalizeOptionalText(string? value, int maximumLength)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length > maximumLength)
            throw new ArgumentOutOfRangeException(nameof(value), $"Максимальная длина — {maximumLength} символов.");
        return normalized;
    }
}
