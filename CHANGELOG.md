# Changelog

本项目遵循 [语义化版本](https://semver.org/lang/zh-CN/)。

## 1.0.0 - 2026-10-22

### 新增

- 图形界面（`btaudio-gui.exe`）：托盘常驻、设备列表、自动连接、断线自动重连（间隔可调）、
  开机自启、启动/关闭最小化到托盘、气泡提示、实时日志、诊断信息、默认播放设备显示与切换。
- 命令行（`btaudio.exe`）：`list` / `watch` / `status` / `stop` / `diag`，
  常驻守护支持断线自动重连与设备回来自动恢复。
- 共用能力：指数退避重连、系统休眠唤醒恢复、单实例、与另一形态互斥、高 DPI 适配。
- Release 资产：自包含单文件 exe（无需安装 .NET 运行时）、`SHA256SUMS.txt`。
- CI（构建）与发布（打 tag 自动出 Release）工作流。

### 修复

- 修复命令行守护退出时对已释放 `CancellationTokenSource` 再次 `Cancel()` 导致的进程崩溃
  （`ObjectDisposedException`）。
- 修复 GUI 第二个实例误判「命令行守护正在运行」并弹框的问题（改为先判断单实例）。
- 修复高 DPI（200% 缩放）下 WinForms 只缩放字体、固定布局被撑破的问题，改为按 DPI 自行缩放全部尺寸。
