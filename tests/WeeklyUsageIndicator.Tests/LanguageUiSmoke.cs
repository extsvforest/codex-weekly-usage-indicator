using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using WeeklyUsageIndicator;

internal static class LanguageUiSmoke
{
    internal static Task RunAsync()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var root = Path.Combine(Environment.GetEnvironmentVariable("GFS_ACCOUNT_TEST_ROOT") ??
                Path.Combine(Path.GetTempPath(), "gfs-agent", "indicator-tests"), "language-ui-" + Guid.NewGuid().ToString("N"));
            try
            {
                UiText.SetLanguage("ko");
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); Application.EnableVisualStyles();
                var home = Directory.CreateDirectory(Path.Combine(root, "home")).FullName;
                var authPath = Path.Combine(home, "auth.json");
                var auth = AccountUsageQueryTests.Auth("language-only", "preview");
                File.WriteAllBytes(authPath, auth);
                var store = new CodexAccountStore(Path.Combine(root, "vault"), home);
                var account = store.RegisterCurrent("메인 · Keep this name");
                store.SaveUsage(store.GetCurrentIdentity().Key, new UsageSnapshot(37, DateTimeOffset.Now.AddDays(2), 10080, "codex"));
                account = store.ListAccounts().Single();
                var combined = new CombinedUsageSnapshot(); combined.Initialize(new[] { account }, batch: true);
                combined.SetResult(account.Id, account, CombinedReadState.Success); combined.CompletedAt = DateTimeOffset.UtcNow;
                var completedAt = combined.CompletedAt; var queryCount = 0;
                using var widget = new UsageIndicatorForm(previewMode: true);
                using var manager = new AccountManagerForm(store, () => Task.CompletedTask, () => { },
                    queryUsage: (_, _) => { queryCount++; throw new InvalidOperationException("Language changes must never query."); },
                    refreshAllOnOpen: false, combined: combined);
                Set(widget, "_accountManager", manager); manager.Show(); Application.DoEvents();
                var bounds = manager.Bounds;
                Set(manager, "_busy", true);
                Check(!widget.ChangeLanguage("en") && UiText.Language == "ko", "in-flight operations block language switching");
                Set(manager, "_busy", false);
                using (var dialog = new AccountNameDialog(account.Label))
                {
                    dialog.Show(manager); Application.DoEvents();
                    Check(!widget.ChangeLanguage("en"), "an open name editor keeps its input and language");
                    dialog.Close();
                }
                manager.Enabled = false;
                Check(!widget.ChangeLanguage("en"), "native modal owner disablement blocks switching");
                manager.Enabled = true;
                Check(widget.ChangeLanguage("en"), "idle manager changes to English"); Application.DoEvents();
                var english = Get<AccountManagerForm>(widget, "_accountManager");
                Check(manager.IsDisposed && english.Visible && english.Text == "Codex accounts", "manager is recreated in English");
                Check(english.Bounds == bounds, "physical bounds stay unchanged across DPI-aware recreation");
                Check(ReferenceEquals(english.CombinedUsage, combined) && combined.CompletedAt == completedAt && combined.IsComplete(DateTimeOffset.Now), "complete batch remains intact");
                Check(Walk(english).Any(c => c.Text == account.Label), "account alias stays exact in the English UI");
                var menu = Get<ContextMenuStrip>(widget, "_contextMenu");
                var languages = (ToolStripMenuItem)menu.Items.Find("LanguageMenu", false).Single();
                Check(((ToolStripMenuItem)languages.DropDownItems.Find("Language-en", false).Single()).Checked, "English menu choice is checked");
                Check(widget.ChangeLanguage("ko"), "language can return to Korean"); Application.DoEvents();
                var korean = Get<AccountManagerForm>(widget, "_accountManager");
                Check(korean.Text == "Codex 계정 관리" && korean.Bounds == bounds, "Korean layout returns at the same bounds");
                Check(queryCount == 0 && File.ReadAllBytes(authPath).SequenceEqual(auth), "switching language never queries or changes authentication");
                Check(store.ListAccounts().Single().Label == account.Label, "alias stays unchanged in storage");
                korean.Close(); widget.Close(); done.TrySetResult();
            }
            catch (Exception ex) { done.TrySetException(ex); }
            finally { UiText.SetLanguage("ko"); if (Directory.Exists(root)) Directory.Delete(root, true); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return done.Task;
    }
    private static IEnumerable<Control> Walk(Control root) => new[] { root }.Concat(root.Controls.Cast<Control>().SelectMany(Walk));
    private static void Set(object target, string name, object value) => target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(target, value);
    private static T Get<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(target)!;
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
