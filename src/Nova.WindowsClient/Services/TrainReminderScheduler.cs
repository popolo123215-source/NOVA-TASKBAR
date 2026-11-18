using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Nova.WindowsClient.Services;

internal enum TrainReminderOperationResult
{
    Saved,
    NotificationTimePassed,
    DeparturePassed,
    AlreadySent,
    NotFound
}

internal interface ITrainReminderNotifier
{
    void Show(TrainDepartureReminder reminder);
}

internal sealed class TrainReminderNotificationException(string message, Exception innerException)
    : IOException(message, innerException);

internal sealed class TrainReminderScheduler
{
    private static readonly TimeSpan MaximumDeliveryDelay = TimeSpan.FromMinutes(1);
    private readonly TrainReminderStore _store;
    private readonly TimeZoneInfo? _timeZone;
    private List<TrainDepartureReminder> _reminders = [];
    private bool _initialized;

    public TrainReminderScheduler(TrainReminderStore store, TimeZoneInfo? timeZone)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _timeZone = timeZone;
    }

    public event EventHandler? Changed;

    public string? LastError { get; private set; }

    public bool IsAvailable => _initialized;

    public bool IsTimeZoneAvailable => _timeZone is not null;

    public IReadOnlyList<TrainDepartureReminder> Reminders => _reminders.ToArray();

    public int ActiveCount => _reminders.Count(reminder => reminder.Enabled && !reminder.NotificationSent);

    public static bool TryParseLeadMinutes(string? value, out int minutes)
    {
        if (int.TryParse(value?.Trim(), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out minutes) &&
            minutes is >= 1 and <= TrainDepartureReminder.MaximumLeadMinutes)
            return true;

        minutes = 0;
        return false;
    }

    public void Initialize()
    {
        try
        {
            _reminders = _store.Load().ToList();
            _initialized = true;
            LastError = _timeZone is null
                ? "Часовой пояс Europe/Moscow недоступен; напоминания не запущены."
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _reminders = [];
            _initialized = false;
            LastError = $"Не удалось загрузить напоминания: {ex.Message}";
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public TrainReminderOperationResult SetReminder(
        DateOnly departureDate,
        ScheduledTrain train,
        int leadMinutes,
        DateTimeOffset nowUtc)
    {
        EnsureReady();
        ArgumentNullException.ThrowIfNull(train);
        if (leadMinutes is < 1 or > TrainDepartureReminder.MaximumLeadMinutes)
            throw new ArgumentOutOfRangeException(nameof(leadMinutes), "Укажите от 1 до 1440 минут.");

        var id = TrainDepartureReminder.CreateId(departureDate, train);
        var existing = _reminders.FirstOrDefault(reminder => reminder.Id == id);
        if (existing?.NotificationSent == true)
            return TrainReminderOperationResult.AlreadySent;

        var departureAt = GetDepartureAtUtc(departureDate, train.Departure);
        if (departureAt <= nowUtc.ToUniversalTime())
            return TrainReminderOperationResult.DeparturePassed;
        if (departureAt - TimeSpan.FromMinutes(leadMinutes) <= nowUtc.ToUniversalTime())
            return TrainReminderOperationResult.NotificationTimePassed;

        var reminder = new TrainDepartureReminder(
            id,
            TrainDepartureReminder.Route,
            departureDate,
            train.Departure,
            train.Arrival,
            train.ArrivalDayOffset,
            train.TrainNumber,
            leadMinutes,
            true,
            false,
            null);
        var updated = _reminders.Where(item => item.Id != id).Append(reminder).ToList();
        Persist(updated);
        return TrainReminderOperationResult.Saved;
    }

    public TrainReminderOperationResult SetEnabled(string id, bool enabled, DateTimeOffset nowUtc)
    {
        EnsureLoaded();
        var existing = _reminders.FirstOrDefault(reminder => reminder.Id == id);
        if (existing is null)
            return TrainReminderOperationResult.NotFound;
        if (!enabled)
        {
            Persist(_reminders.Select(reminder => reminder.Id == id
                ? reminder with { Enabled = false }
                : reminder).ToList());
            return TrainReminderOperationResult.Saved;
        }
        EnsureReady();
        if (existing.NotificationSent)
            return TrainReminderOperationResult.AlreadySent;

        var departureAt = GetDepartureAtUtc(existing.DepartureDate, existing.DepartureTime);
        if (departureAt <= nowUtc.ToUniversalTime())
            return TrainReminderOperationResult.DeparturePassed;
        if (departureAt - TimeSpan.FromMinutes(existing.LeadMinutes) <= nowUtc.ToUniversalTime())
            return TrainReminderOperationResult.NotificationTimePassed;

        Persist(_reminders.Select(reminder => reminder.Id == id
            ? reminder with { Enabled = true }
            : reminder).ToList());
        return TrainReminderOperationResult.Saved;
    }

    public bool Remove(string id)
    {
        EnsureLoaded();
        var updated = _reminders.Where(reminder => reminder.Id != id).ToList();
        if (updated.Count == _reminders.Count)
            return false;
        Persist(updated);
        return true;
    }

    public DateTimeOffset GetNotifyAtLocal(TrainDepartureReminder reminder)
    {
        EnsureReady();
        var departure = GetDepartureAtUtc(reminder.DepartureDate, reminder.DepartureTime);
        return TimeZoneInfo.ConvertTime(departure - TimeSpan.FromMinutes(reminder.LeadMinutes), _timeZone!);
    }

    public int ProcessDue(DateTimeOffset nowUtc, ITrainReminderNotifier notifier)
    {
        ArgumentNullException.ThrowIfNull(notifier);
        if (!_initialized)
            return 0;
        if (_timeZone is null)
        {
            SetError("Часовой пояс Europe/Moscow недоступен; уведомления не отправлены.");
            return 0;
        }

        var now = nowUtc.ToUniversalTime();
        var sentCount = 0;
        foreach (var reminder in _reminders.ToArray())
        {
            if (!reminder.Enabled || reminder.NotificationSent)
                continue;

            var departureAt = GetDepartureAtUtc(reminder.DepartureDate, reminder.DepartureTime);
            var notifyAt = departureAt - TimeSpan.FromMinutes(reminder.LeadMinutes);
            if (departureAt <= now || now - notifyAt > MaximumDeliveryDelay)
            {
                PersistDuringProcessing(_reminders.Select(item => item.Id == reminder.Id
                    ? item with { Enabled = false }
                    : item).ToList());
                continue;
            }
            if (notifyAt > now)
                continue;

            var claimed = reminder with { NotificationSent = true, NotificationSentAt = now };
            if (!TryPersistDuringProcessing(_reminders.Select(item => item.Id == reminder.Id
                    ? claimed
                    : item).ToList()))
                return sentCount;

            try
            {
                notifier.Show(claimed);
                sentCount++;
                LastError = null;
                Changed?.Invoke(this, EventArgs.Empty);
            }
            catch (TrainReminderNotificationException ex)
            {
                var retry = reminder with { Enabled = departureAt > now };
                TryPersistDuringProcessing(_reminders.Select(item => item.Id == reminder.Id
                    ? retry
                    : item).ToList());
                SetError(ex.Message);
            }
        }

        return sentCount;
    }

    public DateTimeOffset GetDepartureAtUtc(DateOnly departureDate, TimeOnly departureTime)
    {
        EnsureReady();
        var local = DateTime.SpecifyKind(departureDate.ToDateTime(departureTime), DateTimeKind.Unspecified);
        if (_timeZone!.IsInvalidTime(local))
            throw new InvalidOperationException("Время отправления не существует в часовом поясе Europe/Moscow.");
        if (_timeZone.IsAmbiguousTime(local))
        {
            var earliestOffset = _timeZone.GetAmbiguousTimeOffsets(local).Max();
            return new DateTimeOffset(local, earliestOffset).ToUniversalTime();
        }

        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, _timeZone), TimeSpan.Zero);
    }

    private void PersistDuringProcessing(List<TrainDepartureReminder> updated)
    {
        if (!TryPersistDuringProcessing(updated))
            return;
    }

    private bool TryPersistDuringProcessing(List<TrainDepartureReminder> updated)
    {
        try
        {
            Persist(updated);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            SetError($"Не удалось сохранить состояние напоминания: {ex.Message}");
            return false;
        }
    }

    private void Persist(List<TrainDepartureReminder> updated)
    {
        _store.Save(updated);
        _reminders = updated;
        LastError = _timeZone is null
            ? "Часовой пояс Europe/Moscow недоступен; напоминания не запущены."
            : null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void EnsureReady()
    {
        EnsureLoaded();
        if (_timeZone is null)
            throw new InvalidOperationException("Часовой пояс Europe/Moscow недоступен.");
    }

    private void EnsureLoaded()
    {
        if (!_initialized)
            throw new InvalidOperationException(LastError ?? "Хранилище напоминаний не загружено.");
    }

    private void SetError(string message)
    {
        LastError = message;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
