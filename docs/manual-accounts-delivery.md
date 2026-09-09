# Manual Codex accounts — delivery board

Outcome: manually switch between the owner's paid Codex accounts from the existing Windows usage widget. Keep the 272 × 64 widget; account management uses a separate optional window. No automatic quota switching, proxy routing, inactive-account polling, or Claude credential handling.

Release candidate: **1.5.1**, branch `codex/manual-account-switch`. Locally installed validation build SHA-256: `797F4309994B4C18753CC2E2FB24106A2BEC6020117D340EBD3B98048C05BBC0`, from code revision `1d28dcb6f111dc7a197647cfcbfa812ca9323067`. Installation verified one supervisor and one widget. On 2026-09-09 the user authorized recording the acceptance result and publishing a GitHub release. Public binaries are built by GitHub Actions; release checksums identify those artifacts separately from this local build.

## UX outcome

The user rejected the first manager's rough button layout and requested wireframe-led refinement, including name editing. The replacement uses a left account list and right details with status, usage and account-specific actions. A name editor supports Enter, Escape and inline validation without restarting the helper. Following the user's overlap report, version 1.5.1 removes account names from the compact indicator and keeps them in the manager. Additional login has a separate explanation/name step and cancellation. Switching has a source/target preparation dialog whose confirmation becomes available after Codex writers stop, then rechecks immediately before acceptance. External unregistered current accounts can register above an existing list. Expired usage is marked for refresh. Smaller windows scroll the details.

The earlier blank-name registration and repeating TopMost assignment were corrected in 1.4.1. This revision also fixes WinForms focusing a topmost form when it reappears: native topmost styles and SWP_NOACTIVATE maintain z-order without setting the managed TopMost property. See the upstream [Form.SetVisibleCore behavior](https://github.com/dotnet/winforms/blob/v8.0.0/src/System.Windows.Forms/src/System/Windows/Forms/Form.cs).

## Verification and review

- All 23 regression groups passed and the release build succeeded. The binary scan found no checked personal username or absolute build path; whitespace validation passed.
- Synthetic UI tests click registration, rename current/saved accounts, cancel editing/addition, reject invalid names, check Enter/Escape wiring, follow switch readiness and recheck on confirmation, and register an externally changed current account. Minimum-size scrolling exposes lower actions. Native keyboard focus and foreground remain unchanged across widget show, hide/re-show and repeated maintenance; the widget remains topmost.
- Synthetic WinForms renders at 175% scaling were inspected. Clipped dialog columns/buttons were corrected. No real-account screenshots are committed.
- A fresh source-only reviewer identified two P1 issues: registration after external login, and clipped lower actions at minimum size. Both were fixed and covered by the UI harness. The reviewer accepted the fixes and found no new confirmed P1. A reported old capture discrepancy was absent in the chair's final capture readback.
- Earlier authentication/lifecycle review and tests cover private DPAPI storage, interrupted encrypted transactions, live credential rotation, third-account refusal, corrupt data, private ACLs, reparse rejection, helper generations, owned login cancellation and browser-child survival. These remain synthetic/source checks; the user acceptance below covers the actual account switch and subsequent use.
- On 2026-09-09 the user reported successfully switching from the main account to the secondary account and using Codex without issues, and explicitly approved the experience. This is user-reported acceptance of the installed 1.5.1 flow, not an agent-observed round trip. Secondary-to-main switching has not yet been reported.
- The nonactivating topmost requirement is now explicit in `AGENTS.md`, linked to the existing native-focus regression coverage.
- Installation completed through the existing least-privilege supervisor task with final-path safeguards. The separate temporary install task was removed afterward. Final installed-screen inspection was stopped by the user's physical Escape key before a fresh UI snapshot was obtained.

## Wireframe evidence

Mode: `standalone`. Canonical structural drafts: `wireframes/02_accounts.manager.yaml` and `wireframes/03_accounts.switch.yaml`. The actual WinForms implementation and its synthetic fixture renders are derived review surfaces. Exact reconstructable revisions are the Git blobs committed with these files; retrieve them with `git rev-parse HEAD:wireframes/02_accounts.manager.yaml` and the corresponding switch path.

Wireframe assurance: **exploratory** for the structural YAML, which has not received a separate exact-revision structure review. The installed account-manager flow has user acceptance as recorded above; this does not imply full visual or accessibility certification. The layout prioritizes account identity and available actions, at the cost of extra selection compared with showing every account's full controls at once. Fixed user decisions are manual switching, widget integration, editable names and no Claude/Fable deliberation. Acceptance applies to the revised installed flow, not the rejected first manager.

Carryover owner: user — confirm secondary-to-main switching when next needed. Second-account registration, main-to-secondary switching and subsequent Codex use are confirmed by the user. The implementation agent must not close the running desktop to test switching. Full screen-reader behavior, all monitor/DPI combinations and official service-policy acceptance are not certified.
