# Privacy

Codex + Claude Usage Indicator reads Codex rate-limit windows from the locally installed Codex app-server. It retrieves Claude's current 5-hour, weekly, and Fable limits by invoking the locally installed Claude Code `/usage` command. Claude Code, rather than this widget, owns authentication and token refresh.

The application:

- does not store usage history;
- stores the last window coordinates and the Claude visibility preference in a local `settings.json` file;
- stores one latest successful Claude snapshot in local `claude-usage-cache.json`, containing only usage percentages, reset times, and the update time;
- does not read, print, log, copy, or persist Claude authentication tokens;
- does not collect account identifiers;
- does not include telemetry;
- registers a per-user Windows logon/recovery task containing the local executable path and Windows user SID, with no stored password or elevated privileges;
- makes no outbound request for Codex usage;
- makes no direct Claude network request; it invokes `claude.exe` in safe mode without a shell or persistent session, caches successful `/usage` results in memory for ten minutes, and deletes the local recovery snapshot after 24 hours, its Fable reset, or an authentication/schema failure.

Codex Desktop, its local app-server, Claude Code, and Anthropic's API remain governed by their own terms and privacy practices.

When reporting a bug, do not attach Codex or Claude logs, credential/configuration files, tokens, or screenshots containing information you do not want to publish.
