# Manual account switching decisions

The existing WinForms widget owns the small usage surface and opens a separate account manager. The user selects accounts explicitly. Inactive usage is a dated observation, not a background login or synthetic combined quota. The current widget geometry and Claude source/cache contracts remain intact.

## On-demand usage (1.6)

An explicit selected-account button reads weekly and optional 5-hour windows with the official `account/rateLimits/read` interface. List selection, list refresh, and the manager's five-second local refresh never query inactive accounts. The UI stores one successful snapshot/time, with no history, delta, or attribution. Failed and canceled requests retain that observation. The active account reuses the widget helper; an inactive account uses a separate private file-store `CODEX_HOME` without copying user configuration or passing tokens in arguments.

Inactive requests hold the existing process-wide mutex and vault file lock on one synchronous worker thread across the async protocol operation. The UI remains responsive, and the active helper continues polling; its optional vault cache write is skipped while the query owns storage. Switching, import, rename, removal, duplicate query, and installer replacement cannot interleave with the transaction.

Rate-limit reads can implicitly refresh authentication even with `account/read.refreshToken=false`. Query staging therefore has a separate encrypted journal, never the disposable `login-*` cleanup path. The owned helper uses a non-breakaway kill-on-close Job and must exit before credential read-back. Success, protocol failure, and cancellation all save its validated same-account credentials before removing staging. Shutdown uncertainty retains staging and journal. Before writing the vault, the chosen auth and expected prior digest are durably written to the encrypted journal; recovery can be interrupted repeatedly and the desktop can independently change accounts without reverting a committed refresh. Active live authentication remains authoritative and is never written by this path.

Crash recovery is explicit and conservatively requires all potential Codex writers to exit, accounting for the narrow process-start/Job-assignment interval. A pending query is announced on startup and blocks account mutations, while active-account usage polling remains available. Missing/corrupt credentials or inconsistent vault revisions preserve recovery evidence and fail closed.

## Authentication and recovery

The local live authentication file is authoritative for the active account. Every switch stops the widget's own app-server, checks for remaining native Codex writers, reads the latest source credential, writes an encrypted recovery transaction, saves the source, and atomically applies the selected credential. File replacement is read back. Saved auth JSON is treated as opaque data so future fields survive.

Recovery examines the actual identity: a source identity reconciles the before-vault, a target identity reconciles the after-vault, and either keeps any newer live tokens. Missing, malformed, or unrelated third-account authentication stops recovery without replacing the live file. A flushed auth temporary file is scoped to this transaction and removed through recovery. Profile identity combines user and workspace information rather than equating an email or workspace alone with a person.

The encrypted vault is separate from the installation folder. Windows CurrentUser DPAPI binds it to the Windows user. Private ACLs, non-reparse paths, bounded reads, atomic flushed writes, and a shared transaction mutex protect local operations. Same-user malware and unsupported external writers are outside this local mechanism's assurance.

Additional registration uses official `codex login` with an isolated private home and explicit file credential storage. No token is passed in a command argument or UI field. Login output is discarded. A private Job closes the owned login process if the widget exits, with descendant breakaway so an OAuth browser survives. A narrow process-start/Job-attachment crash interval remains; owned staging is recoverable after writers exit. Managed requirements and unsupported credential modes fail closed.

## Lifecycle and verification

The prior helper's shared pending requests and reader finalizer could affect a replacement helper. The new client owns state per process, serializes requests, and suspends new starts before cancellation and confirmed exit. The widget rejects both late successes and errors from prior account generations. A changed live identity creates a fresh helper. `account/read` metadata is checked before/after usage; it does not expose full workspace identity and is not represented as proof of the desktop's account.

The manager remains available from the tray when Desktop is closed. Version 1 requires the user to finish work and close Desktop/CLI engines before a switch; it does not force-close arbitrary processes or infer that a running task is idle. Applying the file and reopening Desktop are reported separately. A pending transaction prevents startup polling until explicit recovery.

Installer and uninstaller share the account-operation mutex across process replacement. Login holds it through credential import and staging cleanup, so an upgrade cannot normally interrupt that sequence. The existing least-privilege supervisor and install-path validation remain in place.

## Review disposition

Inherited auth/lifecycle advisors identified stale-token and reader/start races; those were addressed in the store and session client. A fresh critic then inspected the actual implementation without the chair's recommendation. Its valid legacy `auth_mode` finding was adopted using the official fallback contract while rejecting competing credential types. Its parent-exit/login lifetime finding was addressed with the shared UI mutex, Job ownership, and explicit staging recovery. Final actual-source review found no confirmed remaining P0.

The implementation has synthetic crash, process lifecycle, local ACL and UI checks. Those do not establish actual second-account OAuth or successful A→B→A Desktop use. User-assisted acceptance is tracked in the delivery board.

## Implementation references

- [OpenCodex native profile transaction](https://github.com/lidge-jun/opencodex/blob/2f3f736299dca38861f8fb9c4326a4b4d7c664bc/src/codex/native-profile-manager.ts): latest-source capture, durable journal, identity-based recovery.
- [Official Codex authentication storage at 0.153.4](https://github.com/openai/codex/blob/rust-v0.153.4/codex-rs/login/src/auth/storage.rs): file storage and optional mode contract.
- [Official Codex authentication](https://developers.openai.com/codex/auth/): official login and credential ownership.

This tool does not determine whether a particular account usage pattern complies with service terms. It implements explicit local profile selection rather than automatic quota-driven routing.
