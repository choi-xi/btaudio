using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace BtAudioGui;

internal static class Theme
{
    public static readonly Color Bg = Color.FromArgb(245, 247, 250);
    public static readonly Color Card = Color.White;
    public static readonly Color Border = Color.FromArgb(226, 230, 236);
    public static readonly Color BorderSoft = Color.FromArgb(238, 241, 245);
    public static readonly Color Text = Color.FromArgb(24, 28, 33);
    public static readonly Color Muted = Color.FromArgb(107, 114, 128);
    public static readonly Color Accent = Color.FromArgb(37, 99, 235);
    public static readonly Color AccentHover = Color.FromArgb(29, 78, 216);
    public static readonly Color AccentPressed = Color.FromArgb(30, 64, 175);
    public static readonly Color HoverSurface = Color.FromArgb(241, 245, 249);
    public static readonly Color Green = Color.FromArgb(22, 163, 74);
    public static readonly Color Amber = Color.FromArgb(217, 119, 6);
    public static readonly Color Red = Color.FromArgb(220, 38, 38);
    public static readonly Color Blue = Color.FromArgb(37, 99, 235);
    public static readonly Color Disabled = Color.FromArgb(203, 213, 225);

    public static readonly Font Body = new("Segoe UI", 9.75f, FontStyle.Regular, GraphicsUnit.Point);
    public static readonly Font BodyStrong = new("Segoe UI", 10.5f, FontStyle.Regular, GraphicsUnit.Point);
    public static readonly Font H1 = new("Segoe UI Semibold", 17f, FontStyle.Regular, GraphicsUnit.Point);
    public static readonly Font Small = new("Segoe UI", 8.75f, FontStyle.Regular, GraphicsUnit.Point);
    public static readonly Font SectionTitle = new("Segoe UI Semibold", 9f, FontStyle.Regular, GraphicsUnit.Point);
    public static readonly Font Mono = new("Consolas", 9f, FontStyle.Regular, GraphicsUnit.Point);

    public static Color StateColor(LinkState state) => state switch
    {
        LinkState.Opened => Green,
        LinkState.Connecting => Blue,
        LinkState.Error => Red,
        LinkState.Waiting => Amber,
        _ => Muted,
    };

    public static string StateText(LinkState state) => state switch
    {
        LinkState.Opened => "已连接",
        LinkState.Connecting => "连接中",
        LinkState.Error => "异常",
        LinkState.Waiting => "等待中",
        _ => "空闲",
    };

    public static GraphicsPath Rounded(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

/// <summary>圆角白色卡片，带 1px 描边。</summary>
internal class CardPanel : Panel
{
    public int Radius { get; set; } = 12;

    public CardPanel()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Card;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = Theme.Rounded(bounds, Radius);
        using var brush = new SolidBrush(Theme.Card);
        e.Graphics.FillPath(brush, path);
        using var pen = new Pen(Theme.Border);
        e.Graphics.DrawPath(pen, path);
    }
}

/// <summary>圆角按钮（Primary 实心 / Outline 描边）。</summary>
internal class RoundButton : Button
{
    private bool _hover;
    private bool _pressed;

    public bool Outline { get; set; }

    public int Radius { get; set; } = 8;

    public RoundButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        BackColor = Color.Transparent;
        Font = Theme.Body;
        Cursor = Cursors.Hand;
        TabStop = false;
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        _hover = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = false;
        _pressed = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs mevent)
    {
        _pressed = true;
        Invalidate();
        base.OnMouseDown(mevent);
    }

    protected override void OnMouseUp(MouseEventArgs mevent)
    {
        _pressed = false;
        Invalidate();
        base.OnMouseUp(mevent);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = Theme.Rounded(bounds, Radius);

        Color fill;
        Color fore;
        if (!Enabled)
        {
            fill = Outline ? Color.White : Theme.Disabled;
            fore = Outline ? Theme.Disabled : Color.White;
        }
        else if (Outline)
        {
            fill = _pressed ? Theme.BorderSoft : (_hover ? Theme.HoverSurface : Color.White);
            fore = Theme.Text;
        }
        else
        {
            fill = _pressed ? Theme.AccentPressed : (_hover ? Theme.AccentHover : Theme.Accent);
            fore = Color.White;
        }

        using (var brush = new SolidBrush(fill))
        {
            e.Graphics.FillPath(brush, path);
        }

        if (Outline)
        {
            using var pen = new Pen(Theme.Border);
            e.Graphics.DrawPath(pen, path);
        }

        TextRenderer.DrawText(
            e.Graphics,
            Text,
            Font,
            bounds,
            fore,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
    }
}

/// <summary>带一点光晕的状态圆点。</summary>
internal sealed class StatusDot : Control
{
    private Color _color = Theme.Muted;

    public Color DotColor
    {
        get => _color;
        set
        {
            _color = value;
            Invalidate();
        }
    }

    public StatusDot()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Size = new Size(36, 36);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var halo = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var haloBrush = new SolidBrush(Color.FromArgb(28, _color)))
        {
            e.Graphics.FillEllipse(haloBrush, halo);
        }

        var dot = Rectangle.Inflate(halo, -8, -8);
        using var brush = new SolidBrush(_color);
        e.Graphics.FillEllipse(brush, dot);
    }
}
