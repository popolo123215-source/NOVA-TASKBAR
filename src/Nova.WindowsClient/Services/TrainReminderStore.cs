using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Nova.WindowsClient.Services;

internal sealed record TrainDepartureReminder(
    string Id,
    string Direction,
    DateOnly DepartureDate,
    TimeOnly DepartureTime,
    TimeOnly ArrivalTime,
    int ArrivalDayOffset,
    string TrainNumber,
    int LeadMinutes,
    bool Enabled,
    bool NotificationSent,
    DateTimeOffset? NotificationSentAt)
{
    internal const string Route = "Царское Село → Купчино";
    internal const int MaximumLeadMinutes = 1440;

    public static string CreateId(DateOnly date, ScheduledTrain train)
    {
        var key = string.Join('|',
            Route,
            date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            train.Departure.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture),
            train.Arrival.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture),
            train.TrainNumber);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
    }
}

internal sealed class TrainReminderStore
{
    private const string FormatName = "NOVA-TRAIN-REMINDERS";
    private const int CurrentVersion = 1;
    private const long MaximumFileBytes = 1_048_576;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _filePath;

    public TrainReminderStore(string stateDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        _filePath = Path.Combine(stateDirectory, "train-reminders.json");
    }

    public IReadOnlyList<TrainDepartureReminder> Load()
    {
        if (!File.Exists(_filePath))
            return [];
        if (new FileInfo(_filePath).Length > MaximumFileBytes)
            throw new InvalidDataException("Файл напоминаний превышает допустимый размер.");

        var document = JsonSerializer.Deserialize<TrainReminderDocument>(
            File.ReadAllText(_filePath),
            JsonOptions);
        Validate(document);
        return document!.Reminders!.ToArray();
    }

    public void Save(IReadOnlyCollection<TrainDepartureReminder> reminders)
    {
        ArgumentNullException.ThrowIfNull(reminders);
        var document = new TrainReminderDocument(FormatName, CurrentVersion, reminders.ToList());
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

    private static void Validate(TrainReminderDocument? document)
    {
        if (document is null ||
            !string.Equals(document.Format, FormatName, StringComparison.Ordinal) ||
            document.Version != CurrentVersion ||
            document.Reminders is null)
            throw new InvalidDataException("Файл напоминаний имеет неизвестный формат или версию.");

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reminder in document.Reminders)
        {
            if (reminder is null ||
                !string.Equals(reminder.Direction, TrainDepartureReminder.Route, StringComparison.Ordinal) ||
                reminder.LeadMinutes is < 1 or > TrainDepartureReminder.MaximumLeadMinutes ||
                reminder.ArrivalDayOffset is < 0 or > 1 ||
                reminder.ArrivalTime == reminder.DepartureTime ||
                reminder.ArrivalDayOffset != (reminder.ArrivalTime < reminder.DepartureTime ? 1 : 0) ||
                string.IsNullOrWhiteSpace(reminder.TrainNumber) ||
                reminder.TrainNumber.Length > 32 ||
                reminder.TrainNumber.Any(character =>
                    char.IsControl(character) || char.IsWhiteSpace(character) || character == '|') ||
                !ids.Add(reminder.Id) ||
                !string.Equals(
                    reminder.Id,
                    TrainDepartureReminder.CreateId(reminder.DepartureDate, new ScheduledTrain(
                        reminder.DepartureTime,
                        reminder.ArrivalTime,
                        reminder.ArrivalDayOffset,
                        reminder.TrainNumber)),
                    StringComparison.Ordinal) ||
                reminder.NotificationSent != (reminder.NotificationSentAt is not null))
                throw new InvalidDataException("Файл содержит некорректное или повторяющееся напоминание.");
        }
    }

    private sealed record TrainReminderDocument(
        string Format,
        int Version,
        List<TrainDepartureReminder>? Reminders);
}
