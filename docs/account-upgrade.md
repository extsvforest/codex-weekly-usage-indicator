# Unified account manager

The September 28 upgrade combines the approved compact account-table layout with the warm paper palette, adds a selectable Claude weekly metric, and connects manual Claude profiles and installed WSL Codex environments.

## Implementation boundary

- Codex and Claude remain separate provider pages. Percentages from different providers are never added together.
- Claude Code owns login, logout, and refresh. The app keeps only profile metadata and sanitized usage. Existing skills and project conversations stay in their original directory and are referenced by new profiles. Selection applies to new managed launches; already running jobs keep their account.
- WSL support checks for an installed native Linux Codex and a supported file-auth environment. It never installs a CLI or kills user jobs. Each environment has its own saved login; Windows credentials are not repeatedly copied to Linux.
- A switch involving several environments is recoverable, but cannot be one filesystem-atomic operation. The panel must retain and explain a partial result rather than report global success.
- Overall weekly and Fable weekly are alternative displays of the same Claude snapshot. Changing the display does not request usage or change the model.

## Review and validation

The candidate is version `1.9.0-preview.3`. The approved compact table and two-column summary are implemented as native controls with the warm paper palette and texture. Codex and Claude have their own pages; inactive profiles never contribute to the wrong provider's number. Claude's summary shows the selected profile's overall weekly balance and reset time, above its account list; it does not combine accounts or query every profile automatically.

An Opus review during implementation found recovery paths that could lock the manager, a missing WSL-only action for the current Windows account, and unnecessarily blocked isolated logins. The candidate now offers explicit WSL unlinking that preserves live authentication, archives encrypted recovery data, and disables linked switching. Unowned recovery records cannot apply a login. A durable marker distinguishes preparation from an attempted write. Rotated source credentials are preserved in the encrypted vault before a recovery record is archived, without replacing newer stored credentials.

An independent Claude-controller review reproduced and then verified fixes for two display failures: authentication rejection now invalidates the widget immediately, and another account's login or a temporary status failure keeps only an unexpired observation bound to the same identity. Explicit cancellation remains cancellation.

Connecting the already active default Claude account now keeps its accepted usage and client cache. A newly verified identity still invalidates the previous account's values. Regression fixtures cover first registration, repeated registration, identity changes, and the absence of an additional usage request.

Verification completed for the previous `1.9.0-preview.2` installation:

- All 37 regression groups passed on the final candidate, including the four-corner tooltip hover and foreground-focus checks. Opening-layout tracing observed 78 events with no violations.
- Synthetic native previews at 100% and 175% scaling, including English, checked control bounds and were visually inspected. Fractional-DPI row sizing and the WSL dialog's initial scaling were corrected.
- The completed Claude page was checked with one and three profiles, long names, stale values, missing usage and missing reset times. Three rows fit without scrolling. Updating usage or reloading metadata preserves existing row controls and focused actions.
- The installed official Claude CLI reported matching normal and safe-mode subscription identities. No tokens were read or printed.
- A read-only WSL probe found a native Codex installation and active writers, so no real switch was attempted. Synthetic fixtures exercise missing CLI, rotation, cancellation, crashes, third-account preservation and encrypted recovery abandonment.
- The production Claude launch method was exercised through disposable Windows scheduled tasks with a harmless child process. The child survived task stop, normal widget/supervisor exit, and the uninstall stop/kill sequence. Test tasks and processes were removed.

Interactive login of an additional managed Claude profile and resuming a real conversation through that profile remain future acceptance checks. A real WSL account switch also remains unverified; finish running WSL Codex work before attempting it, since the app never stops that work automatically. No public release or successful live cross-account verification is claimed here.

Native visual evidence uses synthetic data: [Codex](design/2026-09-28/100/manager.png), [Claude](design/2026-09-28/100/claude.png), [three Claude accounts at 175%](design/2026-09-28/175/claude-states.png), [one account before its first check](design/2026-09-28/175/claude-single.png), and [English](design/2026-09-28/en-175/claude.png). The original Codex and Claude work remains under the existing accounts; no live switch was performed for these captures.

The local normal-user installation was verified on September 28 from implementation commit `34fc4b8`. The installed `1.9.0-preview.2` executable matches SHA-256 `1fd68470ae73704be8572c4bc025d0d7fa75e598c3f89cc47d1c5fc6bdfe44b9`. The normal-privilege scheduled task runs one supervisor and one widget. The existing Claude profile remained selected, and a fresh identity-bound overall weekly observation was saved after restart. The temporary installer task was removed; previous executables remain available locally for rollback. This is a local candidate, not a GitHub release.

## Preview 3: missing usage and consistent tabs

An unavailable Claude login previously cleared the number but left the account page saying only that usage had not been checked. The controller now retains the failure for each profile and publishes it to both surfaces. A missing CLI login and a different signed-in account have distinct messages and an official sign-in action. Invalid status data is not classified as an expired login. Connecting the default account again preserves its original identity and alias; it cannot silently replace that row with a different account.

Codex and Claude now share the header refresh control and checked-time placement. Their summaries, table columns, account rows, add-account actions and footer follow the same dimensions. Claude's per-account refresh and details are in its overflow menu; the launch action is in the footer. A successful fresh observation clears an earlier usage error only for the profile that recovered.

Native capture assertions passed at 100% and 175% scaling, including English, three-account layouts, missing login, changed identity and recovery from a usage failure. English row actions use a short label that fits the shared button width. The separate pointer-driven hover check could not establish a continuous hover because the cursor moved during the attempt; it is not recorded as a new pass. The previous preview's hover result remains historical evidence only.

An Opus 5.5 review checked the authentication changes in two rounds. Its adopted findings are covered by tests for refusing a different default identity, preserving a recoverable error after canceled login, clearing authentication errors after successful verification, and classifying official command authentication failures consistently. The final round found no remaining defect in those protections. Official normal and safe-mode identity verification passed again on the normal Windows user environment.

The first local preview 3 installation from `840738f` still could not obtain usage after the user restored the correct CLI login. Inspection of official CLI 2.1.283 showed that the app's blanket nonessential-traffic restriction also blocked the usage endpoint. The app now disables telemetry, error reporting and automatic updates individually instead of forcing that blanket restriction. An explicit restriction inherited from the user's environment is preserved.

The usage command now requests verbose JSON and checks both the final zero-turn, zero-cost result and the assistant's structured quota report. This rejects a newer CLI fallback that prints old percentages without a last-known label but omits structured limits. Local session-behavior percentages never become quota balances. The official CLI can reuse a successful observation less than 60 seconds old; the app's displayed check time does not certify a new endpoint request. An additional Opus review found no blocking defect in this repair. Regression fixtures cover the traffic settings, fallback rejection, malformed reports and retained turn/cost guards. Installation and live recovery are checked separately from those fixtures.

## Profile sharing details

Local project conversations, skills and supported working directories are referenced in place. Authentication and account-specific runtime files remain separate. Removing a profile from the manager does not delete its files or the linked original directories. Ordinary terminal `claude` continues to use its existing configuration; the selected extra profile is used by **Open Claude** in this panel and by the widget.

Additional profiles pass the original user settings file through Claude's official `--settings` option. This keeps the file in place without reading its values, but CLI-supplied settings can take precedence over project or local settings. Local plugin registration is initially prepared rather than continuously synchronized. Prompt recall history and instruction files use Windows file links where available; project conversation directories remain shared even if those optional file links are unavailable.
