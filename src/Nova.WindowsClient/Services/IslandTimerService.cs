using System.Diagnostics;

namespace Nova.WindowsClient.Services;

internal enum IslandTimerMode { Countdown, Stopwatch }

internal sealed class IslandTimerService
{
    private readonly Stopwatch _stopwatch = new();
    private TimeSpan _countdownDuration = TimeSpan.FromMinutes(5);

    public IslandTimerMode Mode { get; private set; } = IslandTimerMode.Countdown;
    public bool IsRunning => _stopwatch.IsRunning;
    public bool IsActive => _stopwatch.IsRunning ||
        (Mode == IslandTimerMode.Stopwatch ? _stopwatch.Elapsed > TimeSpan.Zero : Remaining > TimeSpan.Zero && _stopwatch.Elapsed > TimeSpan.Zero);
    public TimeSpan CountdownDuration => _countdownDuration;
    public TimeSpan Remaining => MaxZero(_countdownDuration - CurrentElapsed);
    public TimeSpan Elapsed => CurrentElapsed;
    public bool JustFinished { get; private set; }

    private TimeSpan CurrentElapsed => _stopwatch.Elapsed;

    public void SetMode(IslandTimerMode mode)
    {
        Pause();
        Mode = mode;
        Reset();
    }

    public void SetCountdownDuration(TimeSpan duration)
    {
        if (IsRunning || duration <= TimeSpan.Zero) return;
        _countdownDuration = duration;
        Reset();
    }

    public void Start()
    {
        JustFinished = false;
        if (Mode == IslandTimerMode.Countdown && Remaining <= TimeSpan.Zero) return;
        _stopwatch.Start();
    }

    public void Pause()
    {
        if (_stopwatch.IsRunning) _stopwatch.Stop();
    }

    public void Reset()
    {
        _stopwatch.Reset();
        JustFinished = false;
    }

    public void Tick()
    {
        if (Mode != IslandTimerMode.Countdown || !IsRunning || Remaining > TimeSpan.Zero) return;
        _stopwatch.Stop();
        JustFinished = true;
    }

    public string Display
    {
        get
        {
            var value = Mode == IslandTimerMode.Countdown ? Remaining : Elapsed;
            if (Mode == IslandTimerMode.Countdown) value = TimeSpan.FromSeconds(Math.Ceiling(value.TotalSeconds));
            return value.TotalHours >= 1 ? value.ToString(@"h\:mm\:ss") : value.ToString(@"mm\:ss");
        }
    }

    private static TimeSpan MaxZero(TimeSpan value) => value < TimeSpan.Zero ? TimeSpan.Zero : value;
}
