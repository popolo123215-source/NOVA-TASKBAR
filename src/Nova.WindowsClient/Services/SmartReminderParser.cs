using System.Globalization;
using System.Text.RegularExpressions;

namespace Nova.WindowsClient.Services;

internal static partial class SmartReminderParser
{
    private static readonly Regex TimePattern = new(
        @"(?<!\d)(?<hour>[01]?\d|2[0-3]):(?<minute>[0-5]\d)(?!\d)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex RelativePattern = new(
        @"через\s+(?<amount>\d{1,4})\s*(?<unit>секунд(?:у|ы)?|сек\.?|минут(?:у|ы)?|мин\.?|час(?:а|ов)?|дн(?:я|ей)?|день)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex DatePattern = new(
        @"(?<!\d)(?<day>\d{1,2})\.(?<month>\d{1,2})(?:\.(?<year>\d{4}))?(?!\d)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CleanupPattern = new(
        @"\b(?:сегодня|завтра|послезавтра|ежедневно|каждый\s+день|еженедельно|каждую?\s+неделю|ежемесячно|каждый\s+месяц|кажд(?:ый|ую)\s+(?:понедельник|вторник|среду|четверг|пятницу|субботу|воскресенье)|через\s+\d{1,4}\s*(?:секунд(?:у|ы)?|сек\.?|минут(?:у|ы)?|мин\.?|час(?:а|ов)?|дн(?:я|ей)?|день)|\d{1,2}\.\d{1,2}(?:\.\d{4})?|\d{1,2}:\d{2})\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex LeadingReminderWords = new(
        @"^\s*(?:(?:напомни(?:ть)?|напомните)\s*(?:мне\s*)?)?(?:в\s+)?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static bool TryParse(
        string? text,
        DateTimeOffset now,
        TimeZoneInfo timeZone,
        out ParsedSmartReminder? reminder,
        out string error)
    {
        reminder = null;
        if (string.IsNullOrWhiteSpace(text) || text.Length > 1024)
        {
            error = "Введите фразу длиной от 1 до 1024 символов.";
            return false;
        }

        ArgumentNullException.ThrowIfNull(timeZone);
        var localNow = TimeZoneInfo.ConvertTime(now, timeZone);
        var localDate = DateOnly.FromDateTime(localNow.DateTime);
        var recurrence = ParseRecurrence(text);
        var timeMatch = TimePattern.Match(text);
        var hasTime = timeMatch.Success;
        var clock = hasTime
            ? new TimeOnly(
                int.Parse(timeMatch.Groups["hour"].Value, CultureInfo.InvariantCulture),
                int.Parse(timeMatch.Groups["minute"].Value, CultureInfo.InvariantCulture))
            : (TimeOnly?)null;

        var relative = RelativePattern.Match(text);
        DateTimeOffset dueAt;
        if (relative.Success)
        {
            var amount = int.Parse(relative.Groups["amount"].Value, CultureInfo.InvariantCulture);
            var unit = relative.Groups["unit"].Value;
            if (amount < 1)
            {
                error = "Укажите положительный интервал.";
                return false;
            }

            if (unit.StartsWith("дн", StringComparison.OrdinalIgnoreCase) || unit == "день")
            {
                if (amount > 3650)
                {
                    error = "Интервал не может превышать 3650 дней.";
                    return false;
                }
                var dueDate = localDate.AddDays(amount);
                dueAt = clock is { } dailyClock
                    ? CreateLocalDateTime(dueDate, dailyClock, timeZone)
                    : CreateLocalDateTime(
                        dueDate,
                        TimeOnly.FromDateTime(localNow.DateTime),
                        timeZone);
            }
            else
            {
                if (amount > 100_000)
                {
                    error = "Интервал слишком велик.";
                    return false;
                }
                var duration = unit.StartsWith("сек", StringComparison.OrdinalIgnoreCase)
                    ? TimeSpan.FromSeconds(amount)
                    : unit.StartsWith("мин", StringComparison.OrdinalIgnoreCase)
                        ? TimeSpan.FromMinutes(amount)
                        : TimeSpan.FromHours(amount);
                dueAt = now.ToUniversalTime().Add(duration);
            }
            if (recurrence != ProductivityRecurrence.None)
            {
                error = "Для повторяющегося напоминания задайте время суток, например «каждый день в 09:00».";
                return false;
            }
        }
        else
        {
            if (clock is null)
            {
                error = "Укажите время (например, 09:30) или интервал «через 20 минут».";
                return false;
            }

            var date = ParseDate(text, localDate, clock.Value, recurrence, timeZone, now, out var dateError);
            if (date is null)
            {
                error = dateError;
                return false;
            }
            dueAt = CreateLocalDateTime(date.Value, clock.Value, timeZone);
            if (dueAt <= now.ToUniversalTime())
            {
                error = "Указанное время уже прошло. Выберите будущее время.";
                return false;
            }
        }

        var title = LeadingReminderWords.Replace(CleanupPattern.Replace(text, " "), string.Empty)
            .Trim(' ', ',', '.', ':', ';', '-', '—');
        if (title.Length == 0)
            title = "Напоминание";
        if (title.Length > 160)
            title = title[..160].Trim();

        reminder = new ParsedSmartReminder(title, dueAt, recurrence, timeZone.Id);
        error = string.Empty;
        return true;
    }

    private static ProductivityRecurrence ParseRecurrence(string text)
    {
        if (text.Contains("ежедневно", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("каждый день", StringComparison.OrdinalIgnoreCase))
            return ProductivityRecurrence.Daily;
        if (text.Contains("еженедельно", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("каждую неделю", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("каждый неделю", StringComparison.OrdinalIgnoreCase) ||
            WeekdayPattern().IsMatch(text))
            return ProductivityRecurrence.Weekly;
        if (text.Contains("ежемесячно", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("каждый месяц", StringComparison.OrdinalIgnoreCase))
            return ProductivityRecurrence.Monthly;
        return ProductivityRecurrence.None;
    }

    private static DateOnly? ParseDate(
        string text,
        DateOnly today,
        TimeOnly time,
        ProductivityRecurrence recurrence,
        TimeZoneInfo timeZone,
        DateTimeOffset now,
        out string error)
    {
        error = string.Empty;
        if (text.Contains("послезавтра", StringComparison.OrdinalIgnoreCase))
            return today.AddDays(2);
        if (text.Contains("завтра", StringComparison.OrdinalIgnoreCase))
            return today.AddDays(1);
        if (text.Contains("сегодня", StringComparison.OrdinalIgnoreCase))
            return today;

        var dateMatch = DatePattern.Match(text);
        if (dateMatch.Success)
        {
            var day = int.Parse(dateMatch.Groups["day"].Value, CultureInfo.InvariantCulture);
            var month = int.Parse(dateMatch.Groups["month"].Value, CultureInfo.InvariantCulture);
            var year = dateMatch.Groups["year"].Success
                ? int.Parse(dateMatch.Groups["year"].Value, CultureInfo.InvariantCulture)
                : today.Year;
            try
            {
                var date = new DateOnly(year, month, day);
                if (!dateMatch.Groups["year"].Success && date < today)
                    date = date.AddYears(1);
                error = string.Empty;
                return date;
            }
            catch (ArgumentOutOfRangeException)
            {
                error = "Указана некорректная дата.";
                return null;
            }
        }

        var weekday = FindWeekday(text);
        if (weekday is { } dayOfWeek)
        {
            var daysAhead = ((int)dayOfWeek - (int)today.DayOfWeek + 7) % 7;
            var date = today.AddDays(daysAhead);
            var candidate = CreateLocalDateTime(date, time, timeZone);
            if (candidate <= now.ToUniversalTime())
                date = date.AddDays(7);
            return date;
        }

        if (recurrence == ProductivityRecurrence.Daily ||
            recurrence == ProductivityRecurrence.Weekly ||
            recurrence == ProductivityRecurrence.Monthly)
        {
            var candidate = CreateLocalDateTime(today, time, timeZone);
            if (candidate <= now.ToUniversalTime())
            {
                if (recurrence == ProductivityRecurrence.Daily)
                    return today.AddDays(1);
                if (recurrence == ProductivityRecurrence.Weekly)
                    return today.AddDays(7);
                var month = today.Month == 12 ? new DateOnly(today.Year + 1, 1, 1) : new DateOnly(today.Year, today.Month + 1, 1);
                return new DateOnly(month.Year, month.Month, Math.Min(today.Day, DateTime.DaysInMonth(month.Year, month.Month)));
            }
            return today;
        }

        return today;
    }

    private static DateTimeOffset CreateLocalDateTime(DateOnly date, TimeOnly time, TimeZoneInfo timeZone)
    {
        var local = DateTime.SpecifyKind(date.ToDateTime(time), DateTimeKind.Unspecified);
        if (timeZone.IsInvalidTime(local))
        {
            for (var minute = 1; minute <= 180 && timeZone.IsInvalidTime(local); minute++)
                local = local.AddMinutes(1);
            if (timeZone.IsInvalidTime(local))
                throw new InvalidOperationException("Время не существует в выбранном часовом поясе.");
        }

        var offset = timeZone.IsAmbiguousTime(local)
            ? timeZone.GetAmbiguousTimeOffsets(local).Max()
            : timeZone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }

    private static DayOfWeek? FindWeekday(string text)
    {
        if (text.Contains("понедельник", StringComparison.OrdinalIgnoreCase)) return DayOfWeek.Monday;
        if (text.Contains("вторник", StringComparison.OrdinalIgnoreCase)) return DayOfWeek.Tuesday;
        if (text.Contains("среду", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("среда", StringComparison.OrdinalIgnoreCase)) return DayOfWeek.Wednesday;
        if (text.Contains("четверг", StringComparison.OrdinalIgnoreCase)) return DayOfWeek.Thursday;
        if (text.Contains("пятницу", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("пятница", StringComparison.OrdinalIgnoreCase)) return DayOfWeek.Friday;
        if (text.Contains("субботу", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("суббота", StringComparison.OrdinalIgnoreCase)) return DayOfWeek.Saturday;
        if (text.Contains("воскресенье", StringComparison.OrdinalIgnoreCase)) return DayOfWeek.Sunday;
        return null;
    }

    [GeneratedRegex(@"кажд(?:ый|ую)\s+(?:понедельник|вторник|среду|четверг|пятницу|субботу|воскресенье)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex WeekdayPattern();
}
