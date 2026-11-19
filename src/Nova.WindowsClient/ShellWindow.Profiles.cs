using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Nova.WindowsClient.Services;

namespace Nova.WindowsClient;

public partial class ShellWindow
{
    private readonly ProfileService _profileService;
    private readonly DispatcherTimer _profileTimer =
        new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(5) };
    private readonly ObservableCollection<ProfileRow> _profileRows = [];
    private readonly ObservableCollection<ProfileRuleRow> _profileRuleRows = [];
    private readonly ObservableCollection<ProfileHistoryRow> _profileHistoryRows = [];
    private string? _lastAppliedProfileId;
    private bool _applyingProfile;
    private bool _profileUiInitialized;
    private string? _profileAudioDeviceId;

    private sealed record ProfileRow(string Id, string Name, bool IsBuiltIn)
    {
        public override string ToString() => IsBuiltIn ? $"{Name} · built-in" : Name;
    }

    private sealed record ProfileRuleRow(string Id, string Text);

    private sealed record ProfileHistoryRow(string Text);

    private void InitializeProfilesUi()
    {
        if (_profileUiInitialized)
            return;
        _profileUiInitialized = true;
        ProfileList.ItemsSource = _profileRows;
        ProfileRuleList.ItemsSource = _profileRuleRows;
        ProfileHistoryList.ItemsSource = _profileHistoryRows;
        ProfileQuickCombo.ItemsSource = _profileRows;
        ProfileQuickCombo.DisplayMemberPath = "Name";
        ProfileAutomationCheckBox.IsChecked = _profileService.AutomationEnabled;
        ProfileResumeAutomationButton.Visibility = _profileService.AutomationPaused
            ? Visibility.Visible
            : Visibility.Collapsed;
        _profileService.Changed += ProfileService_Changed;
        _profileTimer.Tick += async (_, _) => await EvaluateProfilesAsync();
        _audioMixerService.DevicesChanged += ProfileAudioDevicesChanged;
        UpdateProfileUi();
        ApplyActiveProfile(force: true);
    }

    private void ProfileAudioDevicesChanged(object? sender, EventArgs e) =>
        _ = RefreshProfileAudioDeviceAsync();

    private async Task RefreshProfileAudioDeviceAsync()
    {
        try
        {
            var snapshot = await _audioMixerService.RefreshAsync();
            _profileAudioDeviceId = snapshot.DefaultOutputId;
            await EvaluateProfilesAsync();
        }
        catch (Exception ex) when (
            ex is InvalidOperationException or IOException or UnauthorizedAccessException or
                System.Runtime.InteropServices.COMException)
        {
            _profileAudioDeviceId = null;
        }
    }

    private async Task EvaluateProfilesAsync()
    {
        if (_profileService.AutomationPaused || !_profileService.AutomationEnabled)
            return;
        var processes = await Task.Run(() =>
            Process.GetProcesses()
                .Select(process =>
                {
                    try { return process.ProcessName; }
                    catch { return string.Empty; }
                    finally { process.Dispose(); }
                })
                .Where(name => name.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray());
        if (_profileAudioDeviceId is null)
        {
            try
            {
                var snapshot = await _audioMixerService.RefreshAsync();
                _profileAudioDeviceId = snapshot.DefaultOutputId;
            }
            catch
            {
                _profileAudioDeviceId = null;
            }
        }
        _profileService.Evaluate(
            DateTimeOffset.UtcNow,
            processes,
            _state == PanelState.Fullscreen,
            _profileAudioDeviceId);
    }

    private void ProfileService_Changed(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            UpdateProfileUi();
            ApplyActiveProfile(force: false);
        }), DispatcherPriority.Background);
    }

    private void UpdateProfileUi()
    {
        _profileRows.Clear();
        foreach (var profile in _profileService.Profiles)
            _profileRows.Add(new ProfileRow(profile.Id, profile.Name, profile.IsBuiltIn));
        ProfileQuickCombo.ItemsSource = null;
        ProfileQuickCombo.ItemsSource = _profileRows;

        var active = _profileService.ActiveProfile;
        ProfileCurrentText.Text = $"Текущий профиль: {active.Name}";
        ProfileSourceText.Text = _profileService.ActiveSource switch
        {
            ProfileActivationSource.Manual => "Источник: ручное включение",
            ProfileActivationSource.Automatic => $"Источник: автоматическое правило {_profileService.PendingRuleDescription}",
            ProfileActivationSource.Fallback => "Источник: запасной профиль Default",
            _ => "Источник: неизвестен"
        };
        ProfilePendingText.Text = _profileService.CurrentPendingProfile is { } pending
            ? $"Ожидание активации: {pending.Name}"
            : string.Empty;
        ProfileAutomationCheckBox.IsChecked = _profileService.AutomationEnabled;
        ProfileResumeAutomationButton.Visibility = _profileService.AutomationPaused
            ? Visibility.Visible
            : Visibility.Collapsed;

        _applyingProfile = true;
        try
        {
            ProfileQuickCombo.SelectedItem = _profileRows.FirstOrDefault(row => row.Id == active.Id);
        }
        finally
        {
            _applyingProfile = false;
        }
        ProfileStatusText.Text = _profileService.LastError ??
            (_profileService.AutomationPaused
                ? "Автоматизация приостановлена; выбранный профиль управляется вручную."
                : "Автоматизация активна.");

        _profileHistoryRows.Clear();
        foreach (var entry in _profileService.History.Take(20))
        {
            var source = entry.Source switch
            {
                ProfileActivationSource.Manual => "руч.",
                ProfileActivationSource.Automatic => "авто",
                _ => "fallback"
            };
            _profileHistoryRows.Add(new ProfileHistoryRow(
                $"{entry.ActivatedAt.ToLocalTime():dd.MM HH:mm} · {entry.ProfileName} · {source} · {entry.Reason}"));
        }

        UpdateSelectedProfileRules();
    }

    private void UpdateSelectedProfileRules()
    {
        _profileRuleRows.Clear();
        NovaProfile? profile = null;
        if (ProfileList.SelectedItem is ProfileRow row)
            profile = _profileService.Profiles.FirstOrDefault(item => item.Id == row.Id);
        profile ??= _profileService.ActiveProfile;
        if (profile is null)
            return;
        if (ProfileNameBox.Text.Length == 0)
            ProfileNameBox.Text = profile.Name;
        foreach (var rule in profile.Rules
                     .OrderByDescending(item => item.Priority)
                     .ThenBy(item => item.Id, StringComparer.Ordinal))
        {
            var kind = rule.Kind switch
            {
                ProfileRuleKind.Process => "Процесс",
                ProfileRuleKind.Time => "Время",
                ProfileRuleKind.Fullscreen => "Fullscreen",
                ProfileRuleKind.AudioDevice => "Аудиоустройство",
                _ => rule.Kind.ToString()
            };
            var detail = rule.Kind == ProfileRuleKind.Time
                ? $"{rule.StartTime:hh\\:mm}–{rule.EndTime:hh\\:mm}, дни {rule.Weekdays}"
                : rule.Value;
            _profileRuleRows.Add(new ProfileRuleRow(
                rule.Id,
                $"[{(rule.Enabled ? "on" : "off")}] P{rule.Priority} · {kind}: {detail} · задержка {rule.ActivationDelaySeconds}s · cooldown {rule.CooldownSeconds}s"));
        }
    }

    private void ApplyActiveProfile(bool force)
    {
        var active = _profileService.ActiveProfile;
        if (!force && _lastAppliedProfileId == active.Id)
            return;
        _lastAppliedProfileId = active.Id;
        _applyingProfile = true;
        try
        {
            ApplyProfileSnapshot(active.Snapshot);
        }
        finally
        {
            _applyingProfile = false;
        }
    }

    private void ApplyProfileSnapshot(ProfileSnapshot snapshot)
    {
        _dockEdge = snapshot.DockEdge is "top" or "bottom" or "left" or "right"
            ? snapshot.DockEdge
            : null;
        _sizeScale = Math.Clamp(snapshot.SizeScale, 0.75, 1.1);
        _widgetIconSize = Math.Clamp(snapshot.WidgetIconSize, 20, 50);
        _keepOnTop = snapshot.KeepOnTop;
        _hoverExpandEnabled = snapshot.HoverExpandEnabled;
        _timerCompletionSound = snapshot.TimerCompletionSound;
        _enableMotion = snapshot.EnableMotion;
        _enableAudioReactive = snapshot.EnableAudioReactive;
        _audioReactiveIntensity = Math.Clamp(snapshot.AudioReactiveIntensity, 0.5, 2);
        _browserAdaptationEnabled = snapshot.BrowserAdaptationEnabled;
        _showMonitorCpu = snapshot.ShowMonitorCpu;
        _showMonitorGpu = snapshot.ShowMonitorGpu;
        _showMonitorRam = snapshot.ShowMonitorRam;
        _showMonitorNetwork = snapshot.ShowMonitorNetwork;
        _showMonitorDisk = snapshot.ShowMonitorDisk;

        Topmost = _keepOnTop;
        SizeSlider.Value = _sizeScale;
        WidgetIconSizeSlider.Value = _widgetIconSize;
        KeepOnTopCheckBox.IsChecked = _keepOnTop;
        HoverExpandCheckBox.IsChecked = _hoverExpandEnabled;
        TimerCompletionSoundCheckBox.IsChecked = _timerCompletionSound;
        EnableMotionCheckBox.IsChecked = _enableMotion;
        EnableAudioReactiveCheckBox.IsChecked = _enableAudioReactive;
        AudioReactiveIntensitySlider.Value = _audioReactiveIntensity;
        BrowserAdaptationCheckBox.IsChecked = _browserAdaptationEnabled;
        ShowMonitorCpuCheckBox.IsChecked = _showMonitorCpu;
        ShowMonitorGpuCheckBox.IsChecked = _showMonitorGpu;
        ShowMonitorRamCheckBox.IsChecked = _showMonitorRam;
        ShowMonitorNetworkCheckBox.IsChecked = _showMonitorNetwork;
        ShowMonitorDiskCheckBox.IsChecked = _showMonitorDisk;
        _widgetRegistry.SetIconSize(_widgetIconSize);
        _widgetRegistry.SetVisibleWidgets(snapshot.VisibleWidgetIds);
        _browserWindowAdapter.SetEnabled(_browserAdaptationEnabled);
        UpdateDockUi();
        UpdateAdaptiveLayout();
        UpdateSystemMetricVisibility();
        UpdateAudioReactiveUi();
        UpdateProfileIndicator();

        if (!string.IsNullOrWhiteSpace(snapshot.SelectedWidget) &&
            _widgetRegistry.Widgets.Any(widget => widget.Id == snapshot.SelectedWidget))
            SelectWidget(snapshot.SelectedWidget, activate: true, save: false);
        ApplyDimensions();
        SchedulePreferenceSave();
    }

    private ProfileSnapshot CaptureProfileSnapshot() => new(
        _dockEdge,
        _sizeScale,
        _widgetIconSize,
        _keepOnTop,
        _hoverExpandEnabled,
        _timerCompletionSound,
        _enableMotion,
        _enableAudioReactive,
        _audioReactiveIntensity,
        _browserAdaptationEnabled,
        _widgetHost.SelectedWidget?.Id,
        _widgetRegistry.Widgets.Where(widget => widget.IsVisible).Select(widget => widget.Id).ToList(),
        _profileService.ActiveProfile.Snapshot.NotificationsEnabled,
        _profileService.ActiveProfile.Snapshot.SuppressNotificationToasts,
        _showMonitorCpu,
        _showMonitorGpu,
        _showMonitorRam,
        _showMonitorNetwork,
        _showMonitorDisk);

    private void UpdateProfileIndicator()
    {
        IslandHeaderSubtitle.Text =
            $"Профиль: {_profileService.ActiveProfile.Name} · " +
            (_profileService.ActiveSource == ProfileActivationSource.Automatic
                ? "авто"
                : _profileService.ActiveSource == ProfileActivationSource.Manual
                    ? "вручную"
                    : "по умолчанию");
    }

    private void ProfileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProfileList.SelectedItem is ProfileRow row)
        {
            var profile = _profileService.Profiles.FirstOrDefault(item => item.Id == row.Id);
            if (profile is not null)
                ProfileNameBox.Text = profile.Name;
        }
        UpdateSelectedProfileRules();
    }

    private void ProfileQuickCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_applyingProfile)
            return;
        if (ProfileQuickCombo.SelectedItem is ProfileRow row)
        {
            _profileService.ActivateManual(row.Id, pauseAutomation: true);
            ProfileStatusText.Text = $"Профиль «{row.Name}» включён вручную.";
        }
    }

    private void ProfileResumeAutomation_Click(object sender, RoutedEventArgs e)
    {
        _profileService.ResumeAutomation();
        ProfileStatusText.Text = "Автоматизация возобновлена.";
    }

    private void ProfileAutomationCheckBox_Click(object sender, RoutedEventArgs e)
    {
        _profileService.SetAutomationEnabled(ProfileAutomationCheckBox.IsChecked == true);
    }

    private void ProfileCreate_Click(object sender, RoutedEventArgs e)
    {
        var profile = _profileService.CreateProfile(ProfileNameBox.Text);
        ProfileList.SelectedItem = _profileRows.FirstOrDefault(row => row.Id == profile.Id);
        ProfileStatusText.Text = $"Профиль «{profile.Name}» создан.";
    }

    private void ProfileDuplicate_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileList.SelectedItem is not ProfileRow row)
            return;
        var profile = _profileService.DuplicateProfile(row.Id);
        ProfileList.SelectedItem = _profileRows.FirstOrDefault(item => item.Id == profile.Id);
        ProfileStatusText.Text = $"Профиль «{profile.Name}» создан.";
    }

    private void ProfileRename_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileList.SelectedItem is not ProfileRow row)
            return;
        var profile = _profileService.RenameProfile(row.Id, ProfileNameBox.Text);
        ProfileStatusText.Text = $"Профиль переименован в «{profile.Name}».";
    }

    private void ProfileDelete_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileList.SelectedItem is not ProfileRow row)
            return;
        if (row.IsBuiltIn)
        {
            ProfileStatusText.Text = "Built-in профили нельзя удалить.";
            return;
        }
        if (System.Windows.MessageBox.Show(
                this,
                $"Удалить профиль «{row.Name}»?",
                "NOVA Profiles",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        _profileService.DeleteProfile(row.Id);
        ProfileStatusText.Text = $"Профиль «{row.Name}» удалён.";
    }

    private void ProfileActivate_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileList.SelectedItem is not ProfileRow row)
            return;
        _profileService.ActivateManual(row.Id, pauseAutomation: true);
        ProfileStatusText.Text = $"Профиль «{row.Name}» включён вручную.";
    }

    private void ProfileCapture_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileList.SelectedItem is not ProfileRow row)
            return;
        _profileService.UpdateProfileSnapshot(row.Id, CaptureProfileSnapshot());
        ProfileStatusText.Text = $"Текущие настройки сохранены в профиль «{row.Name}».";
    }

    private void ProfileRuleAdd_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileList.SelectedItem is not ProfileRow profileRow)
            return;
        if (!TryParseProfileRule(out var rule, out var error))
        {
            ProfileStatusText.Text = error;
            return;
        }
        _profileService.AddRule(profileRow.Id, rule);
        ProfileStatusText.Text = "Правило профиля добавлено.";
    }

    private void ProfileRuleRemove_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileList.SelectedItem is not ProfileRow profileRow ||
            ProfileRuleList.SelectedItem is not ProfileRuleRow ruleRow)
            return;
        _profileService.RemoveRule(profileRow.Id, ruleRow.Id);
        ProfileStatusText.Text = "Правило профиля удалено.";
    }

    private void DisposeProfilesUi()
    {
        _profileTimer.Stop();
        _profileService.Changed -= ProfileService_Changed;
        _audioMixerService.DevicesChanged -= ProfileAudioDevicesChanged;
    }

    private bool TryParseProfileRule(out ProfileRule rule, out string error)
    {
        rule = default!;
        error = string.Empty;
        if (ProfileRuleKindCombo.SelectedItem is not ComboBoxItem item ||
            item.Tag is not string kindTag)
        {
            error = "Выберите тип условия.";
            return false;
        }
        if (!int.TryParse(ProfileRulePriorityBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var priority))
        {
            error = "Приоритет должен быть целым числом.";
            return false;
        }
        if (!int.TryParse(ProfileRuleDelayBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var delay) ||
            delay < 0 || delay > 3600)
        {
            error = "Задержка должна быть от 0 до 3600 секунд.";
            return false;
        }
        if (!int.TryParse(ProfileRuleCooldownBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var cooldown) ||
            cooldown < 0 || cooldown > 86400)
        {
            error = "Cooldown должен быть от 0 до 86400 секунд.";
            return false;
        }
        var kind = kindTag switch
        {
            "Process" => ProfileRuleKind.Process,
            "Time" => ProfileRuleKind.Time,
            "Fullscreen" => ProfileRuleKind.Fullscreen,
            "AudioDevice" => ProfileRuleKind.AudioDevice,
            _ => (ProfileRuleKind)(-1)
        };
        if ((int)kind < 0)
        {
            error = "Неизвестный тип условия.";
            return false;
        }
        var value = ProfileRuleValueBox.Text.Trim();
        TimeOnly? start = null;
        TimeOnly? end = null;
        string? weekdays = null;
        if (kind == ProfileRuleKind.Time)
        {
            if (!TimeOnly.TryParse(ProfileRuleStartBox.Text.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedStart) ||
                !TimeOnly.TryParse(ProfileRuleEndBox.Text.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedEnd))
            {
                error = "Введите время начала и окончания в формате ЧЧ:ММ.";
                return false;
            }
            start = parsedStart;
            end = parsedEnd;
            weekdays = new string(ProfileRuleWeekdaysBox.Text
                .Where(character => character is >= '1' and <= '7')
                .Distinct()
                .ToArray());
            if (weekdays.Length == 0)
            {
                error = "Укажите дни недели цифрами 1–7.";
                return false;
            }
        }
        else if (string.IsNullOrWhiteSpace(value))
        {
            error = "Укажите значение условия.";
            return false;
        }
        rule = new ProfileRule(
            Guid.NewGuid().ToString("N"),
            kind,
            value,
            true,
            priority,
            delay,
            cooldown,
            start,
            end,
            weekdays);
        return true;
    }
}
