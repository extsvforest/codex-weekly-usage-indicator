# Privacy

Codex + Claude Usage Indicator reads Codex rate-limit windows from the locally installed Codex app-server. It also reads the Claude Code OAuth access token from the standard local Claude credential file and sends it only to Anthropic's usage endpoint to retrieve the current 5-hour, weekly, and Fable limits.

The application:

- does not store usage history;
- stores only the last window coordinates and the Claude visibility preference in a local `settings.json` file;
- reads the Claude access token into memory for the Anthropic request but does not print, log, copy, or persist it;
- does not collect account identifiers;
- does not include telemetry;
- makes no outbound request for Codex usage;
- sends Claude usage requests only to `https://api.anthropic.com/api/oauth/usage` and caches successful responses in memory for five minutes.

Codex Desktop, its local app-server, Claude Code, and Anthropic's API remain governed by their own terms and privacy practices.

When reporting a bug, do not attach Codex or Claude logs, credential/configuration files, tokens, or screenshots containing information you do not want to publish.
