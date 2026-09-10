using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using WeeklyUsageIndicator;

internal static class AccountRuntimeTests
{
    internal static async Task RunAsync()
    {
        var stage = Path.Combine(Path.GetTempPath(), "isolated login fixture");
        var start = CodexAccountRuntime.CreateLoginStartInfo("fixture-codex.exe", stage);
        Check(!start.UseShellExecute && start.CreateNoWindow, "login must have no shell or visible console");
        Check(start.RedirectStandardInput && start.RedirectStandardOutput && start.RedirectStandardError,
            "login diagnostics must not reach parent console");
        Check(start.WorkingDirectory == stage && start.Environment["CODEX_HOME"] == stage,
            "login must use only the isolated home");
        Check(start.ArgumentList.SequenceEqual(new[] { "login", "-c", "cli_auth_credentials_store=\"file\"" }),
            "login must use official browser login and file storage, never token arguments");
        Check(!start.Environment.Keys.Any(key =>
            (key.StartsWith("CODEX_", StringComparison.OrdinalIgnoreCase) && key != "CODEX_HOME")
            || key.StartsWith("OPENAI_", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("CHATGPT_", StringComparison.OrdinalIgnoreCase)),
            "inherited auth and endpoint overrides must not reach isolated login");
        Check(start.Environment["RUST_LOG"] == "off", "verbose Rust tracing must be disabled");

        var packagePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "WindowsApps", "OpenAI.Codex_1.2.3.0_x64__2p2nqsd0c76g0", "app", "ChatGPT.exe");
        Check(CodexAccountRuntime.IsPackagedDesktopPath(packagePath), "expected Windows package shape accepted");
        Check(!CodexAccountRuntime.IsPackagedDesktopPath(packagePath.Replace("2p2nqsd0c76g0", "otherpublisher")),
            "unverified package publisher rejected");
        Check(!CodexAccountRuntime.IsPackagedDesktopPath(Path.Combine(stage, "WindowsApps", "OpenAI.Codex_1__2p2nqsd0c76g0", "app", "ChatGPT.exe")),
            "lookalike directory outside Program Files rejected");
        Check(!CodexAccountRuntime.IsPackagedDesktopPath("ChatGPT.exe"), "relative desktop path rejected");
        try { CodexAccountRuntime.LaunchDesktop(null); throw new Exception("invalid launch unexpectedly succeeded"); }
        catch (InvalidOperationException) { }

        var testBase = Environment.GetEnvironmentVariable("GFS_ACCOUNT_TEST_ROOT")
            ?? Path.Combine(Path.GetTempPath(), "gfs-agent", "260909_codex-account-switch", "tests");
        var root = Path.GetFullPath(Path.Combine(testBase, "runtime-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        try
        {
            string created;
            using (var result = CodexAccountRuntime.CreateLoginStaging(root))
            {
                created = result.DirectoryPath;
                Check(Path.GetDirectoryName(created) == root, "staging remains inside vault root");
                Check(result.AuthPath == Path.Combine(created, "auth.json"), "import path is isolated");
                var acl = new DirectoryInfo(created).GetAccessControl();
                Check(acl.AreAccessRulesProtected, "staging must not inherit broad parent permissions");
                var user = WindowsIdentity.GetCurrent().User!;
                var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
                foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                    Check(rule.AccessControlType != AccessControlType.Allow
                        || rule.IdentityReference.Equals(user) || rule.IdentityReference.Equals(system),
                        "only current user and SYSTEM receive access");
                File.WriteAllText(result.AuthPath, "synthetic fixture, never a credential");
                var nested = Directory.CreateDirectory(Path.Combine(created, "log"));
                File.WriteAllText(Path.Combine(nested.FullName, "test.log"), "synthetic");
            }
            Check(!Directory.Exists(created), "dispose removes login output and nested diagnostics");
            var foreign = Directory.CreateDirectory(Path.Combine(root, "unrelated"));
            using var invalid = new CodexLoginResult(root, foreign.FullName);
            try { invalid.Dispose(); throw new Exception("unrelated cleanup unexpectedly succeeded"); }
            catch (InvalidOperationException) { }
            Check(Directory.Exists(foreign.FullName), "cleanup must preserve unrelated directories");
            // Avoid a second deliberate Dispose exception from the invalid fixture.
            Directory.Delete(foreign.FullName);
        }
        finally { Directory.Delete(root, recursive: false); }
        await TestOwnedJobAsync();
        await TestBrowserDescendantSurvivesAsync(testBase);
    }

    private static async Task TestOwnedJobAsync()
    {
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 30" })
            start.ArgumentList.Add(argument);
        using var owned = new Process { StartInfo = start };
        using var unrelated = new Process { StartInfo = start };
        using var job = new CodexLoginJob();
        try
        {
            Check(owned.Start() && unrelated.Start(), "fake sleepers must start");
            job.Attach(owned);
            Check(!owned.HasExited && !unrelated.HasExited, "both fixture children are alive before disposal");
            job.Dispose();
            await owned.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Check(owned.HasExited, "closing job terminates owned login child");
            Check(!unrelated.HasExited, "closing job leaves an unrelated process running");
        }
        finally
        {
            foreach (var process in new[] { owned, unrelated })
            {
                try
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                }
                catch (InvalidOperationException) { }
            }
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static async Task TestBrowserDescendantSurvivesAsync(string testBase)
    {
        var root = Path.Combine(testBase, "job-descendant-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var trigger = Path.Combine(root, "launch-child");
        var pidFile = Path.Combine(root, "child-pid");
        var script = Path.Combine(root, "parent.ps1");
        File.WriteAllText(script, """
            param([string] $TriggerPath, [string] $PidPath)
            while (-not (Test-Path -LiteralPath $TriggerPath)) { Start-Sleep -Milliseconds 30 }
            $child = Start-Process -FilePath (Join-Path $PSHOME 'powershell.exe') -ArgumentList '-NoProfile', '-NonInteractive', '-Command', 'Start-Sleep -Seconds 30' -WindowStyle Hidden -PassThru
            [System.IO.File]::WriteAllText($PidPath, [string]$child.Id)
            Start-Sleep -Seconds 30
            """);
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-File", script, trigger, pidFile })
            start.ArgumentList.Add(argument);
        using var parent = new Process { StartInfo = start };
        using var job = new CodexLoginJob();
        Process? browserStandIn = null;
        try
        {
            Check(parent.Start(), "fake login parent must start");
            job.Attach(parent);
            // Parent cannot create its child until after it belongs to this job.
            File.WriteAllText(trigger, "go");
            var deadline = Stopwatch.StartNew();
            while ((!File.Exists(pidFile) || new FileInfo(pidFile).Length == 0)
                && deadline.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(50);
            Check(File.Exists(pidFile), "fake login must create a browser descendant");
            var childId = int.Parse(File.ReadAllText(pidFile));
            browserStandIn = Process.GetProcessById(childId);
            Check(!browserStandIn.HasExited, "browser descendant is alive before job closes");
            job.Dispose();
            await parent.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Check(parent.HasExited, "closing job terminates fake login parent");
            Check(!browserStandIn.HasExited, "browser descendant breaks away and survives login termination");
        }
        finally
        {
            foreach (var process in new[] { parent, browserStandIn })
            {
                if (process is null) continue;
                try
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                }
                catch (InvalidOperationException) { }
            }
            browserStandIn?.Dispose();
            foreach (var path in new[] { trigger, pidFile, script }) File.Delete(path);
            Directory.Delete(root, recursive: false);
        }
    }
}
