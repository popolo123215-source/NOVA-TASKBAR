using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using Nova.WindowsClient.Services;

namespace Nova.WindowsClient;

public partial class ShellWindow
{
    private readonly ObservableCollection<NotificationCenterRow> _notificationRows = [];
    private NotificationCenterService _notificationCenter = null!;

    private void InitializeNotificationCenterUi(NotificationCenterService notificationCenter)
    {
        _notificationCenter = notificationCenter;
        NotificationReminderDatePicker.SelectedDate = DateTime.Today;
        NotificationReminderTimeBox.Text = DateTime.Now.AddMinutes(10).ToString("HH:mm", CultureInfo.InvariantCulture);
        NotificationCenterList.ItemsSource = _notificationRows;
        _notificationCenter.Changed += NotificationCenter_Changed;
        UpdateNotificationCenterUi();
    }

    private void DisposeNotificationCenterUi() =>
        _notificationCenter.Changed -= NotificationCenter_Changed;

    private void NotificationCenter_Changed(object? sender, EventArgs e)
    {
        if (_islandDisposed)
            return;
        Dispatcher.BeginInvoke(new Action(UpdateNotificationCenterUi), System.Windows.Threading.DispatcherPriority.Background);
    }

    private void UpdateNotificationCenterUi()
    {
        if (_islandDisposed)
            return;
        _notificationRows.Clear();
        foreach (var entry in _notificationCenter.Entries)
            _notificationRows.Add(new NotificationCenterRow(entry));
        NotificationCenterUnreadText.Text = $"{_notificationCenter.UnreadCount} непрочитанных";
        NotificationCenterStatusText.Text = _notificationCenter.LastError ??
            "История хранится локально. Напоминания проверяются и при скрытой панели.";
    }

    private void NotificationAddReminder_Click(object sender, RoutedEventArgs e)
    {
        if (NotificationReminderDatePicker.SelectedDate is not { } selectedDate ||
            !TimeOnly.TryParseExact(
                NotificationReminderTimeBox.Text.Trim(),
                "HH:mm",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var time))
        {
            NotificationCenterStatusText.Text = "Укажите дату и время в формате ЧЧ:ММ.";
            return;
        }

        var localDateTime = DateTime.SpecifyKind(selectedDate.Date + time.ToTimeSpan(), DateTimeKind.Unspecified);
        if (TimeZoneInfo.Local.IsInvalidTime(localDateTime))
        {
            NotificationCenterStatusText.Text = "Это локальное время недоступно из-за перехода часов. Выберите другое время.";
            return;
        }

        var offset = TimeZoneInfo.Local.IsAmbiguousTime(localDateTime)
            ? TimeZoneInfo.Local.GetAmbiguousTimeOffsets(localDateTime).Max()
            : TimeZoneInfo.Local.GetUtcOffset(localDateTime);
        try
        {
            var entry = _notificationCenter.AddReminder(
                NotificationReminderTitleBox.Text,
                NotificationReminderBodyBox.Text,
                new DateTimeOffset(localDateTime, offset),
                DateTimeOffset.UtcNow);
            NotificationReminderTitleBox.Clear();
            NotificationReminderBodyBox.Clear();
            NotificationCenterStatusText.Text = $"Напоминание сохранено на {entry.DueAt!.Value.LocalDateTime:dd.MM.yyyy HH:mm}.";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            NotificationCenterStatusText.Text = ex.Message;
        }
    }

    private void NotificationMarkRead_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: NotificationCenterRow row })
            return;
        _notificationCenter.MarkRead(row.Entry.Id);
    }

    private void NotificationToggleReminder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: NotificationCenterRow row })
            return;
        if (row.Entry.Kind == NotificationEntryKind.Reminder)
        {
            var reminderResult = _notificationCenter.SetReminderEnabled(
                row.Entry.Id,
                !row.Entry.Enabled,
                DateTimeOffset.UtcNow);
            NotificationCenterStatusText.Text = reminderResult switch
            {
                NotificationReminderUpdateResult.Updated => "Состояние напоминания изменено.",
                NotificationReminderUpdateResult.DueTimePassed => "Время напоминания уже прошло; оно не включено.",
                NotificationReminderUpdateResult.AlreadySent => "Это напоминание уже сработало.",
                _ => "Напоминание не найдено."
            };
            return;
        }
        if (row.Entry.Kind != NotificationEntryKind.TrainReminder || row.Entry.RelatedId is null)
            return;

        var result = _trainReminderScheduler.SetEnabled(
            row.Entry.RelatedId,
            !row.Entry.Enabled,
            DateTimeOffset.UtcNow);
        NotificationCenterStatusText.Text = result switch
        {
            TrainReminderOperationResult.Saved => "Состояние напоминания изменено.",
            TrainReminderOperationResult.NotificationTimePassed => "Время уведомления уже прошло; напоминание не включено.",
            TrainReminderOperationResult.DeparturePassed => "Отправление уже прошло.",
            TrainReminderOperationResult.AlreadySent => "Уведомление уже было отправлено.",
            _ => _trainReminderScheduler.LastError ?? "Напоминание не найдено."
        };
    }

    private void NotificationDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: NotificationCenterRow row })
            return;
        if (row.Entry.Kind == NotificationEntryKind.TrainReminder && row.Entry.RelatedId is { } reminderId)
            _trainReminderScheduler.Remove(reminderId);
        _notificationCenter.Delete(row.Entry.Id);
    }

    private sealed class NotificationCenterRow
    {
        private readonly NotificationCenterEntry _entry;

        public NotificationCenterRow(NotificationCenterEntry entry) => _entry = entry;

        public NotificationCenterEntry Entry => _entry;
        public string Id => _entry.Id;
        public string Title => _entry.Title;
        public string Body => _entry.Body;
        public bool IsRead => _entry.IsRead;
        public bool IsUnread => !_entry.IsRead;
        public bool CanToggle => !_entry.Sent &&
            _entry.Kind is NotificationEntryKind.Reminder or NotificationEntryKind.TrainReminder;
        public string ToggleText => _entry.Enabled ? "Отключить" : "Включить";
        public string ReadText => _entry.IsRead ? "Прочитано" : "Прочитано ✓";
        public string KindText => _entry.Kind switch
        {
            NotificationEntryKind.Reminder => "Напоминание",
            NotificationEntryKind.TrainReminder => "Расписание",
            _ => "Событие NOVA"
        };
        public string DateText => _entry.DueAt is { } due
            ? $"{(_entry.Sent ? "Отправлено" : _entry.Enabled ? "Запланировано" : "Отключено")} · {GetDueDateText(due)}"
            : _entry.CreatedAt.LocalDateTime.ToString("dd.MM.yyyy HH:mm", CultureInfo.GetCultureInfo("ru-RU"));

        private string GetDueDateText(DateTimeOffset due)
        {
            var localDue = _entry.Kind == NotificationEntryKind.TrainReminder &&
                LocalTrainScheduleService.GetMoscowTimeZone() is { } moscow
                    ? TimeZoneInfo.ConvertTime(due, moscow)
                    : due.ToLocalTime();
            return localDue.ToString("dd.MM.yyyy HH:mm", CultureInfo.GetCultureInfo("ru-RU"));
        }

    }
}
