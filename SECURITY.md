# Security

Please open a GitHub issue for ordinary bugs. Do not include credentials, authentication tokens, private Codex logs, or personal account information.

For a security-sensitive report, use GitHub's private vulnerability reporting feature when it is available for this repository.

Release binaries are not code-signed. Verify the accompanying `SHA256SUMS.txt` file or build the application from source before running it.

Optional Codex account switching handles local authentication secrets. Do not attach account-vault files, encrypted recovery transactions, or temporary login folders to an issue. Windows CurrentUser DPAPI and restrictive ACLs protect the vault at rest; same-user malware is outside that boundary. The switcher refuses to replace authentication while Codex engines are running and preserves an encrypted transaction for interrupted writes. It does not revoke or log out saved accounts when deleting local entries.
