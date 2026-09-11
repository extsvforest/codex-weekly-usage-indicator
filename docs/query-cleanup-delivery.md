# Usage query cleanup fix — 1.6.1

A successful manual usage refresh in 1.6.0 could leave a committed query journal when temporary-file deletion failed. The manager treated every remaining journal as authentication recovery, disabled account actions, and replaced the original error with a generic banner during reload.

## Diagnosis and decision

The reported installation had a committed encrypted journal, matching saved credentials, and a new usage observation. Staged authentication had already been removed; remaining files included SQLite and plugin startup artifacts. The original deletion error was overwritten, so its exact file or locking process is not established.

Two inherited reviewers examined transaction semantics and helper lifetime. A fixed staging home with separate pre-commit recovery and post-commit cleanup was selected. A unique-home cleanup queue would introduce additional credential-bearing directories and migration states without evidence that they are necessary. Disabling startup work alone cannot address unrelated transient file locks, so it accompanies bounded deletion retries and explicit descendant shutdown verification.

Committed cleanup never reloads or replays saved authentication, including after the target is renamed or removed. Query outcomes and cleanup notices are independent; committed credentials do not prove a successful usage read. Only pre-commit recovery requires all Codex writers to stop.

A fresh reviewer inspected the actual candidate without the chair's preferred conclusion and found no remaining P0. The review checked blocking-state classification, possible replay after account deletion, and descendant shutdown with file cleanup. The original locking cause remains unconfirmed.

## Validation

- The required release build passed all 25 regression groups, with no compiler warnings or errors. The release EXE passed the username and absolute build-path scan in UTF-8 and UTF-16.
- File-lock cases cover automatic retry, persistent cleanup on success/failure/cancel, original-result preservation, account mutation during cleanup, and rejection of a new inactive query until its fixed home is reclaimed.
- A fake app-server leaves an independently running child holding a staging file after the parent exits. Job shutdown and the production cleanup path must terminate the child and remove staging.
- Native manager tests check enabled actions, a separate cleanup button with no query/suspend/resume, and preservation of the original failure through the five-second reload. Existing focus and account recovery coverage remains in the suite.
- An explicit live upgrade check cleaned the existing 1.6.0 committed journal through the new manager button while Desktop remained running. Both live authentication and the encrypted vault stayed byte-for-byte unchanged, with no query or helper restart.
- A second explicit live check queried the selected inactive account through the manager. A new observation was saved, current authentication and active snapshot remained unchanged, Desktop stayed running, and the helper/staging/journal were removed.

## Release and rollback

Build the complete test suite, review the actual change, and verify the GitHub draft EXE against both SHA256SUMS and the ZIP before installation and publication. Keep the existing interactive per-user scheduled-task installer and its final-path guard. Version 1.6.1 reads existing version-1 query journals; resolve pending recovery or cleanup before downgrading. No vault schema migration is required.
