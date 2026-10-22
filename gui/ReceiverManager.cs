using System.Collections.Concurrent;
using BtAudio.Shared;
using Windows.Media.Audio;

namespace BtAudioGui;

internal enum LinkState
{
    Idle,
    Waiting,
    Connecting,
    Opened,
    Error,
}

internal sealed record DeviceSnapshot(
    string Id,
    string Name,
    LinkState State,
    bool AutoConnect,
    bool Present,
    string? LastError,
    DateTime? OpenedAtUtc,
    int Reconnects);

/// <summary>
/// 多设备的 A2DP 接收端守护。独立线程内顺序执行：扫描 → 维护状态 → 按策略连接/重连。
/// 界面线程只读 <see cref="Snapshot"/>，所有 WinRT 调用都发生在这个后台循环里。
/// </summary>
internal sealed class ReceiverManager : IDisposable
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(1200);

    private sealed class Slot
    {
        public string Id = string.Empty;
        public string Name = string.Empty;
        public AudioPlaybackConnection? Connection;
        public LinkState State = LinkState.Waiting;
        public string? LastError;
        public DateTime? OpenedAtUtc;
        public int Reconnects;
        public int Failures;
        public DateTime NextAttemptUtc = DateTime.MinValue;
        public bool AutoConnect;
        public bool Present;
        public bool UserPaused;
        public bool ManualConnectRequested;
    }

    private readonly object _sync = new();
    private readonly Dictionary<string, Slot> _slots = new();
    private readonly ConcurrentQueue<(string Verb, string? Id, bool Flag)> _commands = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly GuiSettings _settings;
    private readonly HashSet<string> _autoIds;

    private Task? _loop;
    private List<DeviceSnapshot> _snapshot = new();
    private string _signature = string.Empty;
    private DateTime _lastScanUtc = DateTime.MinValue;
    private bool _disposed;

    public event EventHandler? Changed;

    public event EventHandler<string>? LogLine;

    public bool AutoReconnect { get; set; }

    public int ReconnectDelaySeconds { get; set; }

    public ReceiverManager(GuiSettings settings)
    {
        _settings = settings;
        _autoIds = new HashSet<string>(settings.AutoConnectDeviceIds, StringComparer.Ordinal);
        AutoReconnect = settings.AutoReconnect;
        ReconnectDelaySeconds = Math.Clamp(settings.ReconnectDelaySeconds, 1, 60);
    }

    public IReadOnlyList<DeviceSnapshot> Snapshot
    {
        get
        {
            lock (_sync)
            {
                return _snapshot;
            }
        }
    }

    public void Start()
    {
        if (_loop is not null)
        {
            return;
        }

        _loop = Task.Run(RunAsync);
    }

    public void Connect(string deviceId)
    {
        _commands.Enqueue(("connect", deviceId, false));
        Wake();
    }

    public void Disconnect(string deviceId)
    {
        _commands.Enqueue(("disconnect", deviceId, false));
        Wake();
    }

    public void SetAutoConnect(string deviceId, bool auto)
    {
        _commands.Enqueue(("auto", deviceId, auto));
        Wake();
    }

    public void Refresh()
    {
        _commands.Enqueue(("refresh", null, false));
        Wake();
    }

    public void Forget(string deviceId)
    {
        _commands.Enqueue(("forget", deviceId, false));
        Wake();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cts.Cancel();
        Wake();
        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // 忽略
        }

        DisposeAll();
        _cts.Dispose();
        _wake.Dispose();
    }

    private void Wake()
    {
        try
        {
            if (_wake.CurrentCount == 0)
            {
                _wake.Release();
            }
        }
        catch (SemaphoreFullException)
        {
            // 已有唤醒信号
        }
    }

    private async Task RunAsync()
    {
        Log("守护已启动");
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await IterateAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log("内部异常：" + ex.Message);
            }

            try
            {
                await _wake.WaitAsync(Tick, _cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        DisposeAll();
        Log("守护已停止");
    }

    private async Task IterateAsync()
    {
        DrainCommands();
        await ScanAsync().ConfigureAwait(false);
        await MaintainAsync().ConfigureAwait(false);
        Publish();
    }

    private void DrainCommands()
    {
        var changed = false;
        while (_commands.TryDequeue(out var command))
        {
            switch (command.Verb)
            {
                case "connect" when command.Id is not null && _slots.TryGetValue(command.Id, out var connectSlot):
                    connectSlot.UserPaused = false;
                    connectSlot.ManualConnectRequested = true;
                    connectSlot.NextAttemptUtc = DateTime.MinValue;
                    Log($"手动连接：{connectSlot.Name}");
                    changed = true;
                    break;

                case "disconnect" when command.Id is not null && _slots.TryGetValue(command.Id, out var disconnectSlot):
                    disconnectSlot.UserPaused = true;
                    disconnectSlot.ManualConnectRequested = false;
                    CloseSlot(disconnectSlot, "手动断开");
                    disconnectSlot.State = LinkState.Idle;
                    disconnectSlot.LastError = null;
                    Log($"手动断开：{disconnectSlot.Name}");
                    changed = true;
                    break;

                case "auto" when command.Id is not null:
                    if (command.Flag)
                    {
                        _autoIds.Add(command.Id);
                    }
                    else
                    {
                        _autoIds.Remove(command.Id);
                    }

                    if (_slots.TryGetValue(command.Id, out var autoSlot))
                    {
                        autoSlot.AutoConnect = command.Flag;
                        autoSlot.NextAttemptUtc = DateTime.MinValue;
                    }

                    SyncAutoIdsToSettings();
                    changed = true;
                    break;

                case "refresh":
                    _lastScanUtc = DateTime.MinValue;
                    break;

                case "forget" when command.Id is not null && _slots.TryGetValue(command.Id, out var forgetSlot):
                    forgetSlot.AutoConnect = false;
                    _autoIds.Remove(forgetSlot.Id);
                    SyncAutoIdsToSettings();
                    CloseSlot(forgetSlot, "已从列表移除");
                    _slots.Remove(forgetSlot.Id);
                    changed = true;
                    break;
            }
        }

        if (changed)
        {
            Publish();
        }
    }

    private void SyncAutoIdsToSettings()
    {
        _settings.AutoConnectDeviceIds = _autoIds.ToList();
        _settings.Save();
    }

    private async Task ScanAsync()
    {
        if (DateTime.UtcNow - _lastScanUtc < ScanInterval)
        {
            return;
        }

        _lastScanUtc = DateTime.UtcNow;

        List<SinkDevice> devices;
        try
        {
            devices = await DeviceCatalog.ListAsync(_cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log("枚举设备失败：" + ex.Message);
            return;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var device in devices)
        {
            seen.Add(device.Id);
            if (!_slots.TryGetValue(device.Id, out var slot))
            {
                slot = new Slot
                {
                    Id = device.Id,
                    AutoConnect = _autoIds.Contains(device.Id),
                    State = LinkState.Waiting,
                };
                _slots[device.Id] = slot;
                Log($"发现设备：{device.Name}");
            }

            slot.Name = device.Name;
            slot.Present = true;
        }

        foreach (var slot in _slots.Values)
        {
            if (seen.Contains(slot.Id))
            {
                continue;
            }

            slot.Present = false;
            if (slot.Connection is not null)
            {
                CloseSlot(slot, "设备已不可见");
                slot.State = LinkState.Waiting;
                slot.LastError = "设备当前不在范围内（手机可能走远了）";
            }
        }
    }

    private async Task MaintainAsync()
    {
        foreach (var slot in _slots.Values.ToList())
        {
            if (slot.Connection is not null)
            {
                AudioPlaybackConnectionState state;
                try
                {
                    state = slot.Connection.State;
                }
                catch (Exception ex)
                {
                    CloseSlot(slot, "状态读取失败");
                    slot.LastError = "连接对象失效：" + ex.Message;
                    slot.State = LinkState.Error;
                    slot.Failures = 0;
                    slot.NextAttemptUtc = NextAttempt(1);
                    continue;
                }

                if (state != AudioPlaybackConnectionState.Opened)
                {
                    CloseSlot(slot, "连接已关闭");
                    slot.Reconnects++;
                    slot.State = LinkState.Error;
                    slot.Failures = 0;
                    slot.NextAttemptUtc = NextAttempt(1);
                    if (AutoReconnect)
                    {
                        slot.LastError = "连接已断开，等待自动重连";
                        Log($"{slot.Name} 连接断开，{ReconnectDelaySeconds} 秒后自动重连");
                    }
                    else
                    {
                        slot.LastError = "连接已断开（自动重连已关闭）";
                        Log($"{slot.Name} 连接断开（自动重连已关闭）");
                    }
                }

                continue;
            }

            if (!slot.Present)
            {
                if (slot.State != LinkState.Waiting)
                {
                    slot.State = LinkState.Waiting;
                }

                continue;
            }

            if (slot.UserPaused)
            {
                slot.State = LinkState.Idle;
                continue;
            }

            var wants = slot.AutoConnect || slot.ManualConnectRequested;
            if (!wants)
            {
                if (slot.State != LinkState.Idle)
                {
                    slot.State = LinkState.Idle;
                }

                continue;
            }

            if (DateTime.UtcNow < slot.NextAttemptUtc)
            {
                slot.State = LinkState.Waiting;
                continue;
            }

            await TryOpenAsync(slot).ConfigureAwait(false);
        }
    }

    private async Task TryOpenAsync(Slot slot)
    {
        slot.State = LinkState.Connecting;
        Publish();
        Log($"正在连接：{slot.Name}");

        AudioPlaybackConnection? connection;
        try
        {
            connection = AudioPlaybackConnection.TryCreateFromId(slot.Id);
        }
        catch (Exception ex)
        {
            connection = null;
            Fail(slot, "创建连接失败：" + ex.Message);
            return;
        }

        if (connection is null)
        {
            Fail(slot, "系统未能创建该设备的连接对象");
            return;
        }

        var opened = false;
        try
        {
            using (var startCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token))
            {
                startCts.CancelAfter(StartTimeout);
                await connection.StartAsync().AsTask(startCts.Token).ConfigureAwait(false);
            }

            AudioPlaybackConnectionOpenResult result;
            using (var openCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token))
            {
                openCts.CancelAfter(OpenTimeout);
                result = await connection.OpenAsync().AsTask(openCts.Token).ConfigureAwait(false);
            }

            switch (result.Status)
            {
                case AudioPlaybackConnectionOpenResultStatus.Success:
                    opened = true;
                    break;
                case AudioPlaybackConnectionOpenResultStatus.RequestTimedOut:
                    Fail(slot, "连接超时（手机可能不在范围内或未响应）");
                    break;
                case AudioPlaybackConnectionOpenResultStatus.DeniedBySystem:
                    Fail(slot, "系统拒绝连接（蓝牙是否已关闭？）");
                    break;
                default:
                    Fail(slot, $"连接失败：{result.Status}（0x{result.ExtendedError?.HResult ?? 0:X8}）");
                    break;
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            connection.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            Fail(slot, ex is TaskCanceledException ? "连接超时被取消" : "连接异常：" + ex.Message);
        }

        if (opened)
        {
            slot.Connection = connection;
            slot.State = LinkState.Opened;
            slot.OpenedAtUtc = DateTime.UtcNow;
            slot.Failures = 0;
            slot.LastError = null;
            slot.ManualConnectRequested = false;
            Log($"已连接：{slot.Name}");
            Publish();
            return;
        }

        try
        {
            connection.Dispose();
        }
        catch
        {
            // 忽略
        }

        slot.ManualConnectRequested = false;
        Publish();
    }

    private void Fail(Slot slot, string message)
    {
        slot.LastError = message;
        slot.State = LinkState.Error;
        slot.Failures++;
        slot.NextAttemptUtc = NextAttempt(slot.Failures);
        Log(message);
    }

    private DateTime NextAttempt(int failures)
    {
        var factor = 1 << Math.Min(Math.Max(failures, 1) - 1, 4);
        return DateTime.UtcNow.AddSeconds(Math.Min(30, Math.Max(1, ReconnectDelaySeconds) * factor));
    }

    private void CloseSlot(Slot slot, string reason)
    {
        var connection = slot.Connection;
        slot.Connection = null;
        slot.OpenedAtUtc = null;
        if (connection is null)
        {
            return;
        }

        try
        {
            connection.Dispose();
        }
        catch
        {
            // 忽略
        }

        Log($"已释放连接：{slot.Name}（{reason}）");
    }

    private void DisposeAll()
    {
        foreach (var slot in _slots.Values)
        {
            CloseSlot(slot, "程序退出");
        }
    }

    private void Publish()
    {
        var list = _slots.Values
            .Select(s => new DeviceSnapshot(s.Id, s.Name, s.State, s.AutoConnect, s.Present, s.LastError, s.OpenedAtUtc, s.Reconnects))
            .ToList();

        var signature = string.Join(
            "|",
            list.Select(x => $"{x.Id}:{x.State}:{x.AutoConnect}:{x.Present}:{x.Reconnects}:{x.LastError}"));

        lock (_sync)
        {
            _snapshot = list;
        }

        if (signature == _signature)
        {
            return;
        }

        _signature = signature;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Log(string message)
    {
        LogLine?.Invoke(this, message);
    }
}
