using System.ComponentModel;
using System.Diagnostics;

namespace WeeklyUsageIndicator;

internal static class WidgetSupervisor
{
    internal static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(1);
    internal const int MaximumRestarts = 999;

    internal static int Run()
    {
        using var singleSupervisor = new Mutex(true,
            @"Local\CodexWeeklyUsageIndicator.Supervisor", out var isFirstInstance);
        if (!isFirstInstance) return 0;

        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot locate the widget executable.");
        var result = RunLoop(() =>
        {
            using var child = Process.Start(new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = AppContext.BaseDirectory
            }) ?? throw new Win32Exception("Cannot start the widget.");
            child.WaitForExit();
            return child.ExitCode;
        }, Thread.Sleep);
        GC.KeepAlive(singleSupervisor);
        return result;
    }

    // A normal menu Quit returns zero and ends supervision. Do not depend on
    // Task Scheduler RestartOnFailure: it did not retry an exited action in UAT.
    internal static int RunLoop(Func<int> runWidget, Action<TimeSpan> wait)
    {
        for (var restarts = 0; ; restarts++)
        {
            int exitCode;
            try { exitCode = runWidget(); }
            catch (Win32Exception) { exitCode = 1; }

            if (exitCode == 0 || restarts >= MaximumRestarts) return exitCode;
            wait(RetryInterval);
        }
    }
}
