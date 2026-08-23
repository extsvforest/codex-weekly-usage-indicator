# Codex Weekly Usage Indicator

An unofficial Windows widget that stays on top while Codex Desktop is running and shows the remaining weekly usage allowance.

## What it does

- Appears while Codex Desktop is running and hides when Codex exits.
- Reads the weekly usage window from the local Codex app-server.
- Refreshes every 60 seconds; double-click to refresh immediately.
- Supports dragging, copying the current value, and toggling always-on-top.
- Remembers the last dragged position and restores it on the next launch.
- Hides while another foreground app is fullscreen, then returns at the saved position.
- Starts a small background watcher at Windows sign-in so it can follow future Codex launches.

The widget does not store login tokens, account details, or usage history. See [PRIVACY.md](PRIVACY.md).

> [!IMPORTANT]
> This is an unofficial community project. It relies on an experimental local Codex app-server method (`account/rateLimits/read`) that may change without notice.

## Requirements

- Windows 10 or 11
- Codex Desktop installed and signed in
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)
- .NET 8 SDK only when building from source

## Install from a release

1. Download and extract the Windows zip from [Releases](https://github.com/GiantForestStudio/codex-weekly-usage-indicator/releases).
2. Review the included PowerShell scripts.
3. Run:

```powershell
.\scripts\install.ps1
```

The app is installed to `%LOCALAPPDATA%\CodexWeeklyUsageIndicator` and a per-user Startup shortcut is created.
The saved window position is kept locally in `settings.json` inside that install directory.

To uninstall:

```powershell
.\scripts\uninstall.ps1
```

Release binaries are currently unsigned, so Windows may display a warning. SHA-256 checksums are included with each release.

## Build from source

```powershell
.\scripts\build.ps1
```

The executable is written to `dist\WeeklyUsageIndicator.exe`. Release builds omit debug paths and symbols.

## How it works

The WinForms process checks for the packaged Codex Desktop host. While Codex is active, it launches `codex app-server --stdio`, initializes the local JSONL protocol, and reads `account/rateLimits/read`. It selects the rate-limit window closest to seven days and renders the remaining percentage.

The app-server child process is stopped whenever Codex is no longer running.

## Project files

- `src/` — WinForms application
- `scripts/` — build, install, and uninstall helpers
- `.github/workflows/` — clean Windows builds and tagged releases
- `AGENTS.md` — guidance for coding agents

## License

[MIT](LICENSE)
