# Agent guide

This repository contains a small Windows-only WinForms utility. Keep changes focused and dependency-free unless a new dependency is clearly necessary.

## Architecture

- `UsageIndicatorForm` owns lifecycle, drawing, polling, and user interaction.
- `CodexDesktopStateReader` detects the packaged Codex Desktop host.
- `AppServerClient` starts the local `codex app-server --stdio` child process and speaks JSONL.
- `ClaudeUsageClient` reads the standard local Claude Code credential and requests the Anthropic usage endpoint.
- Weekly usage is selected by choosing the returned window whose duration is closest to seven days.
- The main Claude value represents the model-scoped Fable weekly limit; its tooltip also reports the 5-hour and all-model weekly windows.

## Invariants

- Never print, log, persist, or transmit authentication tokens anywhere except the Claude access token sent to `https://api.anthropic.com/api/oauth/usage` as required for the usage request.
- Do not add telemetry or other outbound network calls.
- Do not commit absolute local paths, screenshots of real account usage, pet assets, build output, or credentials.
- Treat `account/rateLimits/read` and Anthropic's `/api/oauth/usage` response as experimental and fail gracefully if either changes.
- Re-read Claude credentials for each network refresh, retain successful snapshots only in memory, respect server backoff, and do not refresh more often than every five minutes unless the user explicitly refreshes.
- Stop the app-server child process when the widget pauses or exits.
- Preserve the single-instance mutex and the always-on-top tool-window behavior.

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
6. The context menu works, the widget hides, and its app-server child exits when Codex closes.
7. `install.ps1` and `uninstall.ps1` only modify the current user's dedicated install directory and Startup shortcut.
