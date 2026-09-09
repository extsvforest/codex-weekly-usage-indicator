# Manual Codex accounts — delivery board

Outcome: switch between the owner's paid Codex accounts manually from the existing Windows usage widget. No automatic quota switching, proxy routing, background inactive-account usage polling, or Claude credential handling.

Current artifact: branch `codex/manual-account-switch`, local candidate 1.4.1. Installed candidate SHA-256: `ADCC0CA9C2F396CC9E974909D30F75DE76D73A680A35C57BCB3D21F78FA8D903`. Checksum matched the build; one supervisor and one widget were verified. The management window is open for user-assisted registration.

| Owner | Write set | Status |
|---|---|---|
| Chair | Widget integration, account manager UI, docs, test harness | Local candidate installed; user acceptance pending |
| Auth worker | CodexAccountStore.cs, AccountStoreTests.cs | Complete |
| Runtime worker | CodexAccountRuntime.cs, AccountRuntimeTests.cs | Complete |
| Protocol worker | AppServerClient.cs, AppServerLifecycleTests.cs | Complete |

Two inherited advisors reviewed authentication recovery and desktop lifecycle. A fresh, preference-blind red-team inspected the actual source and verified fixes. It reported no confirmed remaining P0; this is source review, not proof of real account switching. No Claude deliberation was used.

Decisions: keep the 272 × 64 widget; use an optional separate management window and tray entry. Store inactive Codex credentials with Windows CurrentUser DPAPI outside the installation folder. The live auth file owns the active credential. Register additional accounts through official login in an isolated private home. Require Codex and other Codex engines to be closed before auth replacement; do not force-close the desktop. Persist an encrypted transaction before replacement and recover by actual identity, preserving refreshed credentials.

Verified: all 23 harness groups passed (including the optional synthetic UI capture when enabled), release build succeeded, binary contains no checked username/build path, scoped whitespace check passed. Synthetic tests cover interrupted journal phases, token rotation, third-account refusal, corrupt encrypted data, private ACLs, junction rejection, helper cancellation/restart, login Job ownership, and browser-child survival. Synthetic and installed management windows were observed; clipped controls found in the first synthetic render were fixed.

User feedback found two interaction failures: blank names blocked registration with an easy-to-miss status message, and the one-second visibility tick repeatedly assigned WinForms TopMost, interfering with input focus in the same-process manager. Version 1.4.1 allows blank names with unique defaults, preserves an existing current-account name, shows preparation/completion feedback, removes the repeating TopMost setter, and adds WS_EX_NOACTIVATE to the widget. The existing SWP_NOACTIVATE topmost maintenance remains.

The UI regression now actually clicks the registration button with a blank name, verifies the stored account and immediate list update, and checks repeated/explicit-name behavior. All 23 test groups and the release build passed. The installed manager subsequently showed a registered current account and its usage. Current Desktop has not been closed or switched. Next: user confirmation of normal typing/focus, second-account OAuth, then user-initiated A→B→A acceptance.

Release: local candidate installed, public publication not requested. A prior executable backup exists in the session's temporary workspace for rollback. Real switching must be initiated outside the active implementation session. Do not claim production acceptance until the actual account cycle is observed.
