using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Nova.WindowsClient.Services;

namespace Nova.WindowsClient;

public partial class ShellWindow
{
    private readonly AudioMixerService _audioMixerService = new();
    private readonly ObservableCollection<AudioSessionRow> _audioSessionRows = [];
    private readonly DispatcherTimer _audioMixerRefreshTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _audioMixerVolumeTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(140) };
    private readonly Dictionary<string, PendingAudioVolume> _pendingAudioVolumes = new(StringComparer.Ordinal);
    private bool _audioMixerUiUpdating;
    private bool _audioMixerRefreshBusy;
    private bool _audioMixerDisposed;

    private void InitializeAudioMixerUi()
    {
        AudioSessionsList.ItemsSource = _audioSessionRows;
        _audioMixerRefreshTimer.Tick += async (_, _) => await RefreshAudioMixerAsync();
        _audioMixerVolumeTimer.Tick += async (_, _) => await ApplyPendingAudioVolumesAsync();
        _audioMixerService.DevicesChanged += AudioMixerService_DevicesChanged;
    }

    private void StartAudioMixer()
    {
        _audioMixerRefreshTimer.Start();
        _ = RefreshAudioMixerAsync();
    }

    private void AudioMixerService_DevicesChanged(object? sender, EventArgs e)
    {
        if (_islandDisposed || _widgetHost.SelectedWidget?.Id != "Audio")
            return;
        Dispatcher.BeginInvoke(new Action(() => _ = RefreshAudioMixerAsync()), DispatcherPriority.Background);
    }

    private async Task RefreshAudioMixerAsync()
    {
        if (_audioMixerRefreshBusy || _audioMixerDisposed || _islandDisposed)
            return;
        _audioMixerRefreshBusy = true;
        try
        {
            var snapshot = await _audioMixerService.RefreshAsync();
            if (_audioMixerDisposed || _islandDisposed)
                return;
            _audioMixerUiUpdating = true;
            AudioOutputDeviceCombo.ItemsSource = snapshot.Outputs;
            AudioOutputDeviceCombo.SelectedValue = snapshot.DefaultOutputId;
            AudioInputDeviceCombo.ItemsSource = snapshot.Inputs;
            AudioInputDeviceCombo.SelectedValue = snapshot.DefaultInputId;
            ReconcileAudioSessions(snapshot.Sessions);
            AudioMixerStatusText.Text = snapshot.Error ??
                $"Активных сессий: {_audioSessionRows.Count}. Изменения устройств обновляются автоматически.";
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or UnauthorizedAccessException or Win32Exception)
        {
            AudioMixerStatusText.Text = $"Не удалось прочитать Windows Audio: {ex.Message}";
        }
        finally
        {
            _audioMixerUiUpdating = false;
            _audioMixerRefreshBusy = false;
        }
    }

    private void ReconcileAudioSessions(IReadOnlyList<AudioMixerService.AudioSession> sessions)
    {
        var source = sessions.ToDictionary(session => session.Id, StringComparer.Ordinal);
        for (var index = _audioSessionRows.Count - 1; index >= 0; index--)
        {
            if (!source.ContainsKey(_audioSessionRows[index].Id))
                _audioSessionRows.RemoveAt(index);
        }

        foreach (var session in sessions)
        {
            var row = _audioSessionRows.FirstOrDefault(item => item.Id == session.Id);
            if (row is null)
            {
                _audioSessionRows.Add(new AudioSessionRow(session));
                continue;
            }
            row.Update(session);
        }
    }

    private async void AudioDeviceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_audioMixerUiUpdating || sender is not System.Windows.Controls.ComboBox
            { SelectedItem: AudioMixerService.AudioEndpoint endpoint } comboBox)
            return;

        var flow = ReferenceEquals(comboBox, AudioOutputDeviceCombo)
            ? NAudio.CoreAudioApi.DataFlow.Render
            : NAudio.CoreAudioApi.DataFlow.Capture;
        try
        {
            AudioMixerStatusText.Text = "Изменение системного аудиоустройства…";
            await _audioMixerService.SetDefaultDeviceAsync(endpoint.Id, flow);
            AudioMixerStatusText.Text = "Системное устройство по умолчанию изменено.";
            await RefreshAudioMixerAsync();
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or UnauthorizedAccessException or Win32Exception)
        {
            AudioMixerStatusText.Text = $"Не удалось изменить системное устройство: {ex.Message}";
            await RefreshAudioMixerAsync();
        }
    }

    private void AudioSessionVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_audioMixerUiUpdating || sender is not Slider { DataContext: AudioSessionRow row })
            return;
        row.Volume = (float)e.NewValue;
        _pendingAudioVolumes[row.Id] = new PendingAudioVolume(row.DeviceId, row.SessionId, row.Volume);
        _audioMixerVolumeTimer.Stop();
        _audioMixerVolumeTimer.Start();
    }

    private async Task ApplyPendingAudioVolumesAsync()
    {
        _audioMixerVolumeTimer.Stop();
        var pending = _pendingAudioVolumes.Values.ToArray();
        _pendingAudioVolumes.Clear();
        foreach (var item in pending)
        {
            try
            {
                await _audioMixerService.SetSessionVolumeAsync(item.DeviceId, item.SessionId, item.Volume);
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException or UnauthorizedAccessException or Win32Exception)
            {
                AudioMixerStatusText.Text = $"Не удалось изменить громкость приложения: {ex.Message}";
            }
        }
    }

    private async void AudioSessionMute_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: AudioSessionRow row })
            return;
        try
        {
            await _audioMixerService.SetSessionMuteAsync(row.DeviceId, row.SessionId, !row.IsMuted);
            row.IsMuted = !row.IsMuted;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or UnauthorizedAccessException or Win32Exception)
        {
            AudioMixerStatusText.Text = $"Не удалось изменить Mute приложения: {ex.Message}";
        }
    }

    private void DisposeAudioMixerUi()
    {
        if (_audioMixerDisposed)
            return;
        _audioMixerDisposed = true;
        _audioMixerRefreshTimer.Stop();
        _audioMixerVolumeTimer.Stop();
        _audioMixerService.DevicesChanged -= AudioMixerService_DevicesChanged;
        _audioMixerService.Dispose();
    }

    private sealed record PendingAudioVolume(string DeviceId, string SessionId, float Volume);

    private sealed class AudioSessionRow : INotifyPropertyChanged
    {
        private string _deviceId;
        private string _name;
        private System.Windows.Media.Imaging.BitmapSource? _icon;
        private float _volume;
        private bool _isMuted;
        private readonly string _sessionId;

        public AudioSessionRow(AudioMixerService.AudioSession session)
        {
            Id = session.Id;
            _sessionId = session.SessionId;
            _deviceId = session.DeviceId;
            _name = session.Name;
            _icon = session.Icon;
            _volume = session.Volume;
            _isMuted = session.IsMuted;
        }

        public string Id { get; }
        public string SessionId => _sessionId;
        public string DeviceId { get => _deviceId; private set => Set(ref _deviceId, value); }
        public string Name { get => _name; private set => Set(ref _name, value); }
        public string Initial => string.IsNullOrWhiteSpace(Name) ? "?" : Name[..1].ToUpperInvariant();
        public System.Windows.Media.Imaging.BitmapSource? Icon { get => _icon; private set => Set(ref _icon, value); }
        public float Volume { get => _volume; set => Set(ref _volume, Math.Clamp(value, 0, 1)); }
        public bool IsMuted { get => _isMuted; set => Set(ref _isMuted, value); }
        public string MuteText => IsMuted ? "Включить" : "Mute";

        public event PropertyChangedEventHandler? PropertyChanged;

        public void Update(AudioMixerService.AudioSession session)
        {
            DeviceId = session.DeviceId;
            Name = session.Name;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Initial)));
            Icon = session.Icon;
            Volume = session.Volume;
            IsMuted = session.IsMuted;
        }

        private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
                return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            if (propertyName == nameof(IsMuted))
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MuteText)));
        }
    }
}
