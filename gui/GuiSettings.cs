using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BtAudio.Shared;

namespace BtAudioGui;

/// <summary>GUI 自己的设置（与 CLI 的 config.json 分开，互不影响）。</summary>
internal sealed class GuiSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    [JsonPropertyName("autoReconnect")]
    public bool AutoReconnect { get; set; } = true;

    [JsonPropertyName("reconnectDelaySeconds")]
    public int ReconnectDelaySeconds { get; set; } = 2;

    [JsonPropertyName("startMinimized")]
    public bool StartMinimized { get; set; }

    [JsonPropertyName("minimizeToTray")]
    public bool MinimizeToTray { get; set; } = true;

    [JsonPropertyName("showBalloons")]
    public bool ShowBalloons { get; set; } = true;

    [JsonPropertyName("autoConnectDeviceIds")]
    public List<string> AutoConnectDeviceIds { get; set; } = new();

    [JsonPropertyName("defaultOutputDeviceId")]
    public string? DefaultOutputDeviceId { get; set; }

    [JsonIgnore]
    public static string FilePath => Path.Combine(AppPaths.DataDir, "gui-settings.json");

    public static GuiSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                return JsonSerializer.Deserialize<GuiSettings>(File.ReadAllText(FilePath)) ?? new GuiSettings();
            }
        }
        catch
        {
            // 配置损坏时退回默认值
        }

        return new GuiSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions), new UTF8Encoding(false));
        }
        catch
        {
            // 写配置失败不应影响界面
        }
    }
}
