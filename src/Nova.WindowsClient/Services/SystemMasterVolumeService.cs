using NAudio.CoreAudioApi;

namespace Nova.WindowsClient.Services;

internal sealed class SystemMasterVolumeService
{
    public float ReadLevel()
    {
        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        return device.AudioEndpointVolume.MasterVolumeLevelScalar;
    }

    public void SetLevel(float level)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        device.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(level, 0, 1);
    }
}
