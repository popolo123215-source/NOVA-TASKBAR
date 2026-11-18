namespace Nova.WindowsClient.Services;

internal enum ProductivityPriority
{
    Low,
    Normal,
    High
}

internal enum ProductivityTimerKind
{
    Countdown,
    Stopwatch
}

internal enum ProductivityRecurrence
{
    None,
    Daily,
    Weekly,
    Monthly
}

internal enum ProductivityMemoryKind
{
    Context,
    Preference,
    OpenAction
}

internal enum ProductivityQuickActionKind
{
    OpenPath,
    LaunchApplication,
    LockWorkstation,
    Sleep
}

internal sealed record ProductivityTask(
    string Id,
    string Title,
    string Details,
    string Category,
    ProductivityPriority Priority,
    DateTimeOffset? DueAt,
    bool DueNotificationSent,
    bool IsCompleted,
    bool IsPinned,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

internal sealed record ProductivityNote(
    string Id,
    string Title,
    string Content,
    string Category,
    bool IsPinned,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

internal sealed record ProductivityTimer(
    string Id,
    string Name,
    ProductivityTimerKind Kind,
    long DurationTicks,
    long ElapsedTicks,
    DateTimeOffset? StartedAtUtc,
    bool IsCompleted,
    DateTimeOffset CreatedAt);

internal sealed record ProductivityTimerSnapshot(
    ProductivityTimer Timer,
    TimeSpan Elapsed,
    TimeSpan Remaining,
    bool IsRunning);

internal sealed record ProductivityReminder(
    string Id,
    string Title,
    string Body,
    DateTimeOffset DueAt,
    ProductivityRecurrence Recurrence,
    string TimeZoneId,
    bool IsEnabled,
    DateTimeOffset? LastTriggeredAt,
    string? LastOutcome);

internal sealed record ProductivityCalendarEvent(
    string Id,
    string Title,
    string Details,
    DateTimeOffset StartsAt,
    int ReminderMinutes,
    ProductivityRecurrence Recurrence,
    string TimeZoneId,
    string? RelatedTaskId,
    bool IsEnabled,
    DateTimeOffset? LastTriggeredAt);

internal sealed record ProductivityMemoryEntry(
    string Id,
    ProductivityMemoryKind Kind,
    string Text,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

internal sealed record ProductivityQuickAction(
    string Id,
    string Name,
    ProductivityQuickActionKind Kind,
    string Target,
    DateTimeOffset CreatedAt);

internal sealed record ProductivityWorkflow(
    string Id,
    string Name,
    IReadOnlyList<string> ActionIds,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

internal sealed record ProductivitySearchResult(
    string Id,
    string Source,
    string Title,
    string Excerpt,
    DateTimeOffset UpdatedAt,
    string? RelatedId);

internal sealed record ParsedSmartReminder(
    string Title,
    DateTimeOffset DueAt,
    ProductivityRecurrence Recurrence,
    string TimeZoneId);

internal sealed record ProductivityState(
    List<ProductivityTask> Tasks,
    List<ProductivityNote> Notes,
    List<ProductivityTimer> Timers,
    List<ProductivityReminder> Reminders,
    List<ProductivityCalendarEvent> CalendarEvents,
    List<ProductivityMemoryEntry> Memories,
    List<ProductivityQuickAction> QuickActions,
    List<ProductivityWorkflow> Workflows)
{
    public static ProductivityState Empty => new([], [], [], [], [], [], [], []);
}
