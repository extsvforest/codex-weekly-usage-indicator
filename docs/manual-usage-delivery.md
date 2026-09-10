# Manual account usage delivery — 1.6.0

The account manager can fetch a selected account's latest weekly and 5-hour remaining usage, reset times, and successful check time without switching the desktop login. List refresh remains local. No history, comparison, attribution, or inactive background polling is included.

## Verification (2026-09-10)

- Synthetic store tests cover inactive success, request failure and cancellation with rotated credentials; live auth byte preservation; selected-account observation binding; transaction exclusion; disk failure; staged identity mismatch; external activation; and interruption/recovery at durable boundaries. Repeated recovery is tested after another external login.
- App-server tests exercise initialized and canceled owned processes, account-read consistency, helper restart isolation, and parsing of a separate optional 5-hour window.
- UI tests run the actual WinForms message loop for one-click/one-request, busy actions, failure and cancellation, last-observation preservation, and zero helper suspension/resumption. Existing native focus, registration, rename, minimum-size scrolling, and scaled action-bound checks remain in the suite. Synthetic screenshots were inspected locally and are not published.
- An explicit opt-in live smoke invoked the real manager button with one inactive saved account while Codex Desktop remained running. It verified a new observation, byte-for-byte unchanged active authentication, unchanged active snapshot/account, zero suspend/resume callbacks, and removed query staging/journal after the official helper exited. No credentials, account identities, or usage values were printed or committed.
- Two inherited fork-team advisors checked authentication/persistence and UX/lifecycle/release. A separate fresh code reviewer inspected the candidate and its affected regression surfaces without implementing it. The review identified repeated-recovery durability and helper-start failure handling; fixes were adopted. Final code review recommended ready with no remaining confirmed blocker. The reviewer did not run live authentication or tests.

## Operational boundaries

Successful button-level live verification is engineering evidence, not a new user acceptance claim. The previous main-to-secondary switching acceptance remains separate. A new browser login was not required for this live query.

If an inactive login has expired, use account addition to sign into that same account again. If a query is interrupted before credential persistence/cleanup, the manager offers recovery; finish work and close Codex writers before running it. No recovery or query silently replaces active authentication.

## Release and rollback

The tag workflow builds and packages a draft release. Publish only after checking its EXE hash against SHA256SUMS and the ZIP's EXE, lifecycle scripts, and public documents. Install through the existing per-user interactive scheduled-task path, retaining final-path validation. Rollback can reinstall 1.5.1 after any pending query is recovered in 1.6.0; resolve the new query journal before downgrading because older versions do not understand it. The account vault keeps its existing version and supports older snapshots without a 5-hour field.
