using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using WeeklyUsageIndicator;

internal static class UsageTooltipUiSmoke
{
    internal static Task RunAsync()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); Application.EnableVisualStyles();
                using var form = new Form { Text = "Usage tooltip test", ClientSize = new Size(920, 740), StartPosition = FormStartPosition.CenterScreen };
                AccountUiTheme.SetForm(form);
                var input = new TextBox { Location = new Point(24, 24), Width = 300, Text = "Keyboard focus stays here" }; form.Controls.Add(input);
                var now = DateTimeOffset.UtcNow;
                var accounts = new[] { ("메인 계정", 37), ("서브 계정", 19), ("서브 계정2", 76) }
                    .Select((a, i) => new SavedCodexAccount(i.ToString(), a.Item1, "synthetic", i == 0, new(a.Item2, now.AddDays(i + 2), 10080, "codex"), now)).ToArray();
                var set = new CombinedUsageSnapshot(); set.Initialize(accounts, true);
                foreach (var a in accounts) set.SetResult(a.Id, a, CombinedReadState.Success); set.CompletedAt = now;
                var claude = new ClaudeUsageResult(new(new(10, now.AddHours(3)), new(20, now.AddDays(3)), new(30, now.AddDays(3))), now, null, false);
                var text = UsageIndicatorForm.BuildTooltipText(accounts[0].Usage, claude, null, null, true, set, "메인 계정");
                using var tooltip = new UsageToolTip { ShowAlways = true };
                tooltip.SetToolTip(input, text);
                var popped = false; var drawn = false;
                tooltip.Popup += (_, e) => { popped = true; Check(e.ToolTipSize.Height <= Screen.FromControl(form).WorkingArea.Height, "full tooltip fits the available screen height"); };
                tooltip.Draw += (_, _) => drawn = true;
                form.Shown += async (_, _) =>
                {
                    try
                    {
                        input.Focus(); var foreground = GetForegroundWindow(); var focus = GetFocus();
                        tooltip.Show(text, input, new Point(0, input.Height + 8));
                        await Task.Delay(250);
                        Check(popped && drawn, "native tooltip executes measured owner drawing");
                        Check(foreground == GetForegroundWindow() && focus == GetFocus() && input.Focused, "tooltip preserves foreground and keyboard focus");
                        var output = Environment.GetEnvironmentVariable("GFS_ACCOUNT_UI_CAPTURE");
                        if (!string.IsNullOrWhiteSpace(output))
                        {
                            IntPtr window = IntPtr.Zero;
                            EnumWindows((handle, _) =>
                            {
                                GetWindowThreadProcessId(handle, out var pid); var name = new StringBuilder(64); GetClassName(handle, name, name.Capacity);
                                if (pid == Environment.ProcessId && IsWindowVisible(handle) && name.ToString().Contains("tooltips_class32", StringComparison.OrdinalIgnoreCase)) window = handle;
                                return true;
                            }, IntPtr.Zero);
                            Check(window != IntPtr.Zero && GetWindowRect(window, out _), "native tooltip has a visible window");
                            GetWindowRect(window, out var rect);
                            using var bitmap = new Bitmap(rect.Right - rect.Left, rect.Bottom - rect.Top);
                            using var graphics = Graphics.FromImage(bitmap);
                            graphics.CopyFromScreen(rect.Left, rect.Top, 0, 0, bitmap.Size);
                            bitmap.Save(Path.Combine(Path.GetDirectoryName(output)!, "usage-tooltip-native.png"), ImageFormat.Png);
                        }
                        done.TrySetResult();
                    }
                    catch (Exception ex) { done.TrySetException(ex); }
                    finally { tooltip.Hide(input); form.Close(); }
                };
                Application.Run(form);
            }
            catch (Exception ex) { done.TrySetException(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return done.Task;
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException("Native tooltip: " + message); }
    private delegate bool WindowCallback(IntPtr hwnd, IntPtr parameter);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder value, int length);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetFocus();
}
