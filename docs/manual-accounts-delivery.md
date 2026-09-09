# Manual Codex accounts — delivery board

Outcome: manually switch between the owner's paid Codex accounts from the existing Windows usage widget. Keep the 272 × 64 widget; account management uses a separate optional window. No automatic quota switching, proxy routing, inactive-account polling, or Claude credential handling.

Current candidate: **1.5.1**, branch `codex/manual-account-switch`. Installed SHA-256: `797F4309994B4C18753CC2E2FB24106A2BEC6020117D340EBD3B98048C05BBC0`. The installed executable matches the tested build, with exactly one supervisor and one widget. Public publication was not requested.

## UX outcome

The user rejected the first manager's rough button layout and requested wireframe-led refinement, including name editing. The replacement uses a left account list and right details with status, usage and account-specific actions. A name editor supports Enter, Escape and inline validation without restarting the helper. Following the user's overlap report, version 1.5.1 removes account names from the compact indicator and keeps them in the manager. Additional login has a separate explanation/name step and cancellation. Switching has a source/target preparation dialog whose confirmation becomes available after Codex writers stop, then rechecks immediately before acceptance. External unregistered current accounts can register above an existing list. Expired usage is marked for refresh. Smaller windows scroll the details.

The earlier blank-name registration and repeating TopMost assignment were corrected in 1.4.1. This revision also fixes WinForms focusing a topmost form when it reappears: native topmost styles and SWP_NOACTIVATE maintain z-order without setting the managed TopMost property. See the upstream [Form.SetVisibleCore behavior](https://github.com/dotnet/winforms/blob/v8.0.0/src/System.Windows.Forms/src/System/Windows/Forms/Form.cs).

## Verification and review

- All 23 regression groups passed and the release build succeeded. The binary scan found no checked personal username or absolute build path; whitespace validation passed.
- Synthetic UI tests click registration, rename current/saved accounts, cancel editing/addition, reject invalid names, check Enter/Escape wiring, follow switch readiness and recheck on confirmation, and register an externally changed current account. Minimum-size scrolling exposes lower actions. Native keyboard focus and foreground remain unchanged across widget show, hide/re-show and repeated maintenance; the widget remains topmost.
- Synthetic WinForms renders at 175% scaling were inspected. Clipped dialog columns/buttons were corrected. No real-account screenshots are committed.
- A fresh source-only reviewer identified two P1 issues: registration after external login, and clipped lower actions at minimum size. Both were fixed and covered by the UI harness. The reviewer accepted the fixes and found no new confirmed P1. A reported old capture discrepancy was absent in the chair's final capture readback.
- Earlier authentication/lifecycle review and tests cover private DPAPI storage, interrupted encrypted transactions, live credential rotation, third-account refusal, corrupt data, private ACLs, reparse rejection, helper generations, owned login cancellation and browser-child survival. This remains synthetic/source evidence, not a real account-cycle acceptance.
- Installation completed through the existing least-privilege supervisor task with final-path safeguards. The separate temporary install task was removed afterward. Final installed-screen inspection was stopped by the user's physical Escape key before a fresh UI snapshot was obtained.

## Wireframe evidence

Mode: `standalone`. Canonical structural drafts: `wireframes/02_accounts.manager.yaml` and `wireframes/03_accounts.switch.yaml`. The actual WinForms implementation and its synthetic fixture renders are derived review surfaces. Exact reconstructable revisions are the Git blobs committed with these files; retrieve them with `git rev-parse HEAD:wireframes/02_accounts.manager.yaml` and the corresponding switch path.

Assurance: **exploratory**. The account-centered split view and separate dialogs are agent sketches; the user has not yet accepted this exact candidate. The layout prioritizes account identity and available actions, at the cost of extra selection compared with showing every account's full controls at once. Fixed user decisions are manual switching, widget integration, editable names and no Claude/Fable deliberation. The new design does not inherit acceptance from the rejected manager.

Carryover owner: user — review the installed candidate, complete the second account's official browser login, then initiate A→B→A when this active Codex task can be closed. The current account's saved name is preserved. The implementation agent must not close the running desktop to test switching. Full screen-reader behavior, all monitor/DPI combinations, official policy acceptance and actual desktop login after switching are not certified.
