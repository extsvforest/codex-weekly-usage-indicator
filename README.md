# Codex + Claude Usage Indicator

An unofficial Windows widget that stays on top while Codex Desktop is running and shows Codex weekly usage alongside Claude Fable usage.

## What it does

- Appears while Codex Desktop is running and hides when Codex exits.
- Keeps the existing 272 × 64 window and uses one or two fluid panels depending on which providers are available.
- Shows only white remaining percentages: Codex is identified by a blue bar and Claude Fable by a terracotta-orange bar.
- Expands one successful provider to the full width when the other provider is unavailable.
- Shows Codex weekly reset details plus Claude 5-hour, weekly, and Fable reset details in a hover tooltip.
- Reads the weekly usage window from the local Codex app-server.
- Reads Claude limits through Claude Code's built-in `/usage` command with a ten-minute in-memory cache.
- Keeps one credential-free successful Claude snapshot locally for up to 24 hours so a rate-limited cold start does not blank the panel.
- Keeps the last successful Claude value visible during temporary rate limits or service errors and reports the delay in the tooltip.
- Refreshes every 60 seconds; double-click to refresh Codex immediately while Claude continues to honor its ten-minute cache.
- Supports dragging, copying the current values, toggling always-on-top, and turning the Claude panel on or off from the right-click menu.
- Remembers the last dragged position and restores it on the next launch.
- Hides while another foreground app is fullscreen, then returns at the saved position.
- Uses a per-user Windows scheduled task at sign-in, so the widget runs independently of Codex. A lightweight supervisor restarts the widget after an abnormal exit, waiting one minute (up to 999 retries per supervisor run).

Account management is optional. After you register a Codex account, the widget stores account labels, identities, each account's latest usage snapshot, and Codex login snapshots encrypted with Windows CurrentUser DPAPI. The live authentication file remains authoritative for the active account. Claude credentials are never accessed; Claude Code owns its authentication and token refresh. See [PRIVACY.md](PRIVACY.md).

## Manual Codex accounts

Open **Codex 계정 관리…** from the right-click menu, or double-click the tray icon. Register the current account first. Names are optional: a blank field uses `계정 1`, `계정 2`, and so on; registering the current account again preserves its existing name. Choose **다른 계정 로그인** and complete the official browser login using the additional account. This login uses an isolated private `CODEX_HOME` and does not log the desktop out. The widget maintains its topmost position without activating itself, so typing in the manager or another app keeps focus.

To switch, finish your Codex work and close Codex Desktop and other Codex CLI/engine processes. Select the saved account and click **선택 계정으로 전환**. The widget stops its own usage helper, verifies that no Codex writers remain, saves the latest current login, and applies the selected login. It attempts to reopen the previously observed packaged desktop; if necessary, launch Codex from the Start menu and confirm the account there. File application and desktop login verification are separate outcomes.

Only explicit selections cause a switch. There is no automatic quota rotation, proxy, inactive-account polling, or quota pooling. Inactive usage figures show the last observation and its time. A pending encrypted transaction blocks polling until **미완료 전환 복구** reconciles it with the actual live authentication; unknown third-party login changes are not overwritten.

The first version supports local Windows file-based ChatGPT authentication. Unsupported keyring/managed configurations fail closed. The vault is stored separately at `%LOCALAPPDATA%\CodexWeeklyUsageIndicator.Accounts`; uninstall preserves it. Delete inactive accounts from the manager before removing the app if you no longer want their saved credentials. This convenience tool does not establish that any particular multi-account usage pattern is permitted by the service terms.

> [!IMPORTANT]
> This is an unofficial community project. It relies on an experimental local Codex app-server method (`account/rateLimits/read`) and the text output of Claude Code's built-in `/usage` command. Either may change without notice.

## Requirements

- Windows 10 or 11
- Codex Desktop installed and signed in
- Optional: a recent native Claude Code for Windows that supports `/usage` and `--safe-mode`, installed and signed in, to add Claude/Fable usage
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)
- .NET 8 SDK only when building from source

## Install from a release

1. Download and extract the Windows zip from [Releases](https://github.com/GiantForestStudio/codex-weekly-usage-indicator/releases).
2. Review the included PowerShell scripts.
3. Open a standalone Windows PowerShell window (outside packaged apps such as Codex), change to the extracted directory, and run:

```powershell
.\scripts\install.ps1
```

The app is installed to `%LOCALAPPDATA%\CodexWeeklyUsageIndicator`. A per-user `CodexWeeklyUsageIndicator-<Windows SID>` scheduled task starts its supervisor at sign-in, using the signed-in user's normal privileges without storing a password. The supervisor launches and watches the widget; two processes from the same EXE are expected, but only one window. Installation also starts the task immediately, checks that both processes appear, and then removes the old Startup shortcut. Windows Task Scheduler must be available; an installation error must be resolved before relying on automatic recovery.
The saved window position and Claude visibility preference are kept locally in `settings.json` inside that install directory. One sanitized Claude recovery snapshot may be kept in `claude-usage-cache.json` and is ignored after 24 hours or after its Fable reset.

Run installation and removal outside packaged app terminals: Windows can redirect their AppData writes into an app-private folder that Task Scheduler cannot see, causing `0x80070002` even when that terminal reports the EXE exists. Both scripts check the real directory path and refuse redirected locations before changing tasks or running widgets.

To uninstall:

```powershell
.\scripts\uninstall.ps1
```

Release binaries are currently unsigned, so Windows may display a warning. SHA-256 checksums are included with each release.

Choosing **종료** from the widget menu exits normally and also ends the supervisor. It starts again at your next Windows sign-in. To start it sooner with recovery enabled, run its `CodexWeeklyUsageIndicator-<Windows SID>` task in Windows Task Scheduler or run `scripts\install.ps1` again. Launching the EXE directly does not enable supervision for that process. Stopping the scheduled task or killing the supervisor also stops automatic recovery until the task is started again. Uninstall removes the scheduled task before deleting the app.

## Build from source

```powershell
.\scripts\build.ps1
```

The executable is written to `dist\WeeklyUsageIndicator.exe`. Release builds omit debug paths and symbols.

## How it works

The WinForms process checks for the packaged Codex Desktop host. While Codex is active, it launches `codex app-server --stdio`, initializes the local JSONL protocol, and reads `account/rateLimits/read`. It selects the rate-limit window closest to seven days and renders the remaining percentage.

For Claude, it runs the local native executable with `claude -p --safe-mode --no-session-persistence --no-chrome /usage --output-format json --max-turns 0`. Safe mode prevents personal hooks, plugins, MCP servers, and project instructions from affecting the lookup. Claude Code handles its own authentication and token refresh, while the widget parses the returned 5-hour, all-model weekly, and Fable weekly windows. Agentic turns are disabled, the widget also rejects any response that reports a model turn or non-zero cost, and successful results are cached in memory for ten minutes.

The latest successful percentages, reset times, and update time are also written to `claude-usage-cache.json` without credentials or account identifiers. A new process still invokes Claude Code immediately; the local snapshot is used only when that command is temporarily unavailable, and is rejected after 24 hours or after its Fable reset. Authentication or response-schema failures delete it rather than showing data Claude Code has rejected.

If the account does not expose a Fable-specific weekly limit, or one provider fails to refresh, that provider is omitted from the compact surface. Turning off **Claude 사용량 표시** also skips the Claude command until it is turned on again.

Claude percentages remain valid when `/usage` omits reset times (for example, `Current session: 0% used`). Missing or unrecognized reset times appear as unknown. The 5-hour and all-model weekly windows are parsed independently, so an unavailable optional window does not hide a valid Fable percentage.

Temporary Claude Code, network, or service failures keep the last successful Claude value visible while the tooltip shows the last update and next retry time.

The app-server child process is stopped whenever Codex is no longer running.

## Project files

- `src/` — WinForms application
- `scripts/` — build, install, and uninstall helpers
- `wireframes/` — compact widget UI specification
- `.github/workflows/` — clean Windows builds and tagged releases
- `AGENTS.md` — guidance for coding agents

## License

[MIT](LICENSE)
