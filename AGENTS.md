# Agent guide

This repository contains a small Windows-only WinForms utility. Keep changes focused and dependency-free unless a new dependency is clearly necessary.

## Architecture

- `UsageIndicatorForm` owns lifecycle, drawing, polling, and user interaction.
- `CodexDesktopStateReader` detects the packaged Codex Desktop host.
- `AppServerClient` starts the local `codex app-server --stdio` child process and speaks JSONL.
- Weekly usage is selected by choosing the returned window whose duration is closest to seven days.

## Invariants

- Never read, print, persist, or transmit authentication tokens.
- Do not add telemetry or outbound network calls.
- Do not commit absolute local paths, screenshots of real account usage, pet assets, build output, or credentials.
- Treat `account/rateLimits/read` as experimental and fail gracefully if its response changes.
- Stop the app-server child process when the widget pauses or exits.
- Preserve the single-instance mutex and the always-on-top tool-window behavior.

## Validation

Run:

```powershell
.\scripts\build.ps1
```

Then check that:

1. `dist\WeeklyUsageIndicator.exe` contains no username or absolute user-profile build path.
2. The widget appears while Codex Desktop is open.
3. The value refreshes and the context menu works.
4. The widget hides and its app-server child exits when Codex closes.
5. `install.ps1` and `uninstall.ps1` only modify the current user's dedicated install directory and Startup shortcut.
