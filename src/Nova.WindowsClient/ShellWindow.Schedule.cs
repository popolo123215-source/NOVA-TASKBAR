using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Nova.WindowsClient.Services;

namespace Nova.WindowsClient;

public partial class ShellWindow
{
    private readonly ObservableCollection<TrainScheduleDisplayRow> _trainScheduleRows = [];
    private readonly ObservableCollection<TrainScheduleDisplayRow> _allTrainScheduleRows = [];
    private readonly ObservableCollection<TrainReminderDisplayRow> _trainReminderRows = [];
    private readonly TrainReminderScheduler _trainReminderScheduler;
    private LocalTrainSchedule? _localTrainSchedule;
    private long _lastTrainScheduleMinute = long.MinValue;
    private DateOnly? _scheduleDate;
    private TrainScheduleDisplayRow? _selectedScheduleTrain;
    private SchedulePage _schedulePage;
    private SchedulePage _reminderEditorOrigin;
    private string? _lastReminderSchedulerError;

    private void InitializeTrainScheduleUi()
    {
        ScheduleTrainsList.ItemsSource = _trainScheduleRows;
        ScheduleAllTrainsList.ItemsSource = _allTrainScheduleRows;
        ScheduleRemindersList.ItemsSource = _trainReminderRows;
        _trainReminderScheduler.Changed += TrainReminderScheduler_Changed;
        RefreshTrainReminders();
        ShowSchedulePage(SchedulePage.Main);
    }

    private void RefreshTrainSchedule(bool force = false)
    {
        if (force || _localTrainSchedule is null)
            _localTrainSchedule = LocalTrainScheduleService.LoadDefault();

        var utcNow = DateTimeOffset.UtcNow;
        var minuteKey = utcNow.UtcDateTime.Ticks / TimeSpan.TicksPerMinute;
        if (!force && minuteKey == _lastTrainScheduleMinute)
            return;

        _lastTrainScheduleMinute = minuteKey;
        var schedule = _localTrainSchedule;
        var view = LocalTrainScheduleService.GetUpcoming(schedule, utcNow);
        ScheduleNowText.Text = view.LocalNow is { } localNow
            ? $"Москва · {localNow.ToString("dddd, d MMMM yyyy · HH:mm", CultureInfo.GetCultureInfo("ru-RU"))}"
            : "Москва · время недоступно";
        ScheduleDayTypeText.Text = view.LocalNow?.DayOfWeek switch
        {
            DayOfWeek.Saturday => "СУББОТА",
            DayOfWeek.Sunday => "ВОСКРЕСЕНЬЕ",
            _ => "БУДНИ"
        };

        _trainScheduleRows.Clear();
        if (view.LocalNow is { } localTime)
        {
            var date = DateOnly.FromDateTime(localTime.DateTime);
            foreach (var train in view.Upcoming)
                _trainScheduleRows.Add(CreateScheduleRow(date, train, localTime.DateTime));
            RefreshAllTrains(date, localTime.DateTime);
        }
        else
        {
            _scheduleDate = null;
            _allTrainScheduleRows.Clear();
            ScheduleAllDateText.Text = "Дата недоступна";
            ScheduleAllEmptyStateText.Text = "Не удалось определить московскую дату расписания.";
            ScheduleAllEmptyStateText.Visibility = Visibility.Visible;
        }

        var hasTrains = view.State is TrainScheduleState.Available or TrainScheduleState.PartiallyAvailable;
        ScheduleTrainsList.Visibility = hasTrains ? Visibility.Visible : Visibility.Collapsed;
        ScheduleEmptyStateText.Visibility = hasTrains ? Visibility.Collapsed : Visibility.Visible;
        ScheduleEmptyStateText.Text = view.State switch
        {
            TrainScheduleState.Available or TrainScheduleState.PartiallyAvailable => "",
            TrainScheduleState.NoUpcomingTrains when view.IsPartial =>
                "В доступной части расписания больше нет будущих отправлений; список может быть неполным.",
            TrainScheduleState.NoUpcomingTrains => "На сегодня рейсов больше нет.",
            TrainScheduleState.NoScheduleData => "Ошибка: в локальном файле нет рейсов для этого типа дня.",
            TrainScheduleState.Unverified => "В файле есть записи, но они помечены как UNVERIFIED и не отображаются.",
            TrainScheduleState.NotYetEffective => "Срок действия расписания ещё не начался.",
            TrainScheduleState.Expired => "Срок действия расписания закончился. Данные не используются.",
            TrainScheduleState.Invalid => schedule.Error is null
                ? "Не удалось загрузить локальное расписание."
                : $"Не удалось загрузить локальное расписание: {schedule.Error}",
            _ => "Состояние расписания недоступно."
        };

        var status = view.State switch
        {
            TrainScheduleState.Available => "Показаны ближайшие отправления из локального файла.",
            TrainScheduleState.PartiallyAvailable => "Показаны ближайшие доступные рейсы из частичного расписания.",
            TrainScheduleState.NoUpcomingTrains when view.IsPartial =>
                "В локальном частичном списке больше нет будущих рейсов; это не подтверждает отсутствие отправлений.",
            TrainScheduleState.NoUpcomingTrains => "Расписание проверено; следующие рейсы появятся после смены даты.",
            TrainScheduleState.NoScheduleData => "Ошибка загрузки: в локальном расписании нет рейсов для этого дня.",
            TrainScheduleState.Unverified => "Записи не отображаются, пока файл помечен как UNVERIFIED.",
            TrainScheduleState.NotYetEffective => "Локальный файл содержит будущую дату начала действия.",
            TrainScheduleState.Expired => "Нужны актуальные локальные данные расписания.",
            TrainScheduleState.Invalid => "",
            _ => ""
        };
        if (view.IsPartial)
        {
            var dataNote = string.IsNullOrWhiteSpace(view.DataNote)
                ? "Возможны отличия по конкретной дате; перед поездкой проверьте актуальный график."
                : view.DataNote;
            status = string.IsNullOrEmpty(status) ? dataNote : $"{status} {dataNote}";
        }
        if (view.Warnings.Count > 0)
        {
            var warning = $"Пропущено некорректных записей: {view.Warnings.Count}. {view.Warnings[0]}";
            status = string.IsNullOrEmpty(status) ? warning : $"{status} {warning}";
        }
        ScheduleStatusText.Text = status;
        ScheduleAllTrainsButton.Content = $"Все рейсы сегодня ({_allTrainScheduleRows.Count})";
        ScheduleRemindersButton.Content = $"Напоминания ({_trainReminderScheduler.ActiveCount})";
        RefreshTrainReminders();
    }

    private void RefreshAllTrains(DateOnly date, DateTime localNow)
    {
        if (_scheduleDate is { } previousDate &&
            previousDate != date &&
            (_schedulePage is SchedulePage.AllTrains or SchedulePage.Editor))
            ShowSchedulePage(SchedulePage.Main);
        _scheduleDate = date;
        var day = LocalTrainScheduleService.GetTrainsForDate(_localTrainSchedule!, date);
        ScheduleAllDateText.Text = date.ToDateTime(TimeOnly.MinValue)
            .ToString("dddd, d MMMM yyyy", CultureInfo.GetCultureInfo("ru-RU"));
        _allTrainScheduleRows.Clear();
        if (day.State is TrainScheduleState.Available or TrainScheduleState.PartiallyAvailable)
        {
            foreach (var train in day.Trains)
                _allTrainScheduleRows.Add(CreateScheduleRow(date, train, localNow));
            ScheduleAllEmptyStateText.Visibility = Visibility.Collapsed;
            ScheduleAllTrainsList.Visibility = Visibility.Visible;
        }
        else
        {
            ScheduleAllTrainsList.Visibility = Visibility.Collapsed;
            ScheduleAllEmptyStateText.Visibility = Visibility.Visible;
            ScheduleAllEmptyStateText.Text = day.State switch
            {
                TrainScheduleState.NoScheduleData => "Для этой даты нет локальных рейсов.",
                TrainScheduleState.Unverified => "Данные рейсов не проверены и не доступны для настройки напоминаний.",
                TrainScheduleState.NotYetEffective => "Расписание ещё не вступило в силу.",
                TrainScheduleState.Expired => "Срок действия расписания закончился.",
                TrainScheduleState.Invalid => _localTrainSchedule?.Error ?? "Не удалось загрузить расписание.",
                _ => "Рейсы для этой даты недоступны."
            };
        }
        ScheduleAllTrainsButton.Content = $"Все рейсы сегодня ({day.Trains.Count})";
    }

    private TrainScheduleDisplayRow CreateScheduleRow(DateOnly date, ScheduledTrain train, DateTime localNow)
    {
        var id = TrainDepartureReminder.CreateId(date, train);
        var reminder = _trainReminderScheduler.Reminders.FirstOrDefault(item => item.Id == id);
        var isDeparted = date.ToDateTime(train.Departure) <= localNow;
        var hint = isDeparted
            ? "Отправился"
            : reminder is { Enabled: true, NotificationSent: false }
                ? $"Напоминание · за {reminder.LeadMinutes} мин."
                : reminder is { NotificationSent: true }
                    ? "Уведомление уже отправлено"
                    : reminder is { Enabled: false }
                        ? "Напоминание выключено"
                        : "Нажмите, чтобы настроить";
        return new TrainScheduleDisplayRow(
            train,
            date,
            train.Departure.ToString("HH:mm", CultureInfo.InvariantCulture),
            $"Прибытие · {train.Arrival.ToString("HH:mm", CultureInfo.InvariantCulture)}",
            train.ArrivalDayOffset == 0 ? "" : "На следующий день",
            train.TrainNumber,
            hint,
            isDeparted);
    }

    private void ShowSchedulePage(SchedulePage page)
    {
        _schedulePage = page;
        ScheduleMainView.Visibility = page == SchedulePage.Main ? Visibility.Visible : Visibility.Collapsed;
        ScheduleAllTrainsView.Visibility = page == SchedulePage.AllTrains ? Visibility.Visible : Visibility.Collapsed;
        ScheduleReminderEditorView.Visibility = page == SchedulePage.Editor ? Visibility.Visible : Visibility.Collapsed;
        ScheduleRemindersView.Visibility = page == SchedulePage.Reminders ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ScheduleAllTrains_Click(object sender, RoutedEventArgs e)
    {
        RefreshTrainSchedule();
        ShowSchedulePage(SchedulePage.AllTrains);
    }

    private void ScheduleReminders_Click(object sender, RoutedEventArgs e)
    {
        RefreshTrainReminders();
        ShowSchedulePage(SchedulePage.Reminders);
    }

    private void SchedulePageBack_Click(object sender, RoutedEventArgs e)
    {
        if (_schedulePage == SchedulePage.Editor)
        {
            _selectedScheduleTrain = null;
            ShowSchedulePage(_reminderEditorOrigin);
            return;
        }

        ShowSchedulePage(SchedulePage.Main);
    }

    private void ScheduleTrain_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not TrainScheduleDisplayRow row)
            return;
        OpenTrainReminderEditor(row, _schedulePage);
    }

    private void OpenTrainReminderEditor(TrainScheduleDisplayRow row, SchedulePage origin)
    {
        _selectedScheduleTrain = row;
        _reminderEditorOrigin = origin;
        var id = TrainDepartureReminder.CreateId(row.DepartureDate, row.Train);
        var existing = _trainReminderScheduler.Reminders.FirstOrDefault(reminder => reminder.Id == id);
        ScheduleReminderEditorTitle.Text = existing is null ? "Создать напоминание" : "Изменить напоминание";
        ScheduleReminderTrainText.Text = $"{TrainDepartureReminder.Route}\nПоезд {row.TrainNumber}";
        ScheduleReminderDetailsText.Text =
            $"Отправление · {row.DepartureDate:dd.MM.yyyy} в {row.Departure}\n{row.Arrival}" +
            (row.Train.ArrivalDayOffset == 0 ? "" : " · на следующий день");
        ScheduleReminderCustomMinutesBox.Text = existing?.LeadMinutes.ToString(CultureInfo.InvariantCulture) ?? "";
        var selectedLead = existing?.LeadMinutes ?? 10;
        ScheduleReminderLeadComboBox.SelectedItem = ScheduleReminderLeadComboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => item.Tag?.ToString() == selectedLead.ToString(CultureInfo.InvariantCulture))
            ?? ScheduleReminderLeadComboBox.Items
                .OfType<ComboBoxItem>()
                .First(item => item.Tag?.ToString() == "custom");
        ScheduleReminderStatusText.Text = _trainReminderScheduler.LastError ?? (row.IsDeparted
            ? "Отправление уже прошло; напоминание создать нельзя."
            : existing?.NotificationSent == true
                ? "Уведомление уже отправлено. Повторно оно не отправляется."
                : existing is { Enabled: true }
                    ? $"Сейчас напоминание включено: за {existing.LeadMinutes} мин."
                    : "");
        ScheduleReminderSaveButton.IsEnabled =
            !row.IsDeparted && existing?.NotificationSent != true &&
            _trainReminderScheduler.IsAvailable && _trainReminderScheduler.IsTimeZoneAvailable;
        ScheduleReminderDisableButton.Visibility = existing is { Enabled: true, NotificationSent: false }
            ? Visibility.Visible
            : Visibility.Collapsed;
        ScheduleReminderDeleteButton.Visibility = existing is null ? Visibility.Collapsed : Visibility.Visible;
        UpdateCustomReminderInputVisibility();
        ShowSchedulePage(SchedulePage.Editor);
    }

    private void ScheduleReminderLeadComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateCustomReminderInputVisibility();
    }

    private void UpdateCustomReminderInputVisibility()
    {
        var isCustom = (ScheduleReminderLeadComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "custom";
        ScheduleReminderCustomPanel.Visibility = isCustom ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ScheduleReminderSave_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedScheduleTrain is not { } row)
            return;
        if (!TryReadReminderLeadMinutes(out var leadMinutes, out var error))
        {
            ScheduleReminderStatusText.Text = error;
            return;
        }

        try
        {
            var result = _trainReminderScheduler.SetReminder(
                row.DepartureDate,
                row.Train,
                leadMinutes,
                DateTimeOffset.UtcNow);
            if (result != TrainReminderOperationResult.Saved)
            {
                ScheduleReminderStatusText.Text = GetReminderOperationMessage(result);
                return;
            }

            RefreshTrainSchedule(force: true);
            ScheduleRemindersStatusText.Text = "Напоминание сохранено.";
            ShowSchedulePage(SchedulePage.Reminders);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or JsonException)
        {
            ScheduleReminderStatusText.Text = $"Не удалось сохранить напоминание: {ex.Message}";
        }
    }

    private bool TryReadReminderLeadMinutes(out int minutes, out string error)
    {
        var selected = (ScheduleReminderLeadComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        var text = selected == "custom"
            ? ScheduleReminderCustomMinutesBox.Text.Trim()
            : selected;
        if (!TrainReminderScheduler.TryParseLeadMinutes(text, out minutes))
        {
            error = "Введите целое число минут от 1 до 1440.";
            return false;
        }

        error = "";
        return true;
    }

    private void ScheduleReminderCancel_Click(object sender, RoutedEventArgs e)
    {
        _selectedScheduleTrain = null;
        ShowSchedulePage(_reminderEditorOrigin);
    }

    private void ScheduleReminderDisable_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedScheduleTrain is not { } row)
            return;
        var id = TrainDepartureReminder.CreateId(row.DepartureDate, row.Train);
        ExecuteReminderAction(() => _trainReminderScheduler.SetEnabled(id, false, DateTimeOffset.UtcNow),
            "Напоминание выключено.");
        ShowSchedulePage(SchedulePage.Reminders);
    }

    private void ScheduleReminderDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedScheduleTrain is not { } row)
            return;
        var id = TrainDepartureReminder.CreateId(row.DepartureDate, row.Train);
        ExecuteReminderAction(() => _trainReminderScheduler.Remove(id), "Напоминание удалено.");
        _selectedScheduleTrain = null;
        ShowSchedulePage(SchedulePage.Reminders);
    }

    private void ScheduleReminderEdit_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not TrainReminderDisplayRow row)
            return;
        var timeZone = LocalTrainScheduleService.GetMoscowTimeZone();
        if (timeZone is null)
        {
            ScheduleRemindersStatusText.Text = "Часовой пояс Europe/Moscow недоступен.";
            return;
        }
        var reminder = row.Reminder;
        var train = new ScheduledTrain(
            reminder.DepartureTime,
            reminder.ArrivalTime,
            reminder.ArrivalDayOffset,
            reminder.TrainNumber);
        var localNow = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, timeZone);
        OpenTrainReminderEditor(
            CreateScheduleRow(reminder.DepartureDate, train, localNow.DateTime),
            SchedulePage.Reminders);
    }

    private void ScheduleReminderToggle_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not TrainReminderDisplayRow row)
            return;
        var result = TrainReminderOperationResult.NotFound;
        ExecuteReminderAction(
            () => result = _trainReminderScheduler.SetEnabled(
                row.Reminder.Id,
                !row.Reminder.Enabled,
                DateTimeOffset.UtcNow),
            "Состояние напоминания обновлено.");
        if (result != TrainReminderOperationResult.Saved)
            ScheduleRemindersStatusText.Text = GetReminderOperationMessage(result);
    }

    private void ScheduleReminderRemove_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not TrainReminderDisplayRow row)
            return;
        var removed = false;
        ExecuteReminderAction(() => removed = _trainReminderScheduler.Remove(row.Reminder.Id), "Напоминание удалено.");
        if (!removed)
            ScheduleRemindersStatusText.Text = "Напоминание больше не найдено.";
    }

    private void ExecuteReminderAction(Action action, string successMessage)
    {
        try
        {
            action();
            ScheduleRemindersStatusText.Text = successMessage;
            RefreshTrainReminders();
            RefreshTrainSchedule(force: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or JsonException)
        {
            ScheduleRemindersStatusText.Text = $"Операция не выполнена: {ex.Message}";
        }
    }

    private void RefreshTrainReminders()
    {
        if (ScheduleRemindersList is null)
            return;

        _trainReminderRows.Clear();
        foreach (var reminder in _trainReminderScheduler.Reminders
            .OrderBy(item => item.DepartureDate)
            .ThenBy(item => item.DepartureTime))
        {
            var notifyAt = _trainReminderScheduler.IsTimeZoneAvailable
                ? _trainReminderScheduler.GetNotifyAtLocal(reminder)
                : (DateTimeOffset?)null;
            var status = reminder.NotificationSent
                ? "Уведомление отправлено"
                : reminder.Enabled
                    ? "Активно"
                    : "Отключено";
            var canToggle = !reminder.NotificationSent;
            _trainReminderRows.Add(new TrainReminderDisplayRow(
                reminder,
                $"{reminder.DepartureDate:dd.MM.yyyy} · {reminder.DepartureTime:HH:mm} · поезд {reminder.TrainNumber}",
                notifyAt is { } time
                    ? $"Оповестить за {reminder.LeadMinutes} мин. · {time:dd.MM HH:mm}"
                    : $"Оповестить за {reminder.LeadMinutes} мин. · время недоступно",
                status,
                reminder.Enabled ? "Отключить" : "Включить",
                canToggle));
        }

        ScheduleRemindersCountText.Text = $"{_trainReminderScheduler.ActiveCount} активных";
        ScheduleRemindersEmptyStateText.Visibility = _trainReminderRows.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        ScheduleRemindersEmptyStateText.Text = _trainReminderScheduler.LastError ??
            "Пока нет сохранённых напоминаний.";
        if (_trainReminderScheduler.LastError is { } schedulerError)
        {
            ScheduleRemindersStatusText.Text = schedulerError;
            _lastReminderSchedulerError = schedulerError;
        }
        else
        {
            if (_lastReminderSchedulerError is not null &&
                ScheduleRemindersStatusText.Text == _lastReminderSchedulerError)
                ScheduleRemindersStatusText.Text = "Уведомления работают и при скрытой панели NOVA.";
            _lastReminderSchedulerError = null;
            if (string.IsNullOrWhiteSpace(ScheduleRemindersStatusText.Text))
                ScheduleRemindersStatusText.Text = "Уведомления работают и при скрытой панели NOVA.";
        }
        ScheduleRemindersButton.Content = $"Напоминания ({_trainReminderScheduler.ActiveCount})";
    }

    private void TrainReminderScheduler_Changed(object? sender, EventArgs e)
    {
        RefreshTrainReminders();
        if (_scheduleDate is { } date && _localTrainSchedule is not null)
        {
            var timeZone = _localTrainSchedule.TimeZone ?? LocalTrainScheduleService.GetMoscowTimeZone();
            if (timeZone is not null)
            {
                var localNow = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, timeZone);
                RefreshAllTrains(date, localNow.DateTime);
            }
        }
    }

    private static string GetReminderOperationMessage(TrainReminderOperationResult result) => result switch
    {
        TrainReminderOperationResult.NotificationTimePassed =>
            "Время оповещения уже прошло. Просроченное уведомление не будет показано.",
        TrainReminderOperationResult.DeparturePassed =>
            "Время отправления уже прошло; напоминание не создано.",
        TrainReminderOperationResult.AlreadySent =>
            "Уведомление по этому рейсу уже отправлено.",
        TrainReminderOperationResult.NotFound =>
            "Напоминание больше не найдено.",
        _ => ""
    };

    private sealed record TrainScheduleDisplayRow(
        ScheduledTrain Train,
        DateOnly DepartureDate,
        string Departure,
        string Arrival,
        string ArrivalDayNote,
        string TrainNumber,
        string ReminderHint,
        bool IsDeparted);

    private sealed record TrainReminderDisplayRow(
        TrainDepartureReminder Reminder,
        string DepartureText,
        string NotificationText,
        string StatusText,
        string ToggleText,
        bool CanToggle);

    private enum SchedulePage
    {
        Main,
        AllTrains,
        Editor,
        Reminders
    }
}
