using BtAudio.Shared;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Devices.Enumeration;
using Windows.Foundation.Metadata;
using Windows.Media.Devices;

namespace BtAudioSink;

internal static class Commands
{
    public static async Task<int> ListAsync()
    {
        var devices = await DeviceCatalog.ListAsync(CancellationToken.None);
        var settings = Settings.Load();

        if (devices.Count == 0)
        {
            Console.WriteLine("没有找到可用的蓝牙音频接收设备。");
            Console.WriteLine("请先在「设置 → 蓝牙和其他设备」里把手机与电脑配对，并确认蓝牙已打开。");
            return 1;
        }

        Console.WriteLine($"可接收的蓝牙音频设备（A2DP Sink，共 {devices.Count} 个）：");
        for (var i = 0; i < devices.Count; i++)
        {
            var device = devices[i];
            var mark = IsSelected(device, settings) ? "*" : " ";
            Console.WriteLine($"{mark} {i + 1}. {device.Name}");
            Console.WriteLine($"     {device.Id}");
        }

        Console.WriteLine();
        Console.WriteLine("带 * 的是当前配置里保存的设备；用 --device 序号 或 --device \"名称片段\" 切换。");
        return 0;
    }

    public static async Task<int> WatchAsync(string[] args)
    {
        string? deviceSpec = null;
        string? logPath = null;
        var quiet = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--device":
                case "-d":
                    if (i + 1 >= args.Length)
                    {
                        Console.Error.WriteLine("--device 后面要跟设备序号/ID/名称");
                        return 2;
                    }

                    deviceSpec = args[++i];
                    break;

                case "--log":
                    if (i + 1 >= args.Length)
                    {
                        Console.Error.WriteLine("--log 后面要跟文件路径");
                        return 2;
                    }

                    logPath = args[++i];
                    break;

                case "--quiet":
                case "-q":
                    quiet = true;
                    break;

                default:
                    Console.Error.WriteLine("未知参数：" + args[i]);
                    return 2;
            }
        }

        // 单实例：避免两个守护进程抢同一个蓝牙接收端
        using var mutex = new Mutex(false, @"Local\btaudio-watch");
        bool acquired;
        try
        {
            acquired = mutex.WaitOne(TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            acquired = true;
        }

        if (!acquired)
        {
            Console.Error.WriteLine("已经有一个 btaudio watch 在运行（单实例）。用 `btaudio status` 查看其状态。");
            return 3;
        }

        Log.Init(logPath ?? AppPaths.DefaultLogFile, !quiet);

        var settings = Settings.Load();
        var devices = await DeviceCatalog.ListAsync(CancellationToken.None);
        var target = Pick(devices, deviceSpec, settings, out var error);
        if (target is null)
        {
            Console.Error.WriteLine(error);
            Console.WriteLine();
            PrintTable(devices, settings);
            return 4;
        }

        if (settings.DeviceId != target.Id || settings.DeviceName != target.Name)
        {
            settings.DeviceId = target.Id;
            settings.DeviceName = target.Name;
            settings.Save();
        }

        Log.Info($"已选定设备：{target.Name}");

        using var stopSignal = new EventWaitHandle(false, EventResetMode.ManualReset, @"Local\btaudio-stop");
        stopSignal.Reset();

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            Log.Info("收到 Ctrl+C，准备退出…");
            SafeCancel(cts);
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => SafeCancel(cts);

        var keeper = new Keeper(settings, cts.Token, stopSignal);
        await keeper.RunAsync();
        return 0;
    }

    /// <summary>请求正在运行的 watch 优雅退出（释放连接、写状态），等待其结束。</summary>
    public static int Stop()
    {
        EventWaitHandle? signal = null;
        try
        {
            if (EventWaitHandle.TryOpenExisting(@"Local\btaudio-stop", out var existing))
            {
                signal = existing;
            }
        }
        catch
        {
            signal = null;
        }

        if (signal is null)
        {
            Console.WriteLine("没有正在运行的 btaudio watch。");
            return 0;
        }

        using (signal)
        {
            signal.Set();
        }

        Console.WriteLine("已发送停止信号，等待守护退出…");

        var snapshot = StatusStore.Read();
        var pid = snapshot?.Pid ?? 0;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && IsAlive(pid))
        {
            Thread.Sleep(200);
        }

        if (IsAlive(pid))
        {
            Console.WriteLine("等待超时，进程仍在运行；可用 scripts\\stop.ps1 强制结束。");
            return 1;
        }

        Console.WriteLine("守护已停止。");
        return 0;
    }

    private static bool IsAlive(int pid)
    {
        if (pid <= 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>进程退出时 CancellationTokenSource 可能已被 Dispose，Cancel 会抛 ObjectDisposedException。</summary>
    private static void SafeCancel(CancellationTokenSource cts)
    {
        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已经释放，忽略
        }
    }

    public static int Status()
    {
        var snapshot = StatusStore.Read();
        if (snapshot is null)
        {
            Console.WriteLine("还没有状态记录（守护进程可能从未运行过）。");
            return 0;
        }

        var running = Process.GetProcessesByName("btaudio").Any(p =>
        {
            try
            {
                return p.Id == snapshot.Pid;
            }
            catch
            {
                return false;
            }
        });

        Console.WriteLine($"状态      : {Translate(snapshot.State)}（{snapshot.State}）");
        Console.WriteLine($"守护进程  : {(running ? $"运行中（PID {snapshot.Pid}）" : $"未运行（最后 PID {snapshot.Pid}）")}");
        Console.WriteLine($"目标设备  : {snapshot.DeviceName ?? "(无)"}");
        Console.WriteLine($"更新时间  : {snapshot.UpdatedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
        if (!string.IsNullOrEmpty(snapshot.LastError))
        {
            Console.WriteLine($"最近错误  : {snapshot.LastError}");
        }

        return 0;
    }

    public static async Task<int> DiagAsync()
    {
        Console.WriteLine("== 系统 ==");
        Console.WriteLine($"OS         : {RuntimeInformation.OSDescription}");
        Console.WriteLine($"版本       : {Environment.OSVersion.Version}（Build {Environment.OSVersion.Version.Build}）");
        Console.WriteLine($"架构       : {RuntimeInformation.OSArchitecture}");

        var apiPresent = false;
        try
        {
            apiPresent = ApiInformation.IsTypePresent("Windows.Media.Audio.AudioPlaybackConnection");
        }
        catch
        {
            apiPresent = false;
        }

        Console.WriteLine($"A2DP Sink API：{(apiPresent ? "可用" : "不可用（需要 Windows 10 2004+/11）")}");

        Console.WriteLine();
        Console.WriteLine("== 设备 ==");
        var devices = await DeviceCatalog.ListAsync(CancellationToken.None);
        if (devices.Count == 0)
        {
            Console.WriteLine("（无候选设备）");
        }
        else
        {
            var settings = Settings.Load();
            for (var i = 0; i < devices.Count; i++)
            {
                Console.WriteLine($"{i + 1}. {devices[i].Name}{(IsSelected(devices[i], settings) ? "  *" : string.Empty)}");
            }
        }

        Console.WriteLine();
        Console.WriteLine("== 默认播放设备 ==");
        Console.WriteLine($"媒体       : {await DescribeRenderAsync(AudioDeviceRole.Default)}");
        Console.WriteLine($"通信       : {await DescribeRenderAsync(AudioDeviceRole.Communications)}");

        Console.WriteLine();
        Console.WriteLine("== 配置 ==");
        var cfg = Settings.Load();
        Console.WriteLine($"文件       : {AppPaths.ConfigFile}");
        Console.WriteLine($"设备名     : {cfg.DeviceName ?? "(未选)"}");
        Console.WriteLine($"设备 ID    : {cfg.DeviceId ?? "(未选)"}");

        Console.WriteLine();
        Console.WriteLine("== 守护状态 ==");
        var snapshot = StatusStore.Read();
        Console.WriteLine(snapshot is null
            ? "（无记录）"
            : $"{Translate(snapshot.State)}（{snapshot.State}），更新于 {snapshot.UpdatedUtc.ToLocalTime():HH:mm:ss}");
        Console.WriteLine($"日志目录   : {Path.GetDirectoryName(AppPaths.DefaultLogFile)}");

        return apiPresent ? 0 : 5;
    }

    private static async Task<string> DescribeRenderAsync(AudioDeviceRole role)
    {
        try
        {
            var id = MediaDevice.GetDefaultAudioRenderId(role);
            var endpoints = await DeviceInformation.FindAllAsync(MediaDevice.GetAudioRenderSelector());
            var match = endpoints.FirstOrDefault(d => d.Id == id);
            return match?.Name ?? id;
        }
        catch
        {
            return "(读取失败)";
        }
    }

    private static SinkDevice? Pick(List<SinkDevice> devices, string? spec, Settings settings, out string error)
    {
        error = string.Empty;

        if (!string.IsNullOrWhiteSpace(spec))
        {
            if (int.TryParse(spec, out var index))
            {
                if (index >= 1 && index <= devices.Count)
                {
                    return devices[index - 1];
                }

                error = $"序号超出范围（共 {devices.Count} 个，先运行 `btaudio list` 查看）";
                return null;
            }

            var byId = devices.FirstOrDefault(d => d.Id == spec);
            if (byId is not null)
            {
                return byId;
            }

            var byName = devices.Where(d => d.Name.Contains(spec, StringComparison.OrdinalIgnoreCase)).ToList();
            if (byName.Count == 1)
            {
                return byName[0];
            }

            error = byName.Count == 0
                ? $"没有匹配「{spec}」的设备"
                : $"「{spec}」匹配到 {byName.Count} 个设备，请改用序号或完整名称";
            return null;
        }

        if (!string.IsNullOrEmpty(settings.DeviceName))
        {
            if (!string.IsNullOrEmpty(settings.DeviceId))
            {
                var byId = devices.FirstOrDefault(d => d.Id == settings.DeviceId);
                if (byId is not null)
                {
                    return byId;
                }
            }

            var byName = devices.Where(d => d.Name.Contains(settings.DeviceName, StringComparison.OrdinalIgnoreCase)).ToList();
            if (byName.Count == 1)
            {
                return byName[0];
            }
        }

        if (devices.Count == 1)
        {
            return devices[0];
        }

        error = $"有 {devices.Count} 个候选设备，请用 --device 指定（序号、ID 或名称片段）";
        return null;
    }

    private static bool IsSelected(SinkDevice device, Settings settings)
    {
        if (!string.IsNullOrEmpty(settings.DeviceId) && device.Id == settings.DeviceId)
        {
            return true;
        }

        return !string.IsNullOrEmpty(settings.DeviceName)
            && device.Name.Contains(settings.DeviceName, StringComparison.OrdinalIgnoreCase);
    }

    private static void PrintTable(IReadOnlyList<SinkDevice> devices, Settings settings)
    {
        if (devices.Count == 0)
        {
            return;
        }

        Console.WriteLine("可接收的蓝牙音频设备（A2DP Sink）：");
        for (var i = 0; i < devices.Count; i++)
        {
            Console.WriteLine($"{i + 1}. {devices[i].Name}{(IsSelected(devices[i], settings) ? "  *" : string.Empty)}");
        }
    }

    private static string Translate(string state) => state switch
    {
        "Starting" => "启动中",
        "Connecting" => "连接中",
        "Opened" => "已连接",
        "Reconnecting" => "重连中",
        "WaitingDevice" => "等待设备",
        "RetryWait" => "等待重试",
        "Failed" => "连接失败",
        "Stopped" => "已停止",
        "Idle" => "空闲",
        _ => state,
    };
}
