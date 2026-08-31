# Codex + Claude Usage Indicator

An unofficial Windows widget that stays on top while Codex Desktop is running and shows Codex weekly usage alongside Claude Fable usage.

## What it does

- Appears while Codex Desktop is running and hides when Codex exits.
- Keeps the existing 272 × 64 window and uses one or two fluid panels depending on which providers are available.
- Shows only white remaining percentages: Codex is identified by a blue bar and Claude Fable by a terracotta-orange bar.
- Expands one successful provider to the full width when the other provider is unavailable.
- Shows Codex weekly reset details plus Claude 5-hour, weekly, and Fable reset details in a hover tooltip.
- Reads the weekly usage window from the local Codex app-server.
- Reads Claude limits from Anthropic's usage endpoint with a ten-minute in-memory cache.
- Keeps one credential-free successful Claude snapshot locally for up to 24 hours so a rate-limited cold start does not blank the panel.
- Keeps the last successful Claude value visible during temporary rate limits or service errors and reports the delay in the tooltip.
- Refreshes every 60 seconds; double-click to refresh Codex immediately while Claude continues to honor its ten-minute cache.
- Supports dragging, copying the current values, toggling always-on-top, and turning the Claude panel on or off from the right-click menu.
- Remembers the last dragged position and restores it on the next launch.
- Hides while another foreground app is fullscreen, then returns at the saved position.
- Starts a small background watcher at Windows sign-in so it can follow future Codex launches.

The widget does not store login tokens, account details, or usage history. It stores only the latest successful Claude percentages, reset times, and update time for short-lived recovery. The Claude access token is read into memory only for the request to Anthropic's usage endpoint. See [PRIVACY.md](PRIVACY.md).

> [!IMPORTANT]
> This is an unofficial community project. It relies on an experimental local Codex app-server method (`account/rateLimits/read`) and an undocumented Anthropic usage endpoint (`/api/oauth/usage`). Either may change without notice.

## Requirements

- Windows 10 or 11
- Codex Desktop installed and signed in
- Optional: Claude Code installed and signed in on Windows to add Claude/Fable usage
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
The saved window position and Claude visibility preference are kept locally in `settings.json` inside that install directory. One sanitized Claude recovery snapshot may be kept in `claude-usage-cache.json` and is ignored after 24 hours or after its Fable reset.

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

For Claude, it reads the OAuth access token from `CLAUDE_CONFIG_DIR\.credentials.json` or `%USERPROFILE%\.claude\.credentials.json`, then requests `https://api.anthropic.com/api/oauth/usage`. The response supplies the 5-hour, all-model weekly, and model-scoped Fable weekly windows. The token is not logged or persisted by the widget, and successful Claude responses are cached in memory for ten minutes.

The latest successful percentages, reset times, and update time are also written to `claude-usage-cache.json` without credentials or account identifiers. A new process still attempts a live request immediately; the local snapshot is used only when that request is temporarily rate-limited or unavailable, and is rejected after 24 hours, after its Fable reset, or when the Claude credential file changes.

If the account does not expose a Fable-specific weekly limit, or one provider fails to refresh, that provider is omitted from the compact surface. Turning off **Claude 사용량 표시** also skips the Claude network request until it is turned on again.

Temporary rate limits, network failures, and server errors keep the last successful Claude value visible while the tooltip shows the last update and next retry time.

The app-server child process is stopped whenever Codex is no longer running.

## Project files

- `src/` — WinForms application
- `scripts/` — build, install, and uninstall helpers
- `wireframes/` — compact widget UI specification
- `.github/workflows/` — clean Windows builds and tagged releases
- `AGENTS.md` — guidance for coding agents

## License

[MIT](LICENSE)
