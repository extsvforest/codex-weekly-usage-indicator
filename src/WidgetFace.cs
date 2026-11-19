using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace WeeklyUsageIndicator;

/// <summary>What the widget stands on. Inside a taskbar it needs no surface of its own: the two gauges sit on the bar
/// like its own items, in that bar's light or dark. Anywhere else it floats over whatever is behind it, so it brings a
/// surface that keeps its numbers readable.</summary>
internal enum WidgetGround { Floating, DarkTaskbar, LightTaskbar }

/// <summary>One provider's gauge: the share of its weekly window that remains, or none while that is unknown. A stale
/// share (the last observation, kept through a transient failure) is drawn fainter. <c>Loading</c>: a read is under way,
/// which shows only while there is no share to show.</summary>
internal readonly record struct WidgetGauge(int? Remaining, bool Stale = false, bool Loading = false);

/// <summary>Everything the face shows. <c>Claude</c> is null while its half is switched off.</summary>
internal sealed record WidgetFaceState(WidgetGauge Codex, WidgetGauge? Claude, bool Hovered = false,
    WidgetGround Ground = WidgetGround.Floating);

/// <summary>Draws the widget: per provider a ring that fills clockwise with what remains, in the provider's colour, and
/// the number beside it. Sizes are in pixels of the 272 × 64 window, which does not scale with the display, so the face
/// reads the same at every scale. Text is filled as outlines: the face is composed with per-pixel alpha, where ClearType
/// has no opaque background to work against.</summary>
internal static class WidgetFace
{
    internal const int Width = 272, Height = 64;

    private const float Corner = 18f;
    // On a taskbar: the sheet the pointer lights up, which is also all of the window that takes the pointer.
    private static readonly RectangleF Sheet = new(0, 6, Width, Height - 12);
    private const float SheetCorner = 10f;
    private const float Ring = 38f, Stroke = 5.5f, RingToNumber = 10f, Between = 28f;
    private const float Em = 29f, UnitScale = 0.56f, DigitsToUnit = 2.5f;
    // Pretendard has no medium weight embedded: Regular with its outline thickened sits between Regular and SemiBold,
    // which is what holds beside a ring of this stroke.
    private const float Embolden = 0.9f;

    private sealed record Palette(Color Surface, Color SurfaceHovered, Color Value, Color Unit, Color Missing, Color Track,
        Color Codex, Color Claude);

    private static readonly Palette Floating = new(
        Surface: Color.FromArgb(27, 27, 28), SurfaceHovered: Color.FromArgb(37, 37, 38),
        Value: Color.FromArgb(245, 245, 247), Unit: Color.FromArgb(150, 245, 245, 247), Missing: Color.FromArgb(110, 245, 245, 247),
        Track: Color.FromArgb(28, 255, 255, 255), Codex: Color.FromArgb(124, 156, 255), Claude: Color.FromArgb(217, 119, 87));

    private static readonly Palette OnDarkTaskbar = Floating with
    {
        Surface = Color.Transparent, SurfaceHovered = Color.FromArgb(20, 255, 255, 255),
        Unit = Color.FromArgb(160, 245, 245, 247), Track = Color.FromArgb(44, 255, 255, 255)
    };

    private static readonly Palette OnLightTaskbar = new(
        Surface: Color.Transparent, SurfaceHovered: Color.FromArgb(16, 0, 0, 0),
        Value: Color.FromArgb(28, 28, 30), Unit: Color.FromArgb(150, 28, 28, 30), Missing: Color.FromArgb(110, 28, 28, 30),
        Track: Color.FromArgb(34, 0, 0, 0), Codex: Color.FromArgb(78, 112, 224), Claude: Color.FromArgb(201, 98, 66));

    /// <summary>The face as a premultiplied bitmap of the window's size, for a layered window.</summary>
    internal static Bitmap Render(WidgetFaceState state)
    {
        var bitmap = new Bitmap(Width, Height, PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Transparent);
        Draw(graphics, state);
        if (state.Ground != WidgetGround.Floating) KeepPointer(bitmap);
        return bitmap;
    }

    /// <summary>A layered window takes the pointer only where its picture is not fully transparent, so on a taskbar,
    /// where the face has no surface, the clear part of the sheet is given the least alpha there is. It is written into
    /// the pixels: a fill that faint is rounded away by the drawing itself.</summary>
    private static void KeepPointer(Bitmap face)
    {
        var bounds = new Rectangle(0, 0, Width, Height);
        var bits = face.LockBits(bounds, ImageLockMode.ReadWrite, PixelFormat.Format32bppPArgb);
        try
        {
            var pixels = new int[Width * Height];
            Marshal.Copy(bits.Scan0, pixels, 0, pixels.Length);
            var inside = SheetMask.Value;
            for (var index = 0; index < pixels.Length; index++)
                if (inside[index] && (uint)pixels[index] >> 24 == 0) pixels[index] = 0x01000000;
            Marshal.Copy(pixels, 0, bits.Scan0, pixels.Length);
        }
        finally
        {
            face.UnlockBits(bits);
        }
    }

    private static readonly Lazy<bool[]> SheetMask = new(() =>
    {
        using var mask = new Bitmap(Width, Height, PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(mask))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            using var sheet = Rounded(Sheet, SheetCorner);
            graphics.FillPath(Brushes.White, sheet);
        }
        var inside = new bool[Width * Height];
        for (var y = 0; y < Height; y++)
            for (var x = 0; x < Width; x++)
                inside[y * Width + x] = mask.GetPixel(x, y).A >= 128;
        return inside;
    });

    internal static void Draw(Graphics graphics, WidgetFaceState state)
    {
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.CompositingQuality = CompositingQuality.HighQuality;
        var palette = state.Ground switch
        {
            WidgetGround.DarkTaskbar => OnDarkTaskbar,
            WidgetGround.LightTaskbar => OnLightTaskbar,
            _ => Floating
        };
        DrawGround(graphics, state, palette);

        var gauges = state.Claude is { } claude
            ? new[] { (state.Codex, palette.Codex), (claude, palette.Claude) }
            : new[] { (state.Codex, palette.Codex) };
        var family = AccountFonts.Family();
        // Each number keeps the room of its digit count, so the face does not shift as a value changes within it.
        var rooms = gauges.Select(gauge => NumberRoom(family, gauge.Item1)).ToArray();
        var total = rooms.Sum(room => Ring + RingToNumber + room) + Between * (gauges.Length - 1);
        var x = (float)Math.Round((Width - total) / 2f);
        var baseline = Baseline(family);
        for (var index = 0; index < gauges.Length; index++)
        {
            var (gauge, accent) = gauges[index];
            DrawRing(graphics, palette, gauge, accent, x + Ring / 2f, Height / 2f);
            DrawNumber(graphics, palette, family, gauge, x + Ring + RingToNumber, baseline);
            x += Ring + RingToNumber + rooms[index] + Between;
        }
    }

    private static void DrawGround(Graphics graphics, WidgetFaceState state, Palette palette)
    {
        if (state.Ground == WidgetGround.Floating)
        {
            using var body = Rounded(new RectangleF(0, 0, Width, Height), Corner);
            using (var fill = new SolidBrush(state.Hovered ? palette.SurfaceHovered : palette.Surface)) graphics.FillPath(fill, body);
            // A recessed window: a shade under the upper lip and a faint light along the lower one, both inside the edge.
            var saved = graphics.Save();
            graphics.SetClip(body, CombineMode.Intersect);
            using (var shade = Rounded(new RectangleF(0.5f, 0.5f, Width - 1, Height + 6), Corner))
            using (var pen = new Pen(Color.FromArgb(120, 0, 0, 0), 1.6f)) graphics.DrawPath(pen, shade);
            using (var light = Rounded(new RectangleF(0.5f, -6.5f, Width - 1, Height + 6), Corner))
            using (var pen = new Pen(Color.FromArgb(26, 255, 255, 255), 1f)) graphics.DrawPath(pen, light);
            graphics.Restore(saved);
            return;
        }
        // On a taskbar the window is clear until the pointer comes: then a faint sheet with a lit upper edge, as the
        // bar's own items answer.
        if (!state.Hovered) return;
        using var sheet = Rounded(Sheet, SheetCorner);
        using (var fill = new SolidBrush(palette.SurfaceHovered)) graphics.FillPath(fill, sheet);
        var clip = graphics.Save();
        graphics.SetClip(sheet, CombineMode.Intersect);
        using (var lit = Rounded(new RectangleF(Sheet.Left + 0.5f, Sheet.Top + 0.5f, Sheet.Width - 1, Sheet.Height + 6), SheetCorner))
        using (var pen = new Pen(state.Ground == WidgetGround.DarkTaskbar ? Color.FromArgb(30, 255, 255, 255) : Color.FromArgb(120, 255, 255, 255), 1f))
            graphics.DrawPath(pen, lit);
        graphics.Restore(clip);
    }

    private static void DrawRing(Graphics graphics, Palette palette, WidgetGauge gauge, Color accent, float centreX, float centreY)
    {
        var arc = new RectangleF(centreX - Ring / 2f + Stroke / 2f, centreY - Ring / 2f + Stroke / 2f, Ring - Stroke, Ring - Stroke);
        using (var track = new Pen(gauge.Remaining is null ? Fade(palette.Track, 0.66f) : palette.Track, Stroke)) graphics.DrawEllipse(track, arc);
        if (gauge.Remaining is not > 0) return;
        using var pen = new Pen(gauge.Stale ? Fade(accent, 0.5f) : accent, Stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        // From twelve o'clock, clockwise. The round caps turn the smallest share into a dot rather than nothing.
        var sweep = 360f * Math.Min(100, gauge.Remaining.Value) / 100f;
        if (sweep >= 359.5f) graphics.DrawEllipse(pen, arc);
        else graphics.DrawArc(pen, arc, -90f, Math.Max(1f, sweep));
    }

    private static void DrawNumber(Graphics graphics, Palette palette, FontFamily family, WidgetGauge gauge,
        float left, float baseline)
    {
        if (gauge.Remaining is not { } remaining)
        {
            using var mark = Outline(gauge.Loading ? "…" : "—", family, Em * 0.9f, baseline);
            MoveInkTo(mark, left);
            using var missing = new SolidBrush(palette.Missing);
            graphics.FillPath(missing, mark);
            return;
        }
        using var digits = Outline(remaining.ToString(), family, Em, baseline);
        using var unit = Outline("%", family, Em * UnitScale, baseline);
        MoveInkTo(digits, left);
        MoveInkTo(unit, left + digits.GetBounds().Width + DigitsToUnit);
        var value = gauge.Stale ? Fade(palette.Value, 0.55f) : palette.Value;
        var quiet = gauge.Stale ? Fade(palette.Unit, 0.7f) : palette.Unit;
        using var valueBrush = new SolidBrush(value); using var unitBrush = new SolidBrush(quiet);
        using var valuePen = new Pen(value, Embolden) { LineJoin = LineJoin.Round };
        using var unitPen = new Pen(quiet, Embolden * 0.6f) { LineJoin = LineJoin.Round };
        graphics.FillPath(valueBrush, digits); graphics.DrawPath(valuePen, digits);
        graphics.FillPath(unitBrush, unit); graphics.DrawPath(unitPen, unit);
    }

    /// <summary>The width a number is given: that of zeros of its digit count with the percent sign, or of the mark that
    /// stands in for a missing value.</summary>
    private static float NumberRoom(FontFamily family, WidgetGauge gauge)
    {
        if (gauge.Remaining is not { } remaining)
        {
            using var mark = Outline(gauge.Loading ? "…" : "—", family, Em * 0.9f, 0);
            return mark.GetBounds().Width;
        }
        using var digits = Outline(new string('0', remaining.ToString().Length), family, Em, 0);
        using var unit = Outline("%", family, Em * UnitScale, 0);
        return digits.GetBounds().Width + DigitsToUnit + unit.GetBounds().Width;
    }

    /// <summary>The baseline that centres the digits on the ring: by the ink of a digit, not by the font's line box.</summary>
    private static float Baseline(FontFamily family)
    {
        using var digit = Outline("0", family, Em, 0);
        var ink = digit.GetBounds();
        return Height / 2f - (ink.Top + ink.Bottom) / 2f;
    }

    private static GraphicsPath Outline(string text, FontFamily family, float em, float baseline)
    {
        var path = new GraphicsPath();
        using var format = (StringFormat)StringFormat.GenericTypographic.Clone();
        format.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.NoClip;
        var ascent = family.GetCellAscent(FontStyle.Regular) * em / family.GetEmHeight(FontStyle.Regular);
        path.AddString(text, family, (int)FontStyle.Regular, em, new PointF(0, baseline - ascent), format);
        return path;
    }

    private static void MoveInkTo(GraphicsPath path, float left)
    {
        using var shift = new Matrix();
        shift.Translate(left - path.GetBounds().Left, 0);
        path.Transform(shift);
    }

    private static Color Fade(Color color, float share) => Color.FromArgb((int)Math.Round(color.A * share), color);

    internal static GraphicsPath Rounded(RectangleF rectangle, float radius)
    {
        var diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Top, diameter, diameter, 270, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rectangle.Left, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}

/// <summary>Tells whether the widget sits inside a taskbar, and whether that bar is light or dark.</summary>
internal static class WidgetGrounds
{
    /// <summary>The ground under a window with these screen bounds: a taskbar's when the whole window lies inside a
    /// taskbar that is showing, floating otherwise (an auto-hidden bar has slid off the screen, a bar on another
    /// monitor counts like the main one).</summary>
    internal static WidgetGround At(Rectangle bounds)
    {
        foreach (var taskbar in NativeWindow.Taskbars())
            if (taskbar.Contains(bounds)) return LightTaskbar() ? WidgetGround.LightTaskbar : WidgetGround.DarkTaskbar;
        return WidgetGround.Floating;
    }

    /// <summary>Windows draws the taskbar by its "Windows mode": light only where that is set, dark otherwise.</summary>
    private static bool LightTaskbar()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int light && light != 0;
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
