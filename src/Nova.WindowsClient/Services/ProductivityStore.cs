using System.IO;
using System.Text.Json;

namespace Nova.WindowsClient.Services;

internal sealed class ProductivityStore
{
    private const string FormatName = "NOVA-PRODUCTIVITY";
    private const int CurrentVersion = 1;
    private const long MaximumFileBytes = 8 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _filePath;

    public ProductivityStore(string stateDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        _filePath = Path.Combine(stateDirectory, "productivity.json");
    }

    public ProductivityState Load()
    {
        if (!File.Exists(_filePath))
            return ProductivityState.Empty;
        if (new FileInfo(_filePath).Length > MaximumFileBytes)
            throw new InvalidDataException("Файл Productivity превышает допустимый размер.");

        var document = JsonSerializer.Deserialize<ProductivityDocument>(
            File.ReadAllText(_filePath),
            JsonOptions);
        if (document is null ||
            !string.Equals(document.Format, FormatName, StringComparison.Ordinal) ||
            document.Version is < 0 or > CurrentVersion)
            throw new InvalidDataException("Файл Productivity имеет неизвестный формат или версию.");

        var state = document.ToState();
        Validate(state);
        if (document.Version != CurrentVersion)
            Save(state);
        return state;
    }

    public void Save(ProductivityState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        Validate(state);
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        var temporaryPath = $"{_filePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                8192,
                FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, ProductivityDocument.FromState(state), JsonOptions);
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

    private static void Validate(ProductivityState state)
    {
        ValidateCollection(state.Tasks, 2000, "задач");
        ValidateCollection(state.Notes, 2000, "заметок");
        ValidateCollection(state.Timers, 100, "таймеров");
        ValidateCollection(state.Reminders, 1000, "напоминаний");
        ValidateCollection(state.CalendarEvents, 2000, "событий");
        ValidateCollection(state.Memories, 2000, "записей памяти");
        ValidateCollection(state.QuickActions, 200, "быстрых действий");
        ValidateCollection(state.Workflows, 200, "сценариев");

        ValidateIds(state.Tasks.Select(item => item.Id), "задач");
        ValidateIds(state.Notes.Select(item => item.Id), "заметок");
        ValidateIds(state.Timers.Select(item => item.Id), "таймеров");
        ValidateIds(state.Reminders.Select(item => item.Id), "напоминаний");
        ValidateIds(state.CalendarEvents.Select(item => item.Id), "событий");
        ValidateIds(state.Memories.Select(item => item.Id), "записей памяти");
        ValidateIds(state.QuickActions.Select(item => item.Id), "быстрых действий");
        ValidateIds(state.Workflows.Select(item => item.Id), "сценариев");

        foreach (var task in state.Tasks)
        {
            ValidateText(task.Title, 160, "название задачи", required: true);
            ValidateText(task.Details, 4096, "описание задачи");
            ValidateText(task.Category, 64, "категория задачи");
            ValidateEnum(task.Priority, "приоритет задачи");
        }

        foreach (var note in state.Notes)
        {
            ValidateText(note.Title, 160, "название заметки", required: true);
            ValidateText(note.Content, 32_000, "текст заметки");
            ValidateText(note.Category, 64, "категория заметки");
        }

        foreach (var timer in state.Timers)
        {
            ValidateText(timer.Name, 80, "название таймера", required: true);
            ValidateEnum(timer.Kind, "тип таймера");
            if (timer.DurationTicks < 0 ||
                timer.ElapsedTicks < 0 ||
                timer.ElapsedTicks > TimeSpan.MaxValue.Ticks ||
                timer.Kind == ProductivityTimerKind.Countdown &&
                (timer.DurationTicks <= 0 || timer.ElapsedTicks > timer.DurationTicks) ||
                timer.Kind == ProductivityTimerKind.Stopwatch && timer.DurationTicks != 0 ||
                timer.IsCompleted && timer.StartedAtUtc is not null)
                throw new InvalidDataException("Файл Productivity содержит некорректный таймер.");
        }

        foreach (var reminder in state.Reminders)
        {
            ValidateText(reminder.Title, 160, "название напоминания", required: true);
            ValidateText(reminder.Body, 2048, "текст напоминания");
            ValidateEnum(reminder.Recurrence, "повторение напоминания");
            ValidateTimeZone(reminder.TimeZoneId);
            if (reminder.LastOutcome is { Length: > 32 })
                throw new InvalidDataException("Файл Productivity содержит некорректный статус напоминания.");
        }

        var taskIds = state.Tasks.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var calendarEvent in state.CalendarEvents)
        {
            ValidateText(calendarEvent.Title, 160, "название события", required: true);
            ValidateText(calendarEvent.Details, 4096, "описание события");
            ValidateEnum(calendarEvent.Recurrence, "повторение события");
            ValidateTimeZone(calendarEvent.TimeZoneId);
            if (calendarEvent.ReminderMinutes is < 0 or > 1440 ||
                calendarEvent.RelatedTaskId is { } taskId && !taskIds.Contains(taskId))
                throw new InvalidDataException("Файл Productivity содержит некорректное событие календаря.");
        }

        foreach (var memory in state.Memories)
        {
            ValidateEnum(memory.Kind, "тип записи памяти");
            ValidateText(memory.Text, 4096, "запись памяти", required: true);
        }

        foreach (var action in state.QuickActions)
        {
            ValidateText(action.Name, 100, "название действия", required: true);
            ValidateEnum(action.Kind, "тип быстрого действия");
            ValidateText(action.Target, 2048, "цель быстрого действия");
            if (action.Kind is ProductivityQuickActionKind.OpenPath or ProductivityQuickActionKind.LaunchApplication &&
                string.IsNullOrWhiteSpace(action.Target) ||
                action.Kind is ProductivityQuickActionKind.LockWorkstation or ProductivityQuickActionKind.Sleep &&
                action.Target.Length != 0)
                throw new InvalidDataException("Файл Productivity содержит некорректное быстрое действие.");
        }

        var actionIds = state.QuickActions.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var workflow in state.Workflows)
        {
            ValidateText(workflow.Name, 100, "название сценария", required: true);
            if (workflow.ActionIds.Count is < 1 or > 50 ||
                workflow.ActionIds.Distinct(StringComparer.Ordinal).Count() != workflow.ActionIds.Count ||
                workflow.ActionIds.Any(id => !actionIds.Contains(id)))
                throw new InvalidDataException("Файл Productivity содержит некорректный сценарий.");
        }
    }

    private static void ValidateCollection<T>(IReadOnlyCollection<T>? collection, int maximum, string name)
    {
        if (collection is null || collection.Count > maximum)
            throw new InvalidDataException($"Файл Productivity содержит некорректное количество {name}.");
    }

    private static void ValidateIds(IEnumerable<string> ids, string name)
    {
        var unique = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            if (string.IsNullOrWhiteSpace(id) || id.Length > 96 || !unique.Add(id))
                throw new InvalidDataException($"Файл Productivity содержит повторяющийся или пустой ID ({name}).");
        }
    }

    private static void ValidateText(string? value, int maximumLength, string field, bool required = false)
    {
        if (value is null || value.Length > maximumLength || required && string.IsNullOrWhiteSpace(value))
            throw new InvalidDataException($"Файл Productivity содержит некорректное поле: {field}.");
    }

    private static void ValidateEnum<TEnum>(TEnum value, string field) where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
            throw new InvalidDataException($"Файл Productivity содержит неизвестное значение: {field}.");
    }

    private static void ValidateTimeZone(string? timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId) || timeZoneId.Length > 128)
            throw new InvalidDataException("Файл Productivity содержит некорректный часовой пояс.");
    }

    private sealed class ProductivityDocument
    {
        public string Format { get; set; } = FormatName;
        public int Version { get; set; }
        public List<ProductivityTask>? Tasks { get; set; } = [];
        public List<ProductivityNote>? Notes { get; set; } = [];
        public List<ProductivityTimer>? Timers { get; set; } = [];
        public List<ProductivityReminder>? Reminders { get; set; } = [];
        public List<ProductivityCalendarEvent>? CalendarEvents { get; set; } = [];
        public List<ProductivityMemoryEntry>? Memories { get; set; } = [];
        public List<ProductivityQuickAction>? QuickActions { get; set; } = [];
        public List<ProductivityWorkflow>? Workflows { get; set; } = [];

        public ProductivityState ToState() => new(
            Tasks ?? [],
            Notes ?? [],
            Timers ?? [],
            Reminders ?? [],
            CalendarEvents ?? [],
            Memories ?? [],
            QuickActions ?? [],
            Workflows ?? []);

        public static ProductivityDocument FromState(ProductivityState state) => new()
        {
            Format = FormatName,
            Version = CurrentVersion,
            Tasks = state.Tasks,
            Notes = state.Notes,
            Timers = state.Timers,
            Reminders = state.Reminders,
            CalendarEvents = state.CalendarEvents,
            Memories = state.Memories,
            QuickActions = state.QuickActions,
            Workflows = state.Workflows
        };
    }
}
