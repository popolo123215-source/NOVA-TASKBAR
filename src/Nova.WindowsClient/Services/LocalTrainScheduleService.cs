using System.Globalization;
using System.IO;
using System.Text;

namespace Nova.WindowsClient.Services;

internal enum TrainScheduleState
{
    Available,
    PartiallyAvailable,
    NoUpcomingTrains,
    NoScheduleData,
    Unverified,
    NotYetEffective,
    Expired,
    Invalid
}

internal sealed record ScheduledTrain(
    TimeOnly Departure,
    TimeOnly Arrival,
    int ArrivalDayOffset,
    string TrainNumber);

internal sealed record LocalTrainSchedule(
    bool IsVerified,
    bool IsPartial,
    DateOnly? EffectiveFrom,
    DateOnly? EffectiveThrough,
    TimeZoneInfo? TimeZone,
    string? DataNote,
    IReadOnlyList<ScheduledTrain> Weekdays,
    IReadOnlyList<ScheduledTrain> Weekends,
    IReadOnlyList<ScheduledTrain> Saturdays,
    IReadOnlyList<ScheduledTrain> Sundays,
    IReadOnlyList<string> Warnings,
    string? Error);

internal sealed record TrainScheduleView(
    DateTimeOffset? LocalNow,
    bool IsWeekend,
    TrainScheduleState State,
    IReadOnlyList<ScheduledTrain> Upcoming,
    IReadOnlyList<string> Warnings,
    bool IsPartial = false,
    string? DataNote = null);

internal sealed record TrainScheduleDayView(
    DateOnly Date,
    bool IsWeekend,
    TrainScheduleState State,
    IReadOnlyList<ScheduledTrain> Trains,
    IReadOnlyList<string> Warnings,
    bool IsPartial = false,
    string? DataNote = null);

internal static class LocalTrainScheduleService
{
    internal const string TimeZoneId = "Europe/Moscow";
    private const string RouteId = "TSARSKOE_SELO -> KUPCHINO";

    public static LocalTrainSchedule LoadDefault() =>
        Load(Path.Combine(AppContext.BaseDirectory, "assets", "schedules", "tsarskoe_selo_kupchino.txt"));

    public static LocalTrainSchedule Load(string path)
    {
        try
        {
            return Parse(File.ReadAllText(path, Encoding.UTF8));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Invalid($"Не удалось прочитать локальный файл расписания: {ex.Message}");
        }
    }

    public static LocalTrainSchedule Parse(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();
        var warnings = new List<string>();
        var weekdays = new List<ScheduledTrain>();
        var weekends = new List<ScheduledTrain>();
        var saturdays = new List<ScheduledTrain>();
        var sundays = new List<ScheduledTrain>();
        var seenWeekdays = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenWeekends = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenSaturdays = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenSundays = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var section = ScheduleSection.None;
        var hasHeader = false;
        var hasWeekdays = false;
        var hasWeekends = false;
        var hasSaturdays = false;
        var hasSundays = false;
        var lineNumber = 0;

        foreach (var rawLine in content.TrimStart('\uFEFF').Split('\n'))
        {
            lineNumber++;
            var line = rawLine.Trim();
            if (line.Length == 0)
                continue;

            if (line.StartsWith('#'))
            {
                var comment = line[1..].Trim();
                if (comment.Equals("NOVA LOCAL SCHEDULE", StringComparison.OrdinalIgnoreCase))
                {
                    hasHeader = true;
                    continue;
                }

                var separator = comment.IndexOf(':');
                if (separator > 0)
                    metadata[comment[..separator].Trim()] = comment[(separator + 1)..].Trim();
                continue;
            }

            if (line.Equals("[WEEKDAYS]", StringComparison.OrdinalIgnoreCase))
            {
                if (hasWeekdays)
                {
                    warnings.Add($"Строка {lineNumber}: повторная секция WEEKDAYS пропущена.");
                    section = ScheduleSection.None;
                    continue;
                }
                hasWeekdays = true;
                section = ScheduleSection.Weekdays;
                continue;
            }
            if (line.Equals("[WEEKENDS]", StringComparison.OrdinalIgnoreCase))
            {
                if (hasWeekends)
                {
                    warnings.Add($"Строка {lineNumber}: повторная секция WEEKENDS пропущена.");
                    section = ScheduleSection.None;
                    continue;
                }
                hasWeekends = true;
                section = ScheduleSection.Weekends;
                continue;
            }
            if (line.Equals("[SATURDAY]", StringComparison.OrdinalIgnoreCase))
            {
                if (hasSaturdays)
                {
                    warnings.Add($"Строка {lineNumber}: повторная секция SATURDAY пропущена.");
                    section = ScheduleSection.None;
                    continue;
                }
                hasSaturdays = true;
                section = ScheduleSection.Saturday;
                continue;
            }
            if (line.Equals("[SUNDAY]", StringComparison.OrdinalIgnoreCase))
            {
                if (hasSundays)
                {
                    warnings.Add($"Строка {lineNumber}: повторная секция SUNDAY пропущена.");
                    section = ScheduleSection.None;
                    continue;
                }
                hasSundays = true;
                section = ScheduleSection.Sunday;
                continue;
            }
            if (line.StartsWith('['))
            {
                warnings.Add($"Строка {lineNumber}: неизвестная секция пропущена.");
                section = ScheduleSection.None;
                continue;
            }
            if (section == ScheduleSection.None)
            {
                warnings.Add($"Строка {lineNumber}: запись вне секции пропущена.");
                continue;
            }

            var target = section switch
            {
                ScheduleSection.Weekdays => weekdays,
                ScheduleSection.Weekends => weekends,
                ScheduleSection.Saturday => saturdays,
                ScheduleSection.Sunday => sundays,
                _ => throw new InvalidOperationException("Train record has no active schedule section.")
            };
            var seen = section switch
            {
                ScheduleSection.Weekdays => seenWeekdays,
                ScheduleSection.Weekends => seenWeekends,
                ScheduleSection.Saturday => seenSaturdays,
                ScheduleSection.Sunday => seenSundays,
                _ => throw new InvalidOperationException("Train record has no active schedule section.")
            };
            if (!TryParseTrain(line, out var train, out var reason))
            {
                warnings.Add($"Строка {lineNumber}: {reason}");
                continue;
            }

            var key = $"{train.Departure.Ticks}|{train.Arrival.Ticks}|{train.TrainNumber}";
            if (!seen.Add(key))
            {
                warnings.Add($"Строка {lineNumber}: повтор рейса {train.TrainNumber} пропущен.");
                continue;
            }
            target.Add(train);
        }

        RequireMetadata(metadata, "FORMAT_VERSION", "1", errors);
        RequireMetadata(metadata, "ROUTE", RouteId, errors);
        RequireMetadata(metadata, "TIMEZONE", TimeZoneId, errors);
        if (!hasHeader)
            errors.Add("В файле отсутствует заголовок NOVA LOCAL SCHEDULE.");
        if (!hasWeekdays || !hasWeekends)
            errors.Add("В файле должны присутствовать секции WEEKDAYS и WEEKENDS.");

        var dataStatus = metadata.GetValueOrDefault("DATA_STATUS");
        var isVerified = string.Equals(dataStatus, "VERIFIED", StringComparison.OrdinalIgnoreCase);
        var isPartial = string.Equals(dataStatus, "PARTIAL", StringComparison.OrdinalIgnoreCase);
        if (!isVerified && !isPartial &&
            !string.Equals(dataStatus, "UNVERIFIED", StringComparison.OrdinalIgnoreCase))
            errors.Add("В заголовке DATA_STATUS должен быть VERIFIED, PARTIAL или UNVERIFIED.");

        DateOnly? effectiveFrom = null;
        DateOnly? effectiveThrough = null;
        if (isVerified)
        {
            effectiveFrom = ParseDate(metadata.GetValueOrDefault("EFFECTIVE_FROM"));
            effectiveThrough = ParseDate(metadata.GetValueOrDefault("EFFECTIVE_THROUGH"));
            if (effectiveFrom is null || effectiveThrough is null || effectiveThrough < effectiveFrom)
                errors.Add("Для подтверждённого расписания укажите корректные EFFECTIVE_FROM и EFFECTIVE_THROUGH.");
        }
        else if (isPartial)
        {
            var fromText = metadata.GetValueOrDefault("EFFECTIVE_FROM");
            var throughText = metadata.GetValueOrDefault("EFFECTIVE_THROUGH");
            var hasFrom = !IsUnconfirmedDate(fromText);
            var hasThrough = !IsUnconfirmedDate(throughText);
            if (hasFrom != hasThrough)
            {
                errors.Add("Для частичного расписания EFFECTIVE_FROM и EFFECTIVE_THROUGH должны быть заданы вместе.");
            }
            else if (hasFrom)
            {
                effectiveFrom = ParseDate(fromText);
                effectiveThrough = ParseDate(throughText);
                if (effectiveFrom is null || effectiveThrough is null || effectiveThrough < effectiveFrom)
                    errors.Add("Укажите корректные EFFECTIVE_FROM и EFFECTIVE_THROUGH либо оставьте оба значения NOT_CONFIRMED.");
            }
        }

        var timeZone = TryGetTimeZone();
        if (timeZone is null)
            errors.Add($"Часовой пояс {TimeZoneId} недоступен в Windows.");

        return new LocalTrainSchedule(
            isVerified && errors.Count == 0,
            isPartial && errors.Count == 0,
            effectiveFrom,
            effectiveThrough,
            timeZone,
            metadata.GetValueOrDefault("DATA_NOTE"),
            Sort(weekdays),
            Sort(weekends),
            Sort(saturdays),
            Sort(sundays),
            warnings,
            errors.Count == 0 ? null : string.Join(" ", errors));
    }

    public static TrainScheduleView GetUpcoming(LocalTrainSchedule schedule, DateTimeOffset utcNow)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        if (schedule.TimeZone is null)
            return new TrainScheduleView(null, false, TrainScheduleState.Invalid, [], schedule.Warnings);

        var localNow = TimeZoneInfo.ConvertTime(utcNow, schedule.TimeZone);
        var localDate = DateOnly.FromDateTime(localNow.DateTime);
        var day = GetTrainsForDate(schedule, localDate);
        if (day.State is not (TrainScheduleState.Available or TrainScheduleState.PartiallyAvailable))
            return new TrainScheduleView(
                localNow, day.IsWeekend, day.State, [], day.Warnings, day.IsPartial, day.DataNote);

        var currentTime = TimeOnly.FromDateTime(localNow.DateTime);
        var upcoming = day.Trains
            .Where(train => train.Departure > currentTime)
            .OrderBy(train => train.Departure)
            .Take(3)
            .ToArray();
        return new TrainScheduleView(
            localNow,
            day.IsWeekend,
            upcoming.Length == 0
                ? TrainScheduleState.NoUpcomingTrains
                : day.State,
            upcoming,
            day.Warnings,
            day.IsPartial,
            day.DataNote);
    }

    public static TrainScheduleDayView GetTrainsForDate(LocalTrainSchedule schedule, DateOnly localDate)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        var isWeekend = localDate.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
        if (schedule.TimeZone is null || schedule.Error is not null)
            return new TrainScheduleDayView(localDate, isWeekend, TrainScheduleState.Invalid, [], schedule.Warnings);

        var trains = localDate.DayOfWeek switch
        {
            DayOfWeek.Saturday when schedule.Saturdays.Count > 0 => schedule.Saturdays,
            DayOfWeek.Sunday when schedule.Sundays.Count > 0 => schedule.Sundays,
            DayOfWeek.Saturday or DayOfWeek.Sunday => schedule.Weekends,
            _ => schedule.Weekdays
        };
        if (trains.Count == 0)
            return new TrainScheduleDayView(
                localDate, isWeekend, TrainScheduleState.NoScheduleData, [], schedule.Warnings,
                schedule.IsPartial, schedule.DataNote);
        if (!schedule.IsVerified && !schedule.IsPartial)
            return new TrainScheduleDayView(localDate, isWeekend, TrainScheduleState.Unverified, [], schedule.Warnings);
        if (schedule.EffectiveFrom is { } effectiveFrom && localDate < effectiveFrom)
            return new TrainScheduleDayView(
                localDate, isWeekend, TrainScheduleState.NotYetEffective, [], schedule.Warnings,
                schedule.IsPartial, schedule.DataNote);
        if (schedule.EffectiveThrough is { } effectiveThrough && localDate > effectiveThrough)
            return new TrainScheduleDayView(
                localDate, isWeekend, TrainScheduleState.Expired, [], schedule.Warnings,
                schedule.IsPartial, schedule.DataNote);

        return new TrainScheduleDayView(
            localDate,
            isWeekend,
            schedule.IsPartial ? TrainScheduleState.PartiallyAvailable : TrainScheduleState.Available,
            trains,
            schedule.Warnings,
            schedule.IsPartial,
            schedule.DataNote);
    }

    private static LocalTrainSchedule Invalid(string message) =>
        new(false, false, null, null, TryGetTimeZone(), null, [], [], [], [], [], message);

    private static TimeZoneInfo? TryGetTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById("Russian Standard Time");
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                return null;
            }
        }
        catch (InvalidTimeZoneException)
        {
            return null;
        }
    }

    internal static TimeZoneInfo? GetMoscowTimeZone() => TryGetTimeZone();

    private static void RequireMetadata(
        IReadOnlyDictionary<string, string> metadata,
        string key,
        string expected,
        ICollection<string> errors)
    {
        if (!metadata.TryGetValue(key, out var actual) ||
            !actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            errors.Add($"В заголовке отсутствует или некорректен {key}.");
    }

    private static DateOnly? ParseDate(string? value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;

    private static bool IsUnconfirmedDate(string? value) =>
        string.IsNullOrWhiteSpace(value) ||
        value.Equals("NOT_CONFIRMED", StringComparison.OrdinalIgnoreCase);

    private static bool TryParseTrain(string line, out ScheduledTrain train, out string reason)
    {
        train = default!;
        reason = "некорректная запись; ожидается HH:MM | HH:MM | NUMBER.";
        var fields = line.Split('|');
        if (fields.Length != 3)
            return false;

        var departureText = fields[0].Trim();
        var arrivalText = fields[1].Trim();
        var trainNumber = fields[2].Trim();
        if (!TimeOnly.TryParseExact(departureText, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var departure) ||
            !TimeOnly.TryParseExact(arrivalText, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var arrival))
        {
            reason = "время должно быть в формате HH:MM (24 часа).";
            return false;
        }
        if (trainNumber.Length is 0 or > 32 ||
            trainNumber.Any(character => char.IsControl(character) || char.IsWhiteSpace(character) || character == '|'))
        {
            reason = "указан некорректный номер поезда.";
            return false;
        }
        if (arrival == departure)
        {
            reason = "время прибытия не может совпадать со временем отправления.";
            return false;
        }

        train = new ScheduledTrain(departure, arrival, arrival < departure ? 1 : 0, trainNumber);
        reason = "";
        return true;
    }

    private static IReadOnlyList<ScheduledTrain> Sort(IEnumerable<ScheduledTrain> trains) =>
        trains.OrderBy(train => train.Departure).ToArray();

    private enum ScheduleSection
    {
        None,
        Weekdays,
        Weekends,
        Saturday,
        Sunday
    }
}
