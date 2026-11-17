using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using Nova.WindowsClient.Services;

namespace Nova.WindowsClient;

public partial class ShellWindow
{
    private readonly ObservableCollection<TrainScheduleDisplayRow> _trainScheduleRows = [];
    private LocalTrainSchedule? _localTrainSchedule;
    private long _lastTrainScheduleMinute = long.MinValue;

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
        ScheduleTrainsList.ItemsSource ??= _trainScheduleRows;

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
        foreach (var train in view.Upcoming)
        {
            _trainScheduleRows.Add(new TrainScheduleDisplayRow(
                train.Departure.ToString("HH:mm", CultureInfo.InvariantCulture),
                $"Прибытие · {train.Arrival.ToString("HH:mm", CultureInfo.InvariantCulture)}",
                train.ArrivalDayOffset == 0 ? "" : "На следующий день",
                train.TrainNumber));
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
    }

    private sealed record TrainScheduleDisplayRow(
        string Departure,
        string Arrival,
        string ArrivalDayNote,
        string TrainNumber);
}
