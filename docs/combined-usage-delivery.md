# Account overview and light interface — 1.7.0

The account manager now has one light overview and one account list, ordered by weekly reset. The user rejected the additive two-pane layout, requested a light palette and readable multi-account hover details, and asked for three accounts to fit when the window opens. Default size is 920 × 740 logical pixels, bounded by the available screen. Small windows retain scrolling.

## Behavior

- Each newly opened manager runs one sequential full query. Refocusing the same window, selection, local UI timers and hovering do not query inactive accounts. The full refresh button explicitly repeats the batch.
- The overview sums percentages without averaging: three accounts at 63%, 81%, and 24% show 168% out of 300%. Each account contributes a 100% unit; different plans are not normalized to a common token quota. Account switching remains explicit.
- A complete total requires a successful, newly persisted observation for every member, a seven-day window, future reset times, and unchanged account membership. Failures and cancellations retain prior observations with timestamps. An uncertain earlier reset prevents the header from claiming that another confirmed account is the next reset. The latest reset is context, not a common expiry deadline.
- One-account queries preserve other observations but withhold the total until the next full batch. Metadata updates preserve usage values. Active widget polling never changes the manager observation set.
- The widget retains that in-memory observation set after the manager closes. Its light, non-activating hover window separates current Codex usage, all saved accounts and their reset schedule, and Claude. Up to five accounts are listed before a link hint to the manager. There is no added usage-history file.
- A batch aborts remaining requests when credentials need recovery or temporary files still need cleanup. Closing an in-flight batch requests cancellation and waits for safe cleanup before closing the window. The existing encrypted vault and authentication transaction paths are retained.

## Validation and review

The required release build passes 29 regression groups, including native manager registration/rename/focus, manual usage failure/cancellation/cleanup, aggregate coverage/reset boundaries, sequential batches, duplicate suppression, no-op persistence rejection, close cancellation, tooltip freshness boundaries and the native tooltip's non-activating layout. Synthetic UI captures at the target 175% DPI cover normal, partial, in-flight and small-window states. The three-account default layout is asserted to have no vertical scrollbar.

An explicit opt-in live batch uses the production widget callback. Every registered account receives a new observation while the active authentication bytes remain unchanged, Codex Desktop stays running, no active-helper suspension occurs, and no query staging or journal remains. This is an integration check; it is not a claim that the user personally accepted every new screen.

Actual Claude Fable reviews examined the aggregate contract and implementation. Adopted findings include validating persistence time before certifying success, preserving actionable failures and previous observation times, handling an uncertain earlier reset, and stopping unresolved cleanup before querying further accounts. Individual queries deliberately do not certify a mixed observation set; this follows the requested full-refresh boundary.

## Delivery

The Windows artifact is built and tested by GitHub Actions. Verify the draft release checksums, ZIP manifest, binary version and absence of private build paths before installing through the existing interactive per-user scheduled-task installer. Verify the installed binary and supervisor/widget pair, then publish the verified draft. No vault schema migration is required. Resolve pending account recovery before rollback.
