using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using WeeklyUsageIndicator;

internal static class UsageTooltipHoverSmoke
{
    internal static Task RunAsync()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var savedCursor = Cursor.Position;
            try
            {
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); Application.EnableVisualStyles();
                using var focusForm = new Form { Text = "Tooltip hover regression", StartPosition = FormStartPosition.CenterScreen, Size = new(480, 180) };
                var input = new TextBox { Dock = DockStyle.Top, Text = "Focus must stay here" }; focusForm.Controls.Add(input);
                using var widget = new UsageIndicatorForm(previewMode: true);
                var tooltip = (UsageToolTip)Field("_toolTip").GetValue(widget)!;
                var now = DateTimeOffset.UtcNow;
                var accounts = Enumerable.Range(0, 3).Select(i => new SavedCodexAccount(i.ToString(), "테스트 계정 " + i,
                    "synthetic", i == 0, new(20 + i * 10, now.AddDays(i + 2), 10080, "codex"), now)).ToArray();
                var combined = (CombinedUsageSnapshot)Field("_combinedUsage").GetValue(widget)!;
                combined.Initialize(accounts, true);
                foreach (var a in accounts) combined.SetResult(a.Id, a, CombinedReadState.Success);
                combined.CompletedAt = now;
                Field("_showClaude").SetValue(widget, true);
                Field("_claudeUsage").SetValue(widget, new ClaudeUsageResult(new(new(10, now.AddHours(3)), new(20, now.AddDays(3)), new(30, now.AddDays(3))), now, null, false));
                focusForm.Shown += async (_, _) =>
                {
                    try
                    {
                        input.Focus(); widget.Show(); await Task.Delay(150); input.Focus();
                        var foreground = GetForegroundWindow(); var focus = GetFocus();
                        var area = Screen.FromControl(focusForm).WorkingArea;
                        var positions = new[] { new Point(area.Right - widget.Width, area.Bottom - widget.Height), area.Location,
                            new Point(area.Right - widget.Width, area.Top), new Point(area.Left, area.Bottom - widget.Height) };
                        foreach (var position in positions)
                        {
                            Cursor.Position = new Point(area.Left + area.Width / 2, area.Top + area.Height / 2);
                            await Task.Delay(200); widget.Location = position; widget.MaintainVisiblePresentation();
                            var before = tooltip.ShowCount;
                            var pointer = new Point(widget.Left + widget.Width / 2, widget.Top + widget.Height / 2);
                            Cursor.Position = pointer;
                            var visibleSamples = 0; var hiddenAfterShown = 0; Rectangle? firstBounds = null;
                            for (var tick = 0; tick < 80; tick++)
                            {
                                await Task.Delay(50);
                                if (tick % 20 == 0) widget.MaintainVisiblePresentation();
                                var previousText = tooltip.VisibleText;
                                if (tick == 35)
                                {
                                    tooltip.SetHoverText("Pending refresh");
                                    Check(tooltip.VisibleText == previousText, "an updated observation does not redraw the visible tooltip");
                                    typeof(UsageIndicatorForm).GetMethod("UpdateToolTip", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(widget, null);
                                }
                                var bounds = VisibleTooltipBounds();
                                if (bounds is { } rect)
                                {
                                    visibleSamples++; firstBounds ??= rect;
                                    Check(rect == firstBounds.Value, "tooltip position stays fixed while hovering");
                                    Check(!rect.Contains(pointer), "tooltip stays away from the pointer");
                                    Check(!rect.IntersectsWith(widget.Bounds) && area.Contains(rect), "tooltip fits the screen outside the widget");
                                }
                                else if (visibleSamples > 0) hiddenAfterShown++;
                            }
                            var pointerTarget = WindowFromPoint(pointer);
                            GetWindowThreadProcessId(pointerTarget, out var pointerProcess);
                            Console.WriteLine($"Hover diagnostic: popups={tooltip.ShowCount - before}, visible={visibleSamples}, hiddenAfterShown={hiddenAfterShown}, cursorHeld={Cursor.Position == pointer}, targetIsWidget={pointerTarget == widget.Handle}, targetIsTestProcess={pointerProcess == Environment.ProcessId}");
                            Check(tooltip.ShowCount - before == 1 && visibleSamples >= 50 && hiddenAfterShown == 0, "one continuous popup survives four seconds of actual hover and maintenance");
                            Check(GetForegroundWindow() == foreground && GetFocus() == focus && input.Focused, "hover preserves foreground and keyboard focus");
                        }
                        var output = Environment.GetEnvironmentVariable("GFS_ACCOUNT_UI_CAPTURE");
                        if (!string.IsNullOrWhiteSpace(output) && tooltip.VisibleBounds is { } capture)
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                            using var bitmap = new Bitmap(capture.Width, capture.Height);
                            using var graphics = Graphics.FromImage(bitmap);
                            graphics.CopyFromScreen(capture.Location, Point.Empty, capture.Size);
                            bitmap.Save(Path.Combine(Path.GetDirectoryName(output)!, "usage-tooltip-stable.png"), ImageFormat.Png);
                        }
                        typeof(Control).GetMethod("OnMouseDown", BindingFlags.Instance | BindingFlags.NonPublic)!
                            .Invoke(widget, new object[] { new MouseEventArgs(MouseButtons.Right, 1, 5, 5, 0) });
                        Check(!tooltip.IsVisible && VisibleTooltipBounds() is null, "opening the context menu dismisses the tooltip");
                        ((ContextMenuStrip)Field("_contextMenu").GetValue(widget)!).Close();
                        Cursor.Position = new Point(area.Left + area.Width / 2, area.Top + area.Height / 2);
                        await Task.Delay(150);
                        Cursor.Position = new Point(widget.Left + widget.Width / 2, widget.Top + widget.Height / 2);
                        await Task.Delay(50);
                        widget.Hide(); await Task.Delay(450);
                        Check(!tooltip.IsVisible && VisibleTooltipBounds() is null, "hiding the widget cancels a pending hover");
                        done.TrySetResult();
                    }
                    catch (Exception ex) { done.TrySetException(ex); }
                    finally { widget.Close(); focusForm.Close(); }
                };
                Application.Run(focusForm);
            }
            catch (Exception ex) { done.TrySetException(ex); }
            finally { Cursor.Position = savedCursor; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return done.Task;
    }
    private static FieldInfo Field(string name) => typeof(UsageIndicatorForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException("Tooltip hover: " + message); }
    private static Rectangle? VisibleTooltipBounds()
    {
        Rectangle? result = null;
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out var pid); var name = new StringBuilder(128); GetWindowText(h, name, name.Capacity);
            if (pid == Environment.ProcessId && IsWindowVisible(h) && name.ToString() == "Codex usage hover details" && GetWindowRect(h, out var r))
                result = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
            return true;
        }, IntPtr.Zero); return result;
    }
    private delegate bool WindowCallback(IntPtr hwnd, IntPtr parameter);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder value, int length);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetFocus();
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
}
