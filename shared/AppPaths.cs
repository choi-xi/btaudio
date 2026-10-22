using System.Text;

namespace BtAudio.Shared;

internal static class AppPaths
{
    public static string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BtAudioSink");

    public static string ConfigFile => Path.Combine(DataDir, "config.json");

    public static string StatusFile => Path.Combine(DataDir, "status.json");

    public static string DefaultLogFile => Path.Combine(DataDir, "logs", $"watch-{DateTime.Now:yyyyMMdd}.log");
}

/// <summary>记住上次选的接收设备，保证「手机离开再回来」时能重连到同一台。</summary>
internal sealed class Settings
{
    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string? DeviceId { get; set; }

    public string? DeviceName { get; set; }

    public static Settings Load()
    {
        try
        {
            if (File.Exists(AppPaths.ConfigFile))
            {
                var text = File.ReadAllText(AppPaths.ConfigFile);
                return System.Text.Json.JsonSerializer.Deserialize<Settings>(text) ?? new Settings();
            }
        }
        catch
        {
            // 配置损坏时按首次运行处理
        }

        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            File.WriteAllText(
                AppPaths.ConfigFile,
                System.Text.Json.JsonSerializer.Serialize(this, JsonOptions),
                new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            Log.Warn("保存配置失败：" + ex.Message);
        }
    }
}

/// <summary>写一份可被 `btaudio status` 读取的当前状态。</summary>
internal static class StatusStore
{
    private static readonly object Gate = new();

    internal sealed class Snapshot
    {
        public string State { get; set; } = "Idle";

        public string? DeviceName { get; set; }

        public string? LastError { get; set; }

        public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

        public int Pid { get; set; }
    }

    public static void Write(string state, string? deviceName, string? lastError)
    {
        var snapshot = new Snapshot
        {
            State = state,
            DeviceName = deviceName,
            LastError = lastError,
            UpdatedUtc = DateTime.UtcNow,
            Pid = Environment.ProcessId,
        };

        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.DataDir);
                var temp = AppPaths.StatusFile + ".tmp";
                File.WriteAllText(
                    temp,
                    System.Text.Json.JsonSerializer.Serialize(snapshot, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }),
                    new UTF8Encoding(false));
                File.Move(temp, AppPaths.StatusFile, true);
            }
        }
        catch
        {
            // 状态文件只是辅助信息，写失败不影响守护
        }
    }

    public static Snapshot? Read()
    {
        try
        {
            if (!File.Exists(AppPaths.StatusFile))
            {
                return null;
            }

            return System.Text.Json.JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(AppPaths.StatusFile));
        }
        catch
        {
            return null;
        }
    }
}
