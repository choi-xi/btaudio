using System.Text;

namespace BtAudio.Shared;

/// <summary>简单的日志输出：控制台（可关）+ 按天滚动的 UTF-8 文本文件。</summary>
internal static class Log
{
    private static readonly object Gate = new();
    private static string? _file;
    private static bool _echo = true;

    public static void Init(string? file, bool echo)
    {
        _echo = echo;
        if (file is null)
        {
            return;
        }

        _file = file;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        }
        catch
        {
            // 日志目录建不出来时静默降级为仅控制台输出
        }
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? ex = null)
        => Write("ERR ", ex is null ? message : $"{message} | {ex.GetType().Name}: {ex.Message}");

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
        lock (Gate)
        {
            if (_echo)
            {
                try
                {
                    Console.WriteLine(line);
                }
                catch
                {
                    // 无控制台（隐藏窗口启动）时忽略
                }
            }

            if (_file is not null)
            {
                try
                {
                    File.AppendAllText(_file, line + Environment.NewLine, Encoding.UTF8);
                }
                catch
                {
                    // 日志文件被占用/磁盘异常时绝不能让守护进程崩溃
                }
            }
        }
    }
}
