using WeeklyUsageIndicator;

internal static class LiveEnvironmentSmoke
{
    // Opt-in, read-only official identity and installation checks. Never print
    // account names, command output, credentials, or real usage values.
    internal static async Task RunAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var store = new ClaudeProfileStore();
        var runtime = new ClaudeProfileRuntime(store);
        var normal = await runtime.VerifyAsync(store.DefaultProfile, timeout.Token);
        var safe = await runtime.VerifyAsync(store.DefaultProfile, timeout.Token, safeMode: true);
        if (normal.Key != safe.Key) throw new InvalidOperationException("Official launch and usage identities differ.");
        Console.WriteLine("PASS: official Claude normal and safe-mode subscription identities match");
        var root = Environment.GetEnvironmentVariable("GFS_ACCOUNT_TEST_ROOT") ?? throw new InvalidOperationException("Set test root.");
        var service = new WslCodexService(Path.Combine(root, "read-only-wsl"));
        var probes = await service.DiscoverAsync(timeout.Token);
        foreach (var probe in probes)
            Console.WriteLine($"WSL installation: {probe.Availability}; native CLI: {probe.Target is not null}; writers present: {probe.WriterCount > 0}; reason: {probe.ReasonCode}");
        if (service.HasPendingRecovery) throw new InvalidOperationException("Read-only probe created a switch journal.");
    }
}
