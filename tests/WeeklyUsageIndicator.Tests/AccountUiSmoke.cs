using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using WeeklyUsageIndicator;

internal static class AccountUiSmoke
{
    internal static Task RunAsync()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var output = Environment.GetEnvironmentVariable("GFS_ACCOUNT_UI_CAPTURE");
            var parent = string.IsNullOrWhiteSpace(output)
                ? Environment.GetEnvironmentVariable("GFS_ACCOUNT_TEST_ROOT") ?? Path.Combine(Path.GetTempPath(), "gfs-agent", "260909_codex-account-switch", "tests")
                : Path.GetDirectoryName(Path.GetFullPath(output))!;
            var root = Path.Combine(parent, "ui-fixture-" + Guid.NewGuid().ToString("N"));
            try
            {
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
                Application.EnableVisualStyles();
                var home = Directory.CreateDirectory(Path.Combine(root, "home")).FullName;
                var auth = typeof(AccountStoreTests).GetMethod("Auth", BindingFlags.Static | BindingFlags.NonPublic)!;
                byte[] Fixture(string user) => (byte[])auth.Invoke(null, new object[] { user, "personal-workspace-" + user, "ui-only" })!;
                File.WriteAllBytes(Path.Combine(home, "auth.json"), Fixture("a"));
                var store = new CodexAccountStore(Path.Combine(root, "vault"), home);
                var suspended = 0; var resumed = 0;
                using var form = new AccountManagerForm(store, () => { suspended++; return Task.CompletedTask; }, () => resumed++);
                form.Show();
                Application.DoEvents();
                Capture(form, output, "-empty");
                Button(form, "RegisterCurrentButton").PerformClick();
                Check(store.IsEnabled && store.ListAccounts().Single().Label == "계정 1", "registration needs no name entry");
                var list = Find<ListBox>(form, "AccountList");
                Check(list.Items.Count == 1 && list.SelectedIndex == 0, "registration immediately selects the account");
                Check(!form.IsOperationInProgress && suspended == 1 && resumed == 1, "registration restores operation state");
                Check(!Button(form, "SwitchAccountButton").Enabled && !Button(form, "DeleteAccountButton").Enabled, "current account cannot switch or delete");
                Check(Button(form, "RenameAccountButton").Enabled, "current account can rename");

                RespondToDialog(() => Button(form, "RenameAccountButton").PerformClick(), "AccountNameDialog", dialog =>
                {
                    var input = Find<TextBox>(dialog, "AccountNameTextBox");
                    Check(input.Text == "계정 1", "rename opens with current name");
                    input.Text = "Pro A · 주 계정";
                    input.Focus();
                    using (var widget = new UsageIndicatorForm(previewMode: true))
                    {
                        dialog.Activate();
                        input.Focus();
                        Application.DoEvents();
                        Check(input.Focused && GetFocus() == input.Handle, "name editor has native keyboard focus before widget maintenance");
                        var foreground = GetForegroundWindow();
                        for (var tick = 0; tick < 5; tick++)
                        {
                            if (tick == 2) widget.Hide();
                            widget.MaintainVisiblePresentation();
                            Application.DoEvents();
                            Check(GetForegroundWindow() == foreground && GetFocus() == input.Handle && input.Focused, "widget show and maintenance must preserve foreground and editor focus");
                            Check((GetWindowLong(widget.Handle, -20) & 8) != 0, "widget remains topmost without managed activation");
                        }
                        widget.Close();
                    }
                    Capture(dialog, output, "-rename");
                    Button(dialog, "SaveNameButton").PerformClick();
                });
                Check(store.ListAccounts().Single().Label == "Pro A · 주 계정", "save renames current account");
                Check(suspended == 1 && resumed == 1, "metadata rename must not restart helper");
                RespondToDialog(() => Button(form, "RenameAccountButton").PerformClick(), "AccountNameDialog", dialog =>
                {
                    Find<TextBox>(dialog, "AccountNameTextBox").Text = "discard this";
                    Button(dialog, "CancelNameButton").PerformClick();
                });
                Check(store.ListAccounts().Single().Label == "Pro A · 주 계정", "cancel preserves original name");
                Check(!Find<Label>(form, "StatusLabel").Text.Contains("준비하고"), "cancel cannot leave preparing status");
                RespondToDialog(() => Button(form, "AddAccountButton").PerformClick(), "AccountNameDialog", dialog =>
                {
                    Check(ReferenceEquals(dialog.CancelButton, Button(dialog, "CancelNameButton")), "Escape is wired to cancel");
                    Check(ReferenceEquals(dialog.AcceptButton, Button(dialog, "SaveNameButton")), "Enter is wired to the primary action");
                    Capture(dialog, output, "-add");
                    Button(dialog, "CancelNameButton").PerformClick();
                });
                Check(store.ListAccounts().Count == 1 && suspended == 1, "canceling add must not start login or interrupt helper");
                RespondToDialog(() => Button(form, "RenameAccountButton").PerformClick(), "AccountNameDialog", dialog =>
                {
                    Find<TextBox>(dialog, "AccountNameTextBox").Text = " ";
                    Button(dialog, "SaveNameButton").PerformClick();
                    Check(!dialog.IsDisposed && dialog.Visible && !string.IsNullOrWhiteSpace(Find<Label>(dialog, "NameErrorLabel").Text), "invalid name stays in editor with explanation");
                    Button(dialog, "CancelNameButton").PerformClick();
                });
                store.SaveUsage(store.GetCurrentIdentity().Key, new UsageSnapshot(38, DateTimeOffset.Now.AddDays(3), 10080, "codex"));
                var second = Path.Combine(root, "second.json");
                File.WriteAllBytes(second, Fixture("b"));
                store.ImportLoginFile(second, "Pro B · 추가 계정");
                Button(form, "RefreshAccountsButton").PerformClick();
                Check(list.Items.Count == 2, "refresh loads saved accounts");
                list.SelectedIndex = 1;
                Application.DoEvents();
                Check(Button(form, "SwitchAccountButton").Enabled && Button(form, "DeleteAccountButton").Enabled, "saved account exposes switch and delete");
                RespondToDialog(() => Button(form, "RenameAccountButton").PerformClick(), "AccountNameDialog", dialog =>
                {
                    Find<TextBox>(dialog, "AccountNameTextBox").Text = "Pro B · 보조 계정";
                    Button(dialog, "SaveNameButton").PerformClick();
                });
                Check(store.ListAccounts().Any(account => account.Label == "Pro B · 보조 계정"), "saved account rename persists");
                Capture(form, output, "-saved");
                var writersRunning = true;
                using (var preparation = new AccountSwitchDialog("Pro A", "Pro B", () =>
                {
                    if (writersRunning) throw new InvalidOperationException("fixture writer is running");
                }))
                {
                    Exception? readinessFailure = null;
                    using var readyTimer = new System.Windows.Forms.Timer { Interval = 1250 };
                    readyTimer.Tick += (_, _) =>
                    {
                        readyTimer.Stop();
                        try
                        {
                            var confirm = Button(preparation, "ConfirmSwitchButton");
                            Check(confirm.Enabled, "readiness timer enables confirm after writers stop");
                            Capture(preparation, output, "-ready");
                            writersRunning = true;
                            confirm.PerformClick();
                            Check(preparation.Visible && !confirm.Enabled, "confirm rechecks writers immediately before accepting");
                        }
                        catch (Exception ex) { readinessFailure = ex; }
                        finally { Button(preparation, "CancelSwitchButton").PerformClick(); }
                    };
                    RespondToDialog(() => preparation.ShowDialog(form), "AccountSwitchDialog", dialog =>
                    {
                        Check(!Button(dialog, "ConfirmSwitchButton").Enabled, "running writers block confirmation");
                        Capture(dialog, output, "-waiting");
                        writersRunning = false;
                        readyTimer.Start();
                    });
                    if (readinessFailure is not null) throw readinessFailure;
                    Check(preparation.DialogResult == DialogResult.Cancel, "cancel exits preparation without mutation");
                }
                list.SelectedIndex = 0;
                Capture(form, output, "");
                form.Size = form.MinimumSize;
                Capture(form, output, "-minimum");
                var detail = Find<Panel>(form, "AccountDetailPanel");
                detail.ScrollControlIntoView(Button(form, "DeleteAccountButton"));
                Application.DoEvents();
                Check(detail.RectangleToScreen(detail.ClientRectangle).Contains(Button(form, "DeleteAccountButton").RectangleToScreen(Button(form, "DeleteAccountButton").ClientRectangle)), "minimum-size detail scroll exposes lower actions");
                Capture(form, output, "-minimum-actions");
                File.WriteAllBytes(Path.Combine(home, "auth.json"), Fixture("c"));
                Button(form, "RefreshAccountsButton").PerformClick();
                Check(Button(form, "RegisterActiveAccountButton").Visible && !Button(form, "SwitchAccountButton").Enabled, "unregistered current account has a registration path before switching");
                Capture(form, output, "-unregistered");
                Button(form, "RegisterActiveAccountButton").PerformClick();
                Check(store.ListAccounts().Count == 3 && store.ListAccounts().Single(a => a.IsActive).Label == "계정 1", "external current account can register without re-login");
                Check(!Button(form, "RegisterActiveAccountButton").Visible, "registration entry clears after success");
                form.Close();
                completion.SetResult();
            }
            catch (Exception ex) { completion.SetException(ex); }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static void RespondToDialog(Action open, string name, Action<Form> respond)
    {
        Exception? failure = null;
        var handled = false;
        using var timer = new System.Windows.Forms.Timer { Interval = 30 };
        timer.Tick += (_, _) =>
        {
            var dialog = Application.OpenForms.Cast<Form>().FirstOrDefault(form => form.Name == name);
            if (dialog is null) return;
            timer.Stop(); handled = true;
            try { respond(dialog); }
            catch (Exception ex) { failure = ex; dialog.DialogResult = DialogResult.Cancel; dialog.Close(); }
        };
        timer.Start(); open(); timer.Stop();
        if (failure is not null) throw failure;
        Check(handled, "expected modal dialog: " + name);
    }

    private static void Capture(Form form, string? output, string suffix)
    {
        Application.DoEvents();
        if (!string.IsNullOrWhiteSpace(output))
        {
            using var bitmap = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
            bitmap.Save(Path.Combine(Path.GetDirectoryName(output)!, Path.GetFileNameWithoutExtension(output) + suffix + ".png"), ImageFormat.Png);
        }
        CheckActionBounds(form);
    }

    private static void CheckActionBounds(Control root)
    {
        foreach (Control control in root.Controls)
        {
            if (!control.Visible) continue;
            if (control is Button)
            {
                var bounds = control.RectangleToScreen(control.ClientRectangle);
                for (var parent = control.Parent; parent is not null; parent = parent.Parent)
                {
                    if (parent is ScrollableControl { AutoScroll: true }) break;
                    Check(parent.RectangleToScreen(parent.ClientRectangle).Contains(bounds), $"action must fit visible ancestors: {control.Name} {bounds} in {parent.GetType().Name} {parent.RectangleToScreen(parent.ClientRectangle)}");
                }
            }
            CheckActionBounds(control);
        }
    }

    private static T Find<T>(Control form, string name) where T : Control => form.Controls.Find(name, true).OfType<T>().Single();
    private static Button Button(Control form, string name) => Find<Button>(form, name);
    private static void Check(bool passed, string message) { if (!passed) throw new InvalidOperationException(message); }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetFocus();
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr handle, int index);
}
