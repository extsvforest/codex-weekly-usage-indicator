# Agent guide

This repository contains a small Windows-only WinForms utility. Keep changes focused and dependency-free unless a new dependency is clearly necessary.

## Architecture

- `UsageIndicatorForm` owns lifecycle, drawing, polling, and user interaction.
- `CodexDesktopStateReader` detects the packaged Codex Desktop host.
- `AppServerClient` starts the local `codex app-server --stdio` child process and speaks JSONL.
- `ClaudeCodeUsageSource` invokes the native Claude Code `/usage` command; `ClaudeUsageClient` owns caching and backoff.
- Weekly usage is selected by choosing the returned window whose duration is closest to seven days.
- The main Claude value represents the model-scoped Fable weekly limit; its tooltip also reports the 5-hour and all-model weekly windows.

## Invariants

- Never read, print, log, persist, or transmit Claude authentication tokens. Claude Code owns its authentication and token refresh.
- Do not add telemetry or other outbound network calls.
- Do not commit absolute local paths, screenshots of real account usage, pet assets, build output, or credentials.
- Treat `account/rateLimits/read` and Claude Code's `/usage` text output as changeable contracts and fail gracefully if either changes.
- Invoke Claude in safe mode without a shell, browser integration, or session persistence, and set `--max-turns 0`. Require `num_turns == 0` and `total_cost_usd == 0`; reject the response if either guard fails.
- Do not invoke Claude more often than every ten minutes, including explicit UI refreshes, and back off transient command failures.
- Persist at most one credential-free Claude recovery snapshot containing only percentages, reset times, and update time. Use it only for transient cold-start failures, delete it after 24 hours, its Fable reset, or an authentication/schema failure, and never let it suppress the first live request in a new process.
- Stop the app-server child process when the widget pauses or exits.
- Preserve the single-instance mutex and the always-on-top tool-window behavior.
- The installer must launch the `--supervise` mode through the per-user interactive, least-privilege scheduled task, never directly from Codex. The supervisor retries nonzero widget exits/start failures only; normal Quit must end supervision. Do not substitute Task Scheduler RestartOnFailure for this loop: it did not retry an exited action in live tests. Disable/stop the task before upgrade or uninstall; filter processes by the current user's exact installed EXE path.

## Validation

Run:

```powershell
.\scripts\build.ps1
```

Then check that:

1. `dist\WeeklyUsageIndicator.exe` contains no username or absolute user-profile build path.
2. The widget appears while Codex Desktop is open and remains 272 × 64 pixels at 100% scale.
3. Codex and Claude values refresh independently; one provider failing does not leave an empty half-panel.
4. Hover details include Codex weekly plus Claude 5-hour, weekly, and Fable percentages and reset times.
5. The right-click Claude visibility toggle persists and suppresses Claude requests while off.
6. A temporary Claude command, network, or service error keeps the last successful value visible and reports the update delay in the tooltip.
7. A cold-start transient failure uses only a recent, unexpired sanitized snapshot and still attempts a live Claude request first.
8. The context menu works, the widget hides, and its app-server child exits when Codex closes.
9. `install.ps1` and `uninstall.ps1` only modify the current user's dedicated install directory, legacy Startup shortcut, and SID-named scheduled task.
10. The task owns the supervisor and its widget child independently of Codex. Forced termination of the widget child recovers after about one minute; normal Quit ends both processes and does not recover. Reinstall leaves one supervisor and one widget; uninstall removes the task before stopping the app.
