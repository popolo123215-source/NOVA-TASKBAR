using System.Buffers.Binary;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Nova.WindowsClient.Services;

internal sealed record AudioBeatSample(float Level, float Bass, float Treble, float Beat, string? Error);

internal sealed class AudioMeterService : IDisposable
{
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(5);
    private readonly AudioBandAnalyzer _analyzer = new();
    private WasapiRecorder? _capture;
    private CaptureDataAvailableHandler? _captureDataHandler;
    private AudioBeatSample _latest = new(0, 0, 0, 0, null);
    private long _retryAfterUtcTicks;
    private string? _lastError;
    private int _enabled;
    private int _stopRequested;
    private int _disposed;

    public void SetEnabled(bool enabled)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        Volatile.Write(ref _enabled, enabled ? 1 : 0);

        if (!enabled)
        {
            var capture = Volatile.Read(ref _capture);
            if (capture is null || Interlocked.Exchange(ref _stopRequested, 1) != 0)
                return;

            try
            {
                capture.StopRecording();
            }
            catch (Exception ex) when (IsCaptureException(ex))
            {
                PublishError(ex.Message);
            }

            return;
        }

        if (Volatile.Read(ref _capture) is null && DateTime.UtcNow.Ticks >= Volatile.Read(ref _retryAfterUtcTicks))
            StartCapture();
    }

    public AudioBeatSample Sample() => Volatile.Read(ref _latest);

    private void StartCapture()
    {
        WasapiRecorder? capture = null;
        CaptureDataAvailableHandler? dataHandler = null;
        try
        {
            capture = new WasapiRecorderBuilder().WithLoopbackCapture().Build();
            dataHandler = (buffer, _, _, _) => Capture_DataAvailable(capture, buffer);
            capture.DataAvailable += dataHandler;
            _captureDataHandler = dataHandler;
            capture.RecordingStopped += Capture_RecordingStopped;
            Volatile.Write(ref _capture, capture);
            Interlocked.Exchange(ref _stopRequested, 0);
            _analyzer.Reset();
            capture.StartRecording();
            _lastError = null;
            Volatile.Write(ref _latest, new AudioBeatSample(0, 0, 0, 0, null));
        }
        catch (Exception ex) when (IsCaptureException(ex))
        {
            if (capture is not null)
            {
                Interlocked.CompareExchange(ref _capture, null, capture);
                if (dataHandler is not null)
                    capture.DataAvailable -= dataHandler;
                _captureDataHandler = null;
                capture.RecordingStopped -= Capture_RecordingStopped;
                capture.Dispose();
            }

            PublishError(ex.Message);
        }
    }

    private void Capture_DataAvailable(WasapiRecorder capture, ReadOnlySpan<byte> buffer)
    {
        if (Volatile.Read(ref _enabled) == 0)
            return;

        try
        {
            var format = capture.WaveFormat;
            if (format.Encoding == WaveFormatEncoding.Extensible && format is WaveFormatExtensible extensible)
                format = extensible.ToStandardWaveFormat();
            var encoding = format.Encoding;

            var bytesPerSample = (format.BitsPerSample + 7) / 8;
            var frameSize = format.BlockAlign;
            if (format.Channels < 1 || format.SampleRate < 1 || bytesPerSample < 1 ||
                frameSize < bytesPerSample * format.Channels ||
                encoding is not (WaveFormatEncoding.IeeeFloat or WaveFormatEncoding.Pcm))
                throw new NotSupportedException($"Неподдерживаемый формат loopback-аудио: {format}.");

            var frameCount = buffer.Length / frameSize;
            if (frameCount == 0)
                return;

            for (var frame = 0; frame < frameCount; frame++)
            {
                var frameOffset = frame * frameSize;
                var mono = 0f;
                for (var channel = 0; channel < format.Channels; channel++)
                {
                    var sampleOffset = frameOffset + channel * bytesPerSample;
                    mono += ReadSample(buffer.Slice(sampleOffset, bytesPerSample), encoding, format.BitsPerSample);
                }

                mono /= format.Channels;
                _analyzer.AddSample(mono, format.SampleRate);
            }

            Volatile.Write(ref _latest, _analyzer.CompleteBuffer(frameCount, format.SampleRate));
        }
        catch (Exception ex) when (IsAnalysisException(ex))
        {
            PublishError(ex.Message);
            Volatile.Write(ref _enabled, 0);
            if (Interlocked.Exchange(ref _stopRequested, 1) == 0)
            {
                try
                {
                    capture.StopRecording();
                }
                catch (Exception stopException) when (IsCaptureException(stopException))
                {
                    PublishError($"{ex.Message} Ошибка остановки захвата: {stopException.Message}");
                }
            }
        }
    }

    private void Capture_RecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (sender is not WasapiRecorder capture ||
            !ReferenceEquals(Interlocked.CompareExchange(ref _capture, null, capture), capture))
            return;

        if (_captureDataHandler is not null)
            capture.DataAvailable -= _captureDataHandler;
        _captureDataHandler = null;
        capture.RecordingStopped -= Capture_RecordingStopped;
        capture.Dispose();
        Interlocked.Exchange(ref _stopRequested, 0);

        if (e.Exception is not null)
            PublishError(e.Exception.Message);
        else
            Volatile.Write(ref _latest, new AudioBeatSample(0, 0, 0, 0, _lastError));
    }

    private void PublishError(string message)
    {
        _lastError = message;
        Volatile.Write(ref _retryAfterUtcTicks, DateTime.UtcNow.Ticks + RetryInterval.Ticks);
        Volatile.Write(ref _latest, new AudioBeatSample(0, 0, 0, 0, message));
    }

    private static float ReadSample(ReadOnlySpan<byte> sample, WaveFormatEncoding encoding, int bitsPerSample)
    {
        if (encoding == WaveFormatEncoding.IeeeFloat && bitsPerSample == 32)
        {
            var value = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(sample));
            return float.IsFinite(value) ? Math.Clamp(value, -1, 1) : 0;
        }

        if (encoding != WaveFormatEncoding.Pcm)
            throw new NotSupportedException($"Неподдерживаемая разрядность loopback-аудио: {bitsPerSample} бит.");

        return bitsPerSample switch
        {
            8 => (sample[0] - 128) / 128f,
            16 => BinaryPrimitives.ReadInt16LittleEndian(sample) / 32768f,
            24 => ReadInt24(sample) / 8388608f,
            32 => BinaryPrimitives.ReadInt32LittleEndian(sample) / 2147483648f,
            _ => throw new NotSupportedException($"Неподдерживаемая разрядность loopback-аудио: {bitsPerSample} бит.")
        };
    }

    private static int ReadInt24(ReadOnlySpan<byte> sample)
    {
        var value = sample[0] | sample[1] << 8 | sample[2] << 16;
        return (value & 0x800000) == 0 ? value : value | unchecked((int)0xFF000000);
    }

    private static bool IsCaptureException(Exception ex) =>
        ex is COMException or InvalidOperationException or UnauthorizedAccessException or
            NotSupportedException or ArgumentException or ObjectDisposedException or NAudio.MmException;

    private static bool IsAnalysisException(Exception ex) =>
        ex is NotSupportedException or ArgumentException or InvalidOperationException;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        Volatile.Write(ref _enabled, 0);
        var capture = Interlocked.Exchange(ref _capture, null);
        if (capture is null)
            return;

        if (_captureDataHandler is not null)
            capture.DataAvailable -= _captureDataHandler;
        _captureDataHandler = null;
        capture.RecordingStopped -= Capture_RecordingStopped;
        capture.Dispose();
    }
}

internal sealed class AudioBandAnalyzer
{
    private float _bassLowPass;
    private float _trebleLowPass;
    private float _levelBaseline;
    private float _previousLevel;
    private float _previousPeak;
    private float _beatEnvelope;
    private long _lastBeatTimestamp;
    private double _levelEnergy;
    private double _bassEnergy;
    private double _trebleEnergy;
    private float _peak;
    private int _sampleRate;
    private float _bassCoefficient;
    private float _trebleCoefficient;
    private int _sampleCount;

    public void AddSample(float sample, int sampleRate)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (_sampleRate != sampleRate)
        {
            _sampleRate = sampleRate;
            _bassCoefficient = 1 - MathF.Exp(-2 * MathF.PI * 160 / sampleRate);
            _trebleCoefficient = 1 - MathF.Exp(-2 * MathF.PI * 2400 / sampleRate);
        }

        sample = float.IsFinite(sample) ? Math.Clamp(sample, -1, 1) : 0;
        _bassLowPass += _bassCoefficient * (sample - _bassLowPass);
        _trebleLowPass += _trebleCoefficient * (sample - _trebleLowPass);
        var highSample = sample - _trebleLowPass;
        _levelEnergy += sample * sample;
        _bassEnergy += _bassLowPass * _bassLowPass;
        _trebleEnergy += highSample * highSample;
        _peak = Math.Max(_peak, Math.Abs(sample));
        _sampleCount++;
    }

    public AudioBeatSample CompleteBuffer(int frameCount, int sampleRate)
    {
        if (frameCount <= 0) throw new ArgumentOutOfRangeException(nameof(frameCount));
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (_sampleCount != frameCount)
            throw new InvalidOperationException("Audio analyzer sample count does not match the capture buffer.");

        var elapsedSeconds = frameCount / (float)sampleRate;
        var levelRms = MathF.Sqrt((float)(_levelEnergy / frameCount));
        var bassRms = MathF.Sqrt((float)(_bassEnergy / frameCount));
        var trebleRms = MathF.Sqrt((float)(_trebleEnergy / frameCount));
        var levelBaselineRate = 1 - MathF.Exp(-elapsedSeconds / (levelRms > _levelBaseline ? 0.65f : 1.5f));
        _levelBaseline += (levelRms - _levelBaseline) * levelBaselineRate;

        var rise = Math.Max(0, levelRms - _previousLevel);
        var peakRise = Math.Max(0, _peak - _previousPeak);
        _previousLevel = levelRms;
        _previousPeak = _peak;
        _beatEnvelope *= MathF.Exp(-elapsedSeconds / 0.18f);
        var threshold = Math.Max(0.012f, _levelBaseline * 1.45f);
        var fluxThreshold = Math.Max(0.003f, _levelBaseline * 0.18f);
        var now = Environment.TickCount64;
        var onset = Math.Max(rise, peakRise * 0.2f);
        if (levelRms > threshold && onset > fluxThreshold && now - _lastBeatTimestamp >= 130)
        {
            var strength = Math.Clamp((levelRms - threshold) * 3 + (onset - fluxThreshold) * 10, 0, 1);
            _beatEnvelope = Math.Max(_beatEnvelope, strength);
            _lastBeatTimestamp = now;
        }

        var sample = new AudioBeatSample(
            NormalizeRms(levelRms, 0.003f, 3.4f),
            NormalizeRms(bassRms, 0.0025f, 4.5f),
            NormalizeRms(trebleRms, 0.003f, 4.2f),
            _beatEnvelope,
            null);
        ClearBuffer();
        return sample;
    }

    public void Reset()
    {
        _bassLowPass = 0;
        _trebleLowPass = 0;
        _levelBaseline = 0;
        _previousLevel = 0;
        _previousPeak = 0;
        _beatEnvelope = 0;
        _lastBeatTimestamp = 0;
        _sampleRate = 0;
        _bassCoefficient = 0;
        _trebleCoefficient = 0;
        ClearBuffer();
    }

    private void ClearBuffer()
    {
        _levelEnergy = 0;
        _bassEnergy = 0;
        _trebleEnergy = 0;
        _peak = 0;
        _sampleCount = 0;
    }

    private static float NormalizeRms(float value, float noiseFloor, float gain) =>
        Math.Clamp((value - noiseFloor) * gain, 0, 1);
}
