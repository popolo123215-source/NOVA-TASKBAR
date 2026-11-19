namespace Nova.WindowsClient.Services;

internal enum ProfileRuleKind
{
    Process,
    Time,
    Fullscreen,
    AudioDevice
}

internal enum ProfileActivationSource
{
    Manual,
    Automatic,
    Fallback
}

internal sealed record ProfileSnapshot(
    string? DockEdge,
    double SizeScale,
    double WidgetIconSize,
    bool KeepOnTop,
    bool HoverExpandEnabled,
    bool TimerCompletionSound,
    bool EnableMotion,
    bool EnableAudioReactive,
    double AudioReactiveIntensity,
    bool BrowserAdaptationEnabled,
    string? SelectedWidget,
    IReadOnlyList<string>? VisibleWidgetIds,
    bool NotificationsEnabled,
    bool SuppressNotificationToasts,
    bool ShowMonitorCpu,
    bool ShowMonitorGpu,
    bool ShowMonitorRam,
    bool ShowMonitorNetwork,
    bool ShowMonitorDisk)
{
    public static ProfileSnapshot Default => new(
        "top",
        1,
        24,
        true,
        true,
        true,
        true,
        true,
        1,
        false,
        "Overview",
        null,
        true,
        false,
        true,
        true,
        true,
        true,
        true);
}

internal sealed record ProfileRule(
    string Id,
    ProfileRuleKind Kind,
    string Value,
    bool Enabled,
    int Priority,
    int ActivationDelaySeconds,
    int CooldownSeconds,
    TimeOnly? StartTime,
    TimeOnly? EndTime,
    string? Weekdays);

internal sealed record NovaProfile(
    string Id,
    string Name,
    bool IsBuiltIn,
    ProfileSnapshot Snapshot,
    IReadOnlyList<ProfileRule> Rules,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

internal sealed record ProfileHistoryEntry(
    string Id,
    string ProfileId,
    string ProfileName,
    ProfileActivationSource Source,
    string Reason,
    DateTimeOffset ActivatedAt);

internal sealed record ProfileDocument(
    string Format,
    int Version,
    IReadOnlyList<NovaProfile> Profiles,
    string ActiveProfileId,
    ProfileActivationSource ActiveSource,
    bool AutomationEnabled,
    bool AutomationPaused,
    string? LastRuleId,
    IReadOnlyList<ProfileHistoryEntry> History);
