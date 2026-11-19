# Unified account manager

The September 28 upgrade combines the approved compact account-table layout with the warm paper palette, adds a selectable Claude weekly metric, and connects manual Claude profiles and installed WSL Codex environments.

## Preview 7: Claude session lists across accounts (2026-10-01)

The desktop app lists Code sessions per account and organization (`claude-code-sessions\<account>\<org>`), while every account shares the same transcripts. Signing in with another account therefore showed an empty list. The earlier plan of swapping whole app-data folders per account is withdrawn: it split the lists further and held login state in utility folders. That work is kept only on the local branch `codex/desktop-profile-swap-wip`.

Decision (reviewed with Codex before and during implementation): sign-in stays in the official app, and the widget adds missing list entries only while Desktop is closed. Invariants are in `AGENTS.md`. Manual Claude "switch" and **Open Claude…** are removed; **In use** now reflects the account the running app confirmed in its log.

Two implementation review rounds tightened the first build: the plan is rebuilt immediately before publishing and Desktop is checked before every file; an uninspectable process never counts as closed; a gap in the app log drops the confirmed account, and the log file is recognized by its file identity because rotation keeps the old creation time; a malformed writer registration of a live process holds writes; a reopen request belongs to one closure; the widget shows no number while the app is signed out, switching or on an unregistered account; and the compatibility guard checks the data flow of the app's deletion protection, not only its strings.

Live evidence on this PC (app 2.16120.0.0): a one-shot script outside the app waited for Desktop to quit, added 88 entries to the second account's list and reopened the app. The app then logged `Loaded 88 persisted sessions` for that account. One conversation was continued under the second account, then under the first account's original entry after signing back in; the second account's turns were present. Deleting a session in one account while another account still lists it was checked only in the app code, not live. The installed build was then checked live on 2026-10-01 (installed at 02:07 from outside the app package by a one-shot scheduled task). While Desktop ran, the account page showed **In use** on the first account, the widget showed that account's usage, and no reopen was offered because nothing was missing. Five seconds after Desktop quit, the widget filled: one session created after the spike was added to the second account's list (89 entries, confirmed in the ledger, nothing held), and the first account's 104 records were untouched. The reopened app loaded its list normally, **In use** returned, and the footer read "All accounts filled" with the fill time. The reopen request and following an account switched inside the app were not exercised live; fixture tests cover them.

Known limits: the main row's usage profile is the default Claude home, so signing the terminal CLI into another account still shows "different account" until it is signed back in. Utility profiles created earlier keep their folder links; nothing launches sessions with them now.

## Implementation boundary

- Codex and Claude remain separate provider pages. Percentages from different providers are never added together.
- Claude Code owns login, logout, and refresh. The app keeps only profile metadata and sanitized usage. Existing skills and project conversations stay in their original directory and are referenced by new profiles. Selection applies to new managed launches; already running jobs keep their account.
- WSL support checks for an installed native Linux Codex and a supported file-auth environment. It never installs a CLI or kills user jobs. Each environment has its own saved login; Windows credentials are not repeatedly copied to Linux.
- A switch involving several environments is recoverable, but cannot be one filesystem-atomic operation. The panel must retain and explain a partial result rather than report global success.
- Overall weekly and Fable weekly are alternative displays of the same Claude snapshot. Changing the display does not request usage or change the model.

## Review and validation

The candidate is version `1.9.0-preview.6`. The approved compact table and two-column summary are implemented as native controls with the warm paper palette and texture. Codex and Claude have their own pages; inactive profiles never contribute to the wrong provider's number. Claude's summary shows the selected profile's overall weekly balance and reset time, above its account list; it does not combine accounts or query every profile automatically.

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

An additional managed Claude profile is now registered, and its live usage reads are verified in the preview 4 and 5 checks below. Launching and resuming a real conversation through that profile remains an acceptance check. A real WSL account switch also remains unverified; finish running WSL Codex work before attempting it, since the app never stops that work automatically. The installed previews have not been publicly released.

Native visual evidence uses synthetic data: [Codex](design/2026-09-28/100/manager.png), [Claude](design/2026-09-28/100/claude.png), [three Claude accounts at 175%](design/2026-09-28/175/claude-states.png), [one account before its first check](design/2026-09-28/175/claude-single.png), and [English](design/2026-09-28/en-175/claude.png). The original Codex and Claude work remains under the existing accounts; no live switch was performed for these captures.

The local normal-user installation was verified on September 28 from implementation commit `34fc4b8`. The installed `1.9.0-preview.2` executable matches SHA-256 `1fd68470ae73704be8572c4bc025d0d7fa75e598c3f89cc47d1c5fc6bdfe44b9`. The normal-privilege scheduled task runs one supervisor and one widget. The existing Claude profile remained selected, and a fresh identity-bound overall weekly observation was saved after restart. The temporary installer task was removed; previous executables remain available locally for rollback. This is a local candidate, not a GitHub release.

## Preview 3: missing usage and consistent tabs

An unavailable Claude login previously cleared the number but left the account page saying only that usage had not been checked. The controller now retains the failure for each profile and publishes it to both surfaces. A missing CLI login and a different signed-in account have distinct messages and an official sign-in action. Invalid status data is not classified as an expired login. Connecting the default account again preserves its original identity and alias; it cannot silently replace that row with a different account.

Codex and Claude now share the header refresh control and checked-time placement. Their summaries, table columns, account rows, add-account actions and footer follow the same dimensions. Claude's per-account refresh and details are in its overflow menu; the launch action is in the footer. A successful fresh observation clears an earlier usage error only for the profile that recovered.

Native capture assertions passed at 100% and 175% scaling, including English, three-account layouts, missing login, changed identity and recovery from a usage failure. English row actions use a short label that fits the shared button width. The separate pointer-driven hover check could not establish a continuous hover because the cursor moved during the attempt; it is not recorded as a new pass. The previous preview's hover result remains historical evidence only.

An Opus 5.5 review checked the authentication changes in two rounds. Its adopted findings are covered by tests for refusing a different default identity, preserving a recoverable error after canceled login, clearing authentication errors after successful verification, and classifying official command authentication failures consistently. The final round found no remaining defect in those protections. Official normal and safe-mode identity verification passed again on the normal Windows user environment.

The first local preview 3 installation from `840738f` still could not obtain usage after the user restored the correct CLI login. Inspection of official CLI 2.1.283 showed that the app's blanket nonessential-traffic restriction also blocked the usage endpoint. The app now disables telemetry, error reporting and automatic updates individually instead of forcing that blanket restriction. An explicit restriction inherited from the user's environment is preserved.

The usage command now requests verbose JSON and checks both the final zero-turn, zero-cost result and the assistant's structured quota report. This rejects a newer CLI fallback that prints old percentages without a last-known label but omits structured limits. Local session-behavior percentages never become quota balances. The official CLI can reuse a successful observation less than 60 seconds old; the app's displayed check time does not certify a new endpoint request. An additional Opus review found no blocking defect in this repair. Regression fixtures cover the traffic settings, fallback rejection, malformed reports and retained turn/cost guards. Installation and live recovery are checked separately from those fixtures.

The corrected local installation was verified on September 28 at 14:49 KST from commit `c5062cd`. Its executable matches SHA-256 `9c91944b2cdf728f58000322b3fc05089e05a4aca4ea48d958caf8ed5d70af70`. All 37 non-pointer regression groups passed, and the native opening trace reported no violations. The actual installed app obtained overall and Fable weekly limits bound to the original selected account; its account page displayed the current balance and reset time without a failure notice. The normal-user environment had no inherited blanket traffic restriction. One supervisor and one widget are running, the disposable installation task was removed, and the prior executable is retained locally for rollback. This remains a locally installed preview, with no public release in this step.

## Preview 4: Claude account menu actions

Closing the Claude overflow menu could terminate the widget with an `ObjectDisposedException`: its `Closed` handler disposed the menu while WinForms was still hiding it. Rename and manual usage actions shared that path. The panel now owns the menu until the next opening or panel disposal. Rename also handles storage failures within the panel instead of allowing an unhandled exception.

Both providers use **In use** and **Switch** for account state and actions. Claude's footer explains that the choice applies to new sessions opened from the panel; existing sessions retain their account. Native interaction coverage opens and closes the actual menu, saves active and inactive profile names, cancels a rename, simulates a blocked metadata write and queries the inactive profile without changing the selection. A separate opt-in check exercises the same controls with an already registered real additional Claude profile.

All 38 non-pointer regression groups passed. The opt-in native check obtained a fresh weekly observation for the actual registered additional account while preserving the selected account and both aliases. Preview 4 was installed locally from `29feff5` on September 28 at 15:18 KST. The executable matched SHA-256 `1d4798cf8e5018018d4594abbed549923eb2b6e7c4f95b0f1d50833844080e3e`; the installed app obtained a fresh main-account observation and displayed the unified state labels. This was a local preview, not a public release.

## Preview 5: independent Claude account refresh intervals

The user approved changing the ten-minute interval from one global timer to a timer for each verified account. A main-account lookup no longer blocks the first manual lookup of an additional account. Duplicate profiles with the same official account identity share their interval and transient-failure backoff. Manual refreshes reuse each profile's accepted observation during the interval; failures and cancellations still consume that account's request slot. The timer key is passed separately from the profile-specific cache key, so account throttling does not loosen observation isolation.

The ten-minute value is the widget's conservative polling choice, not a published Claude requirement. Other profiles remain manual-only. Existing credential handling, selected-account behavior and profile-bound recovery storage are unchanged.

All 38 non-pointer regression groups passed after the change. New checks cover consecutive main/additional-account reads, duplicate profiles, cached repeat reads, the ten-minute boundary, longer shared failure backoff, cancellations and client recreation. Controller coverage also verifies that a changed identity cannot inherit its previous account's observation when the new usage request fails.

Preview 5 was installed locally from `b2e3e67` on September 28 at 15:36 KST. Its executable matches SHA-256 `f42714733af3724c3174dc035cc4b44df8686772521650adc755c38b32af68ce` and passed the private-build-path scan. The installed app obtained the selected account's usage at 15:36 and the additional account's usage at 15:43, within the same ten-minute interval. Both balances were visible together in the native manager. Repeating the additional-account action reused its observation, the selected account and aliases stayed unchanged, and saving an existing alias through the installed rename dialog succeeded. One supervisor and one widget remain running; disposable verification tasks were removed. This remains a local preview with no public release in this step.

## Preview 6: consistent account-page language

Both tabs use **Weekly remaining**, **Refresh**, and **Last updated · Today HH:mm**. The line below the balance identifies its scope: all Codex accounts combined or the selected Claude account. The underlying read scope and per-account request interval stay unchanged. Healthy account rows no longer repeat observation times; hover details and account details retain them. Failure, expired-data and incomplete-total states remain explicit.

Refresh and session-launch explanations live with their respective actions instead of permanent footer sentences. Both overflow menus use **Refresh usage**. A shared observation-time formatter keeps the Korean and English pages aligned. Native fixtures check the shared labels, quiet healthy rows, long aliases and authentication-recovery states.

All 38 non-pointer regression groups passed. Native previews passed in Korean and English at 100% and 175% scaling, including the shared-copy assertions and existing recovery/long-name checks. The live-pointer check was attempted but could not certify hover stability because the physical cursor moved; it is not counted as a pass. Final synthetic examples: [Codex](design/2026-09-28/copy-preview6/codex-ko.png) and [Claude](design/2026-09-28/copy-preview6/claude-ko.png). Real-account captures remain private.

Preview 6 was installed locally from `e89110c` on September 28 at 16:31 KST. The installed executable matched SHA-256 `f3726f6336d09c530057c555855a5fdc6bdb3512382d4002b4d95596f48b20e4` and passed the private-build-path scan. Read-back of both installed tabs confirmed the same summary title and observation-time format, with no healthy-row timestamps or permanent explanatory footer. One manual refresh restored the additional Claude account's display after restart; the selection was preserved. One supervisor and one widget are running, and the disposable stop/install tasks were removed. This is a local preview, with no public release in this step.

## Profile sharing details

Local project conversations, skills and supported working directories are referenced in place. Authentication and account-specific runtime files remain separate. Removing a profile from the manager does not delete its files or the linked original directories. Ordinary terminal `claude` continues to use its existing configuration; the selected extra profile is used by **Open Claude** in this panel and by the widget.

Additional profiles pass the original user settings file through Claude's official `--settings` option. This keeps the file in place without reading its values, but CLI-supplied settings can take precedence over project or local settings. Local plugin registration is initially prepared rather than continuously synchronized. Prompt recall history and instruction files use Windows file links where available; project conversation directories remain shared even if those optional file links are unavailable.
