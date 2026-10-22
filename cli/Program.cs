namespace BtAudioSink;

internal static class Program
{
    private const string AppId = "btaudio";

    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length == 0)
            {
                PrintHelp();
                return 0;
            }

            switch (args[0].ToLowerInvariant())
            {
                case "list":
                case "devices":
                    return await Commands.ListAsync();

                case "watch":
                    return await Commands.WatchAsync(args.Length > 1 ? args[1..] : Array.Empty<string>());

                case "status":
                    return Commands.Status();

                case "stop":
                    return Commands.Stop();

                case "diag":
                    return await Commands.DiagAsync();

                case "help":
                case "-h":
                case "--help":
                    PrintHelp();
                    return 0;

                default:
                    Console.Error.WriteLine($"未知命令：{args[0]}");
                    PrintHelp();
                    return 1;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("发生未处理异常：" + ex);
            return 1;
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine($"""
        {AppId} - 让手机通过蓝牙把声音送到电脑播放（Windows A2DP Sink 接收端）

        用法：
          {AppId} list                 列出可作为接收端的已配对设备（带序号）
          {AppId} watch [选项]          常驻守护：自动连接 + 断线自动重连
          {AppId} status               查看守护当前状态（读自 status.json）
          {AppId} stop                 请求守护优雅退出（释放蓝牙连接）
          {AppId} diag                 诊断：系统、API、设备、默认输出设备、配置

        watch 选项：
          --device, -d <序号|ID|名称片段>   指定设备；省略则用上次保存的选择
          --log <文件>                     日志文件路径（默认 %LOCALAPPDATA%\BtAudioSink\logs\）
          --quiet, -q                      不往控制台打印（只写日志文件）

        示例：
          {AppId} list
          {AppId} watch --device 2
          {AppId} watch --device "iPhone 15"
        """);
    }
}
