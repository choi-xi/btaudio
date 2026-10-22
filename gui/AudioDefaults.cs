using System.Runtime.InteropServices;
using Windows.Devices.Enumeration;
using Windows.Media.Devices;

namespace BtAudioGui;

internal static class AudioDefaults
{
    private static readonly Guid[] PolicyConfigClassIds =
    {
        new("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9"), // CPolicyConfigClient（Win7+）
        new("294935CE-F637-4E7C-A41B-AB255460B862"), // CPolicyConfigVistaClient
    };

    public static async Task<List<(string Id, string Name)>> ListRenderAsync()
    {
        var result = new List<(string Id, string Name)>();
        try
        {
            var devices = await DeviceInformation.FindAllAsync(MediaDevice.GetAudioRenderSelector());
            foreach (var device in devices)
            {
                result.Add((device.Id, device.Name ?? string.Empty));
            }
        }
        catch
        {
            // 枚举失败时返回空列表
        }

        return result;
    }

    public static string? GetDefaultRenderId()
    {
        try
        {
            return MediaDevice.GetDefaultAudioRenderId(AudioDeviceRole.Default);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 切换系统默认播放设备（eConsole + eMultimedia 两个角色都设置）。
    /// 不同 Windows 版本暴露的接口不同（IPolicyConfig / IPolicyConfigVista），逐个组合尝试。
    /// </summary>
    public static bool SetDefault(string deviceId, out string error)
    {
        var attempts = new List<string>();

        foreach (var classId in PolicyConfigClassIds)
        {
            object? instance;
            try
            {
                var type = Type.GetTypeFromCLSID(classId);
                instance = type is null ? null : Activator.CreateInstance(type);
            }
            catch (Exception ex)
            {
                attempts.Add($"{classId}: 创建失败({ex.GetType().Name})");
                continue;
            }

            if (instance is null)
            {
                continue;
            }

            var unknown = IntPtr.Zero;
            try
            {
                unknown = Marshal.GetIUnknownForObject(instance);

                if (TryApply(unknown, new Guid("568B9108-44BF-40B4-9006-86AFE5B5A620"), vista: true, deviceId, attempts))
                {
                    error = string.Empty;
                    return true;
                }

                if (TryApply(unknown, new Guid("F8679F50-850A-41CF-9C72-430F290290C4"), vista: false, deviceId, attempts))
                {
                    error = string.Empty;
                    return true;
                }
            }
            finally
            {
                if (unknown != IntPtr.Zero)
                {
                    Marshal.Release(unknown);
                }
            }
        }

        error = string.Join("; ", attempts);
        return false;
    }

    private static bool TryApply(IntPtr unknown, Guid iid, bool vista, string deviceId, List<string> attempts)
    {
        var id = iid;
        if (Marshal.QueryInterface(unknown, ref id, out var pointer) != 0 || pointer == IntPtr.Zero)
        {
            attempts.Add($"{iid}: 无此接口");
            return false;
        }

        try
        {
            int media;
            int multimedia;
            if (vista)
            {
                var policy = (IPolicyConfigVista)Marshal.GetTypedObjectForIUnknown(pointer, typeof(IPolicyConfigVista))!;
                media = policy.SetDefaultEndpoint(deviceId, 0);
                multimedia = policy.SetDefaultEndpoint(deviceId, 1);
            }
            else
            {
                var policy = (IPolicyConfig)Marshal.GetTypedObjectForIUnknown(pointer, typeof(IPolicyConfig))!;
                media = policy.SetDefaultEndpoint(deviceId, 0);
                multimedia = policy.SetDefaultEndpoint(deviceId, 1);
            }

            if (media == 0 || multimedia == 0)
            {
                return true;
            }

            attempts.Add($"{iid}: SetDefaultEndpoint=0x{media:X8}/0x{multimedia:X8}");
            return false;
        }
        catch (Exception ex)
        {
            attempts.Add($"{iid}: {ex.GetType().Name} {ex.Message}");
            return false;
        }
        finally
        {
            Marshal.Release(pointer);
        }
    }

    [ComImport]
    [Guid("568B9108-44BF-40B4-9006-86AFE5B5A620")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPolicyConfigVista
    {
        [PreserveSig]
        int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, out IntPtr ppFormat);

        [PreserveSig]
        int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, bool bDefault, out IntPtr ppFormat);

        [PreserveSig]
        int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName);

        [PreserveSig]
        int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr pEndpointFormat, IntPtr mixFormat);

        [PreserveSig]
        int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, bool bDefault, out long pmftDefaultPeriod, out long pmftMinimumPeriod);

        [PreserveSig]
        int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr pmftPeriod);

        [PreserveSig]
        int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, out IntPtr pMode);

        [PreserveSig]
        int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr mode);

        [PreserveSig]
        int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, bool bFxStore, IntPtr key, out IntPtr pv);

        [PreserveSig]
        int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, bool bFxStore, IntPtr key, IntPtr pv);

        [PreserveSig]
        int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, int role);
    }

    [ComImport]
    [Guid("F8679F50-850A-41CF-9C72-430F290290C4")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPolicyConfig
    {
        [PreserveSig]
        int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, out IntPtr ppFormat);

        [PreserveSig]
        int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, bool bDefault, out IntPtr ppFormat);

        [PreserveSig]
        int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName);

        [PreserveSig]
        int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr pEndpointFormat, IntPtr mixFormat);

        [PreserveSig]
        int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, bool bDefault, out long pmftDefaultPeriod, out long pmftMinimumPeriod);

        [PreserveSig]
        int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr pmftPeriod);

        [PreserveSig]
        int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, out IntPtr pMode);

        [PreserveSig]
        int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr mode);

        [PreserveSig]
        int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, bool bFxStore, IntPtr key, out IntPtr pv);

        [PreserveSig]
        int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, bool bFxStore, IntPtr key, IntPtr pv);

        [PreserveSig]
        int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, int role);

        [PreserveSig]
        int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, bool bVisible);
    }
}
