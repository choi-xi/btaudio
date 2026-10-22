using BtAudio.Shared;
using System.Diagnostics;
using Windows.Media.Audio;

namespace BtAudioSink;

/// <summary>
/// 常驻守护核心：保持电脑作为蓝牙音频接收端（A2DP Sink）在线。
///
/// 与 AudioPlaybackConnector 的关键区别：
/// 1) 它只在你手点一次时连一次，状态一变 Closed 就丢弃连接且绝不重连；
///    这里是一个 2 秒粒度的状态机，断线自动重连，失败按指数退避重试（2s→30s）。
/// 2) 设备从系统里消失（手机走远/蓝牙断开）时不停机，只进入等待；
///    设备回来（手机重新进入范围）后自动恢复连接，无需人工点击。
/// 3) 系统睡眠/唤醒会检测到并主动重建连接（用墙钟-单调钟的差值识别）。
/// 4) 每次重连都会先把旧对象 Dispose，并严格遵守「Dispose 之后绝不再读 State」
///    —— 实测释放后再读 State 会直接 AccessViolation 杀掉进程。
/// </summary>
internal sealed class Keeper
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(40);
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DeviceScanWhenIdle = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DeviceScanWhenOpened = TimeSpan.FromSeconds(10);

    private readonly Settings _settings;
    private readonly CancellationToken _cancellationToken;
    private readonly EventWaitHandle _stopSignal;
    private readonly Stopwatch _monotonic = Stopwatch.StartNew();

    private AudioPlaybackConnection? _connection;
    private SinkDevice? _current;
    private List<SinkDevice> _devices = new();
    private DateTime _lastScanUtc = DateTime.MinValue;
    private DateTime _nextAttemptUtc = DateTime.MinValue;
    private DateTime _lastWallUtc = DateTime.UtcNow;
    private TimeSpan _lastMonotonic;
    private int _failureCount;
    private bool _waitingLogged;
    private string? _lastError;

    public Keeper(Settings settings, CancellationToken cancellationToken, EventWaitHandle stopSignal)
    {
        _settings = settings;
        _cancellationToken = cancellationToken;
        _stopSignal = stopSignal;
    }

    public async Task RunAsync()
    {
        Log.Info($"守护启动，目标设备：{_settings.DeviceName ?? "(未指定)"}");
        StatusStore.Write("Starting", _settings.DeviceName, null);

        while (!_cancellationToken.IsCancellationRequested && !_stopSignal.WaitOne(0))
        {
            try
            {
                await TickAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Error("主循环异常，重置连接后继续", ex);
                Detach("主循环异常");
                _lastError = ex.Message;
                ScheduleRetry();
            }

            try
            {
                await Task.Delay(Tick, _cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        Detach("进程退出");
        StatusStore.Write("Stopped", _current?.Name ?? _settings.DeviceName, _lastError);
        Log.Info("守护已停止");
    }

    private async Task TickAsync()
    {
        DetectSystemResume();

        var scanInterval = _connection is null ? DeviceScanWhenIdle : DeviceScanWhenOpened;
        if (DateTime.UtcNow - _lastScanUtc >= scanInterval)
        {
            _devices = await DeviceCatalog.ListAsync(_cancellationToken).ConfigureAwait(false);
            _lastScanUtc = DateTime.UtcNow;
        }

        var target = ResolveTarget(_devices);
        if (target is null)
        {
            if (_connection is not null)
            {
                // 设备被移除（手机走远 / 蓝牙掉线 / 解除配对）——释放后转为等待
                Detach("目标设备已不可见");
            }

            if (!_waitingLogged)
            {
                _waitingLogged = true;
                Log.Warn($"未发现目标设备「{_settings.DeviceName ?? "?"}」，等待中（手机蓝牙重新连上后会自动恢复）");
                StatusStore.Write("WaitingDevice", _settings.DeviceName, _lastError);
            }

            return;
        }

        _waitingLogged = false;

        if (_connection is null)
        {
            if (DateTime.UtcNow >= _nextAttemptUtc)
            {
                await ConnectAsync(target).ConfigureAwait(false);
            }
            else
            {
                StatusStore.Write("RetryWait", target.Name, _lastError);
            }

            return;
        }

        AudioPlaybackConnectionState state;
        try
        {
            state = _connection.State;
        }
        catch (Exception ex)
        {
            Log.Warn("读取连接状态失败，重建连接：" + ex.Message);
            Detach("状态读取失败");
            ScheduleRetry();
            return;
        }

        if (state != AudioPlaybackConnectionState.Opened)
        {
            Log.Warn($"连接已断开（状态 = {state}），准备重连");
            Detach("连接关闭");
            _nextAttemptUtc = DateTime.UtcNow;
            StatusStore.Write("Reconnecting", target.Name, _lastError);
        }
    }

    private async Task ConnectAsync(SinkDevice target)
    {
        StatusStore.Write("Connecting", target.Name, null);
        Log.Info($"正在连接：{target.Name}");

        AudioPlaybackConnection? connection;
        try
        {
            connection = AudioPlaybackConnection.TryCreateFromId(target.Id);
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            Log.Error("创建连接对象失败", ex);
            ScheduleRetry();
            return;
        }

        if (connection is null)
        {
            _lastError = "TryCreateFromId 返回 null";
            Log.Error(_lastError);
            ScheduleRetry();
            return;
        }

        var opened = false;
        try
        {
            await WithTimeoutAsync(connection.StartAsync(), StartTimeout).ConfigureAwait(false);
            var result = await WithTimeoutAsync(connection.OpenAsync(), OpenTimeout).ConfigureAwait(false);
            switch (result.Status)
            {
                case AudioPlaybackConnectionOpenResultStatus.Success:
                    opened = true;
                    break;
                case AudioPlaybackConnectionOpenResultStatus.RequestTimedOut:
                    _lastError = "连接超时（手机可能不在范围内/未响应，稍后自动重试）";
                    Log.Warn(_lastError);
                    break;
                case AudioPlaybackConnectionOpenResultStatus.DeniedBySystem:
                    _lastError = "系统拒绝连接（蓝牙是否已关闭、或连接被别的程序占用？）";
                    Log.Warn(_lastError);
                    break;
                default:
                    _lastError = $"连接失败：{result.Status} (0x{result.ExtendedError?.HResult ?? 0:X8})";
                    Log.Warn(_lastError);
                    break;
            }
        }
        catch (Exception ex)
        {
            _lastError = ex is TaskCanceledException or OperationCanceledException
                ? "连接操作超时（未在限定时间内完成）"
                : $"{ex.GetType().Name}: {ex.Message}";
            Log.Warn("连接过程异常：" + _lastError);
        }

        if (opened)
        {
            _connection = connection;
            _current = target;
            _failureCount = 0;
            _lastError = null;
            Log.Info($"已连接：{target.Name}（手机播放的声音现在会经电脑默认输出设备播放）");
            StatusStore.Write("Opened", target.Name, null);
            return;
        }

        try
        {
            connection.Dispose();
        }
        catch
        {
            // 释放失败无所谓，反正对象会被丢弃
        }

        ScheduleRetry();
        StatusStore.Write("Failed", target.Name, _lastError);
    }

    /// <summary>释放当前连接。注意：释放后绝不能再读 State（会 AccessViolation 崩溃）。</summary>
    private void Detach(string reason)
    {
        var connection = _connection;
        _connection = null;
        _current = null;
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

        Log.Info($"已释放连接（{reason}）");
    }

    private void ScheduleRetry()
    {
        _failureCount = Math.Min(_failureCount + 1, 5);
        var seconds = Math.Min(30, 2 * (1 << (_failureCount - 1)));
        _nextAttemptUtc = DateTime.UtcNow.AddSeconds(seconds);
        Log.Warn($"将在 {seconds} 秒后重试（连续失败 {_failureCount} 次）");
    }

    private void DetectSystemResume()
    {
        var wallNow = DateTime.UtcNow;
        var monotonicNow = _monotonic.Elapsed;
        var wallGap = wallNow - _lastWallUtc;
        var monotonicGap = monotonicNow - _lastMonotonic;

        if (wallGap - monotonicGap > TimeSpan.FromSeconds(15))
        {
            Log.Warn($"检测到系统休眠/唤醒（睡眠约 {wallGap.TotalSeconds - monotonicGap.TotalSeconds:F0} 秒），重建连接");
            if (_connection is not null)
            {
                Detach("系统唤醒");
            }

            _failureCount = 0;
            _nextAttemptUtc = DateTime.MinValue;
            StatusStore.Write("Reconnecting", _current?.Name ?? _settings.DeviceName, _lastError);
        }

        _lastWallUtc = wallNow;
        _lastMonotonic = monotonicNow;
    }

    private SinkDevice? ResolveTarget(IReadOnlyList<SinkDevice> devices)
    {
        if (!string.IsNullOrEmpty(_settings.DeviceId))
        {
            var byId = devices.FirstOrDefault(d => string.Equals(d.Id, _settings.DeviceId, StringComparison.Ordinal));
            if (byId is not null)
            {
                return byId;
            }
        }

        if (!string.IsNullOrEmpty(_settings.DeviceName))
        {
            var matches = devices
                .Where(d => d.Name.Contains(_settings.DeviceName!, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matches.Count == 1)
            {
                return matches[0];
            }
        }

        return null;
    }

    private async Task WithTimeoutAsync(Windows.Foundation.IAsyncAction action, TimeSpan timeout)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cancellationToken);
        cts.CancelAfter(timeout);
        await action.AsTask(cts.Token).ConfigureAwait(false);
    }

    private async Task<T> WithTimeoutAsync<T>(Windows.Foundation.IAsyncOperation<T> operation, TimeSpan timeout)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cancellationToken);
        cts.CancelAfter(timeout);
        return await operation.AsTask(cts.Token).ConfigureAwait(false);
    }
}
