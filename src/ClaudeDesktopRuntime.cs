using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Automation;

namespace WeeklyUsageIndicator;

internal interface IClaudeDesktopRuntime
{
    bool IsRunning { get; }
    Task StopAsync(CancellationToken token);
    void RequireStopped();
    void Launch();
    Task<bool> VerifyAccountAsync(string emailHash, CancellationToken token);
}

/// <summary>Uses the official app's normal Quit menu and public account menu. Never opens its stores.</summary>
internal sealed class ClaudeDesktopRuntime(string live, string storage) : IClaudeDesktopRuntime
{
    internal const string AppId = "Claude_pzs8sxrjxfjjc!Claude";
    private static readonly string[] Plans = ["Max", "Pro"];

    public bool IsRunning { get { using var process = FindMainProcess(); return process is not null; } }

    public async Task StopAsync(CancellationToken token)
    {
        await Task.Run(async () =>
        {
            using var app = FindMainProcess();
            if (app is not null)
            {
                var root = AutomationElement.FromHandle(app.MainWindowHandle);
                var menu = Find(root, ControlType.Button, "Menu", "메뉴")
                    ?? throw new InvalidOperationException("Claude 앱의 종료 메뉴를 찾지 못했습니다. 앱에서 파일 → 종료를 누른 뒤 다시 시도하세요.");
                Invoke(menu, token);
                var file = await WaitForAsync(app, ControlType.MenuItem, ["파일", "File"], token);
                Invoke(file, token);
                var quit = await WaitForAsync(app, ControlType.MenuItem, ["종료", "Quit", "Exit"], token);
                Invoke(quit, token);
                // Claude performs its own job/VM cleanup; do not replace that with a process kill.
                await app.WaitForExitAsync(token).WaitAsync(TimeSpan.FromSeconds(100), token);
            }
        }, token).ConfigureAwait(false);
        await ClaudeDesktopWriters.StopBrowserBridgeAsync(live, token).ConfigureAwait(false);
        RequireStopped();
    }

    public void RequireStopped()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string[] alternatives = [Path.Combine(local, "Claude"),
            Path.Combine(local, "Packages", "Claude_pzs8sxrjxfjjc", "LocalCache", "Roaming", "Claude"),
            Path.Combine(local, "Packages", "Claude_pzs8sxrjxfjjc", "LocalCache", "Local", "Claude")];
        using var probe = ClaudeDesktopWriters.Acquire(live, storage, alternativeHomes: alternatives);
    }

    public void Launch()
    {
        // The normal app identity preserves Start/taskbar/protocol behavior. No private flags or OAuth.
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe", Arguments = @"shell:AppsFolder\" + AppId,
            UseShellExecute = true
        });
    }

    public async Task<bool> VerifyAccountAsync(string emailHash, CancellationToken token)
    {
        return await Task.Run(async () =>
        {
            for (var attempt = 0; attempt < 30; attempt++)
            {
                token.ThrowIfCancellationRequested();
                using var app = FindMainProcess();
                if (app is not null)
                {
                    var root = AutomationElement.FromHandle(app.MainWindowHandle);
                    var buttons = root.FindAll(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
                    var candidates = buttons.Cast<AutomationElement>().Where(element =>
                    {
                        var current = element.Current;
                        return !current.IsOffscreen && current.IsEnabled
                            && Plans.Any(plan => current.Name.EndsWith(" " + plan, StringComparison.Ordinal));
                    }).ToArray();
                    if (candidates.Length == 1)
                    {
                        // Accept only an account popup with explicit menu semantics and its
                        // Sign out action. Addresses elsewhere in a conversation never qualify.
                        var profileName = candidates[0].Current.Name;
                        var popup = FindAccountPopup(root, profileName);
                        var openedHere = popup is null;
                        if (openedHere) Invoke(candidates[0], token);
                        try
                        {
                            for (var wait = 0; popup is null && wait < 15; wait++)
                            {
                                await Task.Delay(100, token);
                                popup = FindAccountPopup(AutomationElement.FromHandle(app.MainWindowHandle), profileName);
                            }
                            if (popup is null) return false; // Unknown UI contract: never certify.
                            var emails = VisibleEmails(popup);
                            return emails.Count == 1 && EmailHash(emails.Single()) == emailHash;
                        }
                        finally
                        {
                            if (openedHere && popup is not null && !token.IsCancellationRequested)
                                Invoke(candidates[0], token);
                        }
                    }
                }
                await Task.Delay(400, token);
            }
            return false;
        }, token).ConfigureAwait(false);
    }

    internal static string EmailHash(string email) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(email.Trim().ToLowerInvariant())));

    private static AutomationElement? FindAccountPopup(AutomationElement root, string profileName)
    {
        var matches = new List<AutomationElement>();
        foreach (AutomationElement menu in root.FindAll(TreeScope.Descendants,
                     new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Menu)))
        {
            if (menu.Current.IsOffscreen || !menu.Current.IsEnabled || menu.Current.Name != profileName) continue;
            if (Find(menu, ControlType.MenuItem, "로그아웃", "Log out", "Sign out") is not null)
                matches.Add(menu);
        }
        return matches.Count == 1 ? matches[0] : null;
    }

    private static HashSet<string> VisibleEmails(AutomationElement root)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Observed official menu contract: its account email is a direct Text child before
        // the menu items. Never inspect nested account-switch choices or conversation text.
        var labels = root.FindAll(TreeScope.Children,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text));
        foreach (AutomationElement label in labels)
        {
            var value = label.Current;
            if (value.IsOffscreen || label.FindFirst(TreeScope.Children, Condition.TrueCondition) is not null) continue;
            var text = value.Name.Trim();
            if (System.Net.Mail.MailAddress.TryCreate(text, out var address)
                && address.Address.Equals(text, StringComparison.OrdinalIgnoreCase)) result.Add(text);
        }
        return result;
    }

    private static Process? FindMainProcess()
    {
        var processes = Process.GetProcessesByName("Claude");
        Process? result = null;
        try
        {
            foreach (var process in processes)
            {
                if (process.HasExited || process.MainWindowHandle == IntPtr.Zero) continue;
                var path = process.MainModule?.FileName;
                if (path is null || !IsPackagedApp(path)) continue;
                if (result is not null) throw new InvalidOperationException("Claude 앱이 여러 버전으로 실행 중입니다. 모두 종료한 뒤 다시 시도하세요.");
                result = process;
            }
            return result;
        }
        finally { foreach (var process in processes) if (process != result) process.Dispose(); }
    }

    internal static bool IsPackagedApp(string path)
    {
        var file = new FileInfo(Path.GetFullPath(path));
        var package = file.Directory?.Parent;
        return file.Name.Equals("Claude.exe", StringComparison.OrdinalIgnoreCase)
            && file.Directory?.Name.Equals("app", StringComparison.OrdinalIgnoreCase) == true
            && package?.Name.StartsWith("Claude_", StringComparison.OrdinalIgnoreCase) == true
            && package.Name.EndsWith("__pzs8sxrjxfjjc", StringComparison.OrdinalIgnoreCase)
            && ClaudeProfileStore.PathsEqual(package.Parent!.FullName,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps"));
    }

    private static AutomationElement? Find(AutomationElement root, ControlType type, params string[] names)
    {
        foreach (AutomationElement element in root.FindAll(TreeScope.Descendants,
                     new PropertyCondition(AutomationElement.ControlTypeProperty, type)))
        {
            var current = element.Current;
            if (!current.IsOffscreen && current.IsEnabled && names.Contains(current.Name, StringComparer.OrdinalIgnoreCase))
                return element;
        }
        return null;
    }

    private static async Task<AutomationElement> WaitForAsync(Process app, ControlType type,
        string[] names, CancellationToken token)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            token.ThrowIfCancellationRequested();
            if (app.HasExited) throw new InvalidOperationException("Claude 앱이 먼저 종료되었습니다. 다시 시도하세요.");
            var element = Find(AutomationElement.FromHandle(app.MainWindowHandle), type, names);
            if (element is not null) return element;
            await Task.Delay(100, token);
        }
        throw new InvalidOperationException("Claude 앱의 종료 메뉴를 찾지 못했습니다. 앱에서 파일 → 종료를 누른 뒤 다시 시도하세요.");
    }

    private static void Invoke(AutomationElement element, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (element.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke)) ((InvokePattern)invoke).Invoke();
        else if (element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var expand)) ((ExpandCollapsePattern)expand).Expand();
        else throw new InvalidOperationException("Claude 앱의 메뉴를 실행하지 못했습니다. 앱에서 파일 → 종료를 누른 뒤 다시 시도하세요.");
    }
}
