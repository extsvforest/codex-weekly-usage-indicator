# Agent guide

This repository contains a small Windows WinForms utility with optional WSL Codex integration. Keep changes focused and dependency-free unless a new dependency is clearly necessary.

## Architecture

- `UsageIndicatorForm` owns widget lifecycle, drawing, polling and interaction. `AccountManagerForm` contains the Codex and Claude account surfaces.
- `CodexDesktopStateReader` detects the packaged Codex Desktop host. `AppServerClient` runs the local official `codex app-server --stdio` and speaks JSONL. Codex weekly usage is the returned window closest to seven days.
- `CodexAccountStore` owns the encrypted Windows login vault and recovery; isolated account queries are separate from active widget polling and the manager's combined observation set.
- `ClaudeAccountController` binds verified profile identity, new managed launches and widget usage. `ClaudeProfileStore` holds non-secret metadata; `ClaudeProfileRuntime` invokes the unmodified official CLI and shares existing local working data.
- `ClaudeCodeUsageSource` parses official `/usage` output. `ClaudeUsageClient` owns the shared request throttle, caching and backoff; `ClaudeDisplayMetric` selects overall or Fable weekly without issuing a query.
- `WslCodexBridge` probes and operates supported Linux targets. `WslCodexService` owns per-target encrypted login bindings and journals; `WslCodexSwitchCoordinator` handles linked Windows/WSL progress, recovery and explicit abandonment.

## Authentication and data invariants

- Never read, print, log, copy, persist or transmit Claude authentication tokens. Official Claude Code owns sign-in, sign-out and refresh. Do not add a Claude token vault, OAuth implementation, proxy or global CLI shim.
- Preserve the original Claude home and its default global-config behavior. Additional profiles have separate official authentication; existing skills, projects and other working data stay in place. Never share the whole home or blindly copy account configuration. Preserve existing files if a link or registry is unsupported.
- One verified profile selection owns new panel-launched Claude sessions and widget usage. Existing terminals, IDE jobs and plain `claude` invocations remain unchanged. Shared launch settings and safe-mode usage must resolve to the same subscription identity. Never return an old profile/account observation under a new selection.
- Codex login replacement requires explicit user selection and stopped writers. Keep current live credentials authoritative during recovery; do not overwrite newer or different authentication with an old snapshot.
- Discover only running WSL distributions on an explicit environment check. Require an installed native Linux CLI, identified Linux user/home and supported file-based auth. Never force-install a missing CLI or rewrite unsupported managed/keyring settings.
- Save independent WSL logins for each bound account and require a binding for the live source before switching. Isolated login preparation may run while jobs continue; live capture/replacement must reject running or unidentifiable writers. Do not kill user jobs.
- Keep WSL credentials out of arguments, environment variables and diagnostics. Use private pipes, restricted Linux files, encrypted Windows vaults, conditional replacement and actual account verification. Windows/WSL cannot commit atomically; retain durable per-target and coordinator journals across partial failure.
- Explicitly abandoning a WSL link must archive encrypted recovery records and disable linked switching without writing either environment's authentication. Uninstall preserves the separate Codex vault, WSL journals and Claude profile directories. Removing a Claude list entry must not delete its files.

## Usage and presentation invariants

- Do not add telemetry or direct provider usage network calls. Do not commit absolute local paths, real-account screenshots, pet assets, build output or credentials.
- Treat `account/rateLimits/read` and Claude Code `/usage` text as changeable contracts; fail gracefully without substituting a different limit.
- Run Claude usage in safe mode without a shell, browser integration or session persistence and with `--max-turns 0`. Require `num_turns == 0` and `total_cost_usd == 0`. Official last-known output must not become a fresh observation.
- Actual Claude usage calls must remain at least ten minutes apart across profiles, including manual refreshes, failures and cancellations. Metric selection reuses the snapshot. A new install defaults to overall weekly; existing settings without a metric preserve Fable.
- Keep the enabled Claude half visible when data is missing or fails. The selected window alone drives its number; show `—` when absent or expired. Parse and expire five-hour, overall-weekly and Fable-weekly windows independently. Share rounding across widget, tooltip and copy text.
- Persist at most one credential-free Claude recovery snapshot containing percentages, resets, check time and an opaque profile/account key. Reject another identity's cache; use matching data only after a transient live failure, expire it after 24 hours or when all windows expire, and clear it on authentication/schema failure. A saved snapshot must not suppress the first live request in a new process. Cached in-memory observations obey the same identity and expiry boundaries.
- Opening a new manager performs one sequential Codex batch; only explicit full refresh starts another. Active polling and individual reads do not silently certify a new aggregate. Missing, expired, failed or canceled rows withhold the full total.
- Preserve Korean/English coverage, the approved light paper design, stable first paint and tooltip hover, keyboard navigation and three-account fit. A synthetic HTML reference alone does not prove the native UI; inspect native captures at supported DPI settings.
- Stop the widget's Codex app-server child when the widget pauses or exits. Preserve singleton behavior and topmost tool-window presentation without keyboard focus or foreground changes. Do not restore topmost through an activating WinForms `TopMost` assignment.

## Install lifecycle

- Launch `--supervise` through the per-user interactive, least-privilege scheduled task, never directly from Codex. The supervisor retries abnormal exits/start failures only; normal Close ends supervision. Do not replace this loop with Task Scheduler RestartOnFailure.
- Disable/stop the task before upgrade or uninstall; filter processes by the current user's exact installed EXE path. Coordinate lifecycle scripts with in-progress account operations.
- Check the install directory's final handle path before stopping tasks or processes: packaged shells can redirect AppData without reporting package identity. Package `install-environment.ps1` with both lifecycle scripts.

## Validation

Run:

```powershell
.\scripts\build.ps1
```

Keep regression coverage for:

1. Settings migration, metric selection without a query, independent window expiry, consistent rounding, global request spacing and profile-bound recovery values.
2. Claude official process arguments, identity checks, shared-data preservation and default-home semantics. Fixture tests do not establish a successful real second-account login or launch.
3. WSL missing/unsupported environments, busy writers, independent login binding, partial failure, restart-safe recovery, identity mismatch and authentication-write-free abandonment.
4. Native account/tooltip UI at supported scales and both languages: stable first paint, preserved focus, selectable controls and no clipped primary actions. Preserve the existing `AccountUiSmoke` focus/topmost and hover regressions.
5. Installer/supervisor ownership: one supervisor and one widget; abnormal widget exit recovers after about a minute; normal Close ends both; uninstall removes the task before the app and preserves separate account/profile data.
6. Release artifact inspection: no username or absolute user-profile build paths, credentials or real-account screenshots. Report actual validation boundaries; do not describe synthetic tests as completed live account switching.
