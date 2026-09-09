using System.Diagnostics;
using System.Text;
using WeeklyUsageIndicator;

internal static class AppServerLifecycleTests
{
    public static async Task RunAsync()
    {
        var taskRoot = Environment.GetEnvironmentVariable("GFS_ACCOUNT_TEST_ROOT")
            ?? Path.Combine(Path.GetTempPath(), "gfs-agent", "260909_codex-account-switch", "tests");
        var root = Path.Combine(taskRoot, "appserver-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await CancelUsageAndResumeAsync(root);
            await CancelInitializeAsync(root);
            await AccountReadConsistencyAsync(root);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task AccountReadConsistencyAsync(string root)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using (var client = new AppServerClient(() => FakeServer(Path.Combine(root, "account.txt"), "none", 0)))
        {
            var response = await client.GetWeeklyUsageWithAccountAsync(limit.Token);
            Check(response.IsChatGpt && response.Email == "fixture@example.invalid" && response.Usage.UsedPercent == 23,
                "Combined read must associate usage with the same helper's sanitized account metadata.");
            await client.SuspendAsync();
        }
        using (var client = new AppServerClient(() => FakeServer(Path.Combine(root, "account-change.txt"), "accountChange", 0)))
        {
            try { await client.GetWeeklyUsageWithAccountAsync(limit.Token); }
            catch (IOException ex) when (ex.Message.Contains("account changed", StringComparison.Ordinal)) { return; }
            throw new InvalidOperationException("Usage spanning an account change must be rejected.");
        }
    }

    private static async Task CancelUsageAndResumeAsync(string root)
    {
        var marker = Path.Combine(root, "usage.txt");
        var starts = 0;
        using var client = new AppServerClient(() => FakeServer(marker, "usage", ++starts == 1 ? 60000 : 0));
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var requests = Enumerable.Range(0, 5).Select(_ => client.GetWeeklyUsageAsync(limit.Token)).ToArray();
        var pid = await WaitForMarkerAsync(marker, limit.Token);
        await client.SuspendAsync().WaitAsync(limit.Token);
        foreach (var request in requests) await ExpectCancellationAsync(request);
        Check(starts == 1, "Queued reads must not start children after suspension.");
        Check(!IsRunning(pid), "Suspend must await actual child exit.");
        await ExpectCancellationAsync(client.GetWeeklyUsageAsync(limit.Token));
        Check(starts == 1, "Suspended reads must remain blocked.");

        client.Resume();
        var usage = await client.GetWeeklyUsageAsync(limit.Token);
        Check(usage.UsedPercent == 23 && usage.WindowDurationMinutes == 10080,
            "A resumed session must receive its own weekly response after the old reader exits.");
        Check(starts == 2, "Resume should create exactly one replacement child.");
        await client.SuspendAsync();
        await client.SuspendAsync();
        client.Resume();
    }

    private static async Task CancelInitializeAsync(string root)
    {
        var marker = Path.Combine(root, "initialize.txt");
        using var client = new AppServerClient(() => FakeServer(marker, "initialize", 60000));
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var request = client.GetWeeklyUsageAsync(limit.Token);
        var pid = await WaitForMarkerAsync(marker, limit.Token);
        await client.SuspendAsync().WaitAsync(limit.Token);
        await ExpectCancellationAsync(request);
        Check(!IsRunning(pid), "Suspension during initialize must also stop the child before returning.");
    }

    private static ProcessStartInfo FakeServer(string marker, string delayAt, int delayMilliseconds)
    {
        const string script = """
            $ErrorActionPreference = 'Stop'
            $accountReads = 0
            while ($null -ne ($line = [Console]::ReadLine())) {
                $message = $line | ConvertFrom-Json
                if ($message.method -eq 'initialized') { continue }
                if ($message.method -eq 'initialize') {
                    if ($env:GFS_FAKE_DELAY_AT -eq 'initialize') {
                        [IO.File]::WriteAllText($env:GFS_FAKE_MARKER, [string]$PID)
                        Start-Sleep -Milliseconds ([int]$env:GFS_FAKE_DELAY_MS)
                    }
                    $result = @{}
                } elseif ($message.method -eq 'account/read') {
                    $accountReads++
                    $email = 'fixture@example.invalid'
                    if ($env:GFS_FAKE_DELAY_AT -eq 'accountChange' -and $accountReads -gt 1) { $email = 'changed@example.invalid' }
                    $result = @{ account = @{ type = 'chatgpt'; email = $email; planType = 'pro' }; requiresOpenaiAuth = $true }
                } else {
                    [IO.File]::WriteAllText($env:GFS_FAKE_MARKER, [string]$PID)
                    if ($env:GFS_FAKE_DELAY_AT -eq 'usage') { Start-Sleep -Milliseconds ([int]$env:GFS_FAKE_DELAY_MS) }
                    $result = @{ rateLimitsByLimitId = @{ codex = @{ limitId = 'codex'; primary = @{ usedPercent = 91; windowDurationMins = 300 }; secondary = @{ usedPercent = 23; windowDurationMins = 10080 } } } }
                }
                [Console]::WriteLine((@{id = $message.id; result = $result} | ConvertTo-Json -Compress -Depth 10))
            }
            """;
        var info = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
            Arguments = "-NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        info.Environment["GFS_FAKE_MARKER"] = marker;
        info.Environment["GFS_FAKE_DELAY_AT"] = delayAt;
        info.Environment["GFS_FAKE_DELAY_MS"] = delayMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return info;
    }

    private static async Task<int> WaitForMarkerAsync(string marker, CancellationToken token)
    {
        while (!File.Exists(marker)) await Task.Delay(25, token);
        // The fixture may have created the file before its one short write finishes.
        while (true)
        {
            try { if (int.TryParse(await File.ReadAllTextAsync(marker, token), out var pid)) return pid; }
            catch (IOException) { }
            await Task.Delay(25, token);
        }
    }

    private static async Task ExpectCancellationAsync(Task task)
    {
        try { await task; }
        catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("Expected the suspended request to be canceled.");
    }

    private static bool IsRunning(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
