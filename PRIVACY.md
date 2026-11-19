# Privacy

Codex + Claude Usage Indicator uses your installed official Codex and Claude Code clients to check usage and manage explicitly selected accounts. It has no telemetry, request proxy or direct usage API connection. The official clients handle communication with their providers under their own terms and privacy practices.

## Local data

| Location | Contents |
| --- | --- |
| `%LOCALAPPDATA%\CodexWeeklyUsageIndicator\settings.json` | Window position, Claude visibility, selected Claude usage metric and interface language |
| `%LOCALAPPDATA%\CodexWeeklyUsageIndicator\claude-usage-cache.json` | At most one Claude recovery snapshot: percentages, reset times, check time and an opaque profile key containing a hash of the verified account identity |
| `%LOCALAPPDATA%\CodexWeeklyUsageIndicator.Accounts` | Windows-user DPAPI encrypted Codex logins, latest account observations and recovery records; connected WSL login vaults and journals |
| `%LOCALAPPDATA%\CodexWeeklyUsageIndicator.ClaudeProfiles` | Non-secret profile metadata and additional official Claude Code profile directories |

Claude profile metadata contains names, directory paths, selected profile, a hashed identity, masked account hint, subscription type and verification time. The official Claude CLI can store its own credentials and account-specific configuration inside each profile directory. **This application does not read, print, log, copy, persist or transmit Claude authentication tokens.**

There is no usage timeline or per-person attribution. Saved Codex accounts retain their latest successful observation. The combined Codex observation set and per-profile Claude observations live in memory, separately from the widget's active-account polling.

The Windows startup task stores the local executable path and Windows user SID. It uses normal user privileges and no stored password.

## Codex credentials and usage

Optional Codex account management reads local `auth.json` for explicit registration, switching, account verification and registered-account usage checks. Saved credentials and recovery journals use Windows CurrentUser DPAPI with restricted file permissions. They are never sent to this project's developers. DPAPI protects data at rest; it does not protect against other software already running as the same Windows user.

Additional Windows accounts sign in through the official Codex client in a restricted temporary home. Inactive-account usage checks also use a private temporary `CODEX_HOME`; they do not replace the active login. If the official client refreshes authentication, the updated credentials are saved after the helper exits, including when the usage query fails or is canceled. Temporary plaintext files are removed after verified persistence. An encrypted journal retains recoverable state if that process is interrupted.

Credential recovery compares the live account before applying saved state and does not overwrite a newer, different sign-in. If credentials are already saved and only file cleanup remains, cleanup can retry without changing authentication. Cleanup failures retain a safe category and numeric code rather than raw exception text or paths in the journal.

## WSL Codex

WSL support is opt-in for running distributions with an existing native Linux Codex CLI and supported personal file-based authentication. A connection identifies the distribution, Linux user and Codex home. Each saved Windows account has independently registered WSL credentials, verified against the same account identity; the tool does not share whole Codex homes or copy Windows credentials into Linux as a shortcut.

WSL authentication data passes through private process pipes to the account-management code and encrypted Windows vault. It is not placed in command arguments, environment variables or diagnostic output. Linux replacements use private files, stopped-writer checks and verification through a new official client. The tool does not install Codex or kill existing jobs. Isolated additional sign-in can run while existing WSL jobs continue; capturing or replacing the live login requires those jobs to stop.

Windows and WSL updates use durable recovery journals because separate filesystems cannot commit atomically. **Stop WSL linking** disables the selected links and archives the encrypted journal without writing, rolling back or deleting either environment's current authentication. Archived recovery records can still contain encrypted login data and are preserved with the account vault.

## Claude profiles and usage

Claude Code owns all sign-in, sign-out and token refresh. Additional profiles use separate `CLAUDE_CONFIG_DIR` locations; the original default profile keeps its existing home behavior. Sign-in runs through the unmodified official CLI. The app checks the official account-status response and stores only the non-secret metadata described above.

Existing local conversation, skill and related working directories stay in place and are shared with additional profiles through folder links. User instructions and settings are referenced by the official CLI. Supported local plugin registries are copied with only installation metadata; account-synced plugin state is not copied. Prompt recall history is linked when Windows file-link support allows it. The application does not duplicate the original Claude home or replace existing working files to create a profile.

Selecting a profile controls new Claude sessions launched from the account panel and the widget's Claude usage source. Plain `claude` commands, existing terminals and IDE sessions keep their own environment. The app does not install a global command shim or alter the machine-wide account selection.

Usage checks invoke Claude Code in safe mode without a shell, browser integration or session persistence, with `--max-turns 0`. The app rejects responses reporting a model turn or nonzero cost. Actual usage requests are at least ten minutes apart across profiles, including explicit refreshes and failures. Changing the displayed weekly metric makes no request.

The single recovery cache is bound to the verified profile and account. It cannot supply another account's values. A new process attempts a live usage request before using it for a transient failure. Each usage window is withheld after its own reset; the cache is invalid after 24 hours or when all windows expire. Authentication or response-format failures clear the affected cached data. Claude's own last-known usage output is treated as a delayed check rather than a new observation.

## Removal and bug reports

Uninstall removes the widget, its settings/cache and scheduled task. It intentionally preserves the separate Codex account vault, WSL recovery records and Claude profile directories. Removing a Claude account from the list removes its registration only; its official CLI login and working files remain. Use the profile's **Sign out** action to end its login before removal when desired.

Additional Claude profile directories contain links to original working data. Do not recursively delete the contents of linked conversation or skill folders when removing a profile. The original default Claude folder is not an application-owned cleanup target.

When reporting a bug, do not attach credential or configuration files, raw Codex/Claude logs, tokens, or screenshots containing information you do not want to publish.
