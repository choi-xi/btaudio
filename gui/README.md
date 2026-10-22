# btaudio GUI —— 托盘图形界面

Windows 蓝牙 A2DP 接收端的图形界面版。详见 [仓库根 README](../README.md)。

![主界面](../docs/screenshot.png)

## 功能

**接收与连接**

- 设备列表：自动枚举可作为接收端的已配对手机，显示状态、重连次数、最近错误
- 自动连接：勾选「自动」即在该设备出现时自动建立连接（可多台分别勾选）
- 手动连接/断开：双击设备行、右键菜单，或标题栏「立即重连 / 断开」
- 断线自动重连：可开关，默认开启；重连间隔可调（1–30 秒），连续失败按指数退避
- 设备离开/回来、系统睡眠唤醒后都会自动恢复

**系统集成**

- 托盘常驻：关闭窗口默认最小化到托盘（可关），双击托盘图标重新打开
- 开机自启：登录后自动以最小化方式启动
- 启动最小化、连接/断开气泡提示
- 单实例：重复启动只唤醒已有窗口

**输出与排查**

- 显示当前系统默认播放设备，支持「设为默认」（受系统接口限制，见下）
- 窗口内实时日志 + 按天写入 `%LOCALAPPDATA%\BtAudioSink\logs\gui-yyyyMMdd.log`
- 诊断信息：系统版本、DPI、A2DP API 可用性、设备状态、配置/日志路径
- 托盘菜单：显示窗口、立即重连、诊断、打开日志/配置目录、快速开关自动重连与开机自启、退出
- 按显示器 DPI 自行缩放尺寸，200% 缩放下布局不错位

## 运行

```powershell
# 发布版（仓库 Releases 解压后）
.\btaudio-gui.exe

# 源码调试
dotnet run --project gui\btaudio-gui.csproj
```

首次使用：确认手机已配对 → 勾选设备「自动」或双击连接 → 手机放音。
iPhone 首次可能需要在控制中心把音频输出选到这台电脑。

## 与命令行版的关系

两者共用同一套接收逻辑，**互斥运行**：

- GUI 启动时会占用命令行守护的互斥体；若检测到 `btaudio watch` 在跑，会询问是否先停止它；
- GUI 运行时执行 `btaudio watch` 会提示已有实例；
- `btaudio stop` 也能让 GUI 优雅退出。

## 配置

| 内容 | 路径 |
| --- | --- |
| 配置 | `%LOCALAPPDATA%\BtAudioSink\gui-settings.json` |
| 日志 | `%LOCALAPPDATA%\BtAudioSink\logs\gui-yyyyMMdd.log` |

## 已知限制

- **切换默认播放设备**依赖 Windows 未公开的 `IPolicyConfig/IPolicyConfigVista`。部分系统会返回
  `0x80070032`，此时程序会打开「声音设置」让你手动切换——这不影响接收功能本身。
- 仅媒体音频（A2DP），不含通话音频（HFP）。
- 未签名的 exe 首次运行可能触发 SmartScreen 提示。
