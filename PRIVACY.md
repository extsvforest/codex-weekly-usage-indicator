# Privacy

Codex + Claude Usage Indicator reads Codex rate-limit windows from the locally installed Codex app-server. It retrieves Claude's current 5-hour, weekly, and Fable limits by invoking the locally installed Claude Code `/usage` command. Claude Code, rather than this widget, owns authentication and token refresh.

The application:

- does not store a timeline of usage history; optional Codex account management retains each account's latest usage snapshot;
- stores the last window coordinates and the Claude visibility preference in a local `settings.json` file;
- stores one latest successful Claude snapshot in local `claude-usage-cache.json`, containing only usage percentages, reset times, and the update time;
- does not read, print, log, copy, or persist Claude authentication tokens;
- reads Codex account identifiers only after optional account registration, and stores them with labels and login snapshots in a Windows CurrentUser DPAPI encrypted vault;
- does not include telemetry;
- registers a per-user Windows logon/recovery task containing the local executable path and Windows user SID, with no stored password or elevated privileges;
- makes no direct outbound request for Codex usage; the official local app-server manages service communication;
- makes no direct Claude network request; it invokes `claude.exe` in safe mode without a shell or persistent session, caches successful `/usage` results in memory for ten minutes, and deletes the local recovery snapshot after 24 hours, its Fable reset, or an authentication/schema failure.

Codex Desktop, its local app-server, Claude Code, and Anthropic's API remain governed by their own terms and privacy practices.

Optional Codex account management reads local Codex `auth.json` for explicit registration, switching, and identity checks while refreshing registered-account usage. Replacement happens only on an explicit switch. Additional accounts use official Codex browser login in a restricted temporary home; temporary credentials are removed after import or cancellation. The separate account vault and recovery transaction are DPAPI encrypted and ACL restricted to the Windows user; the login staging directory also permits SYSTEM. They are never sent to this project's developers. DPAPI protects data at rest; it does not protect against other software already running as the same Windows user. The active Codex credential remains owned by the live authentication file: saved snapshots never override its newer tokens during recovery. Uninstall intentionally preserves the separate account vault to avoid losing saved logins.

When reporting a bug, do not attach Codex or Claude logs, credential/configuration files, tokens, or screenshots containing information you do not want to publish.
