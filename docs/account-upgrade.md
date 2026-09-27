# Unified account manager

The September 28 upgrade combines the approved compact account-table layout with the warm paper palette, adds a selectable Claude weekly metric, and connects manual Claude profiles and installed WSL Codex environments.

## Implementation boundary

- Codex and Claude remain separate provider pages. Percentages from different providers are never added together.
- Claude Code owns login, logout, and refresh. The app keeps only profile metadata and sanitized usage. Existing skills and project conversations stay in their original directory and are referenced by new profiles. Selection applies to new managed launches; already running jobs keep their account.
- WSL support checks for an installed native Linux Codex and a supported file-auth environment. It never installs a CLI or kills user jobs. Each environment has its own saved login; Windows credentials are not repeatedly copied to Linux.
- A switch involving several environments is recoverable, but cannot be one filesystem-atomic operation. The panel must retain and explain a partial result rather than report global success.
- Overall weekly and Fable weekly are alternative displays of the same Claude snapshot. Changing the display does not request usage or change the model.

## Review and validation

The candidate is version `1.9.0-preview.2`. The approved compact table and two-column summary are implemented as native controls with the warm paper palette and texture. Codex and Claude have their own pages; inactive profiles never contribute to the wrong provider's number. Claude's summary shows the selected profile's overall weekly balance and reset time, above its account list; it does not combine accounts or query every profile automatically.

An Opus review during implementation found recovery paths that could lock the manager, a missing WSL-only action for the current Windows account, and unnecessarily blocked isolated logins. The candidate now offers explicit WSL unlinking that preserves live authentication, archives encrypted recovery data, and disables linked switching. Unowned recovery records cannot apply a login. A durable marker distinguishes preparation from an attempted write. Rotated source credentials are preserved in the encrypted vault before a recovery record is archived, without replacing newer stored credentials.

An independent Claude-controller review reproduced and then verified fixes for two display failures: authentication rejection now invalidates the widget immediately, and another account's login or a temporary status failure keeps only an unexpired observation bound to the same identity. Explicit cancellation remains cancellation.

Connecting the already active default Claude account now keeps its accepted usage and client cache. A newly verified identity still invalidates the previous account's values. Regression fixtures cover first registration, repeated registration, identity changes, and the absence of an additional usage request.

Verification completed before installation:

- All 37 regression groups passed on the final candidate, including the four-corner tooltip hover and foreground-focus checks. Opening-layout tracing observed 78 events with no violations.
- Synthetic native previews at 100% and 175% scaling, including English, checked control bounds and were visually inspected. Fractional-DPI row sizing and the WSL dialog's initial scaling were corrected.
- The completed Claude page was checked with one and three profiles, long names, stale values, missing usage and missing reset times. Three rows fit without scrolling. Updating usage or reloading metadata preserves existing row controls and focused actions.
- The installed official Claude CLI reported matching normal and safe-mode subscription identities. No tokens were read or printed.
- A read-only WSL probe found a native Codex installation and active writers, so no real switch was attempted. Synthetic fixtures exercise missing CLI, rotation, cancellation, crashes, third-account preservation and encrypted recovery abandonment.
- The production Claude launch method was exercised through disposable Windows scheduled tasks with a harmless child process. The child survived task stop, normal widget/supervisor exit, and the uninstall stop/kill sequence. Test tasks and processes were removed.

There is currently no second Claude account available. Interactive login of an additional account and resuming a real conversation through that profile remain future acceptance checks. A real WSL account switch also remains unverified; finish running WSL Codex work before attempting it, since the app never stops that work automatically. No public release or successful live cross-account verification is claimed here.

Native visual evidence uses synthetic data: [Codex](design/2026-09-28/100/manager.png), [Claude](design/2026-09-28/100/claude.png), [three Claude accounts at 175%](design/2026-09-28/175/claude-states.png), [one account before its first check](design/2026-09-28/175/claude-single.png), and [English](design/2026-09-28/en-175/claude.png). The original Codex and Claude work remains under the existing accounts; no live switch was performed for these captures.

## Profile sharing details

Local project conversations, skills and supported working directories are referenced in place. Authentication and account-specific runtime files remain separate. Removing a profile from the manager does not delete its files or the linked original directories. Ordinary terminal `claude` continues to use its existing configuration; the selected extra profile is used by **Open Claude** in this panel and by the widget.

Additional profiles pass the original user settings file through Claude's official `--settings` option. This keeps the file in place without reading its values, but CLI-supplied settings can take precedence over project or local settings. Local plugin registration is initially prepared rather than continuously synchronized. Prompt recall history and instruction files use Windows file links where available; project conversation directories remain shared even if those optional file links are unavailable.
