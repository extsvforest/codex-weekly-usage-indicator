using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Windows.Forms;
using WeeklyUsageIndicator;

internal static class AccountUiSmoke
{
    internal static Task RunAsync()
    {
        var output = Environment.GetEnvironmentVariable("GFS_ACCOUNT_UI_CAPTURE");
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
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
                var suspended = 0;
                var resumed = 0;
                using var form = new AccountManagerForm(store, () => { suspended++; return Task.CompletedTask; }, () => resumed++);
                form.Show();
                Application.DoEvents();
                var controls = Descendants(form).ToArray();
                var register = controls.OfType<Button>().Single(button => button.Text == "현재 계정 등록");
                var label = controls.OfType<TextBox>().Single();
                var list = controls.OfType<ListView>().Single();
                register.PerformClick();
                Check(store.IsEnabled && store.ListAccounts().Single().Label == "계정 1", "blank-name click must register current account");
                Check(list.Items.Count == 1 && list.Items[0].Text == "계정 1", "registered account must appear immediately");
                Check(controls.OfType<Label>().Any(item => item.Text.Contains("등록 완료")), "registration must display completion");
                Check(register.Enabled && !form.IsOperationInProgress && !form.UseWaitCursor && suspended == 1 && resumed == 1,
                    "registration must restore controls and polling callbacks");
                label.Text = "   ";
                register.PerformClick();
                Check(store.ListAccounts().Count == 1 && store.ListAccounts()[0].Label == "계정 1", "repeated blank registration must preserve the current label");
                label.Text = "Pro A · 주 계정";
                register.PerformClick();
                Check(store.ListAccounts().Single().Label == label.Text, "explicit label must remain supported");
                label.Clear();
                store.SaveUsage(store.GetCurrentIdentity().Key, new UsageSnapshot(38, DateTimeOffset.Now.AddDays(3), 10080, "codex"));
                var second = Path.Combine(root, "second.json");
                File.WriteAllBytes(second, Fixture("b"));
                store.ImportLoginFile(second, "Pro B · 추가 계정");
                controls.OfType<Button>().Single(button => button.Text == "목록 새로고침").PerformClick();
                if (!string.IsNullOrWhiteSpace(output))
                {
                    using var bitmap = new Bitmap(form.Width, form.Height);
                    form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                    bitmap.Save(output, ImageFormat.Png);
                }
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

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control control in parent.Controls)
        {
            yield return control;
            foreach (var child in Descendants(control)) yield return child;
        }
    }

    private static void Check(bool passed, string message)
    {
        if (!passed) throw new InvalidOperationException(message);
    }
}
