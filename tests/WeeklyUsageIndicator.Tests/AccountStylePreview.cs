using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;
using WeeklyUsageIndicator;

// Production forms with synthetic data, for visual review without touching live accounts.
internal static class AccountStylePreview
{
    internal static Task RunAsync(bool hold, bool standardDpi = false)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var basePath = Environment.GetEnvironmentVariable("GFS_ACCOUNT_TEST_ROOT")
                ?? throw new InvalidOperationException("Set GFS_ACCOUNT_TEST_ROOT to the task workspace.");
            var root = Path.Combine(basePath, "style-" + Guid.NewGuid().ToString("N"));
            try
            {
                Application.SetHighDpiMode(standardDpi ? HighDpiMode.DpiUnaware : HighDpiMode.PerMonitorV2); Application.EnableVisualStyles();
                var home = Directory.CreateDirectory(Path.Combine(root, "home")).FullName;
                File.WriteAllBytes(Path.Combine(home, "auth.json"), AccountUsageQueryTests.Auth("style-a", "preview"));
                var store = new CodexAccountStore(Path.Combine(root, "vault"), home);
                var main = store.RegisterCurrent("메인 계정");
                var file = Path.Combine(root, "import.json");
                File.WriteAllBytes(file, AccountUsageQueryTests.Auth("style-b", "preview")); var second = store.ImportLoginFile(file, "서브 계정");
                File.WriteAllBytes(file, AccountUsageQueryTests.Auth("style-c", "preview")); var third = store.ImportLoginFile(file, "서브 계정2");
                var now = DateTimeOffset.Now;
                var accounts = new[] { main, second, third }.Select((account, i) => account with
                {
                    Usage = new UsageSnapshot(new[] { 37, 19, 76 }[i], now.AddDays(2 + i * 2), 10080, "codex"),
                    ObservedAt = now
                }).ToArray();
                var snapshot = new CombinedUsageSnapshot();
                snapshot.Initialize(accounts, batch: true);
                foreach (var account in accounts) snapshot.SetResult(account.Id, account, CombinedReadState.Success);
                snapshot.CompletedAt = now;
                using var form = new AccountManagerForm(store, () => Task.CompletedTask, () => { },
                    refreshAllOnOpen: false, combined: snapshot,
                    queryUsage: (_, _) => throw new InvalidOperationException("디자인 미리보기에서는 실제 계정을 조회하지 않습니다."));
                form.Text += UiText.IsEnglish ? " · Design preview" : " · 디자인 미리보기";
                form.Shown += async (_, _) =>
                {
                    try
                    {
                        await Task.Delay(200); Capture(form, "manager.png");
                        using (var widget = new UsageIndicatorForm(previewMode: true))
                        {
                            typeof(UsageIndicatorForm).GetField("_showClaude", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(widget, true);
                            typeof(UsageIndicatorForm).GetField("_claudeUsage", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(widget,
                                new ClaudeUsageResult(new(new(10, now.AddHours(3)), new(20, now.AddDays(3)), new(40, now.AddDays(3))), now, null, false));
                            widget.Show(); await Task.Delay(100); Capture(widget, "widget.png"); widget.Close();
                        }
                        store.Rename(second.Id, "서브 계정 · 긴 한글 이름과 English 0123456789 확인");
                        form.MenuAction("RefreshAccountsButton").PerformClick();
                        await Task.Delay(100); Capture(form, "long-name.png");
                        using (var dialog = new AccountNameDialog("메인 계정"))
                        {
                            dialog.Show(form); await Task.Delay(100); Capture(dialog, "rename.png"); dialog.Close();
                        }
                        using (var dialog = new AccountNameDialog("", adding: true))
                        {
                            dialog.Show(form); await Task.Delay(100); Capture(dialog, "add.png"); dialog.Close();
                        }
                        using (var dialog = new AccountSwitchDialog("메인 계정", "서브 계정", () => throw new InvalidOperationException("Fixture writer")))
                        {
                            dialog.Show(form); await Task.Delay(100); Capture(dialog, "switch.png"); dialog.Close();
                        }
                        var output = Environment.GetEnvironmentVariable("GFS_ACCOUNT_UI_CAPTURE");
                        if (!string.IsNullOrEmpty(output))
                        {
                            using var tooltip = new UsageToolTip();
                            var text = UsageTooltipText.Build(accounts[0].Usage, null, null, null, false, snapshot, accounts[0].Label);
                            using var screen = form.CreateGraphics();
                            var scale = form.DeviceDpi / 96f; var size = tooltip.Measure(text, scale, screen);
                            using var bitmap = new Bitmap(size.Width, size.Height); bitmap.SetResolution(screen.DpiX, screen.DpiY);
                            using var graphics = Graphics.FromImage(bitmap);
                            tooltip.Render(graphics, new Rectangle(Point.Empty, size), text, scale);
                            bitmap.Save(Path.Combine(Path.GetDirectoryName(output)!, "tooltip.png"), ImageFormat.Png);
                        }
                        store.Rename(second.Id, "서브 계정");
                        form.MenuAction("RefreshAccountsButton").PerformClick();
                        if (!hold) form.Close();
                    }
                    catch (Exception ex) { done.TrySetException(ex); form.Close(); }
                };
                Application.Run(form); done.TrySetResult();
            }
            catch (Exception ex) { done.TrySetException(ex); }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return done.Task;
    }
    private static void Capture(Form form, string name)
    {
        var output = Environment.GetEnvironmentVariable("GFS_ACCOUNT_UI_CAPTURE");
        if (string.IsNullOrEmpty(output)) return;
        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
        bitmap.Save(Path.Combine(Path.GetDirectoryName(output)!, name), ImageFormat.Png);
        IEnumerable<Control> Walk(Control c) => new[] { c }.Concat(c.Controls.Cast<Control>().SelectMany(Walk));
        foreach (var c in Walk(form).Where(c => c.Visible && c.Parent is not null && c.Parent is not ScrollableControl { AutoScroll: true }))
            if (!c.Parent!.ClientRectangle.Contains(c.Bounds))
                throw new InvalidOperationException($"Native preview clips {c.Name} ({c.GetType().Name}) at {c.DeviceDpi} DPI.");
        File.WriteAllText(Path.ChangeExtension(Path.Combine(Path.GetDirectoryName(output)!, name), ".json"),
            System.Text.Json.JsonSerializer.Serialize(Walk(form).Select(c => new { c.Name, Type = c.GetType().Name, c.Text, Bounds = c.Bounds.ToString(), c.DeviceDpi, Font = c.Font.Size, Parent = c.Parent?.GetType().Name })));
    }
}
