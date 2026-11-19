using System.Collections.ObjectModel;

namespace Nova.WindowsClient.Services;

internal sealed class ProfileService
{
    private readonly ProfileStore _store;
    private ProfileDocument _document;
    private string? _pendingProfileId;
    private DateTimeOffset? _pendingSince;
    private DateTimeOffset? _lastAutomaticActivation;
    private string? _lastAutomaticProfileId;

    public event EventHandler? Changed;

    public ProfileService(ProfileStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _document = store.Load();
    }

    public bool IsAvailable => _store.IsAvailable;
    public string? LastError => _store.LastError;
    public IReadOnlyList<NovaProfile> Profiles => _document.Profiles;
    public NovaProfile ActiveProfile =>
        _document.Profiles.First(profile => profile.Id == _document.ActiveProfileId);
    public ProfileActivationSource ActiveSource => _document.ActiveSource;
    public bool AutomationEnabled => _document.AutomationEnabled;
    public bool AutomationPaused => _document.AutomationPaused;
    public IReadOnlyList<ProfileHistoryEntry> History => _document.History;
    public string PendingRuleDescription
    {
        get
        {
            if (_document.LastRuleId is not { } ruleId)
                return "правило не указано";
            foreach (var profile in _document.Profiles)
            {
                var rule = profile.Rules.FirstOrDefault(item => item.Id == ruleId);
                if (rule is not null)
                    return RuleDescription(rule);
            }
            return "правило не указано";
        }
    }
    public NovaProfile? CurrentPendingProfile => _pendingProfileId is { } id
        ? _document.Profiles.FirstOrDefault(profile => profile.Id == id)
        : null;

    public void Initialize()
    {
        if (_document.Profiles.All(profile => profile.Id != _document.ActiveProfileId))
            _document = _document with { ActiveProfileId = "default", ActiveSource = ProfileActivationSource.Fallback };
        Persist();
    }

    public NovaProfile CreateProfile(string name)
    {
        var now = DateTimeOffset.UtcNow;
        var profile = new NovaProfile(
            Guid.NewGuid().ToString("N"),
            NormalizeName(name),
            false,
            ProfileSnapshot.Default,
            [],
            now,
            now);
        _document = _document with { Profiles = _document.Profiles.Append(profile).ToList() };
        Persist();
        Changed?.Invoke(this, EventArgs.Empty);
        return profile;
    }

    public NovaProfile DuplicateProfile(string id)
    {
        var source = FindProfile(id);
        var now = DateTimeOffset.UtcNow;
        var profile = new NovaProfile(
            Guid.NewGuid().ToString("N"),
            $"{source.Name} копия",
            false,
            source.Snapshot,
            source.Rules.Select(rule => rule with { Id = Guid.NewGuid().ToString("N") }).ToList(),
            now,
            now);
        _document = _document with { Profiles = _document.Profiles.Append(profile).ToList() };
        Persist();
        Changed?.Invoke(this, EventArgs.Empty);
        return profile;
    }

    public NovaProfile RenameProfile(string id, string name)
    {
        var profile = FindProfile(id);
        var updated = profile with { Name = NormalizeName(name), UpdatedAt = DateTimeOffset.UtcNow };
        ReplaceProfile(updated);
        return updated;
    }

    public bool DeleteProfile(string id)
    {
        if (FindProfile(id).IsBuiltIn)
            throw new InvalidOperationException("Built-in profile cannot be deleted.");
        if (_document.Profiles.Count <= 1)
            return false;
        var active = _document.ActiveProfileId == id;
        _document = _document with
        {
            Profiles = _document.Profiles.Where(profile => profile.Id != id).ToList(),
            ActiveProfileId = active ? "default" : _document.ActiveProfileId,
            ActiveSource = active ? ProfileActivationSource.Fallback : _document.ActiveSource
        };
        Persist();
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public NovaProfile UpdateProfileSnapshot(string id, ProfileSnapshot snapshot)
    {
        var profile = FindProfile(id);
        var updated = profile with { Snapshot = snapshot, UpdatedAt = DateTimeOffset.UtcNow };
        ReplaceProfile(updated);
        return updated;
    }

    public NovaProfile ActivateManual(string id, bool pauseAutomation = true)
    {
        var profile = FindProfile(id);
        _pendingProfileId = null;
        _pendingSince = null;
        _document = _document with
        {
            ActiveProfileId = profile.Id,
            ActiveSource = ProfileActivationSource.Manual,
            AutomationPaused = pauseAutomation
        };
        AddHistory(profile, ProfileActivationSource.Manual, "Ручное включение");
        Persist();
        Changed?.Invoke(this, EventArgs.Empty);
        return profile;
    }

    public void SetAutomationEnabled(bool enabled)
    {
        _document = _document with
        {
            AutomationEnabled = enabled,
            AutomationPaused = !enabled
        };
        if (!enabled)
            ActivateFallback("Автоматические правила выключены");
        else
            Changed?.Invoke(this, EventArgs.Empty);
    }

    public void ResumeAutomation()
    {
        _document = _document with { AutomationEnabled = true, AutomationPaused = false };
        _pendingProfileId = null;
        _pendingSince = null;
        Persist();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public NovaProfile AddRule(string profileId, ProfileRule rule)
    {
        var profile = FindProfile(profileId);
        var updated = profile with
        {
            Rules = profile.Rules.Append(rule).ToList(),
            UpdatedAt = DateTimeOffset.UtcNow
        };
        ReplaceProfile(updated);
        return updated;
    }

    public void RemoveRule(string profileId, string ruleId)
    {
        var profile = FindProfile(profileId);
        var updated = profile with
        {
            Rules = profile.Rules.Where(rule => rule.Id != ruleId).ToList(),
            UpdatedAt = DateTimeOffset.UtcNow
        };
        ReplaceProfile(updated);
    }

    public string Evaluate(
        DateTimeOffset now,
        IReadOnlyCollection<string> processes,
        bool isFullscreen,
        string? audioDeviceId)
    {
        if (!_document.AutomationEnabled || _document.AutomationPaused)
            return _document.ActiveProfileId;

        var matches = _document.Profiles
            .Select(profile => new
            {
                Profile = profile,
                Rule = profile.Rules
                    .Where(rule => rule.Enabled && RuleMatches(rule, now, processes, isFullscreen, audioDeviceId))
                    .OrderByDescending(rule => rule.Priority)
                    .ThenBy(rule => rule.Id, StringComparer.Ordinal)
                    .FirstOrDefault()
            })
            .Where(item => item.Rule is not null)
            .OrderByDescending(item => item.Rule!.Priority)
            .ThenBy(item => item.Profile.Id, StringComparer.Ordinal)
            .FirstOrDefault();

        if (matches is null)
        {
            _pendingProfileId = null;
            _pendingSince = null;
            if (_document.ActiveProfileId != "default")
            {
                var fallback = FindProfile("default");
                ActivateAutomatic(fallback, "Нет подходящих правил");
            }
            return "default";
        }

        var target = matches.Profile;
        var rule = matches.Rule!;
        if (_document.ActiveProfileId == target.Id)
        {
            _pendingProfileId = null;
            _pendingSince = null;
            return target.Id;
        }

        if (_pendingProfileId != target.Id)
        {
            _pendingProfileId = target.Id;
            _pendingSince = now;
        }

        var delay = TimeSpan.FromSeconds(Math.Clamp(rule.ActivationDelaySeconds, 0, 3600));
        if (now - _pendingSince!.Value < delay)
            return _document.ActiveProfileId;

        if (_lastAutomaticActivation is { } last &&
            now - last < TimeSpan.FromSeconds(Math.Clamp(rule.CooldownSeconds, 0, 86400)))
            return _document.ActiveProfileId;

        ActivateAutomatic(target, $"Правило «{RuleDescription(rule)}»", rule.Id);
        _pendingProfileId = null;
        _pendingSince = null;
        _lastAutomaticProfileId = target.Id;
        _lastAutomaticActivation = now;
        return target.Id;
    }

    private void ActivateAutomatic(NovaProfile profile, string reason, string? ruleId = null)
    {
        _document = _document with
        {
            ActiveProfileId = profile.Id,
            ActiveSource = ProfileActivationSource.Automatic,
            LastRuleId = ruleId
        };
        AddHistory(profile, ProfileActivationSource.Automatic, reason);
        Persist();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void ActivateFallback(string reason)
    {
        var profile = FindProfile("default");
        _document = _document with
        {
            ActiveProfileId = "default",
            ActiveSource = ProfileActivationSource.Fallback,
            LastRuleId = null
        };
        AddHistory(profile, ProfileActivationSource.Fallback, reason);
        Persist();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static bool RuleMatches(
        ProfileRule rule,
        DateTimeOffset now,
        IReadOnlyCollection<string> processes,
        bool isFullscreen,
        string? audioDeviceId)
    {
        if (!rule.Enabled)
            return false;
        return rule.Kind switch
        {
            ProfileRuleKind.Process => processes.Any(process =>
                process.Contains(rule.Value.Trim(), StringComparison.OrdinalIgnoreCase)),
            ProfileRuleKind.Fullscreen => isFullscreen == (rule.Value.Equals("enabled", StringComparison.OrdinalIgnoreCase) ||
                rule.Value.Equals("true", StringComparison.OrdinalIgnoreCase)),
            ProfileRuleKind.AudioDevice => !string.IsNullOrWhiteSpace(rule.Value) &&
                string.Equals(rule.Value, audioDeviceId, StringComparison.OrdinalIgnoreCase),
            ProfileRuleKind.Time => MatchesTimeAndWeekdays(rule, now),
            _ => false
        };
    }

    private static bool MatchesTimeAndWeekdays(ProfileRule rule, DateTimeOffset now)
    {
        if (rule.StartTime is not { } start || rule.EndTime is not { } end)
            return false;
        var day = ((int)now.DayOfWeek == 0 ? 7 : (int)now.DayOfWeek).ToString();
        if (!string.IsNullOrWhiteSpace(rule.Weekdays) && !rule.Weekdays.Contains(day, StringComparison.Ordinal))
            return false;
        var local = TimeOnly.FromTimeSpan(now.TimeOfDay);
        return start <= end ? local >= start && local <= end : local >= start || local <= end;
    }

    private void AddHistory(NovaProfile profile, ProfileActivationSource source, string reason)
    {
        var entry = new ProfileHistoryEntry(
            Guid.NewGuid().ToString("N"),
            profile.Id,
            profile.Name,
            source,
            reason,
            DateTimeOffset.UtcNow);
        _document = _document with
        {
            History = new[] { entry }.Concat(_document.History).Take(100).ToList()
        };
    }

    private NovaProfile FindProfile(string id) =>
        _document.Profiles.FirstOrDefault(profile => profile.Id == id)
        ?? throw new KeyNotFoundException($"Profile '{id}' not found.");

    private void ReplaceProfile(NovaProfile updated)
    {
        _document = _document with
        {
            Profiles = _document.Profiles
                .Select(profile => profile.Id == updated.Id ? updated : profile)
                .ToList()
        };
        Persist();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Persist()
    {
        _store.Save(_document);
    }

    private static string NormalizeName(string name)
    {
        var normalized = name?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
            throw new ArgumentException("Profile name cannot be empty.", nameof(name));
        return normalized.Length <= 40 ? normalized : normalized[..40];
    }

    private static string RuleDescription(ProfileRule rule) => rule.Kind switch
    {
        ProfileRuleKind.Process => $"процесс: {rule.Value}",
        ProfileRuleKind.Time => $"{rule.StartTime:hh\\:mm}–{rule.EndTime:hh\\:mm}",
        ProfileRuleKind.Fullscreen => "fullscreen",
        ProfileRuleKind.AudioDevice => $"аудиоустройство: {rule.Value}",
        _ => rule.Kind.ToString()
    };

    public static ProfileDocument CreateDefaultDocument()
    {
        var now = DateTimeOffset.UtcNow;
        var defaultProfile = new NovaProfile("default", "Default", true,
            ProfileSnapshot.Default, [], now, now);
        var work = new NovaProfile("work", "Work", true,
            ProfileSnapshot.Default with { SelectedWidget = "Schedule", SizeScale = 0.95, EnableAudioReactive = false },
            [], now, now);
        var gaming = new NovaProfile("gaming", "Gaming", true,
            ProfileSnapshot.Default with { SelectedWidget = "Monitoring", EnableAudioReactive = true, EnableMotion = false, KeepOnTop = false },
            [], now, now);
        var focus = new NovaProfile("focus", "Focus", true,
            ProfileSnapshot.Default with { SelectedWidget = "Timer", NotificationsEnabled = false, SuppressNotificationToasts = true, EnableAudioReactive = false },
            [], now, now);
        var multimedia = new NovaProfile("multimedia", "Multimedia", true,
            ProfileSnapshot.Default with { SelectedWidget = "Music", EnableAudioReactive = true, AudioReactiveIntensity = 1.5 },
            [], now, now);
        return new ProfileDocument(
            "NOVA-PROFILES",
            1,
            [defaultProfile, work, gaming, focus, multimedia],
            "default",
            ProfileActivationSource.Fallback,
            true,
            false,
            null,
            []);
    }

    public static IReadOnlyList<NovaProfile> MergeBuiltInProfiles(IReadOnlyList<NovaProfile> existing)
    {
        var defaults = CreateDefaultDocument().Profiles;
        var result = existing.ToList();
        foreach (var builtIn in defaults)
        {
            var index = result.FindIndex(profile => profile.Id == builtIn.Id);
            if (index < 0)
                result.Insert(0, builtIn);
            else
                result[index] = result[index] with { IsBuiltIn = true };
        }
        return result;
    }
}
