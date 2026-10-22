using System.Threading;

namespace BtAudioGui;

internal static class Program
{
    private const string WatchMutexName = @"Local\btaudio-watch";
    private const string GuiMutexName = @"Local\btaudio-gui";
    private const string ShowEventName = @"Local\btaudio-gui-show";
    private const string StopEventName = @"Local\btaudio-stop";

    [STAThread]
    private static void Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var startMinimized = args.Any(a => string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase));

        // 开发自检用：正常启动，N 秒后把窗口渲染成 PNG 并退出（不依赖屏幕截图权限）
        var screenshotIndex = Array.FindIndex(args, a => string.Equals(a, "--screenshot", StringComparison.OrdinalIgnoreCase));
        var screenshotPath = screenshotIndex >= 0 && screenshotIndex + 1 < args.Length ? args[screenshotIndex + 1] : null;
        var delayIndex = Array.FindIndex(args, a => string.Equals(a, "--screenshot-delay", StringComparison.OrdinalIgnoreCase));
        var screenshotDelay = delayIndex >= 0 && delayIndex + 1 < args.Length && int.TryParse(args[delayIndex + 1], out var parsedDelay)
            ? Math.Clamp(parsedDelay, 1, 60)
            : 8;

        // 开发自检用：验证 IPolicyConfig 切换默认播放设备这条链路
        if (args.Any(a => string.Equals(a, "--self-test", StringComparison.OrdinalIgnoreCase)))
        {
            SelfTest();
            return;
        }

        // 单实例 GUI：第二次启动时唤醒已运行窗口。
        // 必须先于 watch 互斥判断，否则第二个实例会把第一个实例误当成命令行守护去弹框。
        using var guiMutex = new Mutex(true, GuiMutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            if (EventWaitHandle.TryOpenExisting(ShowEventName, out var existingShow))
            {
                existingShow.Set();
                existingShow.Dispose();
            }

            return;
        }

        // 与命令行守护互斥：两者都会抢同一个蓝牙接收端，只允许一个在跑
        Mutex? watchMutex = null;
        if (!TryAcquire(WatchMutexName, out watchMutex))
        {
            var answer = MessageBox.Show(
                "检测到命令行守护（btaudio watch）正在运行，它会和本程序争用同一个蓝牙接收端。\n\n是否先停止它并继续？",
                "btaudio",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);
            if (answer != DialogResult.Yes)
            {
                return;
            }

            SignalStop();
            if (!TryAcquire(WatchMutexName, out watchMutex, 5000))
            {
                MessageBox.Show("命令行守护仍在运行，请先执行 btaudio stop 后再试。", "btaudio", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
        }

        using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        using var stopEvent = new EventWaitHandle(false, EventResetMode.ManualReset, StopEventName);
        stopEvent.Reset();

        var settings = GuiSettings.Load();
        var manager = new ReceiverManager(settings);
        var form = new MainForm(settings, manager, startMinimized);
        if (screenshotPath is not null)
        {
            form.ScreenshotPath = screenshotPath;
            form.ScreenshotDelaySeconds = screenshotDelay;
        }

        ThreadPool.RegisterWaitForSingleObject(showEvent, (_, _) => form.ShowFromTray(), null, Timeout.Infinite, false);
        ThreadPool.RegisterWaitForSingleObject(stopEvent, (_, _) => form.RequestExit(), null, Timeout.Infinite, false);

        Application.Run(form);
        manager.Dispose();

        try
        {
            watchMutex?.ReleaseMutex();
        }
        catch
        {
            // 已被回收
        }

        watchMutex?.Dispose();
    }

    private static void SelfTest()
    {
        var lines = new List<string>();
        try
        {
            var current = AudioDefaults.GetDefaultRenderId();
            lines.Add("默认播放设备 ID: " + (current ?? "(null)"));
            var devices = AudioDefaults.ListRenderAsync().GetAwaiter().GetResult();
            foreach (var (id, name) in devices)
            {
                lines.Add($"  {name}  =>  {id}");
            }

            if (current is not null)
            {
                var ok = AudioDefaults.SetDefault(current, out var error);
                lines.Add(ok ? "SetDefault(设为同一设备) 成功" : "SetDefault 失败: " + error);
            }
        }
        catch (Exception ex)
        {
            lines.Add("异常: " + ex);
        }

        var report = string.Join(Environment.NewLine, lines);
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "selftest.txt"), report);
    }

    private static bool TryAcquire(string name, out Mutex? mutex, int timeoutMs = 0)
    {
        Mutex? candidate = null;
        mutex = null;
        try
        {
            candidate = new Mutex(false, name);
            if (candidate.WaitOne(timeoutMs))
            {
                mutex = candidate;
                return true;
            }

            candidate.Dispose();
            return false;
        }
        catch (AbandonedMutexException)
        {
            mutex = candidate;
            return true;
        }
        catch
        {
            candidate?.Dispose();
            return false;
        }
    }

    private static void SignalStop()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(StopEventName, out var signal))
            {
                using (signal)
                {
                    signal.Set();
                }
            }
        }
        catch
        {
            // 没有可通知的对象
        }
    }
}
