using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using WeeklyUsageIndicator;

// The size the user chooses for the widget: where a new size puts it, the menu item, and the size window that shows
// each value on the widget at once. The widget runs in preview mode, which saves nothing; the saved setting itself is
// checked with the other preferences (LocalizationTests). Run it on a hidden desktop: it opens windows.
internal static class WidgetScaleTests
{
    internal static Task RunAsync()
    {
        CheckPlacement();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                UiText.SetLanguage("ko");
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); Application.EnableVisualStyles();
                CheckWindow();
                done.TrySetResult();
            }
            catch (Exception exception) { done.TrySetException(exception); }
            finally { UiText.SetLanguage("ko"); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return done.Task;
    }

    private static void CheckPlacement()
    {
        var screen = new Rectangle(0, 0, 1920, 1080);
        var floating = new Rectangle(800, 500, 272, 64);
        Check(WidgetScale.Place(floating, new Size(237, 56), null, screen) == new Rectangle(818, 504, 237, 56),
            "a new size keeps the widget's centre");
        Check(WidgetScale.Place(floating, floating.Size, null, screen) == floating, "the size it had puts it back exactly");
        var offscreen = new Rectangle(1800, 1050, 272, 64);
        Check(WidgetScale.Place(offscreen, offscreen.Size, null, screen) == offscreen, "the size it had moves nothing, wherever it was");
        // A taskbar 72 px high, as at 150 % display scale: a size that fits settles on its middle line.
        var bar = new Rectangle(0, 1008, 1920, 72);
        var docked = new Rectangle(1500, 1012, 272, 64);
        Check(WidgetScale.Place(docked, new Size(237, 56), bar, screen) == new Rectangle(1518, 1016, 237, 56),
            "inside a taskbar a smaller widget settles on the bar's middle line");
        var tall = WidgetScale.Place(docked, new Size(327, 77), bar, screen);
        Check(tall == new Rectangle(1473, 1003, 327, 77), $"a widget taller than the bar keeps its centre and stays on the screen ({tall})");
        var edge = WidgetScale.Place(new Rectangle(1640, 500, 272, 64), new Size(544, 128), null, screen);
        Check(edge == new Rectangle(1376, 468, 544, 128), $"at the screen's edge a larger widget is moved back onto it ({edge})");
        var top = WidgetScale.Place(new Rectangle(100, 0, 272, 64), new Size(544, 128), null, screen);
        Check(top == new Rectangle(0, 0, 544, 128), $"in the top corner it stays on the screen ({top})");
        var second = new Rectangle(1920, 0, 1280, 1024);
        Check(second.Contains(WidgetScale.Place(new Rectangle(3000, 980, 272, 44), new Size(544, 128), null, second)),
            "on a second monitor it stays on that monitor");
        var vertical = new Rectangle(0, 0, 62, 1080);
        Check(WidgetScale.Place(floating, new Size(237, 56), vertical, screen) == new Rectangle(818, 504, 237, 56),
            "a vertical taskbar has no middle line to settle on");
    }

    private static void CheckWindow()
    {
        using var widget = new UsageIndicatorForm(previewMode: true);
        widget.Show();
        Pump(250);
        var area = Screen.PrimaryScreen!.WorkingArea;
        widget.Location = new Point(area.Left + 420, area.Top + 300);
        Pump(60);
        var origin = widget.Bounds;
        Check(origin.Size == WidgetFace.SizeAt(100) && widget.ScalePercent == 100, "without a saved scale the widget is the design's size");
        CheckOnScreen(widget, origin, "the widget as it starts");

        var menu = Get<ContextMenuStrip>(widget, "_contextMenu");
        CheckMenu(menu, "크기…");
        Check(widget.ChangeLanguage("en"), "the language changes while no size window is open");
        CheckMenu(menu, "Size…");
        var english = widget.ShowScaleDialog();
        Pump(80);
        Check(english.Text == "Widget size" && Find<Button>(english, "WidgetScaleReset").Text == "Back to 100%" &&
              Find<Button>(english, "WidgetScaleOk").Text == "OK" && Find<Button>(english, "WidgetScaleCancel").Text == "Cancel",
            "the size window speaks English");
        english.Close();
        Pump(40);
        Check(widget.ChangeLanguage("ko"), "the language returns to Korean");

        var dialog = widget.ShowScaleDialog();
        Pump(80);
        var input = Find<TextBox>(dialog, "WidgetScaleInput");
        Check(dialog.Visible && dialog.Text == "위젯 크기" && dialog.Percent == 100 && input.Text == "100", "the size window opens at the size the widget has");
        Check(ReferenceEquals(widget.ShowScaleDialog(), dialog), "a second press brings the open window forward");
        Check(!widget.ChangeLanguage("en") && UiText.Language == "ko", "the language waits while the size window is open");

        Find<Button>(dialog, "WidgetScaleLarger").PerformClick();
        CheckAt(widget, 101, origin, "+ makes it 1 % larger at once");
        Find<Button>(dialog, "WidgetScaleSmaller").PerformClick();
        Find<Button>(dialog, "WidgetScaleSmaller").PerformClick();
        CheckAt(widget, 99, origin, "− makes it 1 % smaller at once");
        Check(input.Text == "99", "the box shows the size a step gives");
        input.Text = "8";
        Pump(20);
        Check(widget.ScalePercent == 99, "a number still being typed waits until it is a size in range");
        input.Text = "87";
        CheckAt(widget, 87, origin, "a typed size shows at once");
        input.Text = "201";
        Pump(20);
        Check(widget.ScalePercent == 87 && !dialog.Keep() && Find<Label>(dialog, "WidgetScaleError").Text.Length > 0 && dialog.Visible,
            "OK with a number out of range says why and stays open");
        Find<Button>(dialog, "WidgetScaleReset").PerformClick();
        Pump(20);
        Check(widget.ScalePercent == 100 && widget.Bounds == origin && input.Text == "100" &&
              Find<Label>(dialog, "WidgetScaleError").Text.Length == 0, "100 % puts the widget back exactly where it was");
        for (var step = 0; step < 120; step++) dialog.StepBy(1);
        CheckAt(widget, 200, origin, "steps stop at 200 %");
        Check(!dialog.Bounds.IntersectsWith(widget.Bounds), $"the size window stays clear of the widget at its largest ({dialog.Bounds}, {widget.Bounds})");
        for (var step = 0; step < 160; step++) dialog.StepBy(-1);
        CheckAt(widget, 50, origin, "steps stop at 50 %");
        input.Text = "120";
        CheckAt(widget, 120, origin, "a typed size shows at once");
        Find<Button>(dialog, "WidgetScaleCancel").PerformClick();
        Pump(40);
        Check(dialog.IsDisposed && widget.ScalePercent == 100 && widget.Bounds == origin, "Cancel gives the widget back its size and place");
        CheckOnScreen(widget, origin, "after Cancel");
        Check(widget.ChangeLanguage("en") && widget.ChangeLanguage("ko"), "the language can change again once the window has closed");

        dialog = widget.ShowScaleDialog();
        Pump(60);
        Find<TextBox>(dialog, "WidgetScaleInput").Text = "113";
        Find<Button>(dialog, "WidgetScaleOk").PerformClick();
        Pump(40);
        Check(dialog.IsDisposed, "OK closes the size window");
        CheckAt(widget, 113, origin, "OK keeps the new size");
        var kept = widget.Bounds;

        dialog = widget.ShowScaleDialog();
        Pump(60);
        Check(dialog.Percent == 113 && Find<TextBox>(dialog, "WidgetScaleInput").Text == "113", "the window opens at the size kept");
        dialog.Set(100);
        dialog.Close();
        Pump(40);
        Check(widget.ScalePercent == 113 && widget.Bounds == kept, "closing the window without OK is a cancel");

        // Dragged while the window is open: the sizes it shows next are placed from where the widget was dropped.
        dialog = widget.ShowScaleDialog();
        Pump(60);
        widget.Location = new Point(kept.Left + 150, kept.Top + 80);
        typeof(UsageIndicatorForm).GetMethod("HandleMouseUp", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(widget, [null, new MouseEventArgs(MouseButtons.Left, 1, 0, 0, 0)]);
        var dropped = widget.Bounds;
        dialog.Set(100);
        CheckAt(widget, 100, dropped, "after a drag a new size keeps the centre the widget was dropped on");
        dialog.Close();
        Pump(40);
        Check(widget.ScalePercent == 113 && widget.Bounds == dropped, "a cancel after a drag returns the kept size where it was dropped");

        // Closing the widget with the window open: the window closes and the widget keeps the size kept, the one whose place
        // it saves.
        dialog = widget.ShowScaleDialog();
        Pump(60);
        dialog.Set(160);
        widget.Close();
        Pump(40);
        Check(dialog.IsDisposed && widget.ScalePercent == 113, "closing the widget closes the size window and keeps the kept size");
    }

    private static void CheckMenu(ContextMenuStrip menu, string text)
    {
        var items = menu.Items.Cast<ToolStripItem>().ToList();
        var language = items.FindIndex(item => item.Name == "LanguageMenu");
        var scale = items.FindIndex(item => item.Name == "WidgetScaleMenu");
        Check(language >= 0 && scale == language + 1 && items[scale].Text == text, $"the menu has {text} right after the language");
    }

    /// <summary>The widget shows the scale at once: its window and its picture at that size, on the centre it was given.</summary>
    private static void CheckAt(UsageIndicatorForm widget, int scale, Rectangle origin, string what)
    {
        Pump(20);
        var size = WidgetFace.SizeAt(scale);
        Check(widget.ScalePercent == scale && widget.Size == size, $"{what}: {scale} % is {size} (the widget is {widget.Size})");
        // Where the screen leaves room for it, the widget is centred where it was.
        var centred = new Rectangle(origin.Left + origin.Width / 2 - size.Width / 2, origin.Top + origin.Height / 2 - size.Height / 2,
            size.Width, size.Height);
        if (Screen.FromRectangle(origin).Bounds.Contains(centred))
            Check(Math.Abs(widget.Left + widget.Width / 2 - (origin.Left + origin.Width / 2)) <= 1 &&
                  Math.Abs(widget.Top + widget.Height / 2 - (origin.Top + origin.Height / 2)) <= 1,
                $"{what}: the widget keeps its centre ({widget.Bounds} from {origin})");
        CheckOnScreen(widget, origin, what);
    }

    /// <summary>What the window manager has: the layered window at the widget's bounds, which its picture sets.</summary>
    private static void CheckOnScreen(UsageIndicatorForm widget, Rectangle origin, string what)
    {
        Check(GetWindowRect(widget.Handle, out var rect) &&
              Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom) == widget.Bounds,
            $"{what}: the window on screen is the widget's bounds ({rect.Left},{rect.Top},{rect.Right},{rect.Bottom} vs {widget.Bounds})");
        Check(Screen.AllScreens.Any(screen => screen.Bounds.Contains(widget.Bounds)), $"{what}: the widget is on a screen ({widget.Bounds})");
    }

    private static T Find<T>(Control root, string name) where T : Control =>
        root.Controls.Find(name, searchAllChildren: true).OfType<T>().Single();

    private static T Get<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(target)!;

    private static void Pump(int milliseconds)
    {
        var until = Environment.TickCount64 + milliseconds;
        do { Application.DoEvents(); Thread.Sleep(5); } while (Environment.TickCount64 < until);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);

    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException("Widget scale: " + message); }
}
