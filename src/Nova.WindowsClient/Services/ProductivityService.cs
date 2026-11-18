using System.IO;
using System.Text.Json;

namespace Nova.WindowsClient.Services;

internal sealed class ProductivityService
{
    private static readonly TimeSpan ReminderDeliveryGrace = TimeSpan.FromMinutes(5);
    private readonly ProductivityStore _store;
    private ProductivityState _state = ProductivityState.Empty;
    private bool _initialized;

    public ProductivityService(ProductivityStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public event EventHandler? Changed;

    public bool IsAvailable => _initialized;
    public string? LastError { get; private set; }

    public IReadOnlyList<ProductivityTask> Tasks => _state.Tasks
        .OrderByDescending(item => item.IsPinned)
        .ThenBy(item => item.IsCompleted)
        .ThenBy(item => item.DueAt)
        .ThenByDescending(item => item.UpdatedAt)
        .ToArray();

    public IReadOnlyList<ProductivityNote> Notes => _state.Notes
        .OrderByDescending(item => item.IsPinned)
        .ThenByDescending(item => item.UpdatedAt)
        .ToArray();

    public IReadOnlyList<ProductivityReminder> Reminders => _state.Reminders
        .OrderBy(item => item.DueAt)
        .ToArray();

    public IReadOnlyList<ProductivityCalendarEvent> CalendarEvents => _state.CalendarEvents
        .OrderBy(item => item.StartsAt)
        .ToArray();

    public IReadOnlyList<ProductivityMemoryEntry> Memories => _state.Memories
        .OrderByDescending(item => item.UpdatedAt)
        .ToArray();

    public IReadOnlyList<ProductivityQuickAction> QuickActions => _state.QuickActions
        .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
        .ToArray();

    public IReadOnlyList<ProductivityWorkflow> Workflows => _state.Workflows
        .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
        .ToArray();

    public void Initialize()
    {
        try
        {
            _state = _store.Load();
            _initialized = true;
            LastError = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            _state = ProductivityState.Empty;
            _initialized = false;
            LastError = $"Не удалось загрузить локальные данные Productivity: {ex.Message}";
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public ProductivityTask CreateTask(
        string title,
        string details,
        string category,
        ProductivityPriority priority,
        DateTimeOffset? dueAt,
        bool isPinned,
        DateTimeOffset now)
    {
        EnsureReady();
        var task = new ProductivityTask(
            NewId(),
            NormalizeRequired(title, 160, "Название задачи"),
            NormalizeOptional(details, 4096, "Описание задачи"),
            NormalizeOptional(category, 64, "Категория"),
            ValidatePriority(priority),
            dueAt?.ToUniversalTime(),
            false,
            false,
            isPinned,
            now.ToUniversalTime(),
            now.ToUniversalTime());
        Persist(_state with { Tasks = _state.Tasks.Append(task).ToList() });
        return task;
    }

    public ProductivityTask UpdateTask(
        string id,
        string title,
        string details,
        string category,
        ProductivityPriority priority,
        DateTimeOffset? dueAt,
        bool isCompleted,
        bool isPinned,
        DateTimeOffset now)
    {
        EnsureReady();
        var existing = Find(_state.Tasks, id);
        var updated = existing with
        {
            Title = NormalizeRequired(title, 160, "Название задачи"),
            Details = NormalizeOptional(details, 4096, "Описание задачи"),
            Category = NormalizeOptional(category, 64, "Категория"),
            Priority = ValidatePriority(priority),
            DueAt = dueAt?.ToUniversalTime(),
            DueNotificationSent = existing.DueAt?.ToUniversalTime() == dueAt?.ToUniversalTime()
                ? existing.DueNotificationSent
                : false,
            IsCompleted = isCompleted,
            IsPinned = isPinned,
            UpdatedAt = now.ToUniversalTime()
        };
        Persist(_state with { Tasks = Replace(_state.Tasks, updated) });
        return updated;
    }

    public bool SetTaskCompleted(string id, bool isCompleted, DateTimeOffset now)
    {
        EnsureReady();
        var existing = _state.Tasks.FirstOrDefault(item => item.Id == id);
        if (existing is null || existing.IsCompleted == isCompleted)
            return false;
        Persist(_state with
        {
            Tasks = Replace(_state.Tasks, existing with { IsCompleted = isCompleted, UpdatedAt = now.ToUniversalTime() })
        });
        return true;
    }

    public bool DeleteTask(string id)
    {
        EnsureReady();
        if (!_state.Tasks.Any(item => item.Id == id))
            return false;
        Persist(_state with
        {
            Tasks = _state.Tasks.Where(item => item.Id != id).ToList(),
            CalendarEvents = _state.CalendarEvents
                .Select(item => item.RelatedTaskId == id ? item with { RelatedTaskId = null } : item)
                .ToList()
        });
        return true;
    }

    public ProductivityNote CreateNote(
        string title,
        string content,
        string category,
        bool isPinned,
        DateTimeOffset now)
    {
        EnsureReady();
        var note = new ProductivityNote(
            NewId(),
            NormalizeRequired(title, 160, "Название заметки"),
            NormalizeOptional(content, 32_000, "Текст заметки"),
            NormalizeOptional(category, 64, "Категория"),
            isPinned,
            now.ToUniversalTime(),
            now.ToUniversalTime());
        Persist(_state with { Notes = _state.Notes.Append(note).ToList() });
        return note;
    }

    public ProductivityNote UpdateNote(
        string id,
        string title,
        string content,
        string category,
        bool isPinned,
        DateTimeOffset now)
    {
        EnsureReady();
        var existing = Find(_state.Notes, id);
        var updated = existing with
        {
            Title = NormalizeRequired(title, 160, "Название заметки"),
            Content = NormalizeOptional(content, 32_000, "Текст заметки"),
            Category = NormalizeOptional(category, 64, "Категория"),
            IsPinned = isPinned,
            UpdatedAt = now.ToUniversalTime()
        };
        Persist(_state with { Notes = Replace(_state.Notes, updated) });
        return updated;
    }

    public bool DeleteNote(string id)
    {
        EnsureReady();
        var updated = _state.Notes.Where(item => item.Id != id).ToList();
        if (updated.Count == _state.Notes.Count)
            return false;
        Persist(_state with { Notes = updated });
        return true;
    }

    public ProductivityTimer CreateTimer(string name, ProductivityTimerKind kind, TimeSpan duration, DateTimeOffset now)
    {
        EnsureReady();
        if (_state.Timers.Count >= 100)
            throw new InvalidOperationException("Достигнут лимит в 100 сохранённых таймеров.");
        if (kind == ProductivityTimerKind.Countdown &&
            (duration <= TimeSpan.Zero || duration > TimeSpan.FromDays(7)))
            throw new ArgumentOutOfRangeException(nameof(duration), "Длительность таймера должна быть больше 0 и не превышать 7 дней.");
        if (kind == ProductivityTimerKind.Stopwatch)
            duration = TimeSpan.Zero;
        else if (kind != ProductivityTimerKind.Countdown)
            throw new ArgumentOutOfRangeException(nameof(kind));

        var timer = new ProductivityTimer(
            NewId(),
            NormalizeRequired(name, 80, "Название таймера"),
            kind,
            duration.Ticks,
            0,
            null,
            false,
            now.ToUniversalTime());
        Persist(_state with { Timers = _state.Timers.Append(timer).ToList() });
        return timer;
    }

    public IReadOnlyList<ProductivityTimerSnapshot> GetTimerSnapshots(DateTimeOffset now)
    {
        EnsureReady();
        return _state.Timers
            .Select(timer => CreateTimerSnapshot(timer, now))
            .OrderByDescending(snapshot => snapshot.IsRunning)
            .ThenBy(snapshot => snapshot.Timer.CreatedAt)
            .ToArray();
    }

    public ProductivityTimer StartTimer(string id, DateTimeOffset now)
    {
        EnsureReady();
        var timer = Find(_state.Timers, id);
        if (timer.IsCompleted)
            throw new InvalidOperationException("Таймер завершён. Сбросьте его перед новым запуском.");
        if (timer.StartedAtUtc is not null)
            return timer;
        var snapshot = CreateTimerSnapshot(timer, now);
        if (timer.Kind == ProductivityTimerKind.Countdown && snapshot.Remaining <= TimeSpan.Zero)
            throw new InvalidOperationException("Сначала сбросьте завершившийся таймер.");
        var updated = timer with { StartedAtUtc = now.ToUniversalTime() };
        Persist(_state with { Timers = Replace(_state.Timers, updated) });
        return updated;
    }

    public ProductivityTimer PauseTimer(string id, DateTimeOffset now)
    {
        EnsureReady();
        var timer = Find(_state.Timers, id);
        if (timer.StartedAtUtc is null)
            return timer;
        var snapshot = CreateTimerSnapshot(timer, now);
        var elapsed = timer.Kind == ProductivityTimerKind.Countdown
            ? TimeSpan.FromTicks(Math.Min(timer.DurationTicks, snapshot.Elapsed.Ticks))
            : snapshot.Elapsed;
        var updated = timer with { ElapsedTicks = elapsed.Ticks, StartedAtUtc = null };
        Persist(_state with { Timers = Replace(_state.Timers, updated) });
        return updated;
    }

    public ProductivityTimer ResetTimer(string id)
    {
        EnsureReady();
        var timer = Find(_state.Timers, id);
        var updated = timer with { ElapsedTicks = 0, StartedAtUtc = null, IsCompleted = false };
        Persist(_state with { Timers = Replace(_state.Timers, updated) });
        return updated;
    }

    public bool DeleteTimer(string id)
    {
        EnsureReady();
        var updated = _state.Timers.Where(item => item.Id != id).ToList();
        if (updated.Count == _state.Timers.Count)
            return false;
        Persist(_state with { Timers = updated });
        return true;
    }

    public ProductivityReminder CreateReminder(ParsedSmartReminder parsed, string? body, DateTimeOffset now)
    {
        EnsureReady();
        ArgumentNullException.ThrowIfNull(parsed);
        if (parsed.DueAt.ToUniversalTime() <= now.ToUniversalTime())
            throw new ArgumentOutOfRangeException(nameof(parsed), "Время напоминания должно быть в будущем.");
        var reminder = new ProductivityReminder(
            NewId(),
            NormalizeRequired(parsed.Title, 160, "Название напоминания"),
            NormalizeOptional(body, 2048, "Текст напоминания"),
            parsed.DueAt.ToUniversalTime(),
            ValidateRecurrence(parsed.Recurrence),
            NormalizeTimeZone(parsed.TimeZoneId),
            true,
            null,
            null);
        Persist(_state with { Reminders = _state.Reminders.Append(reminder).ToList() });
        return reminder;
    }

    public ProductivityReminder UpdateReminder(
        string id,
        ParsedSmartReminder parsed,
        string? body,
        DateTimeOffset now)
    {
        EnsureReady();
        ArgumentNullException.ThrowIfNull(parsed);
        if (parsed.DueAt.ToUniversalTime() <= now.ToUniversalTime())
            throw new ArgumentOutOfRangeException(nameof(parsed), "Время напоминания должно быть в будущем.");
        var existing = Find(_state.Reminders, id);
        var updated = existing with
        {
            Title = NormalizeRequired(parsed.Title, 160, "Название напоминания"),
            Body = NormalizeOptional(body, 2048, "Текст напоминания"),
            DueAt = parsed.DueAt.ToUniversalTime(),
            Recurrence = ValidateRecurrence(parsed.Recurrence),
            TimeZoneId = NormalizeTimeZone(parsed.TimeZoneId),
            IsEnabled = true,
            LastOutcome = null
        };
        Persist(_state with { Reminders = Replace(_state.Reminders, updated) });
        return updated;
    }

    public ProductivityReminder SetReminderEnabled(string id, bool enabled, DateTimeOffset now)
    {
        EnsureReady();
        var reminder = Find(_state.Reminders, id);
        if (enabled && reminder.DueAt <= now.ToUniversalTime())
            throw new InvalidOperationException("Время напоминания уже прошло. Перенесите его на будущее.");
        var updated = reminder with { IsEnabled = enabled };
        Persist(_state with { Reminders = Replace(_state.Reminders, updated) });
        return updated;
    }

    public ProductivityReminder SnoozeReminder(string id, TimeSpan delay, DateTimeOffset now)
    {
        EnsureReady();
        if (delay <= TimeSpan.Zero || delay > TimeSpan.FromDays(30))
            throw new ArgumentOutOfRangeException(nameof(delay), "Перенос должен быть от 1 секунды до 30 дней.");
        var reminder = Find(_state.Reminders, id);
        var updated = reminder with
        {
            DueAt = now.ToUniversalTime().Add(delay),
            IsEnabled = true,
            LastOutcome = null
        };
        Persist(_state with { Reminders = Replace(_state.Reminders, updated) });
        return updated;
    }

    public bool DeleteReminder(string id)
    {
        EnsureReady();
        var updated = _state.Reminders.Where(item => item.Id != id).ToList();
        if (updated.Count == _state.Reminders.Count)
            return false;
        Persist(_state with { Reminders = updated });
        return true;
    }

    public ProductivityCalendarEvent CreateCalendarEvent(
        string title,
        string details,
        DateTimeOffset startsAt,
        int reminderMinutes,
        ProductivityRecurrence recurrence,
        string timeZoneId,
        string? relatedTaskId,
        DateTimeOffset now)
    {
        EnsureReady();
        if (startsAt.ToUniversalTime() <= now.ToUniversalTime())
            throw new ArgumentOutOfRangeException(nameof(startsAt), "Событие должно начинаться в будущем.");
        if (reminderMinutes is < 0 or > 1440)
            throw new ArgumentOutOfRangeException(nameof(reminderMinutes), "Напоминание задаётся от 0 до 1440 минут.");
        if (relatedTaskId is not null && !_state.Tasks.Any(task => task.Id == relatedTaskId))
            throw new ArgumentException("Связанная задача больше не существует.", nameof(relatedTaskId));

        var calendarEvent = new ProductivityCalendarEvent(
            NewId(),
            NormalizeRequired(title, 160, "Название события"),
            NormalizeOptional(details, 4096, "Описание события"),
            startsAt.ToUniversalTime(),
            reminderMinutes,
            ValidateRecurrence(recurrence),
            NormalizeTimeZone(timeZoneId),
            relatedTaskId,
            true,
            null);
        Persist(_state with { CalendarEvents = _state.CalendarEvents.Append(calendarEvent).ToList() });
        return calendarEvent;
    }

    public ProductivityCalendarEvent UpdateCalendarEvent(
        string id,
        string title,
        string details,
        DateTimeOffset startsAt,
        int reminderMinutes,
        ProductivityRecurrence recurrence,
        string timeZoneId,
        string? relatedTaskId,
        DateTimeOffset now)
    {
        EnsureReady();
        var existing = Find(_state.CalendarEvents, id);
        if (startsAt.ToUniversalTime() <= now.ToUniversalTime())
            throw new ArgumentOutOfRangeException(nameof(startsAt), "Событие должно начинаться в будущем.");
        if (reminderMinutes is < 0 or > 1440)
            throw new ArgumentOutOfRangeException(nameof(reminderMinutes), "Напоминание задаётся от 0 до 1440 минут.");
        if (relatedTaskId is not null && !_state.Tasks.Any(task => task.Id == relatedTaskId))
            throw new ArgumentException("Связанная задача больше не существует.", nameof(relatedTaskId));
        var updated = existing with
        {
            Title = NormalizeRequired(title, 160, "Название события"),
            Details = NormalizeOptional(details, 4096, "Описание события"),
            StartsAt = startsAt.ToUniversalTime(),
            ReminderMinutes = reminderMinutes,
            Recurrence = ValidateRecurrence(recurrence),
            TimeZoneId = NormalizeTimeZone(timeZoneId),
            RelatedTaskId = relatedTaskId,
            IsEnabled = true,
            LastTriggeredAt = null
        };
        Persist(_state with { CalendarEvents = Replace(_state.CalendarEvents, updated) });
        return updated;
    }

    public ProductivityCalendarEvent SetCalendarEventEnabled(string id, bool enabled, DateTimeOffset now)
    {
        EnsureReady();
        var calendarEvent = Find(_state.CalendarEvents, id);
        if (enabled &&
            calendarEvent.StartsAt.AddMinutes(-calendarEvent.ReminderMinutes) <= now.ToUniversalTime())
            throw new InvalidOperationException("Время события уже прошло. Измените его дату и время.");
        var updated = calendarEvent with { IsEnabled = enabled };
        Persist(_state with { CalendarEvents = Replace(_state.CalendarEvents, updated) });
        return updated;
    }

    public bool DeleteCalendarEvent(string id)
    {
        EnsureReady();
        var updated = _state.CalendarEvents.Where(item => item.Id != id).ToList();
        if (updated.Count == _state.CalendarEvents.Count)
            return false;
        Persist(_state with { CalendarEvents = updated });
        return true;
    }

    public ProductivityMemoryEntry CreateMemory(ProductivityMemoryKind kind, string text, DateTimeOffset now)
    {
        EnsureReady();
        var memory = new ProductivityMemoryEntry(
            NewId(),
            ValidateMemoryKind(kind),
            NormalizeRequired(text, 4096, "Текст локальной памяти"),
            now.ToUniversalTime(),
            now.ToUniversalTime());
        Persist(_state with { Memories = _state.Memories.Append(memory).ToList() });
        return memory;
    }

    public ProductivityMemoryEntry UpdateMemory(string id, ProductivityMemoryKind kind, string text, DateTimeOffset now)
    {
        EnsureReady();
        var existing = Find(_state.Memories, id);
        var updated = existing with
        {
            Kind = ValidateMemoryKind(kind),
            Text = NormalizeRequired(text, 4096, "Текст локальной памяти"),
            UpdatedAt = now.ToUniversalTime()
        };
        Persist(_state with { Memories = Replace(_state.Memories, updated) });
        return updated;
    }

    public bool DeleteMemory(string id)
    {
        EnsureReady();
        var updated = _state.Memories.Where(item => item.Id != id).ToList();
        if (updated.Count == _state.Memories.Count)
            return false;
        Persist(_state with { Memories = updated });
        return true;
    }

    public IReadOnlyList<ProductivitySearchResult> SearchContext(string? query)
    {
        EnsureReady();
        var term = query?.Trim() ?? string.Empty;
        bool Matches(params string?[] values) =>
            term.Length == 0 || values.Any(value => value?.Contains(term, StringComparison.CurrentCultureIgnoreCase) == true);

        var results = new List<ProductivitySearchResult>();
        results.AddRange(_state.Tasks
            .Where(item => Matches(item.Title, item.Details, item.Category))
            .Select(item => new ProductivitySearchResult(
                $"task-{item.Id}",
                item.IsCompleted ? "Выполненная задача" : "Незавершённая задача",
                item.Title,
                item.Details,
                item.UpdatedAt,
                item.Id)));
        results.AddRange(_state.Notes
            .Where(item => Matches(item.Title, item.Content, item.Category))
            .Select(item => new ProductivitySearchResult(
                $"note-{item.Id}",
                "Заметка",
                item.Title,
                item.Content,
                item.UpdatedAt,
                item.Id)));
        results.AddRange(_state.Memories
            .Where(item => Matches(item.Text))
            .Select(item => new ProductivitySearchResult(
                $"memory-{item.Id}",
                item.Kind switch
                {
                    ProductivityMemoryKind.Preference => "Предпочтение",
                    ProductivityMemoryKind.OpenAction => "Незавершённое действие",
                    _ => "Локальный контекст"
                },
                item.Text,
                string.Empty,
                item.UpdatedAt,
                item.Id)));
        return results
            .OrderByDescending(item => item.UpdatedAt)
            .Take(200)
            .ToArray();
    }

    public ProductivityQuickAction CreateQuickAction(
        string name,
        ProductivityQuickActionKind kind,
        string? target,
        DateTimeOffset now)
    {
        EnsureReady();
        var action = new ProductivityQuickAction(
            NewId(),
            NormalizeRequired(name, 100, "Название действия"),
            ValidateActionKind(kind),
            NormalizeActionTarget(kind, target),
            now.ToUniversalTime());
        Persist(_state with { QuickActions = _state.QuickActions.Append(action).ToList() });
        return action;
    }

    public ProductivityQuickAction UpdateQuickAction(
        string id,
        string name,
        ProductivityQuickActionKind kind,
        string? target,
        DateTimeOffset now)
    {
        EnsureReady();
        var existing = Find(_state.QuickActions, id);
        var updated = existing with
        {
            Name = NormalizeRequired(name, 100, "Название действия"),
            Kind = ValidateActionKind(kind),
            Target = NormalizeActionTarget(kind, target)
        };
        if (_state.Workflows.Any(workflow => workflow.ActionIds.Contains(id, StringComparer.Ordinal)) &&
            updated.Kind != existing.Kind)
            throw new InvalidOperationException("Нельзя менять тип действия, пока оно используется в сценарии.");
        Persist(_state with { QuickActions = Replace(_state.QuickActions, updated) });
        return updated;
    }

    public bool DeleteQuickAction(string id)
    {
        EnsureReady();
        if (_state.Workflows.Any(workflow => workflow.ActionIds.Contains(id, StringComparer.Ordinal)))
            throw new InvalidOperationException("Сначала удалите сценарии, использующие это действие.");
        var updated = _state.QuickActions.Where(item => item.Id != id).ToList();
        if (updated.Count == _state.QuickActions.Count)
            return false;
        Persist(_state with { QuickActions = updated });
        return true;
    }

    public ProductivityWorkflow CreateWorkflow(string name, IReadOnlyCollection<string> actionIds, DateTimeOffset now)
    {
        EnsureReady();
        ArgumentNullException.ThrowIfNull(actionIds);
        var ids = actionIds.ToArray();
        ValidateWorkflowSteps(ids);
        var workflow = new ProductivityWorkflow(
            NewId(),
            NormalizeRequired(name, 100, "Название сценария"),
            ids,
            now.ToUniversalTime(),
            now.ToUniversalTime());
        Persist(_state with { Workflows = _state.Workflows.Append(workflow).ToList() });
        return workflow;
    }

    public ProductivityWorkflow UpdateWorkflow(string id, string name, IReadOnlyCollection<string> actionIds, DateTimeOffset now)
    {
        EnsureReady();
        ArgumentNullException.ThrowIfNull(actionIds);
        var existing = Find(_state.Workflows, id);
        var ids = actionIds.ToArray();
        ValidateWorkflowSteps(ids);
        var updated = existing with
        {
            Name = NormalizeRequired(name, 100, "Название сценария"),
            ActionIds = ids,
            UpdatedAt = now.ToUniversalTime()
        };
        Persist(_state with { Workflows = Replace(_state.Workflows, updated) });
        return updated;
    }

    public bool DeleteWorkflow(string id)
    {
        EnsureReady();
        var updated = _state.Workflows.Where(item => item.Id != id).ToList();
        if (updated.Count == _state.Workflows.Count)
            return false;
        Persist(_state with { Workflows = updated });
        return true;
    }

    public IReadOnlyList<ProductivityQuickAction> GetWorkflowActions(string workflowId)
    {
        EnsureReady();
        var workflow = Find(_state.Workflows, workflowId);
        return workflow.ActionIds
            .Select(id => Find(_state.QuickActions, id))
            .ToArray();
    }

    public int ProcessDue(
        DateTimeOffset now,
        Action<string, string, string> deliverNotification)
    {
        ArgumentNullException.ThrowIfNull(deliverNotification);
        if (!_initialized)
            return 0;
        var utcNow = now.ToUniversalTime();
        var dueNotifications = new List<(string Title, string Body, string Id, Action Restore)>();
        var timerUpdates = _state.Timers.ToList();
        foreach (var timer in _state.Timers.Where(item => !item.IsCompleted))
        {
            var snapshot = CreateTimerSnapshot(timer, utcNow);
            if (timer.Kind != ProductivityTimerKind.Countdown || snapshot.Remaining > TimeSpan.Zero)
                continue;
            timerUpdates[timerUpdates.FindIndex(item => item.Id == timer.Id)] = timer with
            {
                ElapsedTicks = timer.DurationTicks,
                StartedAtUtc = null,
                IsCompleted = true
            };
            dueNotifications.Add((
                "Таймер завершён",
                timer.Name,
                $"productivity-timer-{timer.Id}-{utcNow.UtcTicks}",
                () => Persist(_state with
                {
                    Timers = ReplaceById(_state.Timers, timer.Id, timer, static item => item.Id)
                })));
        }
        if (timerUpdates.Where((item, index) => !ReferenceEquals(item, _state.Timers[index])).Any())
            Persist(_state with { Timers = timerUpdates });

        var taskUpdates = _state.Tasks.ToList();
        var tasksChanged = false;
        foreach (var task in _state.Tasks.Where(item =>
                     !item.IsCompleted && !item.DueNotificationSent && item.DueAt is { } due && due <= utcNow))
        {
            var dueAt = task.DueAt!.Value;
            var updated = task with { DueNotificationSent = true };
            taskUpdates[taskUpdates.FindIndex(item => item.Id == task.Id)] = updated;
            tasksChanged = true;
            if (utcNow - dueAt <= ReminderDeliveryGrace)
                dueNotifications.Add((
                    "Срок задачи",
                    task.Title,
                    $"productivity-task-{task.Id}-{dueAt.UtcTicks}",
                    () => Persist(_state with
                    {
                        Tasks = ReplaceById(_state.Tasks, task.Id, task, static item => item.Id)
                    })));
        }
        if (tasksChanged)
            Persist(_state with { Tasks = taskUpdates });

        var reminderUpdates = _state.Reminders.ToList();
        var remindersChanged = false;
        foreach (var reminder in _state.Reminders.Where(item => item.IsEnabled && item.DueAt <= utcNow))
        {
            var isLate = utcNow - reminder.DueAt > ReminderDeliveryGrace;
            var nextDue = AdvanceOccurrence(reminder.DueAt, reminder.Recurrence, reminder.TimeZoneId, utcNow, isLate);
            var updated = reminder with
            {
                DueAt = nextDue ?? reminder.DueAt,
                IsEnabled = nextDue is not null,
                LastTriggeredAt = reminder.DueAt,
                LastOutcome = isLate ? "Пропущено" : "Отправлено"
            };
            reminderUpdates[reminderUpdates.FindIndex(item => item.Id == reminder.Id)] = updated;
            remindersChanged = true;
            if (!isLate)
                dueNotifications.Add((
                    reminder.Title,
                    reminder.Body,
                    $"productivity-reminder-{reminder.Id}-{reminder.DueAt.UtcTicks}",
                    () => Persist(_state with
                    {
                        Reminders = ReplaceById(_state.Reminders, reminder.Id, reminder, static item => item.Id)
                    })));
        }
        if (remindersChanged)
            Persist(_state with { Reminders = reminderUpdates });

        var eventUpdates = _state.CalendarEvents.ToList();
        var eventsChanged = false;
        foreach (var calendarEvent in _state.CalendarEvents.Where(item =>
                     item.IsEnabled && item.StartsAt.AddMinutes(-item.ReminderMinutes) <= utcNow))
        {
            var notifyAt = calendarEvent.StartsAt.AddMinutes(-calendarEvent.ReminderMinutes);
            var isLate = utcNow - notifyAt > ReminderDeliveryGrace;
            var nextStart = AdvanceOccurrence(calendarEvent.StartsAt, calendarEvent.Recurrence, calendarEvent.TimeZoneId, utcNow, isLate);
            var updated = calendarEvent with
            {
                StartsAt = nextStart ?? calendarEvent.StartsAt,
                IsEnabled = nextStart is not null,
                LastTriggeredAt = calendarEvent.StartsAt
            };
            eventUpdates[eventUpdates.FindIndex(item => item.Id == calendarEvent.Id)] = updated;
            eventsChanged = true;
            if (!isLate)
            {
                var taskSuffix = calendarEvent.RelatedTaskId is { } taskId
                    ? $" · задача: {_state.Tasks.FirstOrDefault(task => task.Id == taskId)?.Title}"
                    : string.Empty;
                dueNotifications.Add((
                    $"Событие: {calendarEvent.Title}",
                    $"{calendarEvent.Details}{taskSuffix}".Trim(),
                    $"productivity-event-{calendarEvent.Id}-{calendarEvent.StartsAt.UtcTicks}",
                    () => Persist(_state with
                    {
                        CalendarEvents = ReplaceById(
                            _state.CalendarEvents,
                            calendarEvent.Id,
                            calendarEvent,
                            static item => item.Id)
                    })));
            }
        }
        if (eventsChanged)
            Persist(_state with { CalendarEvents = eventUpdates });

        var delivered = 0;
        foreach (var notification in dueNotifications)
        {
            try
            {
                deliverNotification(notification.Title, notification.Body, notification.Id);
                delivered++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                try
                {
                    notification.Restore();
                }
                catch (Exception restoreException) when (
                    restoreException is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    LastError = $"Не удалось повторно сохранить уведомление «{notification.Title}»: {restoreException.Message}";
                    Changed?.Invoke(this, EventArgs.Empty);
                    continue;
                }
                LastError = $"Не удалось отправить уведомление «{notification.Title}»: {ex.Message}";
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
        return delivered;
    }

    private static List<T> ReplaceById<T>(
        List<T> items,
        string id,
        T replacement,
        Func<T, string> getId)
    {
        var updated = items.ToList();
        var index = updated.FindIndex(item => getId(item) == id);
        if (index >= 0)
            updated[index] = replacement;
        return updated;
    }

    private DateTimeOffset? AdvanceOccurrence(
        DateTimeOffset occurrence,
        ProductivityRecurrence recurrence,
        string timeZoneId,
        DateTimeOffset now,
        bool skipCurrent)
    {
        if (recurrence == ProductivityRecurrence.None)
            return null;
        try
        {
            var timeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            var next = GetNextOccurrence(occurrence, recurrence, timeZone);
            if (skipCurrent)
            {
                var attempts = 0;
                while (next <= now && attempts++ < 10_000)
                    next = GetNextOccurrence(next, recurrence, timeZone);
            }
            return next;
        }
        catch (TimeZoneNotFoundException ex)
        {
            LastError = $"Часовой пояс «{timeZoneId}» недоступен: {ex.Message}";
            Changed?.Invoke(this, EventArgs.Empty);
            return null;
        }
        catch (InvalidTimeZoneException ex)
        {
            LastError = $"Часовой пояс «{timeZoneId}» повреждён: {ex.Message}";
            Changed?.Invoke(this, EventArgs.Empty);
            return null;
        }
    }

    internal static DateTimeOffset GetNextOccurrence(
        DateTimeOffset previous,
        ProductivityRecurrence recurrence,
        TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(timeZone);
        var localPrevious = TimeZoneInfo.ConvertTime(previous, timeZone);
        var localDate = DateOnly.FromDateTime(localPrevious.DateTime);
        var localTime = TimeOnly.FromDateTime(localPrevious.DateTime);
        var nextDate = recurrence switch
        {
            ProductivityRecurrence.Daily => localDate.AddDays(1),
            ProductivityRecurrence.Weekly => localDate.AddDays(7),
            ProductivityRecurrence.Monthly => NextMonth(localDate),
            _ => throw new ArgumentOutOfRangeException(nameof(recurrence), "Одноразовое событие не повторяется.")
        };
        var local = DateTime.SpecifyKind(nextDate.ToDateTime(localTime), DateTimeKind.Unspecified);
        for (var attempt = 0; timeZone.IsInvalidTime(local) && attempt < 180; attempt++)
            local = local.AddMinutes(1);
        if (timeZone.IsInvalidTime(local))
            throw new InvalidOperationException("Следующее время не существует в часовом поясе события.");
        var offset = timeZone.IsAmbiguousTime(local)
            ? timeZone.GetAmbiguousTimeOffsets(local).Max()
            : timeZone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }

    private static DateOnly NextMonth(DateOnly previous)
    {
        var year = previous.Month == 12 ? previous.Year + 1 : previous.Year;
        var month = previous.Month == 12 ? 1 : previous.Month + 1;
        return new DateOnly(year, month, Math.Min(previous.Day, DateTime.DaysInMonth(year, month)));
    }

    private static ProductivityTimerSnapshot CreateTimerSnapshot(ProductivityTimer timer, DateTimeOffset now)
    {
        var elapsedTicks = timer.ElapsedTicks;
        if (timer.StartedAtUtc is { } startedAt)
            elapsedTicks += Math.Max(0, (now.ToUniversalTime() - startedAt.ToUniversalTime()).Ticks);
        if (timer.Kind == ProductivityTimerKind.Countdown)
            elapsedTicks = Math.Min(timer.DurationTicks, elapsedTicks);
        var elapsed = TimeSpan.FromTicks(Math.Max(0, elapsedTicks));
        var remaining = timer.Kind == ProductivityTimerKind.Countdown
            ? TimeSpan.FromTicks(Math.Max(0, timer.DurationTicks - elapsedTicks))
            : TimeSpan.Zero;
        return new ProductivityTimerSnapshot(timer, elapsed, remaining, timer.StartedAtUtc is not null && !timer.IsCompleted);
    }

    private void ValidateWorkflowSteps(IReadOnlyCollection<string> actionIds)
    {
        if (actionIds.Count is < 1 or > 50 ||
            actionIds.Distinct(StringComparer.Ordinal).Count() != actionIds.Count)
            throw new ArgumentException("Сценарий должен содержать от 1 до 50 уникальных действий.", nameof(actionIds));
        if (actionIds.Any(id => !_state.QuickActions.Any(action => action.Id == id)))
            throw new ArgumentException("Сценарий содержит действие, которого больше нет.", nameof(actionIds));
        var actions = actionIds.Select(id => Find(_state.QuickActions, id)).ToArray();
        if (actions.Take(actions.Length - 1).Any(action =>
                action.Kind is ProductivityQuickActionKind.LockWorkstation or ProductivityQuickActionKind.Sleep))
            throw new ArgumentException("Блокировка и сон могут быть только последним шагом сценария.", nameof(actionIds));
    }

    private static string NormalizeActionTarget(ProductivityQuickActionKind kind, string? target)
    {
        if (kind is ProductivityQuickActionKind.LockWorkstation or ProductivityQuickActionKind.Sleep)
            return string.Empty;
        var normalized = NormalizeRequired(target, 2048, "Путь действия");
        if (!Path.IsPathFullyQualified(normalized))
            throw new ArgumentException("Укажите абсолютный локальный путь.", nameof(target));
        var fullPath = Path.GetFullPath(normalized);
        if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
            throw new FileNotFoundException("Указанный файл или папка не существует.", fullPath);
        if (kind == ProductivityQuickActionKind.LaunchApplication &&
            !string.Equals(Path.GetExtension(fullPath), ".exe", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(Path.GetExtension(fullPath), ".lnk", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Для запуска выберите EXE-файл или ярлык.", nameof(target));
        return fullPath;
    }

    private static ProductivityQuickActionKind ValidateActionKind(ProductivityQuickActionKind kind)
    {
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind));
        return kind;
    }

    private static ProductivityPriority ValidatePriority(ProductivityPriority priority)
    {
        if (!Enum.IsDefined(priority))
            throw new ArgumentOutOfRangeException(nameof(priority));
        return priority;
    }

    private static ProductivityRecurrence ValidateRecurrence(ProductivityRecurrence recurrence)
    {
        if (!Enum.IsDefined(recurrence))
            throw new ArgumentOutOfRangeException(nameof(recurrence));
        return recurrence;
    }

    private static ProductivityMemoryKind ValidateMemoryKind(ProductivityMemoryKind kind)
    {
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind));
        return kind;
    }

    private static string NormalizeTimeZone(string timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId) || timeZoneId.Length > 128)
            throw new ArgumentException("Часовой пояс не указан.", nameof(timeZoneId));
        _ = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        return timeZoneId;
    }

    private static string NormalizeRequired(string? value, int maximumLength, string field)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            throw new ArgumentException($"{field} не может быть пустым.", field);
        if (normalized.Length > maximumLength)
            throw new ArgumentOutOfRangeException(field, $"{field}: максимум {maximumLength} символов.");
        return normalized;
    }

    private static string NormalizeOptional(string? value, int maximumLength, string field)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length > maximumLength)
            throw new ArgumentOutOfRangeException(field, $"{field}: максимум {maximumLength} символов.");
        return normalized;
    }

    private static string NewId() => Guid.NewGuid().ToString("N");

    private static T Find<T>(IEnumerable<T> items, string id) where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return items.FirstOrDefault(item => item switch
        {
            ProductivityTask task => task.Id == id,
            ProductivityNote note => note.Id == id,
            ProductivityTimer timer => timer.Id == id,
            ProductivityReminder reminder => reminder.Id == id,
            ProductivityCalendarEvent calendarEvent => calendarEvent.Id == id,
            ProductivityMemoryEntry memory => memory.Id == id,
            ProductivityQuickAction action => action.Id == id,
            ProductivityWorkflow workflow => workflow.Id == id,
            _ => false
        }) ?? throw new KeyNotFoundException($"Запись Productivity «{id}» не найдена.");
    }

    private static List<T> Replace<T>(IEnumerable<T> items, T updated) where T : class
    {
        var id = GetId(updated);
        var result = items.Select(item => GetId(item) == id ? updated : item).ToList();
        if (result.All(item => GetId(item) != id))
            throw new KeyNotFoundException($"Запись Productivity «{id}» не найдена.");
        return result;
    }

    private static string GetId<T>(T item) where T : class => item switch
    {
        ProductivityTask task => task.Id,
        ProductivityNote note => note.Id,
        ProductivityTimer timer => timer.Id,
        ProductivityReminder reminder => reminder.Id,
        ProductivityCalendarEvent calendarEvent => calendarEvent.Id,
        ProductivityMemoryEntry memory => memory.Id,
        ProductivityQuickAction action => action.Id,
        ProductivityWorkflow workflow => workflow.Id,
        _ => throw new ArgumentException("Неизвестный тип записи Productivity.", nameof(item))
    };

    private void Persist(ProductivityState updated)
    {
        try
        {
            _store.Save(updated);
            _state = updated;
            LastError = null;
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            LastError = $"Не удалось сохранить данные Productivity: {ex.Message}";
            Changed?.Invoke(this, EventArgs.Empty);
            throw;
        }
    }

    private void EnsureReady()
    {
        if (!_initialized)
            throw new InvalidOperationException(LastError ?? "Локальное хранилище Productivity недоступно.");
    }
}
