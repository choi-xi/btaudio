using System.Drawing.Drawing2D;

namespace BtAudioGui;

internal static class TrayIcons
{
    private static readonly Dictionary<Color, Icon> Cache = new();

    public static Icon For(LinkState state) => Get(Theme.StateColor(state));

    private static Icon Get(Color color)
    {
        lock (Cache)
        {
            if (!Cache.TryGetValue(color, out var icon))
            {
                icon = Create(color);
                Cache[color] = icon;
            }

            return icon;
        }
    }

    private static Icon Create(Color color)
    {
        using var bitmap = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);

            using (var brush = new SolidBrush(color))
            {
                graphics.FillEllipse(brush, 1, 1, 30, 30);
            }

            var speaker = new[]
            {
                new PointF(11.5f, 13f), new PointF(14.5f, 13f), new PointF(18f, 9.5f),
                new PointF(18f, 22.5f), new PointF(14.5f, 19f), new PointF(11.5f, 19f),
            };
            using (var white = new SolidBrush(Color.White))
            {
                graphics.FillPolygon(white, speaker);
            }

            using var pen = new Pen(Color.White, 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            graphics.DrawArc(pen, 19f, 12f, 6f, 8f, -60f, 120f);
        }

        var handle = bitmap.GetHicon();
        return Icon.FromHandle(handle);
    }
}
