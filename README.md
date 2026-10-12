# Codex + Claude Usage Indicator

See how much weekly usage you have left, then choose the account for your next session. A compact Windows widget and one account manager bring Codex, Claude Code and connected WSL Codex environments together.

**Windows · 한국어 / English · Manual account switching**

<img src="docs/design/2026-09-28/100/widget.png" alt="Compact widget showing Codex and Claude weekly remaining percentages" width="272">

Blue is Codex; orange is Claude. Choose **all-model weekly** or **Fable weekly** for the Claude number from the right-click menu.

![A light account manager showing combined Codex usage, reset times and manual switch actions](docs/design/2026-09-28/100/manager.png)

- **See what is left across your Codex accounts.** Check their remaining weekly percentages and the next reset without signing out of your current account.
- **Choose the account you want to use.** Switch Codex manually. For Claude, switch accounts in the Claude app; every account keeps this PC's Code sessions in its list.
- **Keep Windows and WSL Codex in step.** Connect an installed WSL environment once, then include it when you switch accounts.

Screenshots use synthetic accounts and usage. This is an unofficial community project.

[Download the Windows release](https://github.com/extsvforest/codex-weekly-usage-indicator/releases/latest) · [Installation](#install-from-a-release) · [Privacy](PRIVACY.md)

## Widget

The widget appears while Codex Desktop is running and hides when Codex exits or another app is fullscreen. Drag it to move; double-click to refresh; right-click for account management, display choices, language and other controls. Its hover details stay in place while you read and never take keyboard focus.

Choose **Claude display → All models · weekly remaining / Fable · weekly remaining** to change the right-hand number. This changes the display immediately without requesting usage. A new installation starts with all-model weekly; existing installations keep Fable until you choose otherwise. If the selected limit is absent or has passed its reset, the number is **—**. It never silently substitutes another limit. Turning Claude off leaves a single Codex panel and stops Claude usage requests.

Codex refreshes every minute while active. Claude usage requests are at least ten minutes apart per account, including manual refreshes and failed attempts. Checking one account does not delay another; duplicate profiles for the same verified account share the interval. This is the widget's conservative polling interval, not a published provider requirement. The tooltip lists the selected Claude window first, followed by the other weekly window and the five-hour limit. Overall and Fable percentages are separate limits; they are not added together.

Temporary Claude failures can keep a recent, unexpired value visible with its last check time and retry time. Each window expires independently, and no recovery value is used after 24 hours. Missing reset times stay unknown. Account verification or response-format failures clear the affected cached value.

## Codex accounts

Open **Account manager… / 계정 관리…** from the widget or tray menu, or double-click the tray icon. Choose the **Codex** tab.

1. **Save your current account.** Choose **Save current account**. Rename it from its **···** menu whenever you like.
2. **Add another account.** Choose **+ Add account** and complete the official browser sign-in. Your current Codex session stays signed in.
3. **Check the balances.** Opening a new manager window checks saved Codex accounts once. Use **Refresh all** for another complete check or a row's **···** menu to refresh just that account.
4. **Switch when ready.** Choose **Switch**, save your work, and close Codex Desktop and its terminal or IDE sessions. Confirm the switch once they have stopped. The tool applies the saved login and tries to reopen Codex; check the account there.

The list puts the soonest reset first. Three accounts fit in the default window on a sufficiently tall display. Names appear in the manager and hover details, keeping the small widget focused on percentages.

In the screenshot, **63% + 81% + 24% = 168% out of 300%**. Each account contributes 100% when full. Different plans can have different actual quotas, so this sum does not represent an equal number of messages or tokens. A connected Windows/WSL pair counts as one account.

**Next reset** identifies the first account to reset. **Latest reset** gives the last reset among the accounts; it is not a deadline for spending the entire combined balance. Each account follows its own reset time.

Changing tabs, language or focus does not request another full refresh. If a query fails, an account changes or a reset passes, the manager distinguishes previous values from current observations and withholds an incomplete total. Refreshing one account does not certify a new full total.

<details>
<summary>Saved Codex logins and interrupted operations</summary>

Saved logins are encrypted for your Windows user. Inactive-account usage checks use the official local app-server in a private temporary `CODEX_HOME`; they do not replace the active login. If the official client refreshes credentials, the new credentials are saved even when the usage request fails or is canceled.

An interrupted credential operation offers recovery. Recovery compares the actual current account before proceeding and does not overwrite a newer, different sign-in. If credentials are already saved and only temporary files remain, **Clean up temporary files** can finish cleanup while Codex stays open. Closing the manager during a batch cancels the request and waits for safe cleanup.

Local file-based ChatGPT authentication is supported. Keyring and managed authentication configurations are not changed. See [PRIVACY.md](PRIVACY.md) for storage and recovery details.

</details>

## Claude Code accounts

1. Open the **Claude** tab and choose **Connect existing account** to register the Claude Code account you already use.
2. Choose **+ Add account** and sign in through the official Claude Code login window. Each additional account gets its own official login profile, used for its usage checks.
3. Switch accounts in the Claude desktop app itself: sign out, then sign in with the other account. The row of the account the app is using shows **In use**, and the widget follows it. While the app is signed out, signed in to an account not registered here, or still confirming a switch, the widget shows `—` with the reason instead of another account's usage.

### Keep your Code sessions in every account

The Claude desktop app lists Code sessions per account, so signing in with another account used to show an empty list even though the conversations were still on this PC. The widget fixes the list, not the login: whenever Claude is fully closed, it adds each registered account's missing Code sessions to the other accounts' lists. The next time you open Claude with either account, the same sessions are there, and opening one continues the same conversation.

- It only adds entries. It never moves, changes or deletes a list entry the app wrote, never touches conversations themselves, and never reads or copies sign-in data.
- It writes only while Claude and its Code sessions are closed. If you switch accounts inside the app, the account tab says how many sessions are still missing; quit Claude once and they are added. **Reopen after quitting** reopens Claude after that fill (once, within 30 minutes).
- A conversation continued in another account is listed once, in its newest state. An older copy the widget made earlier is archived (find it in the app's archive), never deleted.
- A copy keeps its original's name. Rename a session in one account and its copies in the other accounts take the new name at the next fill, unless you renamed, continued or used that copy in its own account.
- The widget itself never changes pins or groups. Ask Claude to align them instead (below).
- Archived sessions, sessions still being written, folderless scratch sessions, remote/worktree/Cowork sessions and sessions whose folder or conversation file is gone are left out. A session you delete in one account's list is not added back to that account.
- After a Claude update the widget checks that the app still behaves the way this relies on. If not, it pauses filling and says so on the account tab.

### Keep session names, pins and groups in step

Choose one account as the reference: on the Claude tab, open its **···** menu and choose **Use as session layout reference**. After the Claude app switches to another account, ask Claude in any Code session to "sync my sessions" (세션 맞춰줘). The `claude-session-sync` skill asks the widget for a plan and applies it with the Claude app's own session tools, so the app keeps the result.

- The plan only reads: titles from each account's session records, pins from the list the app's sidebar shows, groups from the app's own storage. It covers conversations both accounts have.
- Missing groups are created, then sessions are moved, pinned and renamed to match the reference.
- A conversation continued in another account also leaves its older state in the account where it started, so it shows twice. Claude offers to archive the older state (you can bring it back from the app's archive) and keeps the newest, which takes over its pin and group. Nothing is deleted; other sessions and groups only one account has stay as they are.
- When it is done, Claude shows both accounts' pins and groups side by side and says why anything still differs.
- Group order and pin order are left alone. Run it once in each account other than the reference; in the reference account it only tidies older states.

The summary adds up the overall weekly balance of every Claude account at 100% per account, like the Codex total, with the soonest and the latest reset. Opening the Claude tab checks every signed-in account once, the in-use one first; **Refresh** in the upper-right corner does it again, and **Check this account's usage** in an account's **···** menu checks just that one. Manual and widget checks share a ten-minute interval for each account: an account checked within the last ten minutes reuses that observation instead of a new check, and temporary failures delay retries only for the affected account. An account that failed, needs a sign-in or shows a previous value withholds the total, which then reads as a partial balance. Accounts are not polled in the background. The **···** menu also provides account details, rename, sign in again, sign out and removal from the list. **Remove from list** keeps that profile's login files; use **Sign out** first if you want the official CLI to end its login.

Usage requires a working [Claude Code CLI sign-in](https://code.claude.com/docs/en/authentication). Being able to chat in the Claude desktop app does not verify the CLI's sign-in. If authentication is missing or another account is signed in, the account tab shows the reason and a **Sign in again** button. Sign in with the account originally connected to that row. The tool keeps the saved identity, and it does not display another account's usage under the old label. A malformed status response is reported as an error, not as an expired login.

## Connect WSL Codex

Choose **Windows · WSL** in the Codex tab, start the WSL distribution you want to use, and press **Find environments**. Only a running environment with a native Linux Codex CLI and supported personal file-based authentication can be connected. The tool does not install a missing CLI, start unrelated distributions or change unsupported authentication settings. If the official login does not open your browser, use **Open sign-in page**. The temporary link stays in memory and is cleared when login ends or is canceled.

For each account you want to switch:

1. Select the WSL environment and its corresponding saved Windows account.
2. If WSL already uses that account, finish and close its Codex jobs, then choose **Connect current WSL login**. Otherwise choose **Sign in to selected account** and complete the official login. This additional login is isolated and can be prepared while existing jobs continue.
3. Enable **Switch together with Windows** after connecting the logins.

Before the first switch, connect **both the current WSL account and the destination account**. WSL logins are saved independently from Windows logins; Windows credential files are not copied over as a shortcut. An identity check must match the selected account before a binding is saved.

Actual capture and switching require WSL Codex processes, including app and IDE connections, to be stopped. The tool does not kill those processes. If Windows is already on the account you want, its row's **···** menu can apply that account to the connected WSL environments without switching Windows again.

Windows and WSL cannot be updated in one atomic filesystem operation. If a switch is interrupted, the manager keeps an encrypted recovery record and shows what still needs attention. **Recover account switch** completes verification where possible. **Stop WSL linking** instead leaves every environment's current login untouched, disables linked switching and archives the encrypted recovery record. It does not roll back or write authentication files.

## Language

Choose **Language / 언어 → 한국어 / English** in the widget or tray menu. Menus, account management, dialogs, usage details and known application errors change immediately. An idle manager keeps its size and latest observations. Finish or cancel a running account operation before changing language.

The choice is saved locally. Existing installations keep Korean; new installations use Korean on Korean Windows and English otherwise. Account names, identities and usage values are not translated. Windows dialogs and external diagnostic text can follow their own language settings.

## Requirements

- Windows 10 or 11 and [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)
- Codex Desktop installed and signed in
- Optional Claude support: a current native Claude Code for Windows with `/usage`, `auth` and `--safe-mode`
- Optional WSL switching: a running WSL distribution with native Linux Codex CLI and supported file-based ChatGPT authentication
- .NET 8 SDK only when building from source

## Install from a release

1. Download and extract the Windows zip from [Releases](https://github.com/extsvforest/codex-weekly-usage-indicator/releases).
2. Review the included PowerShell scripts.
3. Open a standalone Windows PowerShell window outside packaged apps such as Codex, change to the extracted directory, and run:

```powershell
.\scripts\install.ps1
```

The app is installed to `%LOCALAPPDATA%\CodexWeeklyUsageIndicator`. A per-user `CodexWeeklyUsageIndicator-<Windows SID>` scheduled task starts its supervisor at sign-in with normal user privileges and no stored password. Installation starts the task immediately. Two processes from the same EXE are expected: the supervisor and one widget.

The supervisor retries an abnormal widget exit after one minute. Choosing **Close** from the widget menu ends both normally. To start again before your next Windows sign-in, run the scheduled task or the installer. Launching the EXE directly does not enable supervision.

Run installation and removal outside packaged app terminals. Windows can redirect their AppData writes into a location Task Scheduler cannot see; both scripts check the actual directory before changing tasks or processes. Release binaries are unsigned, and SHA-256 checksums are included with each release.

To uninstall:

```powershell
.\scripts\uninstall.ps1
```

Uninstall removes the app and scheduled task. It **preserves saved Codex accounts, WSL recovery records and Claude profile folders**, so reinstalling does not discard your logins. Claude profile folders can contain official CLI credentials and links to your original work; do not recursively delete their linked contents. See [PRIVACY.md](PRIVACY.md) before removing saved data.

## How it works

The WinForms widget reads `account/rateLimits/read` through the official local Codex app-server and selects the window closest to seven days. Claude usage comes from the official CLI's built-in `/usage`, run in safe mode without a shell, browser integration or persistent session and with `--max-turns 0`. Responses reporting a model turn or nonzero cost are rejected. The app does not read Claude tokens or implement its own OAuth flow.

One local Claude recovery snapshot holds percentages, reset times, its check time and an opaque profile/account key. It cannot be reused for a different verified account. A new process attempts a live request first; temporary failures may use the matching snapshot for up to 24 hours, with each expired window withheld independently. Claude's own last-known output is not relabeled as a fresh check.

Account changes are manual. There is no quota-based rotation, request proxy, pooled subscription quota, usage timeline or per-person attribution. This convenience tool does not establish that any particular multi-account usage pattern is permitted by the service terms.

The local Codex app-server method and Claude Code's usage text are changeable interfaces. An upstream change can require an app update.

## Build from source

```powershell
.\scripts\build.ps1
```

The executable is written to `dist\WeeklyUsageIndicator.exe`. The script runs the regression suite before publishing; release builds omit debug paths and symbols. Synthetic design captures live under `docs/design/`.

## License

[MIT](LICENSE). Bundled Pretendard fonts use the SIL Open Font License; see [THIRD_PARTY_NOTICES.txt](THIRD_PARTY_NOTICES.txt).
