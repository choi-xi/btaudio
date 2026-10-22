using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using BtAudio.Shared;
using Windows.Foundation.Metadata;

namespace BtAudioGui;

internal sealed class MainForm : Form
{
    private readonly GuiSettings _settings;
    private readonly ReceiverManager _manager;
    private readonly string _logFile;
    private readonly bool _startMinimized;
    private float _scale = 1f;

    private readonly NotifyIcon _tray = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private readonly ContextMenuStrip _gridMenu = new();

    private CardPanel _headerCard = null!;
    private StatusDot _dot = null!;
    private Label _stateText = null!;
    private Label _deviceText = null!;
    private Label _detailText = null!;
    private RoundButton _btnReconnect = null!;
    private RoundButton _btnDisconnect = null!;
    private DataGridView _grid = null!;
    private RichTextBox _logBox = null!;
    private Label _statusLabel = null!;
    private CheckBox _chkAutoReconnect = null!;
    private NumericUpDown _numDelay = null!;
    private CheckBox _chkStartup = null!;
    private CheckBox _chkStartMinimized = null!;
    private CheckBox _chkMinimize = null!;
    private CheckBox _chkBalloons = null!;
    private ComboBox _cmbOutput = null!;
    private RoundButton _btnSetDefault = null!;
    private ToolStripMenuItem _trayStatusItem = null!;
    private ToolStripMenuItem _trayAutoItem = null!;
    private ToolStripMenuItem _trayStartupItem = null!;

    private bool _updating;
    private bool _allowExit;
    private bool _initialSelectionCleared;
    private System.Windows.Forms.Timer? _captureTimer;
    private string? _primaryId;
    private LinkState _lastNotifiedState = LinkState.Idle;
    private string _lastNotifiedDevice = string.Empty;
    private bool _trayHintShown;

    public MainForm(GuiSettings settings, ReceiverManager manager, bool startMinimized)
    {
        _settings = settings;
        _manager = manager;
        _startMinimized = startMinimized;
        _logFile = Path.Combine(AppPaths.DataDir, "logs", $"gui-{DateTime.Now:yyyyMMdd}.log");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_logFile)!);
        }
        catch
        {
            // 日志目录建不出来也不影响主流程
        }

        using (var graphics = CreateGraphics())
        {
            _scale = graphics.DpiX / 96f;
        }

        // 自行按 DPI 缩放所有尺寸：WinForms 自带的自动缩放只放大字体，会撑破固定布局
        AutoScaleMode = AutoScaleMode.None;
        Text = "btaudio · 手机声音从电脑播放";
        Font = Theme.Body;
        BackColor = Theme.Bg;
        ClientSize = Ds(1040, 760);
        MinimumSize = Ds(900, 620);
        StartPosition = FormStartPosition.CenterScreen;
        DoubleBuffered = true;
        Icon = TrayIcons.For(LinkState.Waiting);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            BackColor = Theme.Bg,
            Padding = Dp(14, 14, 14, 6),
            Margin = Padding.Empty,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, D(112)));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, D(168)));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, D(24)));
        Controls.Add(root);

        _headerCard = BuildHeader();
        root.Controls.Add(_headerCard, 0, 0);
        root.Controls.Add(BuildContent(), 0, 1);
        root.Controls.Add(BuildLogCard(), 0, 2);
        root.Controls.Add(BuildStatusBar(), 0, 3);

        InitTray();
        WireEvents();
        LayoutHeader();

        _manager.Start();
        _timer.Start();
        _ = LoadOutputDevicesAsync();
        RefreshUi();
    }

    internal void ShowFromTray()
    {
        if (IsDisposed)
        {
            return;
        }

        if (InvokeRequired)
        {
            BeginInvoke(new Action(ShowFromTray));
            return;
        }

        ShowInTaskbar = true;
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    internal void RequestExit()
    {
        if (IsDisposed)
        {
            return;
        }

        if (InvokeRequired)
        {
            BeginInvoke(new Action(RequestExit));
            return;
        }

        _allowExit = true;
        Close();
    }

    internal string? ScreenshotPath { get; set; }

    internal int ScreenshotDelaySeconds { get; set; } = 8;

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (_startMinimized || _settings.StartMinimized)
        {
            ShowInTaskbar = false;
            Hide();
        }

        if (!string.IsNullOrEmpty(ScreenshotPath))
        {
            _captureTimer = new System.Windows.Forms.Timer { Interval = Math.Clamp(ScreenshotDelaySeconds, 1, 60) * 1000 };
            var captureTimer = _captureTimer;
            captureTimer.Tick += (_, _) =>
            {
                captureTimer.Stop();
                try
                {
                    CaptureSelf();
                }
                catch (Exception ex)
                {
                    try
                    {
                        File.WriteAllText(ScreenshotPath + ".err.txt", ex.ToString());
                    }
                    catch
                    {
                        // 忽略
                    }
                }
                finally
                {
                    Environment.Exit(0);
                }
            };
            captureTimer.Start();
        }
    }

    private void CaptureSelf()
    {
        if (string.IsNullOrEmpty(ScreenshotPath))
        {
            return;
        }

        Refresh();
        Application.DoEvents();
        using var bitmap = new Bitmap(Width, Height);
        DrawToBitmap(bitmap, new Rectangle(0, 0, Width, Height));
        var directory = Path.GetDirectoryName(Path.GetFullPath(ScreenshotPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        bitmap.Save(ScreenshotPath, System.Drawing.Imaging.ImageFormat.Png);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        LayoutHeader();
        if (WindowState == FormWindowState.Minimized && _settings.MinimizeToTray)
        {
            WindowState = FormWindowState.Normal;
            ShowInTaskbar = false;
            Hide();
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_allowExit && _settings.MinimizeToTray && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            ShowInTaskbar = false;
            Hide();
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                _tray.ShowBalloonTip(2000, "btaudio 仍在后台运行", "双击托盘图标可以重新打开窗口", ToolTipIcon.Info);
            }

            return;
        }

        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _timer.Stop();
        _timer.Dispose();
        _settings.Save();
        _manager.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        base.OnFormClosed(e);
    }

    // ---------- DPI 缩放辅助 ----------

    private int D(int value) => (int)Math.Round(value * _scale);

    private Size Ds(int width, int height) => new(D(width), D(height));

    private Padding Dp(int left, int top, int right, int bottom)
        => new(D(left), D(top), D(right), D(bottom));

    // ---------- 版面 ----------

    private CardPanel BuildHeader()
    {
        var card = new CardPanel
        {
            Dock = DockStyle.Fill,
            Margin = Dp(0, 0, 0, 10),
            Radius = D(12),
        };

        _dot = new StatusDot { Size = Ds(34, 34), Margin = Padding.Empty };
        _stateText = new Label
        {
            AutoSize = true,
            Font = Theme.H1,
            ForeColor = Theme.Text,
            Text = "准备中…",
            BackColor = Theme.Card,
        };
        _deviceText = new Label
        {
            AutoSize = true,
            Font = Theme.BodyStrong,
            ForeColor = Theme.Text,
            Text = "未选择设备",
            BackColor = Theme.Card,
        };
        _detailText = new Label
        {
            AutoSize = true,
            Font = Theme.Small,
            ForeColor = Theme.Muted,
            Text = string.Empty,
            BackColor = Theme.Card,
        };
        _btnReconnect = new RoundButton { Text = "立即重连", Size = Ds(92, 34), Radius = D(8) };
        _btnDisconnect = new RoundButton { Text = "断开", Size = Ds(72, 34), Outline = true, Radius = D(8) };

        card.Controls.Add(_dot);
        card.Controls.Add(_stateText);
        card.Controls.Add(_deviceText);
        card.Controls.Add(_detailText);
        card.Controls.Add(_btnReconnect);
        card.Controls.Add(_btnDisconnect);
        return card;
    }

    private void LayoutHeader()
    {
        if (_headerCard is null || _dot is null || _headerCard.Width <= 0)
        {
            return;
        }

        var pad = D(18);
        _dot.Location = new Point(pad, (_headerCard.Height - _dot.Height) / 2);

        var textX = pad + _dot.Width + D(14);
        var buttonsWidth = _btnReconnect.Width + _btnDisconnect.Width + D(8);
        var buttonsX = Math.Max(textX + D(120), _headerCard.Width - pad - buttonsWidth);

        _btnReconnect.Location = new Point(buttonsX, (_headerCard.Height - _btnReconnect.Height) / 2);
        _btnDisconnect.Location = new Point(buttonsX + _btnReconnect.Width + D(8), (_headerCard.Height - _btnDisconnect.Height) / 2);

        var textWidth = Math.Max(D(200), buttonsX - textX - D(12));
        _stateText.MaximumSize = new Size(textWidth, 0);
        _deviceText.MaximumSize = new Size(textWidth, 0);
        _detailText.MaximumSize = new Size(textWidth, 0);

        var y = D(10);
        _stateText.Location = new Point(textX, y);
        y += _stateText.Height + D(2);
        _deviceText.Location = new Point(textX, y);
        y += _deviceText.Height + D(2);
        _detailText.Location = new Point(textX, y);
    }

    private Control BuildContent()
    {
        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Theme.Bg,
            Margin = Padding.Empty,
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, D(300)));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        content.Controls.Add(BuildDevicesCard(), 0, 0);
        content.Controls.Add(BuildSettingsHost(), 1, 0);
        return content;
    }

    private Control BuildDevicesCard()
    {
        var card = new CardPanel { Dock = DockStyle.Fill, Padding = Dp(14, 12, 14, 12), Margin = Dp(0, 0, 10, 0), Radius = D(12) };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Color.White,
            Margin = Padding.Empty,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, D(30)));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var head = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Color.White,
            Margin = Padding.Empty,
        };
        head.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        head.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, D(82)));
        head.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        head.Controls.Add(new Label
        {
            Text = "接收设备",
            Font = Theme.SectionTitle,
            ForeColor = Theme.Text,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            BackColor = Color.White,
            Margin = Padding.Empty,
        }, 0, 0);
        var refresh = new RoundButton { Text = "刷新", Outline = true, Size = Ds(72, 26), Anchor = AnchorStyles.Right, Margin = Dp(0, 2, 0, 2), Radius = D(6) };
        refresh.Click += (_, _) =>
        {
            _manager.Refresh();
            AppendLog("正在刷新设备列表…");
        };
        head.Controls.Add(refresh, 1, 0);
        layout.Controls.Add(head, 0, 0);

        _grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            AllowUserToResizeColumns = false,
            BorderStyle = BorderStyle.None,
            BackgroundColor = Color.White,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            EnableHeadersVisualStyles = false,
            ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None,
            ColumnHeadersHeight = D(30),
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
            GridColor = Theme.BorderSoft,
            CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
            ShowCellToolTips = true,
            Margin = Padding.Empty,
        };
        _grid.DefaultCellStyle.Font = Theme.Body;
        _grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(235, 243, 255);
        _grid.DefaultCellStyle.SelectionForeColor = Theme.Text;
        _grid.ColumnHeadersDefaultCellStyle.Font = Theme.Small;
        _grid.ColumnHeadersDefaultCellStyle.ForeColor = Theme.Muted;
        _grid.ColumnHeadersDefaultCellStyle.BackColor = Color.White;
        _grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Color.White;
        _grid.RowTemplate.Height = D(36);

        _grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "auto", HeaderText = "自动", Width = D(58), FlatStyle = FlatStyle.Flat });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "name", HeaderText = "设备", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 55, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "state", HeaderText = "状态", Width = D(78), ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "reconnects", HeaderText = "重连", Width = D(50), ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "last", HeaderText = "最近错误", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 45, ReadOnly = true });

        layout.Controls.Add(_grid, 0, 1);
        card.Controls.Add(layout);
        return card;
    }

    private Control BuildSettingsHost()
    {
        var host = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, AutoScroll = true, Margin = Padding.Empty };
        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Theme.Bg,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };

        _chkAutoReconnect = NewCheckBox("断线自动重连", _settings.AutoReconnect);
        _numDelay = new NumericUpDown
        {
            Minimum = 1,
            Maximum = 30,
            Value = Math.Clamp(_settings.ReconnectDelaySeconds, 1, 30),
            Width = D(58),
            BorderStyle = BorderStyle.FixedSingle,
            Font = Theme.Body,
            BackColor = Color.White,
        };
        var delayRow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Theme.Card,
            Margin = new Padding(0, D(2), 0, 0),
        };
        delayRow.Controls.Add(new Label
        {
            Text = "重连间隔（秒）",
            AutoSize = true,
            ForeColor = Theme.Muted,
            Font = Theme.Small,
            BackColor = Theme.Card,
            Margin = new Padding(0, D(6), D(6), 0),
        });
        delayRow.Controls.Add(_numDelay);
        flow.Controls.Add(BuildSection("守护", _chkAutoReconnect, delayRow));

        _chkStartup = NewCheckBox("开机自启（登录后托盘运行）", File.Exists(StartupShortcutPath));
        _chkStartMinimized = NewCheckBox("启动时最小化到托盘", _settings.StartMinimized);
        _chkMinimize = NewCheckBox("关闭窗口时最小化到托盘", _settings.MinimizeToTray);
        _chkBalloons = NewCheckBox("连接/断开时弹出气泡", _settings.ShowBalloons);
        flow.Controls.Add(BuildSection("系统", _chkStartup, _chkStartMinimized, _chkMinimize, _chkBalloons));

        _cmbOutput = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Flat,
            Width = D(248),
            Font = Theme.Body,
            BackColor = Color.White,
        };
        _btnSetDefault = new RoundButton { Text = "设为系统默认播放设备", Outline = true, Size = Ds(248, 30), Radius = D(8) };
        var soundLink = new LinkLabel
        {
            Text = "打开 Windows 声音设置",
            AutoSize = true,
            LinkColor = Theme.Accent,
            Font = Theme.Small,
            BackColor = Theme.Card,
        };
        soundLink.LinkClicked += (_, _) => OpenUrl("ms-settings:sound");
        flow.Controls.Add(BuildSection("输出", _cmbOutput, _btnSetDefault, soundLink));

        host.Controls.Add(flow);
        return host;
    }

    private static CheckBox NewCheckBox(string text, bool isChecked) => new()
    {
        Text = text,
        AutoSize = true,
        Checked = isChecked,
        Font = Theme.Body,
        ForeColor = Theme.Text,
        BackColor = Theme.Card,
        Margin = Padding.Empty,
    };

    private CardPanel BuildSection(string title, params Control[] children)
    {
        var card = new CardPanel
        {
            Width = D(280),
            Margin = Dp(0, 0, 0, 8),
            Radius = D(10),
        };

        var titleLabel = new Label
        {
            Text = title,
            Font = Theme.SectionTitle,
            ForeColor = Theme.Muted,
            AutoSize = true,
            BackColor = Theme.Card,
            Location = new Point(D(16), D(10)),
        };
        card.Controls.Add(titleLabel);

        var y = titleLabel.Top + titleLabel.Height + D(6);
        foreach (var child in children)
        {
            child.Location = new Point(D(16), y);
            card.Controls.Add(child);
            y += child.Height + D(6);
        }

        card.Height = y + D(6);
        return card;
    }

    private Control BuildLogCard()
    {
        var card = new CardPanel { Dock = DockStyle.Fill, Padding = Dp(14, 10, 14, 10), Margin = Dp(0, 10, 0, 10), Radius = D(12) };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Color.White,
            Margin = Padding.Empty,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, D(30)));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var head = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 1,
            BackColor = Color.White,
            Margin = Padding.Empty,
        };
        head.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        head.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, D(78)));
        head.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, D(104)));
        head.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, D(62)));
        head.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        head.Controls.Add(new Label
        {
            Text = "运行日志",
            Font = Theme.SectionTitle,
            ForeColor = Theme.Text,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            BackColor = Color.White,
            Margin = Padding.Empty,
        }, 0, 0);
        var btnDiag = new RoundButton { Text = "诊断", Outline = true, Size = Ds(70, 24), Anchor = AnchorStyles.Right, Margin = Dp(0, 3, D(6), 3), Radius = D(6) };
        btnDiag.Click += (_, _) => ShowDiagnostics();
        head.Controls.Add(btnDiag, 1, 0);
        var btnOpenLog = new RoundButton { Text = "日志目录", Outline = true, Size = Ds(96, 24), Anchor = AnchorStyles.Right, Margin = Dp(0, 3, D(6), 3), Radius = D(6) };
        btnOpenLog.Click += (_, _) => OpenFolder(Path.GetDirectoryName(_logFile)!);
        head.Controls.Add(btnOpenLog, 2, 0);
        var btnClear = new RoundButton { Text = "清空", Outline = true, Size = Ds(56, 24), Anchor = AnchorStyles.Right, Margin = Dp(0, 3, 0, 3), Radius = D(6) };
        btnClear.Click += (_, _) => _logBox.Clear();
        head.Controls.Add(btnClear, 3, 0);
        layout.Controls.Add(head, 0, 0);

        _logBox = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            BackColor = Color.White,
            ForeColor = Theme.Text,
            Font = Theme.Mono,
            DetectUrls = false,
            HideSelection = false,
            ScrollBars = RichTextBoxScrollBars.Vertical,
            Margin = Padding.Empty,
        };
        layout.Controls.Add(_logBox, 0, 1);

        card.Controls.Add(layout);
        return card;
    }

    private Control BuildStatusBar()
    {
        var panel = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Margin = Padding.Empty };
        _statusLabel = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Theme.Muted,
            Font = Theme.Small,
            BackColor = Theme.Bg,
            AutoSize = false,
        };
        panel.Controls.Add(_statusLabel);
        return panel;
    }

    private void InitTray()
    {
        _trayStatusItem = new ToolStripMenuItem("状态：准备中") { Enabled = false };
        _trayAutoItem = new ToolStripMenuItem("断线自动重连", null, (_, _) => _chkAutoReconnect.Checked = !_chkAutoReconnect.Checked);
        _trayStartupItem = new ToolStripMenuItem("开机自启", null, (_, _) => _chkStartup.Checked = !_chkStartup.Checked);

        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("显示主窗口", null, (_, _) => ShowFromTray()));
        menu.Items.Add(new ToolStripMenuItem("立即重连", null, (_, _) => ReconnectPrimary()));
        menu.Items.Add(new ToolStripMenuItem("诊断信息", null, (_, _) => ShowDiagnostics()));
        menu.Items.Add(new ToolStripMenuItem("打开日志目录", null, (_, _) => OpenFolder(Path.GetDirectoryName(_logFile)!)));
        menu.Items.Add(new ToolStripMenuItem("打开配置目录", null, (_, _) => OpenFolder(AppPaths.DataDir)));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_trayStatusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_trayAutoItem);
        menu.Items.Add(_trayStartupItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("退出", null, (_, _) => RequestExit()));

        _tray.ContextMenuStrip = menu;
        _tray.Icon = TrayIcons.For(LinkState.Waiting);
        _tray.Text = "btaudio";
        _tray.Visible = true;
        _tray.DoubleClick += (_, _) => ShowFromTray();
    }

    private void WireEvents()
    {
        _manager.Changed += (_, _) => Post(RefreshUi);
        _manager.LogLine += (_, message) => Post(() => AppendLog(message));

        _btnReconnect.Click += (_, _) => ReconnectPrimary();
        _btnDisconnect.Click += (_, _) => DisconnectPrimary();

        _grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_grid.IsCurrentCellDirty)
            {
                _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            }
        };
        _grid.CellValueChanged += OnGridCellValueChanged;
        _grid.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex >= 0)
            {
                ConnectRow(e.RowIndex);
            }
        };
        _grid.CellMouseDown += (_, e) =>
        {
            if (e.RowIndex >= 0)
            {
                _grid.ClearSelection();
                _grid.Rows[e.RowIndex].Selected = true;
            }
        };
        _gridMenu.Items.Add("连接", null, (_, _) => ConnectSelectedRow());
        _gridMenu.Items.Add("断开", null, (_, _) => DisconnectSelectedRow());
        _gridMenu.Items.Add(new ToolStripSeparator());
        _gridMenu.Items.Add("复制设备 ID", null, (_, _) => CopySelectedDeviceId());
        _gridMenu.Items.Add("从列表移除", null, (_, _) => ForgetSelectedRow());
        _grid.ContextMenuStrip = _gridMenu;

        _chkAutoReconnect.CheckedChanged += (_, _) =>
        {
            if (_updating)
            {
                return;
            }

            _manager.AutoReconnect = _chkAutoReconnect.Checked;
            _settings.AutoReconnect = _chkAutoReconnect.Checked;
            _settings.Save();
            AppendLog("断线自动重连：" + (_settings.AutoReconnect ? "开启" : "关闭"));
            UpdateStatusLine();
        };

        _numDelay.ValueChanged += (_, _) =>
        {
            if (_updating)
            {
                return;
            }

            _settings.ReconnectDelaySeconds = (int)_numDelay.Value;
            _manager.ReconnectDelaySeconds = _settings.ReconnectDelaySeconds;
            _settings.Save();
            UpdateStatusLine();
        };

        _chkStartMinimized.CheckedChanged += (_, _) =>
        {
            _settings.StartMinimized = _chkStartMinimized.Checked;
            _settings.Save();
        };

        _chkMinimize.CheckedChanged += (_, _) =>
        {
            _settings.MinimizeToTray = _chkMinimize.Checked;
            _settings.Save();
        };

        _chkBalloons.CheckedChanged += (_, _) =>
        {
            _settings.ShowBalloons = _chkBalloons.Checked;
            _settings.Save();
        };

        _chkStartup.CheckedChanged += (_, _) =>
        {
            if (_updating)
            {
                return;
            }

            SetAutoStart(_chkStartup.Checked);
        };

        _btnSetDefault.Click += async (_, _) => await ApplyDefaultOutputAsync();
        _cmbOutput.DropDown += async (_, _) => await LoadOutputDevicesAsync();
        _timer.Tick += (_, _) => RefreshDuration();
    }

    private async Task LoadOutputDevicesAsync()
    {
        try
        {
            var devices = await AudioDefaults.ListRenderAsync();
            var selected = _settings.DefaultOutputDeviceId ?? AudioDefaults.GetDefaultRenderId();

            _updating = true;
            _cmbOutput.Items.Clear();
            OutputDevice? picked = null;
            foreach (var (id, name) in devices)
            {
                var item = new OutputDevice(id, name);
                _cmbOutput.Items.Add(item);
                if (picked is null && id == selected)
                {
                    picked = item;
                }
            }

            _cmbOutput.SelectedItem = picked ?? _cmbOutput.Items.Cast<object>().FirstOrDefault();
            _updating = false;
        }
        catch (Exception ex)
        {
            _updating = false;
            AppendLog("读取输出设备失败：" + ex.Message);
        }
    }

    private async Task ApplyDefaultOutputAsync()
    {
        if (_cmbOutput.SelectedItem is not OutputDevice device)
        {
            return;
        }

        var (success, error) = await Task.Run(() =>
        {
            var ok = AudioDefaults.SetDefault(device.Id, out var message);
            return (ok, message);
        });

        if (success)
        {
            _settings.DefaultOutputDeviceId = device.Id;
            _settings.Save();
            AppendLog("默认播放设备已切换为：" + device.Name);
        }
        else
        {
            AppendLog("切换默认播放设备失败：" + error);
            MessageBox.Show(
                this,
                "Windows 拒绝了程序自动切换默认播放设备（部分系统版本会返回 0x80070032）。\n\n已为你打开「声音设置」，请手动把输出设备切换过去。",
                "btaudio",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            OpenUrl("ms-settings:sound");
        }

        await LoadOutputDevicesAsync();
    }

    private void SetAutoStart(bool enable)
    {
        try
        {
            if (string.IsNullOrEmpty(StartupShortcutPath))
            {
                return;
            }

            if (!enable)
            {
                if (File.Exists(StartupShortcutPath))
                {
                    File.Delete(StartupShortcutPath);
                }

                AppendLog("已关闭开机自启");
                return;
            }

            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null)
            {
                throw new InvalidOperationException("系统没有 WScript.Shell");
            }

            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic link = shell.CreateShortcut(StartupShortcutPath);
            link.TargetPath = Application.ExecutablePath;
            link.Arguments = "--minimized";
            link.WorkingDirectory = Path.GetDirectoryName(Application.ExecutablePath) ?? string.Empty;
            link.WindowStyle = 7;
            link.Description = "btaudio 蓝牙音频接收（托盘常驻）";
            link.Save();
            AppendLog("已开启开机自启");
        }
        catch (Exception ex)
        {
            AppendLog("设置开机自启失败：" + ex.Message);
            MessageBox.Show(this, "设置开机自启失败：\n" + ex.Message, "btaudio", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static string StartupShortcutPath
    {
        get
        {
            try
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "BtAudioGui.lnk");
            }
            catch
            {
                return string.Empty;
            }
        }
    }

    private void RefreshUi()
    {
        var snapshot = _manager.Snapshot;
        SyncGrid(snapshot);

        var primary = snapshot.FirstOrDefault(d => d.State == LinkState.Opened)
            ?? snapshot.FirstOrDefault(d => d.State == LinkState.Connecting)
            ?? snapshot.FirstOrDefault(d => d.State == LinkState.Error)
            ?? snapshot.FirstOrDefault(d => d.AutoConnect && d.Present)
            ?? snapshot.FirstOrDefault(d => d.AutoConnect)
            ?? snapshot.FirstOrDefault();

        _primaryId = primary?.Id;
        UpdateHeader(primary);
        LayoutHeader();
        UpdateTray(primary);
        NotifyTransition(primary);
        UpdateStatusLine();
    }

    private void RefreshDuration()
    {
        var primary = _manager.Snapshot.FirstOrDefault(d => d.Id == _primaryId);
        if (primary is { State: LinkState.Opened } && _detailText.Text.Length > 0)
        {
            _detailText.Text = BuildDetail(primary);
            LayoutHeader();
        }
    }

    private void UpdateHeader(DeviceSnapshot? primary)
    {
        if (primary is null)
        {
            _stateText.Text = "等待设备";
            _stateText.ForeColor = Theme.Muted;
            _dot.DotColor = Theme.Muted;
            _deviceText.Text = "还没有发现可作为接收端的手机";
            _detailText.Text = "请先在 Windows 蓝牙设置里把手机与电脑配对";
            _btnReconnect.Enabled = false;
            _btnDisconnect.Enabled = false;
            return;
        }

        var color = Theme.StateColor(primary.State);
        _stateText.Text = Theme.StateText(primary.State);
        _stateText.ForeColor = color;
        _dot.DotColor = color;
        _deviceText.Text = primary.Present ? primary.Name : primary.Name + "（不在范围内）";
        _detailText.Text = BuildDetail(primary);
        _btnReconnect.Enabled = primary.State != LinkState.Opened;
        _btnDisconnect.Enabled = primary.State is LinkState.Opened or LinkState.Connecting;
    }

    private static string BuildDetail(DeviceSnapshot device) => device.State switch
    {
        LinkState.Opened => $"已连接 {FormatDuration(device.OpenedAtUtc)} · 累计重连 {device.Reconnects} 次 · 手机声音从电脑默认输出设备播放",
        LinkState.Connecting => "正在建立蓝牙音频连接…",
        LinkState.Error => string.IsNullOrEmpty(device.LastError) ? "连接异常，稍后自动重试" : device.LastError!,
        LinkState.Waiting => device.Present ? "等待重试，或等待手机进入可连接状态…" : "设备当前不在范围内，等待手机回来…",
        _ => "双击设备或勾选「自动」即可开始接收",
    };

    private static string FormatDuration(DateTime? since)
    {
        if (since is null)
        {
            return "—";
        }

        var span = DateTime.UtcNow - since.Value;
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours:00}:{span.Minutes:00}:{span.Seconds:00}"
            : $"{span.Minutes:00}:{span.Seconds:00}";
    }

    private void SyncGrid(IReadOnlyList<DeviceSnapshot> snapshot)
    {
        _updating = true;
        try
        {
            for (var i = _grid.Rows.Count - 1; i >= 0; i--)
            {
                var id = _grid.Rows[i].Tag as string;
                if (snapshot.All(d => d.Id != id))
                {
                    _grid.Rows.RemoveAt(i);
                }
            }

            foreach (var device in snapshot)
            {
                DataGridViewRow? row = null;
                foreach (DataGridViewRow candidate in _grid.Rows)
                {
                    if ((candidate.Tag as string) == device.Id)
                    {
                        row = candidate;
                        break;
                    }
                }

                if (row is null)
                {
                    row = _grid.Rows[_grid.Rows.Add()];
                    row.Tag = device.Id;
                }

                row.Cells[0].Value = device.AutoConnect;
                row.Cells[1].Value = device.Name;
                row.Cells[2].Value = Theme.StateText(device.State);
                row.Cells[2].Style.ForeColor = Theme.StateColor(device.State);
                row.Cells[3].Value = device.Reconnects > 0 ? device.Reconnects.ToString() : string.Empty;
                row.Cells[4].Value = device.Present ? device.LastError ?? string.Empty : "不在范围";
                row.Cells[4].Style.ForeColor = device.Present ? Theme.Muted : Theme.Amber;
            }

            if (!_initialSelectionCleared && _grid.Rows.Count > 0)
            {
                _grid.ClearSelection();
                _initialSelectionCleared = true;
            }
        }
        finally
        {
            _updating = false;
        }
    }

    private void OnGridCellValueChanged(object? sender, DataGridViewCellEventArgs e)
    {
        if (_updating || e.RowIndex < 0 || e.ColumnIndex != 0)
        {
            return;
        }

        var id = _grid.Rows[e.RowIndex].Tag as string;
        if (id is null)
        {
            return;
        }

        var auto = _grid.Rows[e.RowIndex].Cells[0].Value is true;
        _manager.SetAutoConnect(id, auto);
        AppendLog((auto ? "已开启自动连接：" : "已关闭自动连接：") + _grid.Rows[e.RowIndex].Cells[1].Value);
    }

    private void ConnectRow(int rowIndex)
    {
        if (_grid.Rows[rowIndex].Tag is string id)
        {
            _manager.Connect(id);
        }
    }

    private void ConnectSelectedRow()
    {
        if (_grid.SelectedRows.Count > 0)
        {
            ConnectRow(_grid.SelectedRows[0].Index);
        }
    }

    private void DisconnectSelectedRow()
    {
        if (_grid.SelectedRows.Count > 0 && _grid.SelectedRows[0].Tag is string id)
        {
            _manager.Disconnect(id);
        }
    }

    private void ForgetSelectedRow()
    {
        if (_grid.SelectedRows.Count > 0 && _grid.SelectedRows[0].Tag is string id)
        {
            _manager.Forget(id);
        }
    }

    private void CopySelectedDeviceId()
    {
        if (_grid.SelectedRows.Count > 0 && _grid.SelectedRows[0].Tag is string id)
        {
            try
            {
                Clipboard.SetText(id);
                AppendLog("设备 ID 已复制到剪贴板");
            }
            catch
            {
                // 剪贴板被占用时忽略
            }
        }
    }

    private void ReconnectPrimary()
    {
        if (_primaryId is not null)
        {
            _manager.Connect(_primaryId);
            return;
        }

        if (_grid.SelectedRows.Count > 0)
        {
            ConnectSelectedRow();
        }
    }

    private void DisconnectPrimary()
    {
        if (_primaryId is not null)
        {
            _manager.Disconnect(_primaryId);
        }
    }

    private void UpdateTray(DeviceSnapshot? primary)
    {
        var state = primary?.State ?? LinkState.Idle;
        _tray.Icon = TrayIcons.For(state);

        var caption = primary is null
            ? "btaudio · 等待设备"
            : $"btaudio · {Theme.StateText(state)} · {Trim(primary.Name, 28)}";
        _tray.Text = Trim(caption, 63);

        _trayStatusItem.Text = primary is null
            ? "状态：等待设备"
            : $"状态：{Theme.StateText(state)} · {Trim(primary.Name, 30)}";
        _trayAutoItem.Checked = _settings.AutoReconnect;
        _trayStartupItem.Checked = File.Exists(StartupShortcutPath);
    }

    private void NotifyTransition(DeviceSnapshot? primary)
    {
        if (!_settings.ShowBalloons || primary is null)
        {
            return;
        }

        if (primary.State == _lastNotifiedState && primary.Id == _lastNotifiedDevice)
        {
            return;
        }

        var previousState = _lastNotifiedState;
        var previousDevice = _lastNotifiedDevice;
        _lastNotifiedState = primary.State;
        _lastNotifiedDevice = primary.Id;

        if (primary.State == LinkState.Opened)
        {
            _tray.ShowBalloonTip(2500, "已连接", $"{primary.Name} 的声音现在从电脑播放", ToolTipIcon.Info);
        }
        else if (primary.State == LinkState.Error && previousState == LinkState.Opened && previousDevice == primary.Id)
        {
            _tray.ShowBalloonTip(2500, "连接断开", "守护会在后台自动重连，无需手动操作", ToolTipIcon.Warning);
        }
    }

    private void UpdateStatusLine()
    {
        _statusLabel.Text = $"自动重连：{(_settings.AutoReconnect ? "开" : "关")} · 间隔 {_settings.ReconnectDelaySeconds}s · 设备 {_manager.Snapshot.Count} 个 · 日志 {_logFile}";

        if (_chkAutoReconnect.Checked != _settings.AutoReconnect)
        {
            _updating = true;
            _chkAutoReconnect.Checked = _settings.AutoReconnect;
            _updating = false;
        }

        if ((int)_numDelay.Value != _settings.ReconnectDelaySeconds)
        {
            _updating = true;
            _numDelay.Value = Math.Clamp(_settings.ReconnectDelaySeconds, 1, 30);
            _updating = false;
        }
    }

    private void AppendLog(string message)
    {
        if (IsDisposed)
        {
            return;
        }

        try
        {
            File.AppendAllText(_logFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}", new UTF8Encoding(false));
        }
        catch
        {
            // 日志写失败不影响功能
        }

        _logBox.AppendText($"{DateTime.Now:HH:mm:ss}  {message}{Environment.NewLine}");
        if (_logBox.TextLength > 200_000)
        {
            _logBox.Text = _logBox.Text[^120_000..];
        }

        _logBox.SelectionStart = _logBox.TextLength;
        _logBox.ScrollToCaret();
    }

    private void Post(Action action)
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }

        try
        {
            BeginInvoke(action);
        }
        catch (ObjectDisposedException)
        {
            // 窗口已销毁
        }
    }

    private void ShowDiagnostics()
    {
        var builder = new StringBuilder();
        builder.AppendLine("Windows        : " + Environment.OSVersion + "  (" + RuntimeInformation.OSDescription + ")");
        builder.AppendLine("DPI            : " + DeviceDpi + "（缩放 " + Math.Round(_scale * 100) + "%）");
        builder.AppendLine("A2DP Sink API  : " + (ApiInformation.IsTypePresent("Windows.Media.Audio.AudioPlaybackConnection") ? "可用" : "不可用"));
        builder.AppendLine("默认播放设备   : " + (_cmbOutput.SelectedItem?.ToString() ?? "(未知)"));
        builder.AppendLine("配置文件       : " + GuiSettings.FilePath);
        builder.AppendLine("日志文件       : " + _logFile);
        builder.AppendLine();
        builder.AppendLine("设备：");
        foreach (var device in _manager.Snapshot)
        {
            builder.AppendLine($"- {device.Name} | {Theme.StateText(device.State)} | 自动={device.AutoConnect} | 在范围={device.Present} | 重连={device.Reconnects} | 错误={device.LastError ?? "-"}");
        }

        var dialog = new Form
        {
            Text = "诊断信息",
            ClientSize = Ds(720, 420),
            StartPosition = FormStartPosition.CenterParent,
            Font = Theme.Body,
            BackColor = Theme.Bg,
            MinimizeBox = false,
            MaximizeBox = false,
            AutoScaleMode = AutoScaleMode.None,
        };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = Dp(14, 12, 14, 12),
            BackColor = Theme.Bg,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, D(40)));
        layout.Controls.Add(new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            Font = Theme.Mono,
            Text = builder.ToString(),
            BackColor = Color.White,
        }, 0, 0);

        var footer = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
        var close = new RoundButton
        {
            Text = "关闭",
            Outline = true,
            Dock = DockStyle.Right,
            Width = D(90),
            Height = D(30),
            Margin = Padding.Empty,
        };
        close.Click += (_, _) => dialog.Close();
        footer.Controls.Add(close);
        layout.Controls.Add(footer, 0, 1);
        dialog.Controls.Add(layout);
        dialog.ShowDialog(this);
    }

    private void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppendLog("打开目录失败：" + ex.Message);
        }
    }

    private void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppendLog("打开设置失败：" + ex.Message);
        }
    }

    private static string Trim(string value, int max)
        => value.Length <= max ? value : value[..(max - 1)] + "…";

    private sealed class OutputDevice
    {
        public OutputDevice(string id, string name)
        {
            Id = id;
            Name = name;
        }

        public string Id { get; }

        public string Name { get; }

        public override string ToString() => Name;
    }
}
