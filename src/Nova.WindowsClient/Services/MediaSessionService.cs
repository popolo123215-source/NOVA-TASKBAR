using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using Windows.Foundation;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace Nova.WindowsClient.Services;

// Adapted from Atoll's MIT-licensed MediaService; see licenses/Atoll.LICENSE.
internal sealed record MediaSnapshot(
    string Title,
    string Artist,
    string Album,
    string Source,
    BitmapImage? Artwork,
    bool IsPlaying,
    TimeSpan Position,
    DateTimeOffset PositionUpdatedAt,
    TimeSpan Duration)
{
    public TimeSpan CurrentPosition
    {
        get
        {
            var position = Position;
            if (IsPlaying && PositionUpdatedAt != default)
                position += DateTimeOffset.UtcNow - PositionUpdatedAt;
            if (position < TimeSpan.Zero) return TimeSpan.Zero;
            return Duration > TimeSpan.Zero && position > Duration ? Duration : position;
        }
    }
}

internal sealed class MediaSessionService : IDisposable
{
    private readonly object _gate = new();
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private TypedEventHandler<GlobalSystemMediaTransportControlsSessionManager, CurrentSessionChangedEventArgs>? _sessionChangedHandler;
    private TypedEventHandler<GlobalSystemMediaTransportControlsSession, MediaPropertiesChangedEventArgs>? _propertiesChangedHandler;
    private TypedEventHandler<GlobalSystemMediaTransportControlsSession, PlaybackInfoChangedEventArgs>? _playbackChangedHandler;
    private TypedEventHandler<GlobalSystemMediaTransportControlsSession, TimelinePropertiesChangedEventArgs>? _timelineChangedHandler;
    private MediaSnapshot? _current;
    private bool _disposed;

    public event EventHandler? Changed;
    public string? Error { get; private set; }

    public MediaSnapshot? Current
    {
        get { lock (_gate) return _current; }
    }

    public async Task InitializeAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _sessionChangedHandler = (_, _) => _ = RefreshSessionAsync();
            _manager.CurrentSessionChanged += _sessionChangedHandler;
            await RefreshSessionAsync();
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidOperationException)
        {
            Error = ex.Message;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task RefreshSessionAsync()
    {
        if (_disposed) return;
        if (_manager is null)
        {
            await InitializeAsync();
            return;
        }

        try
        {
            var sessions = _manager.GetSessions();
            var playing = sessions.FirstOrDefault(session =>
                session.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing);
            var selected = playing ?? _manager.GetCurrentSession();
            if (!ReferenceEquals(selected, _session))
                Attach(selected);
            else if (selected is not null)
                UpdatePlaybackAndTimeline(selected);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            Error = ex.Message;
            Attach(null);
        }
    }

    private void Attach(GlobalSystemMediaTransportControlsSession? session)
    {
        if (_session is not null)
        {
            if (_propertiesChangedHandler is not null) _session.MediaPropertiesChanged -= _propertiesChangedHandler;
            if (_playbackChangedHandler is not null) _session.PlaybackInfoChanged -= _playbackChangedHandler;
            if (_timelineChangedHandler is not null) _session.TimelinePropertiesChanged -= _timelineChangedHandler;
        }

        _session = session;
        _propertiesChangedHandler = null;
        _playbackChangedHandler = null;
        _timelineChangedHandler = null;
        if (session is null)
        {
            SetCurrent(null);
            return;
        }

        _propertiesChangedHandler = (_, _) => _ = ReadPropertiesAsync(session);
        _playbackChangedHandler = (_, _) => UpdatePlaybackAndTimeline(session);
        _timelineChangedHandler = (_, _) => UpdatePlaybackAndTimeline(session);
        session.MediaPropertiesChanged += _propertiesChangedHandler;
        session.PlaybackInfoChanged += _playbackChangedHandler;
        session.TimelinePropertiesChanged += _timelineChangedHandler;
        SetCurrent(new MediaSnapshot("", "", "", PrettySource(session.SourceAppUserModelId ?? ""), null, false, TimeSpan.Zero, default, TimeSpan.Zero));
        UpdatePlaybackAndTimeline(session);
        _ = ReadPropertiesAsync(session);
    }

    private void UpdatePlaybackAndTimeline(GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            var playback = session.GetPlaybackInfo();
            var timeline = session.GetTimelineProperties();
            var old = Current;
            SetCurrent(new MediaSnapshot(
                old?.Title ?? "",
                old?.Artist ?? "",
                old?.Album ?? "",
                PrettySource(session.SourceAppUserModelId ?? ""),
                old?.Artwork,
                playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                timeline.Position,
                timeline.LastUpdatedTime,
                timeline.EndTime > timeline.StartTime ? timeline.EndTime - timeline.StartTime : TimeSpan.Zero));
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            Error = ex.Message;
        }
    }

    private async Task ReadPropertiesAsync(GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            var properties = await session.TryGetMediaPropertiesAsync();
            if (properties is null || _disposed || session != _session) return;
            var artwork = properties.Thumbnail is null ? null : await LoadArtworkAsync(properties.Thumbnail);
            var old = Current;
            SetCurrent(new MediaSnapshot(
                properties.Title ?? "",
                properties.Artist ?? "",
                properties.AlbumTitle ?? "",
                old?.Source ?? PrettySource(session.SourceAppUserModelId ?? ""),
                artwork,
                old?.IsPlaying ?? false,
                old?.Position ?? TimeSpan.Zero,
                old?.PositionUpdatedAt ?? default,
                old?.Duration ?? TimeSpan.Zero));
            Error = null;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or UnauthorizedAccessException or IOException or ArgumentException or NotSupportedException)
        {
            Error = ex.Message;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private static async Task<BitmapImage?> LoadArtworkAsync(IRandomAccessStreamReference reference)
    {
        using var stream = await reference.OpenReadAsync();
        if (stream.Size == 0 || stream.Size > 8 * 1024 * 1024) return null;
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        await reader.LoadAsync((uint)stream.Size);
        var bytes = new byte[(int)stream.Size];
        reader.ReadBytes(bytes);

        using var memory = new MemoryStream(bytes, writable: false);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = 256;
        image.StreamSource = memory;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private void SetCurrent(MediaSnapshot? snapshot)
    {
        lock (_gate) _current = snapshot;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static string PrettySource(string appId)
    {
        if (string.IsNullOrWhiteSpace(appId)) return "Медиаплеер Windows";
        var separator = appId.LastIndexOf('!');
        var value = separator >= 0 ? appId[(separator + 1)..] : appId;
        if (value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) value = value[..^4];
        return value.Length > 0 ? value : "Медиаплеер Windows";
    }

    public Task<bool> TogglePlayPauseAsync() => ExecuteAsync(session => session.TryTogglePlayPauseAsync());
    public Task<bool> SkipNextAsync() => ExecuteAsync(session => session.TrySkipNextAsync());
    public Task<bool> SkipPreviousAsync() => ExecuteAsync(session => session.TrySkipPreviousAsync());

    public async Task<bool> SeekAsync(TimeSpan position)
    {
        var session = _session;
        if (session is null) return false;
        try
        {
            var duration = Current?.Duration ?? TimeSpan.Zero;
            var target = duration > TimeSpan.Zero ? TimeSpan.FromTicks(Math.Clamp(position.Ticks, 0, duration.Ticks)) : position;
            return await session.TryChangePlaybackPositionAsync(target.Ticks);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            Error = ex.Message;
            Changed?.Invoke(this, EventArgs.Empty);
            return false;
        }
    }

    private async Task<bool> ExecuteAsync(Func<GlobalSystemMediaTransportControlsSession, IAsyncOperation<bool>> action)
    {
        var session = _session;
        if (session is null) return false;
        try { return await action(session); }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            Error = ex.Message;
            Changed?.Invoke(this, EventArgs.Empty);
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_manager is not null && _sessionChangedHandler is not null)
            _manager.CurrentSessionChanged -= _sessionChangedHandler;
        if (_session is not null)
        {
            if (_propertiesChangedHandler is not null) _session.MediaPropertiesChanged -= _propertiesChangedHandler;
            if (_playbackChangedHandler is not null) _session.PlaybackInfoChanged -= _playbackChangedHandler;
            if (_timelineChangedHandler is not null) _session.TimelinePropertiesChanged -= _timelineChangedHandler;
        }
        _manager = null;
        _session = null;
    }
}
