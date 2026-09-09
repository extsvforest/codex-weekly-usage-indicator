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
        if (string.IsNullOrWhiteSpace(output)) return Task.CompletedTask;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var root = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!, "ui-fixture-" + Guid.NewGuid().ToString("N"));
            try
            {
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
                Application.EnableVisualStyles();
                var home = Directory.CreateDirectory(Path.Combine(root, "home")).FullName;
                var auth = typeof(AccountStoreTests).GetMethod("Auth", BindingFlags.Static | BindingFlags.NonPublic)!;
                byte[] Fixture(string user) => (byte[])auth.Invoke(null, new object[] { user, "personal-workspace-" + user, "ui-only" })!;
                File.WriteAllBytes(Path.Combine(home, "auth.json"), Fixture("a"));
                var store = new CodexAccountStore(Path.Combine(root, "vault"), home);
                store.RegisterCurrent("Pro A · 주 계정");
                store.SaveUsage(store.GetCurrentIdentity().Key, new UsageSnapshot(38, DateTimeOffset.Now.AddDays(3), 10080, "codex"));
                var second = Path.Combine(root, "second.json");
                File.WriteAllBytes(second, Fixture("b"));
                store.ImportLoginFile(second, "Pro B · 추가 계정");
                using var form = new AccountManagerForm(store, () => Task.CompletedTask, () => { });
                form.Show();
                Application.DoEvents();
                using var bitmap = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                bitmap.Save(output, ImageFormat.Png);
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
}
