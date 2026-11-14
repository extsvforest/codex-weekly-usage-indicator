using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Reflection;
using System.Windows.Forms;
using WeeklyUsageIndicator;

// The widget's face is composed off screen, so what it draws can be read back pixel by pixel.
internal static class WidgetFaceTests
{
    private static readonly Color Codex = Color.FromArgb(124, 156, 255), Claude = Color.FromArgb(217, 119, 87);

    internal static Task RunAsync()
    {
        using (var floating = WidgetFace.Render(new(new(29), new WidgetGauge(64))))
        {
            Check(floating.Size == new Size(272, 64) && floating.PixelFormat == PixelFormat.Format32bppPArgb, "the face is the window's size, premultiplied");
            Check(floating.GetPixel(0, 0).A == 0 && floating.GetPixel(271, 63).A == 0, "outside the rounded corners nothing is drawn");
            Check(floating.GetPixel(136, 32).A == 255 && floating.GetPixel(136, 5).A == 255, "a floating face is opaque, so its numbers read over anything");
            Check(Count(floating, Codex) > 20 && Count(floating, Claude) > 20, "each provider's ring shows in its own colour");
            Check(Count(floating, Codex, from: 136) == 0 && Count(floating, Claude, to: 136) == 0, "Codex is on the left, Claude on the right");
        }
        using (var docked = WidgetFace.Render(new(new(29), new WidgetGauge(64), Ground: WidgetGround.DarkTaskbar)))
        {
            Check(Enumerable.Range(0, 272).All(x => docked.GetPixel(x, 2).A == 0 && docked.GetPixel(x, 61).A == 0),
                "on a taskbar the face has no surface of its own");
            var faintest = Enumerable.Range(12, 248).Min(x => (int)docked.GetPixel(x, 32).A);
            var sheet = docked.GetPixel(136, 12).A;
            Check(faintest > 0 && sheet is > 0 and < 8,
                $"the whole sheet still takes the pointer, at an alpha no eye sees (faintest {faintest}, sheet {sheet})");
        }
        using (var hovered = WidgetFace.Render(new(new(29), new WidgetGauge(64), Hovered: true, Ground: WidgetGround.DarkTaskbar)))
            Check(hovered.GetPixel(136, 12).A >= 16, "the pointer lights the sheet up");
        using (var light = WidgetFace.Render(new(new(29), new WidgetGauge(64), Ground: WidgetGround.LightTaskbar)))
            Check(Darkest(light) < 60, "on a light taskbar the numbers turn dark");

        using (var missing = WidgetFace.Render(new(new(29), new WidgetGauge(null))))
        using (var off = WidgetFace.Render(new(new(29), null)))
        using (var empty = WidgetFace.Render(new(new(0), new WidgetGauge(100))))
        {
            Check(Count(missing, Claude) == 0 && Count(missing, Codex) > 20, "a provider without a value keeps its place and shows no share");
            Check(Count(off, Claude) == 0 && Count(off, Codex, to: 90) == 0, "with Claude off, the one gauge takes the middle");
            Check(Count(empty, Codex) == 0, "nothing remaining is an empty ring");
        }
        // The ring fills clockwise from twelve o'clock, so a larger share covers a smaller one and reaches further round.
        using (var quarter = WidgetFace.Render(new(new(25), null)))
        using (var most = WidgetFace.Render(new(new(75), null)))
        using (var full = WidgetFace.Render(new(new(100), null)))
        {
            var (low, mid, high) = (Count(quarter, Codex), Count(most, Codex), Count(full, Codex));
            Check(low > 0 && mid > low * 2 && high > mid, "the ring grows with what remains");
            // Three quarters reach every side of the ring, and both values have as many digits, so the ring is in one place.
            var ring = RingBounds(most);
            var (middle, upperRight, nine) = (ring.Top + ring.Height / 2, new Point(ring.Right - 3, ring.Top + ring.Height / 2 - 6), ring.Left + 2);
            Check(Near(quarter.GetPixel(upperRight.X, upperRight.Y), Codex) && !Near(quarter.GetPixel(nine, middle), Codex)
                  && Near(most.GetPixel(nine, middle), Codex), "it turns clockwise from the top");
        }
        // A value changing within the same number of digits moves nothing else.
        using (var a = WidgetFace.Render(new(new(41), new WidgetGauge(64))))
        using (var b = WidgetFace.Render(new(new(47), new WidgetGauge(64))))
            Check(RingBounds(a, from: 136) == RingBounds(b, from: 136), "a new value of as many digits shifts no gauge");
        using (var fresh = WidgetFace.Render(new(new(29), new WidgetGauge(64))))
        using (var stale = WidgetFace.Render(new(new(29), new WidgetGauge(64, Stale: true))))
            Check(Count(stale, Claude) == 0 && Count(fresh, Claude) > 0 && Brightest(stale, from: 136) < Brightest(fresh, from: 136),
                "a share kept through a failure is drawn fainter");
        return Task.CompletedTask;
    }

    /// <summary>Writes the face in its states over each ground, at its real size and enlarged, for a look by eye:
    /// <c>--widget-face-preview &lt;folder&gt; [&lt;taskbar capture.png&gt; &lt;x&gt; &lt;y&gt;]</c>. With a capture of a real
    /// taskbar the faces are laid over it where the widget's corner was.</summary>
    internal static void Preview(string[] args)
    {
        var at = Array.IndexOf(args, "--widget-face-preview");
        var folder = args[at + 1];
        Directory.CreateDirectory(folder);
        using var capture = args.Length > at + 4 ? new Bitmap(args[at + 2]) : null;
        var corner = capture is null ? Point.Empty : new Point(int.Parse(args[at + 3]), int.Parse(args[at + 4]));
        WidgetFaceState[] states =
        [
            new(new(29), new WidgetGauge(64)),
            new(new(8), new WidgetGauge(100)),
            new(new(29), new WidgetGauge(null)),
            new(new(72), new WidgetGauge(41), Hovered: true),
            new(new(38), null),
            new(new(55), new WidgetGauge(41, Stale: true)),
            new(new(null), new WidgetGauge(null), Refreshing: true),
            new(new(100), new WidgetGauge(100)),
        ];
        foreach (var (name, ground, behind) in new[]
                 {
                     ("dark-taskbar", WidgetGround.DarkTaskbar, Color.FromArgb(48, 47, 45)),
                     ("light-taskbar", WidgetGround.LightTaskbar, Color.FromArgb(238, 238, 238)),
                     ("floating-over-light", WidgetGround.Floating, Color.FromArgb(243, 243, 243)),
                     ("floating-over-dark", WidgetGround.Floating, Color.FromArgb(32, 32, 32)),
                 })
        {
            const int cellWidth = 372, cellHeight = 130, columns = 4, zoom = 2;
            var rows = (states.Length + columns - 1) / columns;
            using var sheet = new Bitmap(cellWidth * columns, cellHeight * rows + cellHeight * zoom, PixelFormat.Format32bppPArgb);
            using var graphics = Graphics.FromImage(sheet);
            graphics.Clear(behind);
            Bitmap? first = null;
            for (var index = 0; index < states.Length; index++)
            {
                var cell = new Bitmap(cellWidth, cellHeight, PixelFormat.Format32bppPArgb);
                using (var on = Graphics.FromImage(cell))
                {
                    on.Clear(behind);
                    if (capture is not null && ground == WidgetGround.DarkTaskbar)
                    {
                        on.DrawImage(capture, new Rectangle(0, 0, cellWidth, cellHeight), new Rectangle(corner.X - 50, corner.Y - 40, cellWidth, cellHeight), GraphicsUnit.Pixel);
                        using var erase = new SolidBrush(capture.GetPixel(corner.X - 20, corner.Y + 32));
                        on.FillRectangle(erase, 48, 38, WidgetFace.Width + 4, WidgetFace.Height + 4);
                    }
                    using var face = WidgetFace.Render(states[index] with { Ground = ground });
                    on.DrawImage(face, 50, 40);
                }
                graphics.DrawImage(cell, index % columns * cellWidth, index / columns * cellHeight);
                if (index == 0) first = cell; else cell.Dispose();
            }
            graphics.InterpolationMode = InterpolationMode.NearestNeighbor; graphics.PixelOffsetMode = PixelOffsetMode.Half;
            graphics.DrawImage(first!, new Rectangle(0, cellHeight * rows, cellWidth * zoom, cellHeight * zoom), new Rectangle(0, 0, cellWidth, cellHeight), GraphicsUnit.Pixel);
            first!.Dispose();
            sheet.Save(Path.Combine(folder, "face-" + name + ".png"), ImageFormat.Png);
        }
        using (var alone = WidgetFace.Render(states[0])) alone.Save(Path.Combine(folder, "widget.png"), ImageFormat.Png);
        Console.WriteLine("Widget face preview: " + folder);
    }

    /// <summary>Shows the real window for a moment with its corner at a screen position and saves what the screen
    /// shows there: <c>--widget-native-preview &lt;png&gt; &lt;x&gt; &lt;y&gt;</c>. It takes no focus. The face tests read the
    /// composed picture; this is for a look by eye at the layered window itself, on a taskbar or off it.</summary>
    internal static Task NativePreview(string[] args)
    {
        var at = Array.IndexOf(args, "--widget-native-preview");
        var output = args[at + 1];
        var corner = new Point(int.Parse(args[at + 2]), int.Parse(args[at + 3]));
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
                Application.EnableVisualStyles();
                using var widget = new UsageIndicatorForm(previewMode: true);
                var now = DateTimeOffset.UtcNow;
                Field("_showClaude").SetValue(widget, true);
                Field("_claudeUsage").SetValue(widget,
                    new ClaudeUsageResult(new(new(10, now.AddHours(3)), new(36, now.AddDays(3)), new(40, now.AddDays(3))), now, null, false));
                widget.Show();
                Pump(250);
                widget.Location = corner;
                Pump(900);
                widget.Invalidate();
                Pump(300);
                using var shot = new Bitmap(widget.Width + 120, widget.Height + 80);
                using (var graphics = Graphics.FromImage(shot)) graphics.CopyFromScreen(widget.Left - 60, widget.Top - 40, 0, 0, shot.Size);
                shot.Save(output, ImageFormat.Png);
                Console.WriteLine($"ground: {Field("_ground").GetValue(widget)}, bounds: {widget.Bounds}");
                widget.Close();
                completion.SetResult();
            }
            catch (Exception exception) { completion.SetException(exception); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;

        static FieldInfo Field(string name) => typeof(UsageIndicatorForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
        static void Pump(int milliseconds)
        {
            var until = Environment.TickCount64 + milliseconds;
            while (Environment.TickCount64 < until) { Application.DoEvents(); Thread.Sleep(15); }
        }
    }

    private static bool Near(Color pixel, Color colour) =>
        pixel.A > 200 && Math.Abs(pixel.R - colour.R) < 12 && Math.Abs(pixel.G - colour.G) < 12 && Math.Abs(pixel.B - colour.B) < 12;

    private static int Count(Bitmap face, Color colour, int from = 0, int to = WidgetFace.Width)
    {
        var count = 0;
        for (var y = 0; y < face.Height; y++)
            for (var x = from; x < to; x++)
                if (Near(face.GetPixel(x, y), colour)) count++;
        return count;
    }

    /// <summary>The box round every pixel of either provider's colour from <paramref name="from"/> on.</summary>
    private static Rectangle RingBounds(Bitmap face, int from = 0)
    {
        int left = int.MaxValue, top = int.MaxValue, right = -1, bottom = -1;
        for (var y = 0; y < face.Height; y++)
            for (var x = from; x < face.Width; x++)
                if (Near(face.GetPixel(x, y), Codex) || Near(face.GetPixel(x, y), Claude))
                { left = Math.Min(left, x); top = Math.Min(top, y); right = Math.Max(right, x); bottom = Math.Max(bottom, y); }
        return right < 0 ? Rectangle.Empty : Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
    }

    private static int Brightest(Bitmap face, int from = 0)
    {
        var brightest = 0;
        for (var y = 0; y < face.Height; y++)
            for (var x = from; x < face.Width; x++)
            {
                var pixel = face.GetPixel(x, y);
                // Neutral pixels only: the digits, not the rings.
                if (pixel.A > 0 && Math.Abs(pixel.R - pixel.B) < 6) brightest = Math.Max(brightest, pixel.A * pixel.G / 255);
            }
        return brightest;
    }

    private static int Darkest(Bitmap face)
    {
        var darkest = 255;
        for (var y = 0; y < face.Height; y++)
            for (var x = 0; x < face.Width; x++)
            {
                var pixel = face.GetPixel(x, y);
                if (pixel.A > 230) darkest = Math.Min(darkest, Math.Max(pixel.R, Math.Max(pixel.G, pixel.B)));
            }
        return darkest;
    }

    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException("Widget face: " + message); }
}
