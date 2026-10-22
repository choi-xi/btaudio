using Windows.Devices.Enumeration;
using Windows.Media.Audio;

namespace BtAudio.Shared;

internal sealed record SinkDevice(string Id, string Name);

internal static class DeviceCatalog
{
    /// <summary>
    /// 枚举支持 A2DP Sink（可作为蓝牙音频接收端）的设备。
    /// 只依赖 Windows 自带的 AudioPlaybackConnection（Windows 10 2004+）。
    /// </summary>
    public static async Task<List<SinkDevice>> ListAsync(CancellationToken cancellationToken)
    {
        var result = new List<SinkDevice>();
        var selector = AudioPlaybackConnection.GetDeviceSelector();
        var devices = await DeviceInformation.FindAllAsync(selector).AsTask(cancellationToken).ConfigureAwait(false);
        foreach (var device in devices)
        {
            result.Add(new SinkDevice(device.Id, device.Name ?? string.Empty));
        }

        return result;
    }
}
