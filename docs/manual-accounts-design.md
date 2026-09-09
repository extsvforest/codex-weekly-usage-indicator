# Manual account switching decisions

The existing WinForms widget owns the small usage surface and opens a separate account manager. The user selects accounts explicitly. Inactive usage is a dated observation, not a background login or synthetic combined quota. The current widget geometry and Claude source/cache contracts remain intact.

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
