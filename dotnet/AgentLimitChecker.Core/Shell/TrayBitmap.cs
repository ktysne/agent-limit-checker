namespace AgentLimitChecker.Core.Shell;

public static class TrayBitmap
{
    /// <summary>指定ピクセル数で描いた premultiplied BGRA の正方形画像を返す。</summary>
    public static byte[] Draw(int size, double? claudeUtil, double? codexUtil, bool claudeError = false, bool codexError = false)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 1);
        var pixels = new byte[checked(size * size * 4)];
        var scale = size / 32.0;
        var cy = size / 2.0;
        var radius = (size / 2.0 - 2 * scale) / 1.05;
        var claudeCx = size / 4.0;
        var codexCx = size * 3 / 4.0;
        DrawDonut(claudeCx, claudeUtil, claudeError);
        DrawDonut(codexCx, codexUtil, codexError);
        Glyph(claudeCx, cy, ["111", "100", "100", "100", "111"], Math.Max(1, Round(2 * scale)));
        Glyph(codexCx, cy, ["101", "101", "010", "101", "101"], Math.Max(1, Round(2 * scale)));
        if (claudeError) DrawBadge(claudeCx);
        if (codexError) DrawBadge(codexCx);
        return pixels;

        void Pixel(int x, int y, int r, int g, int b, int alpha)
        {
            if (x < 0 || y < 0 || x >= size || y >= size) return;
            var offset = (y * size + x) * 4;
            pixels[offset] = (byte)Round(b * alpha / 255.0);
            pixels[offset + 1] = (byte)Round(g * alpha / 255.0);
            pixels[offset + 2] = (byte)Round(r * alpha / 255.0);
            pixels[offset + 3] = (byte)alpha;
        }

        void Glyph(double cx, double glyphCy, string[] rows, int pixelSize, int alpha = 230)
        {
            var left = Round(cx - rows[0].Length * pixelSize / 2.0);
            var top = Round(glyphCy - rows.Length * pixelSize / 2.0);
            for (var row = 0; row < rows.Length; row++)
                for (var col = 0; col < rows[row].Length; col++)
                    if (rows[row][col] == '1')
                        for (var y = 0; y < pixelSize; y++)
                            for (var x = 0; x < pixelSize; x++)
                                Pixel(left + col * pixelSize + x, top + row * pixelSize + y,
                                    alpha == 255 ? 255 : 245, alpha == 255 ? 255 : 245, alpha == 255 ? 255 : 245, alpha);
        }

        void DrawDonut(double cx, double? utilization, bool error)
        {
            var valid = utilization.HasValue && !double.IsNaN(utilization.Value);
            var u = valid ? Math.Clamp(utilization!.Value, 0, 1) : 0;
            var color = ColorForUtilization(utilization, error);
            for (var y = 0; y < size; y++)
                for (var x = 0; x < size; x++)
                {
                    var dx = x + .5 - cx;
                    var dy = y + .5 - cy;
                    var distance = Math.Sqrt(dx * dx + dy * dy);
                    var innerRadius = radius * .55;
                    if (distance < innerRadius || distance > radius) continue;
                    var angle = (Math.Atan2(dy, dx) + Math.PI / 2 + Math.PI * 2) % (Math.PI * 2);
                    var fill = u >= 1 || valid && utilization > 0 && angle <= u * Math.PI * 2;
                    var alpha = Round((fill ? 255 : 96) * Math.Clamp(Math.Min(distance - innerRadius, radius - distance), 0, 1));
                    Pixel(x, y, fill ? color.R : 180, fill ? color.G : 180, fill ? color.B : 180, alpha);
                }
        }

        void DrawBadge(double cx)
        {
            var badgeCx = cx + 4 * scale;
            var badgeCy = cy - 7 * scale;
            var badgeRadius = 4.8 * scale;
            for (var y = (int)Math.Floor(badgeCy - badgeRadius); y <= Math.Ceiling(badgeCy + badgeRadius); y++)
                for (var x = (int)Math.Floor(badgeCx - badgeRadius); x <= Math.Ceiling(badgeCx + badgeRadius); x++)
                    if (Math.Pow(x + .5 - badgeCx, 2) + Math.Pow(y + .5 - badgeCy, 2) <= badgeRadius * badgeRadius)
                        Pixel(x, y, 220, 53, 69, 255);
            Glyph(badgeCx, badgeCy, ["1", "1", "1", "0", "1"], Math.Max(1, Round(scale)), 255);
        }
    }

    public static (int R, int G, int B) ColorForUtilization(double? u, bool error) => error ? (160, 160, 160)
        : u is null || double.IsNaN(u.Value) ? (120, 120, 120) : u < .7 ? (76, 175, 80)
        : u < .85 ? (255, 152, 0) : (244, 67, 54);

    private static int Round(double value) => (int)Math.Floor(value + .5);
}
