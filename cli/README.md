# btaudio CLI —— 命令行 / 脚本化版本

Windows 蓝牙 A2DP 接收端的命令行实现，适合脚本化、无人值守或没有桌面会话的场景。
日常使用建议直接用图形界面版（[`../gui/README.md`](../gui/README.md)）。

## 快速开始

```powershell
# 发布版（Release 解压后）或源码构建产物 cli\dist\btaudio.exe
.\btaudio.exe list                  # 列出可作为接收端的已配对设备
.\btaudio.exe watch --device 2      # 常驻守护：连接 + 断线自动重连
.\btaudio.exe status                # 查看守护状态（读 status.json）
.\btaudio.exe stop                  # 请求守护优雅退出
.\btaudio.exe diag                  # 诊断：系统 / API / 设备 / 默认输出设备
```

`watch` 省略 `--device` 时沿用上次保存的设备（存于 `%LOCALAPPDATA%\BtAudioSink\config.json`）。

## 命令

| 命令 | 说明 |
| --- | --- |
| `btaudio list` | 列出候选设备（序号、名称、设备 ID），`*` 标出当前选择 |
| `btaudio watch [--device <序号\|ID\|名称片段>] [--log <文件>] [--quiet]` | 常驻守护 |
| `btaudio status` | 读取状态文件：状态、PID、目标设备、最近错误 |
| `btaudio stop` | 让守护优雅退出（释放蓝牙连接、写状态） |
| `btaudio diag` | 系统版本、A2DP API 可用性、候选设备、默认播放设备、配置路径 |

守护是单实例的；日志按天写入 `%LOCALAPPDATA%\BtAudioSink\logs\watch-yyyyMMdd.log`。

## 后台运行脚本

`scripts/` 下提供：

| 脚本 | 作用 |
| --- | --- |
| `start.ps1` | 隐藏窗口后台启动 `watch`（可 `-Device 2` 指定设备） |
| `stop.ps1` | 先请求优雅退出，超时再强制结束 |
| `install-autostart.ps1` | 在启动文件夹创建开机自启快捷方式 |
| `uninstall-autostart.ps1` | 取消开机自启 |

脚本会自动在 exe 所在目录或 `dist\` 下寻找 `btaudio.exe`。

## 工作原理

1. 用 WinRT `AudioPlaybackConnection.GetDeviceSelector()` 枚举可作为 A2DP Sink 的设备；
2. `TryCreateFromId` → `StartAsync`（允许手机发起连接）→ `OpenAsync`（主动建立连接）；
3. 2 秒粒度守护循环：断线检测、按指数退避（2s→30s）重连、设备回来自动恢复；
4. 通过「墙钟 − 单调钟」差值识别系统休眠唤醒并重建连接；
5. 严格遵守「`Dispose` 之后不再读 `State`」（实测会触发 AccessViolation 崩溃）。

## 从源码构建

```powershell
dotnet build cli\btaudio-cli.csproj -c Release
.\cli\build.ps1          # 发布到 cli\dist\btaudio.exe
```

## 故障排查

### 手机在放声音，电脑没动静

1. 手机控制中心确认音频输出选的是这台电脑（iPhone 首次需手动选一次）；
2. `btaudio diag` 确认默认播放设备正确；
3. 关掉其他占用接收端的程序（例如 AudioPlaybackConnector），并确认 GUI 没有在同时运行。

### 卡顿、爆音、断续

A2DP 链路/无线环境问题：

- 手机热点 / 2.4GHz Wi‑Fi 与蓝牙抢频段，组合网卡还共享天线 → 热点改用 **5GHz**；
- 设备管理器 → 蓝牙适配器 → 电源管理 → 取消「允许计算机关闭此设备以节约电源」；
- 电源选项 → USB 设置 → USB 选择性暂停 → 禁用；
- USB 蓝牙适配器用延长线远离 USB 3.0 接口。

### 连接超时 / `0x8007001F`

手机不在范围内、蓝牙关闭或未解锁；守护会自动重试。反复失败时：Windows 蓝牙设置里删除设备后重新配对，
再 `btaudio list` 重新选择。
