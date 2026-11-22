using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using WeeklyUsageIndicator;

/// <summary>
/// The account window over made-up accounts at the design's moment, 2026-11-21 (Sat) 13:30 KST (docs/design/2026-11-21),
/// to set beside the design's screens. Claude rows are read through the real controller from a source that answers only
/// when the preview lets it, so screen B is a real query in progress. Nothing reads or signs in to a real account.
/// With GFS_ACCOUNT_UI_CAPTURE naming a file, each screen is saved as a PNG in that file's folder ("-en" under --english)
/// and findings.txt there lists what was clipped, shortened or drawn in another font.
/// </summary>
internal static class AccountStylePreview
{
    private static readonly TimeSpan Kst = TimeSpan.FromHours(9);
    private static readonly string MainKey = new('A', 64), WorkKey = new('B', 64), NewKey = new('C', 64);
    private static DateTimeOffset At(int day, int hour, int minute) => new(2026, 11, day, hour, minute, 0, Kst);
    private static string Words(string korean, string english) => UiText.IsEnglish ? english : korean;
    private static Exception NoAccess(string what) => new InvalidOperationException($"The design preview never uses {what}.");

    internal static Task RunAsync(bool hold, bool standardDpi = false)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var clock = AccountUsageText.Clock; var motion = AccountMotion.Frozen;
            string? root = null;
            try
            {
                var basePath = Environment.GetEnvironmentVariable("GFS_ACCOUNT_TEST_ROOT")
                    ?? Path.Combine(Path.GetTempPath(), "gfs-agent", "account-style-preview");
                root = Path.Combine(basePath, "style-" + Guid.NewGuid().ToString("N"));
                Application.SetHighDpiMode(standardDpi ? HighDpiMode.DpiUnaware : HighDpiMode.PerMonitorV2); Application.EnableVisualStyles();
                using (var preview = new Preview(root)) preview.Run(hold);
                done.TrySetResult();
            }
            catch (Exception ex) { done.TrySetException(ex); }
            finally
            {
                AccountUsageText.Clock = clock; AccountMotion.Frozen = motion;
                if (root is not null) DeleteFixture(root);
            }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return done.Task;
    }

    private sealed class Preview : IDisposable
    {
        private readonly string _root;
        private readonly string? _folder = Environment.GetEnvironmentVariable("GFS_ACCOUNT_UI_CAPTURE") is { Length: > 0 } output
            ? Path.GetDirectoryName(Path.GetFullPath(output)) : null;
        private readonly string _suffix = UiText.IsEnglish ? "-en" : "";
        private readonly PreviewClock _clock = new(At(21, 13, 30));
        private readonly PreviewRunner _runner = new();
        private readonly PreviewUsage _usage = new();
        private readonly CodexAccountStore _codex;
        private readonly CombinedUsageSnapshot _combined = new();
        private readonly ClaudeProfileStore _claude;
        private readonly ClaudeAccountController _controller;
        private readonly Dictionary<string, Exception> _failures;
        private readonly ClaudeProfile _main, _c2, _c3, _c5;
        private readonly string _mainLabel;
        private readonly AntigravitySession _antigravity;
        private readonly List<string> _summary = new();
        private readonly List<string> _unpainted = new();
        private Func<CancellationToken, Task<AntigravityReadResult>> _antigravityRead = _ => throw NoAccess("Antigravity");
        private Func<SavedCodexAccount, CancellationToken, Task> _codexQuery = (_, _) => throw NoAccess("a Codex account");
        private readonly List<string> _codexQueried = new();
        private SavedCodexAccount[] _codexRead = Array.Empty<SavedCodexAccount>();
        private int _antigravityReads;
        private int? _canceledBeforeClosing;
        private bool _hold;
        private Control? _shelfOwner;
        private ClaudeShelfService? _shelf;

        internal Preview(string root)
        {
            _root = root;
            AccountUsageText.Clock = () => _clock.Now;
            // Spinners and moving bars hold one frame so that still pictures agree.
            AccountMotion.Frozen = .3;
            if (_folder is not null)
            {
                Directory.CreateDirectory(_folder);
                File.WriteAllText(Path.Combine(_folder, "findings" + _suffix + ".txt"), "", Encoding.UTF8);
            }

            // Codex (TASK_design §5): three accounts read at 13:20 in a full query.
            var home = Directory.CreateDirectory(Path.Combine(root, "home")).FullName;
            File.WriteAllBytes(Path.Combine(home, "auth.json"), AccountUsageQueryTests.Auth("style-a", "preview"));
            _codex = new CodexAccountStore(Path.Combine(root, "vault"), home);
            var file = Path.Combine(root, "import.json");
            var accounts = new List<SavedCodexAccount> { _codex.RegisterCurrent(Words("메인 계정", "Main account")) };
            File.WriteAllBytes(file, AccountUsageQueryTests.Auth("style-b", "preview"));
            accounts.Add(_codex.ImportLoginFile(file, Words("서브 계정", "Sub account")));
            File.WriteAllBytes(file, AccountUsageQueryTests.Auth("style-c", "preview"));
            accounts.Add(_codex.ImportLoginFile(file, Words("서브 계정2", "Sub account 2")));
            File.Delete(file);
            var read = accounts.Select((account, i) => account with
            {
                Usage = new UsageSnapshot(new[] { 37, 19, 76 }[i], At(23 + 2 * i, 13, 27), 10080, "codex"),
                ObservedAt = At(21, 13, 20)
            }).ToArray();
            _codexRead = read; ReadCodexInThisRun(_combined);

            // Claude: five profiles, three of them read earlier in this run, one signed out and one never read.
            var original = Directory.CreateDirectory(Path.Combine(root, "claude-original")).FullName;
            _claude = new ClaudeProfileStore(Path.Combine(root, "claude-profiles"), original, false);
            _mainLabel = Words("메인 Claude", "Main Claude");
            _main = _claude.RegisterDefault(_mainLabel, Identity(_claude.DefaultProfile));
            ClaudeProfile Add(int number)
            {
                var profile = _claude.CreateProfile($"Claude {number}");
                return _claude.RecordVerifiedIdentity(profile.Id, Identity(profile));
            }
            _c2 = Add(2); _c3 = Add(3); var c4 = Add(4); _c5 = Add(5);
            _controller = new ClaudeAccountController(_claude, new ClaudeProfileRuntime(_claude, _runner, "preview-claude.exe"), _clock,
                (profile, key, accountKey) => new ClaudeUsageClient(_usage.For(profile.Id), _clock, ClaudeUsageNullCacheStore.Instance, key,
                    accountKey: accountKey),
                new ClaudeUsageFileCacheStore(Path.Combine(root, "claude-usage-cache.json"), _clock));
            _failures = (Dictionary<string, Exception>)typeof(ClaudeAccountController)
                .GetField("_failures", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_controller)!;
            // Each read goes through the controller at its own moment, before any window exists.
            Read(_c3, At(20, 23, 10), Snapshot(18, At(26, 2, 0), 23, 0, null));
            Read(_c2, At(21, 12, 40), Snapshot(66, At(23, 9, 0), 60, 0, null));
            Read(_main, At(21, 13, 5), Snapshot(41, At(21, 17, 59), 79, 12, At(21, 15, 10)));
            _runner.SignOut(c4.ConfigDirectory);
            Read(c4, At(21, 13, 6), null);
            _clock.Now = At(21, 13, 30);

            // Antigravity: the account signed in now, read at 13:10, and one read three days ago.
            var store = new AntigravityAccountStore(Path.Combine(root, "antigravity"));
            store.RecordObservation(WorkKey, "wo***@example.test",
                new AntigravityUsage(new(.40, At(24, 9, 12)), null, null, null), At(18, 16, 40));
            store.RecordObservation(MainKey, "ma***@example.test",
                new AntigravityUsage(new(.88, At(26, 1, 1)), new(.98, At(21, 18, 8)), new(1, null), new(1, null)), At(21, 13, 10));
            store.Rename(MainKey, Words("메인 Antigravity", "Main Antigravity"));
            store.Rename(WorkKey, Words("작업용 Antigravity", "Work Antigravity"));
            _antigravity = new AntigravitySession(store)
            { ReadSource = token => { Interlocked.Increment(ref _antigravityReads); return _antigravityRead(token); } };
            _antigravity.LastRead = new AntigravityReadResult(AntigravityReadKind.Success,
                store.ListAccounts().Single(account => account.IdentityKey == MainKey));
        }

        private static ClaudeUsageSnapshot Snapshot(int weeklyUsed, DateTimeOffset weeklyReset, int fableUsed, int fiveHourUsed,
            DateTimeOffset? fiveHourReset) => new(new(fiveHourUsed, fiveHourReset), new(weeklyUsed, weeklyReset), new(fableUsed, weeklyReset));

        private void Read(ClaudeProfile profile, DateTimeOffset at, ClaudeUsageSnapshot? snapshot)
        {
            _clock.Now = at;
            if (snapshot is not null) _usage.Set(profile.Id, snapshot);
            // No window and no synchronization context yet: the read completes on this thread.
            try { _controller.ReadUsageAsync(profile.Id, CancellationToken.None).GetAwaiter().GetResult(); }
            catch (ClaudeLoginRequiredException) when (snapshot is null) { }
        }

        private static ClaudeShelfStatus InUse(string profileId, string? unreviewed = null, int? pending = null) => ClaudeShelfStatus.Initial with
        {
            Desktop = ClaudeDesktopRunState.Running, AppProfileId = profileId, PendingForApp = pending, MappedAccounts = 5,
            Unreviewed = unreviewed,
            App = new ClaudeDesktopAccountState(ClaudeDesktopAccountStatus.SignedIn,
                "a1a1a1a1-0000-4000-8000-0000000000a1", "b1b1b1b1-0000-4000-8000-0000000000b1")
        };

        internal void Run(bool hold)
        {
            _hold = hold;
            // The shelf reports only what the preview sets. It never starts, so nothing observes or fills Claude.
            _shelfOwner = new Control();
            var ledger = Directory.CreateDirectory(Path.Combine(_root, "shelf-ledger")).FullName;
            var paths = new ClaudeShelfPaths(Path.Combine(_root, "sessions"), Path.Combine(_root, "projects"),
                Path.Combine(_root, "scratch"), [], ledger);
            _shelf = new ClaudeShelfService(_shelfOwner, () => _claude.ListProfiles(),
                new ClaudeSessionShelf(paths, () => throw NoAccess("Claude sessions"), () => throw NoAccess("the operation lease")),
                new ClaudeShelfEnvironment(() => throw NoAccess("Claude"), () => null, _ => throw NoAccess("Claude"),
                    _ => throw NoAccess("Claude"), () => _clock.Now),
                new ClaudeDesktopLogWatcher(Path.Combine(_root, "claude-logs")));
            _shelf.SetStatusForTest(InUse(_main.Id));
            ExceptionDispatchInfo? failure = null;
            using var form = new AccountManagerForm(_codex, () => Task.CompletedTask, () => { }, combined: _combined,
                claudeAccounts: _controller, claudeShelf: _shelf, antigravity: _antigravity,
                queryUsage: (account, token) => { _codexQueried.Add(account.Id); return _codexQuery(account, token); });
            form.Shown += async (_, _) =>
            {
                try { await CaptureAllAsync(form); }
                catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
                // A failed step may have left a query running, which would keep the window open.
                if (failure is not null) form.Dispose();
                else if (!hold) form.Close();
            };
            Application.Run(form);
            failure?.Throw();
            Check(_unpainted.Count == 0, "every control paints all of itself, as Windows shows it: " + string.Join("; ", _unpainted.Take(12)));
            // The last step closed the window during a query (BehaviourAsync): it waited, canceled the reads and closed.
            if (_canceledBeforeClosing is { } canceled)
                Check(form.IsDisposed && _usage.Canceled == canceled + 3, "closing during a query cancels its reads, then closes the window");
            Console.WriteLine("PASS: account window preview and behaviour" + (_folder is null ? ""
                : $" · {_summary.Count} captures in {_folder} · findings: " + string.Join(", ", _summary)));
        }

        private async Task CaptureAllAsync(AccountManagerForm form)
        {
            var s = form.DeviceDpi / 96f;
            var wide = form.ClientSize; var narrow = new Size((int)Math.Round(780 * s), wide.Height);
            async Task Shoot(string name, bool alsoNarrow = false)
            {
                await Settle(); Capture(form, name);
                if (!alsoNarrow) return;
                form.ClientSize = narrow; await Settle(); Capture(form, name + "-780");
                form.ClientSize = wide; await Settle();
            }
            var claude = Find<ClaudeAccountPanel>(form, "ClaudeAccountPanel");
            var antigravity = Find<AntigravityAccountPanel>(form, "AntigravityAccountPanel");
            var (reads, checks) = (_usage.Calls, _runner.Calls);

            // A: the Claude page as the window opens. Opening reads nothing.
            Click(form, "ClaudeProviderTab");
            await Shoot("A-claude", alsoNarrow: true);
            Check(_usage.Calls == reads && _runner.Calls == checks, "opening the window and the page reads no account");

            // E: a row's menu, and the details it opens.
            Click(form, "ClaudeMore-" + _c2.Id); await Settle();
            var menu = Menu(claude);
            Capture(form, "E-menu", menu);
            Check(!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) || menu is AccountMenu { Rounded: true },
                "on Windows 11 the row menu takes the system's small rounded corners and their shadow");
            menu.Close(ToolStripDropDownCloseReason.AppClicked); await Settle();
            await MenuDialogAsync<AccountDetailsDialog>(form, claude, "ClaudeMore-" + _c2.Id, "ClaudeDetailsMenuItem", "I-claude-details");

            // B: the full query: three accounts at once and the fourth waiting for a slot, then the first one done.
            _usage.Hold(_main.Id, _c2.Id, _c3.Id, _c5.Id);
            Click(form, "RefreshAllUsageButton");
            await Until(() => new[] { _main, _c2, _c3 }.All(profile => _controller.IsReading(profile.Id)), "three reads at once");
            await Shoot("B2-claude-waiting");
            Check(!_controller.IsReading(_c5.Id), "a fourth account waits for one of the three read slots");
            _usage.Release(_main.Id);
            await Until(() => claude.Progress is { } progress && progress.Done == 1 && _controller.IsReading(_c5.Id), "the first result");
            await Shoot("B-claude-querying");
            Click(form, "CancelLoginButton");
            await Until(() => !claude.IsBusy && !claude.IsReading, "the query to stop");
            Check(_usage.Most == 3 && _usage.Canceled == 3, "the full query reads three accounts at most, and canceling it stops the reads in flight");

            // G: the states the design sets beside the screens: a failed read, a long name, the account to use next and
            // an app version not reviewed yet.
            _clock.Now = At(21, 13, 32);
            _claude.Rename(_main.Id, Words("메인 Claude · 스튜디오 공동 작업용 긴 계정 이름 English 012345", "Main Claude · Studio shared work account 012345"));
            _failures[_c2.Id] = new ClaudeUsageTemporarilyUnavailableException(At(21, 13, 40));
            _shelf!.SetStatusForTest(InUse(_c3.Id, unreviewed: "1.9.0", pending: 2));
            claude.Reload();
            await Shoot("G-states", alsoNarrow: true);
            _claude.Rename(_main.Id, _mainLabel); _failures.Remove(_c2.Id);
            _shelf.SetStatusForTest(InUse(_main.Id)); _clock.Now = At(21, 13, 30);
            claude.Reload();

            // C: Codex.
            Click(form, "CodexProviderTab");
            await Shoot("C-codex", alsoNarrow: true);
            Check(_codexQueried.Count == 0, "opening the Codex page reads no account");
            // C2: the state of the maintainer's window on 11/22: the widget restarted since the values were read, so each
            // is one kept from an earlier run, and hours old. The total still adds them up and the rows say they are old.
            form.CombinedUsage.Initialize(_codexRead, batch: false);
            _clock.Now = At(21, 23, 30); form.Reload(quiet: true);
            await Shoot("C2-codex-old");
            Check(Find<Label>(form, "CombinedRemainingLabel").Text == "168%" && Find<Control>(form, "CombinedCoverageLabel").Text.Length == 0
                  && Find<Label>(form, "NextResetLabel").Text != UiText.T("확인 필요"),
                "values kept from an earlier run add up and give the next reset (2026-11-21 design)");
            ReadCodexInThisRun(form.CombinedUsage);
            _clock.Now = At(21, 13, 30); form.Reload(quiet: true);

            // D: Antigravity, then no account signed in, then nothing read yet, then a first read of a new account.
            Click(form, "AntigravityProviderTab");
            await Shoot("D-antigravity", alsoNarrow: true);
            Check(_antigravityReads == 0, "opening the Antigravity page reads nothing");
            var previousReads = Walk(antigravity).OfType<AccountIconButton>()
                .Where(button => button.Name.StartsWith("AntigravityRowRead-", StringComparison.Ordinal)).ToArray();
            Check(previousReads.Length == 1 && !previousReads[0].Enabled, "an account read before cannot be read until Antigravity is signed in to it");
            _antigravity.LastRead = new AntigravityReadResult(AntigravityReadKind.NotSignedIn);
            antigravity.Reload();
            await Shoot("D2-antigravity-signed-out", alsoNarrow: true);
            foreach (var account in _antigravity.Store.ListAccounts()) _antigravity.Store.Remove(account.IdentityKey);
            _antigravity.LastRead = null; antigravity.Reload();
            await Shoot("D3-antigravity-empty");
            var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _antigravityRead = async token =>
            {
                await answer.Task.WaitAsync(token);
                var (account, first) = _antigravity.Store.RecordObservation(NewKey, "ne***@example.test",
                    new AntigravityUsage(new(.62, At(27, 8, 30)), new(1, null), new(.95, At(25, 11, 0)), new(1, null)), _clock.Now);
                return new AntigravityReadResult(AntigravityReadKind.Success, account, first);
            };
            Click(form, "RefreshAllUsageButton");
            await Until(() => antigravity.IsReading, "the Antigravity read");
            await Shoot("D4-antigravity-reading");
            answer.SetResult();
            var naming = await Modal<AccountNameDialog>();
            await Settle(); Capture(naming, "D5-antigravity-first-seen");
            Find<TextBox>(naming, "AccountNameTextBox").Text = Words("새 Antigravity", "New Antigravity");
            Find<Button>(naming, "SaveNameButton").PerformClick();
            await Until(() => !naming.Visible && !antigravity.IsReading, "the new account's name");
            await Shoot("D6-antigravity-added");
            Check(_antigravity.Store.ListAccounts().SingleOrDefault(account => account.IdentityKey == NewKey)?.Label
                == Words("새 Antigravity", "New Antigravity"), "an account seen for the first time is named and added to the list");

            // F: the widget size window.
            using (var size = new WidgetScaleDialog(87, _ => { }, _ => { }))
            {
                size.Show(form); await Settle(); Capture(size, "F-widget-size");
                var box = Find<TextBox>(size, "WidgetScaleInput");
                Check(box.Focused && box.SelectionLength == 0 && box.SelectionStart == box.TextLength,
                    "the size window opens with the number in the box and nothing selected");
                // A number typed before anything else replaces the one shown (WM_CHAR to the box itself, not the keyboard).
                foreach (var key in "120") SendMessage(box.Handle, 0x0102, key, 0);
                Check(box.Text == "120" && size.Percent == 120, $"a number typed first replaces the one shown ({box.Text})");
                size.Close();
            }

            // The other windows the page opens, in the same style.
            Click(form, "CodexProviderTab"); await Settle();
            var wsl = Path.Combine(_root, "wsl-preview");
            using (var environments = new WslCodexConnectionsDialog(_codex, new WslCodexService(wsl, new PreviewWslBridge()), new WslCodexConnections(wsl)))
            { environments.Show(form); await Task.Delay(300); await Settle(); Capture(environments, "H-environments"); environments.Close(); }
            foreach (var (name, dialog) in new (string, Form)[]
            {
                ("I-rename", new AccountNameDialog(Words("메인 계정", "Main account"))),
                ("I-add", new AccountNameDialog("", adding: true)),
                ("I-switch", new AccountSwitchDialog(Words("메인 계정", "Main account"), Words("서브 계정", "Sub account"), () => throw NoAccess("Codex")))
            })
                using (dialog) { dialog.Show(form); await Settle(); Capture(dialog, name); dialog.Close(); }
            await Settle();
            if (!_hold) await BehaviourAsync(form, claude, antigravity);
        }

        /// <summary>What the pictures cannot show. It changes the fixture, so it runs after them; its last step closes the window.</summary>
        private async Task BehaviourAsync(AccountManagerForm form, ClaudeAccountPanel claude, AntigravityAccountPanel antigravity)
        {
            // A row's ↻ reads that account and no other, once the ten minutes an account waits between reads have passed.
            // The reads held for picture B were canceled; their gates would hold the next ones.
            _usage.ReleaseAll(); _clock.Now = _clock.Now.AddMinutes(11);
            Click(form, "ClaudeProviderTab"); await Settle();
            _usage.TakeRead(); var calls = _usage.Calls;
            Click(form, "ClaudeRead-" + _c2.Id);
            await Until(() => _usage.Calls > calls && !claude.IsReading, "the Claude row's read");
            Check(_usage.TakeRead().SequenceEqual(new[] { _c2.Id }), "a Claude row's ↻ reads that account alone");

            Click(form, "CodexProviderTab"); await Settle();
            var sub = _codex.ListAccounts().First(account => !account.IsActive);
            _codexQuery = (account, token) => Task.Run(() => _codex.QueryInactiveUsage(account.Id,
                (_, _) => Task.FromResult(AccountUsageQueryTests.Result()), token), token);
            Click(form, "ReadAccount-" + sub.Id);
            await Until(() => _codexQueried.Count > 0 && !form.IsOperationInProgress, "the Codex row's read");
            Check(_codexQueried.SequenceEqual(new[] { sub.Id }) && _codex.ListAccounts().Single(account => account.Id == sub.Id).ObservedAt is not null,
                "a Codex row's ↻ reads that account alone");

            // A read that finds no account signed in says so; the accounts read before keep their values.
            _antigravityRead = _ => Task.FromResult(new AntigravityReadResult(AntigravityReadKind.NotSignedIn));
            Click(form, "AntigravityProviderTab"); await Settle();
            var reads = _antigravityReads;
            Click(form, "RefreshAllUsageButton");
            await Until(() => _antigravityReads > reads && !antigravity.IsReading, "the read that finds no account signed in");
            await Settle();
            Check(Find<AccountPill>(antigravity, "AntigravityStatePill") is { Visible: true } pill && pill.Text == UiText.T("로그인 안 됨")
                && _antigravity.Store.ListAccounts().Single(account => account.IdentityKey == NewKey).Usage is not null,
                "a read with no account signed in says so and keeps the values read before");

            // Closing the window during a query cancels the reads and closes once they have stopped (the rest is checked in Run).
            _clock.Now = _clock.Now.AddMinutes(11);
            Click(form, "ClaudeProviderTab"); await Settle();
            _usage.Hold(_main.Id, _c2.Id, _c3.Id);
            Click(form, "RefreshAllUsageButton");
            await Until(() => new[] { _main, _c2, _c3 }.All(profile => _controller.IsReading(profile.Id)), "three reads before closing");
            _canceledBeforeClosing = _usage.Canceled;
            form.Close();
            Check(form.Visible && !form.IsDisposed, "closing during a query waits for its reads to stop");
        }

        /// <summary>Opens a row's menu, chooses an item that shows a dialog, captures the dialog and closes it.</summary>
        private async Task MenuDialogAsync<T>(Form form, Control panel, string more, string item, string name) where T : Form
        {
            Click(form, more); await Settle();
            var menu = Menu(panel);
            var entry = menu.Items.Cast<ToolStripItem>().Single(candidate => candidate.Name == item);
            menu.Close(ToolStripDropDownCloseReason.ItemClicked);
            // The dialog is modal: it opens from a posted call so that this method goes on inside its message loop.
            form.BeginInvoke((Action)entry.PerformClick);
            var dialog = await Modal<T>();
            await Settle(); Capture(dialog, name);
            dialog.Close();
            await Until(() => !dialog.Visible, "the dialog to close");
        }

        /// <summary>The Codex values as a full query of this run read them at 13:20.</summary>
        private void ReadCodexInThisRun(CombinedUsageSnapshot set)
        {
            set.Initialize(_codexRead, batch: true);
            foreach (var account in _codexRead) set.SetResult(account.Id, account, CombinedReadState.Success);
            set.CompletedAt = At(21, 13, 20);
        }

        private void Capture(Form form, string name, Control? overlay = null)
        {
            HideFocusCues(form);
            // Every screen, pictures or not: a control that leaves part of itself unpainted shows on the screen whatever
            // its buffer held before (1.10.0's tabs and buttons), and no picture below shows it.
            var unpainted = Unpainted(form).Concat(overlay is null ? [] : Unpainted(overlay)).ToList();
            _unpainted.AddRange(unpainted.Select(line => name + ": " + line));
            if (_folder is null) return;
            var origin = form.PointToScreen(Point.Empty);
            // Two pictures of each screen: what the window paints of itself through WM_PRINT (DrawToBitmap: every child
            // over its parent's pixels), and what Windows composed from each control's own WM_PAINT ("-screen",
            // PrintWindow with PW_RENDERFULLCONTENT), the one a person sees.
            foreach (var (picture, file) in new (Func<Control, Bitmap?>, string)[] { (Printed, name), (Composed, name + "-screen") })
            {
                using var whole = picture(form);
                if (whole is null) { _unpainted.Add(name + ": the composed picture could not be taken"); continue; }
                var client = new Rectangle(origin.X - form.Left, origin.Y - form.Top, form.ClientSize.Width, form.ClientSize.Height);
                client.Intersect(new Rectangle(Point.Empty, whole.Size));
                using var image = whole.Clone(client, PixelFormat.Format32bppArgb);
                if (overlay is { Visible: true, Width: > 0, Height: > 0 })
                {
                    // A menu is its own window: it is drawn where it stands over the client area.
                    using var layer = picture(overlay);
                    using var graphics = Graphics.FromImage(image);
                    if (layer is not null) graphics.DrawImage(layer, overlay.Left - origin.X, overlay.Top - origin.Y, layer.Width, layer.Height);
                }
                image.Save(Path.Combine(_folder, file + _suffix + ".png"), ImageFormat.Png);
            }
            Inspect(form, name + _suffix, overlay, unpainted);
        }

        private static Bitmap? Printed(Control window)
        {
            var image = new Bitmap(window.Width, window.Height);
            window.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
            return image;
        }

        private static Bitmap? Composed(Control window)
        {
            var image = new Bitmap(window.Width, window.Height, PixelFormat.Format32bppArgb);
            bool done;
            using (var graphics = Graphics.FromImage(image))
            {
                var dc = graphics.GetHdc();
                try { done = PrintWindow(window.Handle, dc, 2); }
                finally { graphics.ReleaseHdc(dc); }
            }
            if (done) return image;
            image.Dispose();
            return null;
        }

        // What a reviewer would otherwise look for by eye: a control cut by its parent, text shortened with an ellipsis,
        // a button or label narrower than its text, and text in a font other than the bundled Pretendard.
        private void Inspect(Control top, string name, Control? overlay, List<string> unpainted)
        {
            var lines = new List<string>(unpainted);
            foreach (var control in Walk(top).Skip(1).Where(control => control.Visible && control.Width > 0 && control.Height > 0))
            {
                var parent = control.Parent!;
                if (parent is not ScrollableControl { AutoScroll: true } && !parent.ClientRectangle.Contains(control.Bounds))
                    lines.Add($"clipped   {Describe(control)} {control.Bounds} in {Describe(parent)} {parent.ClientSize}");
                if (control is AccountText { Truncated: true } text)
                    lines.Add($"shortened {Describe(control)} \"{text.Text}\" needs {text.ContentWidth}px, has {text.Width}px");
                var preferred = control switch
                {
                    AccountButton button => button.GetPreferredSize(Size.Empty).Width,
                    AccountPill pill => pill.Preferred.Width,
                    _ => 0
                };
                if (preferred > control.Width + 1)
                    lines.Add($"squeezed  {Describe(control)} \"{control.Text}\" needs {preferred}px, has {control.Width}px");
                if (control.Text.Length > 0 && control is Label or ButtonBase or TextBoxBase
                    && !control.Font.FontFamily.Name.StartsWith("Pretendard", StringComparison.Ordinal))
                    lines.Add($"font      {Describe(control)} \"{control.Text}\" in {control.Font.FontFamily.Name}");
            }
            if (overlay is ToolStrip strip)
                foreach (ToolStripItem item in strip.Items)
                {
                    if (item.Text is { Length: > 0 } && !item.Font.FontFamily.Name.StartsWith("Pretendard", StringComparison.Ordinal))
                        lines.Add($"font      {item.Name} \"{item.Text}\" in {item.Font.FontFamily.Name}");
                    // A drop-down that sizes itself from its labels cuts the items it was told to show whole.
                    if (item.Available && (item.Bounds.Right > strip.Width || item.Bounds.Bottom > strip.Height))
                        lines.Add($"clipped   {item.Name} {item.Bounds} in menu {strip.Size}");
                }
            _summary.Add($"{name} {lines.Count}");
            File.AppendAllText(Path.Combine(_folder!, "findings" + _suffix + ".txt"),
                $"[{name}]\n" + (lines.Count == 0 ? "  -\n" : string.Concat(lines.Select(line => "  " + line + "\n"))), Encoding.UTF8);
        }

        public void Dispose()
        {
            _controller.Dispose();
            _shelf?.Dispose();
            _shelfOwner?.Dispose();
        }
    }

    // Windows paints each control on its own. A double-buffered one draws into a buffer the window's controls share,
    // which still holds whatever was drawn there last, and only what the control paints over it reaches the screen; a
    // Button is opaque to WinForms (ButtonBase sets ControlStyles.Opaque), so nothing is painted under it unless it
    // paints that itself. Each such control is painted alone twice, as WM_PAINT paints it, over two colours: a pixel
    // that differs between the two is one it left unpainted.
    private static List<string> Unpainted(Control top)
    {
        var lines = new List<string>();
        foreach (var control in Walk(top).Where(control => control.Visible && control.ClientSize is { Width: > 0, Height: > 0 }
            && Style(control, ControlStyles.UserPaint) && Style(control, ControlStyles.OptimizedDoubleBuffer)))
        {
            using var magenta = PaintAlone(control, Color.FromArgb(255, 0, 255));
            using var green = PaintAlone(control, Color.FromArgb(0, 255, 0));
            if (Differing(magenta, green) is var count and > 0)
                lines.Add($"unpainted {Describe(control)} {count} of {control.ClientSize.Width * control.ClientSize.Height} px");
        }
        return lines;
    }

    private static readonly MethodInfo GetStyle = typeof(Control).GetMethod("GetStyle", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo OnPaint = typeof(Control).GetMethod("OnPaint", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo OnPaintBackground = typeof(Control).GetMethod("OnPaintBackground", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static bool Style(Control control, ControlStyles style) => (bool)GetStyle.Invoke(control, [style])!;

    private static Bitmap PaintAlone(Control control, Color under)
    {
        var image = new Bitmap(control.ClientSize.Width, control.ClientSize.Height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(image);
        graphics.Clear(under);
        using var e = new PaintEventArgs(graphics, control.ClientRectangle);
        if (!Style(control, ControlStyles.Opaque)) OnPaintBackground.Invoke(control, [e]);
        OnPaint.Invoke(control, [e]);
        return image;
    }

    private static int Differing(Bitmap first, Bitmap second)
    {
        var area = new Rectangle(Point.Empty, first.Size);
        var a = first.LockBits(area, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var b = second.LockBits(area, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var (left, right) = (new int[first.Width], new int[first.Width]);
            var count = 0;
            for (var y = 0; y < first.Height; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(a.Scan0 + y * a.Stride, left, 0, left.Length);
                System.Runtime.InteropServices.Marshal.Copy(b.Scan0 + y * b.Stride, right, 0, right.Length);
                for (var x = 0; x < left.Length; x++) if (left[x] != right[x]) count++;
            }
            return count;
        }
        finally { first.UnlockBits(a); second.UnlockBits(b); }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool PrintWindow(nint window, nint dc, uint flags);

    private static string Describe(Control control) => string.IsNullOrEmpty(control.Name) ? control.GetType().Name : control.Name;
    private static IEnumerable<Control> Walk(Control control) => new[] { control }.Concat(control.Controls.Cast<Control>().SelectMany(Walk));
    private static T Find<T>(Control root, string name) where T : Control => root.Controls.Find(name, true).OfType<T>().Single();
    private static ContextMenuStrip Menu(Control panel) => (ContextMenuStrip)panel.GetType()
        .GetField("_menu", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(panel)!;
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException("Expected: " + message); }

    // The widget's windows open from a mouse click, so focus rings stay hidden until a key is pressed. The hidden desktop
    // has had no input at all and shows them; each capture asks for the state a mouse leaves (WM_CHANGEUISTATE: UIS_SET,
    // focus and access keys).
    private static void HideFocusCues(Form form) => SendMessage(form.Handle, 0x0127, 0x0003_0001, 0);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern nint SendMessage(nint window, int message, nint wParam, nint lParam);

    private static void Click(Control root, string name)
    {
        var button = Find<Button>(root, name);
        Check(button.CanSelect, name + " can be pressed");
        button.PerformClick();
    }

    // Posted layout and repaint run before a picture is taken.
    private static Task Settle() => Task.Delay(150);

    private static async Task Until(Func<bool> condition, string what)
    {
        var deadline = Environment.TickCount64 + 15_000;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline) throw new TimeoutException("The preview waited too long for " + what + ".");
            await Task.Delay(15);
        }
    }

    private static async Task<T> Modal<T>() where T : Form
    {
        T? dialog = null;
        await Until(() => (dialog = Application.OpenForms.OfType<T>().FirstOrDefault(form => form.Visible)) is not null, typeof(T).Name);
        return dialog!;
    }

    // The fixture makes no links. Should one be there, the folder stays rather than be walked through.
    private static void DeleteFixture(string root)
    {
        if (!Directory.Exists(root)) return;
        if (HasLink(new DirectoryInfo(root))) { Console.WriteLine("The preview fixture holds a link and stays at " + root); return; }
        Directory.Delete(root, true);
    }

    private static bool HasLink(DirectoryInfo folder) => folder.EnumerateFileSystemInfos().Any(entry =>
        entry.Attributes.HasFlag(FileAttributes.ReparsePoint) || entry is DirectoryInfo child && HasLink(child));

    private static ClaudeProfileIdentity Identity(ClaudeProfile profile) =>
        ClaudeProfileRuntime.ParseStatus(PreviewRunner.Status(profile.ConfigDirectory), profile);

    /// <summary>The design's clock, moved by the preview; the controller, the clients and every page read it.</summary>
    private sealed class PreviewClock(DateTimeOffset start) : TimeProvider
    {
        private long _ticks = start.UtcTicks;
        internal DateTimeOffset Now
        {
            get => GetUtcNow().ToOffset(Kst);
            set => Interlocked.Exchange(ref _ticks, value.UtcTicks);
        }
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
    }

    /// <summary>Claude's status for a made-up address per profile folder; a folder signed out reports no sign-in.</summary>
    private sealed class PreviewRunner : IClaudeProfileCommandRunner
    {
        private readonly HashSet<string> _signedOut = new(StringComparer.OrdinalIgnoreCase);
        private int _calls;
        internal int Calls => Volatile.Read(ref _calls);
        internal void SignOut(string directory) { lock (_signedOut) _signedOut.Add(directory); }
        internal static ClaudeProfileCommandResult Status(string directory) => new(0, JsonSerializer.Serialize(new
        {
            loggedIn = true, authMethod = "claude.ai", apiProvider = "firstParty",
            email = Path.GetFileName(directory) + "@example.test", configDirectory = directory
        }));
        public Task<ClaudeProfileCommandResult> RunStatusAsync(ProcessStartInfo start, CancellationToken token)
        {
            Interlocked.Increment(ref _calls);
            var directory = start.Environment["CLAUDE_CONFIG_DIR"]!;
            lock (_signedOut)
                if (_signedOut.Contains(directory))
                    return Task.FromResult(new ClaudeProfileCommandResult(1, JsonSerializer.Serialize(new { loggedIn = false })));
            return Task.FromResult(Status(directory));
        }
        public Task<int> RunInteractiveAsync(ProcessStartInfo start, CancellationToken token) => throw NoAccess("a Claude sign-in");
        public Task<int> RunQuietAsync(ProcessStartInfo start, CancellationToken token) => throw NoAccess("a Claude sign-out");
    }

    /// <summary>Usage per profile, answered at once or, for a profile held, when the preview releases it.</summary>
    private sealed class PreviewUsage
    {
        private readonly ConcurrentDictionary<string, ClaudeUsageSnapshot> _snapshots = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _gates = new(StringComparer.Ordinal);
        private readonly ConcurrentQueue<string> _read = new();
        private int _calls, _active, _most, _canceled;
        internal int Calls => Volatile.Read(ref _calls);
        /// <summary>The most reads at one moment, and the reads a cancellation stopped.</summary>
        internal int Most => Volatile.Read(ref _most);
        internal int Canceled => Volatile.Read(ref _canceled);
        /// <summary>The accounts read since the last call, in order.</summary>
        internal string[] TakeRead()
        {
            var ids = new List<string>();
            while (_read.TryDequeue(out var id)) ids.Add(id);
            return ids.ToArray();
        }
        internal void Set(string id, ClaudeUsageSnapshot snapshot) => _snapshots[id] = snapshot;
        internal void Hold(params string[] ids)
        {
            foreach (var id in ids) _gates[id] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        internal void Release(string id) { if (_gates.TryRemove(id, out var gate)) gate.TrySetResult(); }
        internal void ReleaseAll() { foreach (var id in _gates.Keys) Release(id); }
        internal IClaudeUsageSource For(string id) => new Source(this, id);

        private sealed class Source(PreviewUsage owner, string id) : IClaudeUsageSource
        {
            public async Task<ClaudeUsageSnapshot> ReadAsync(CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref owner._calls); owner._read.Enqueue(id);
                var active = Interlocked.Increment(ref owner._active);
                for (var most = Volatile.Read(ref owner._most); active > most; most = Volatile.Read(ref owner._most))
                    if (Interlocked.CompareExchange(ref owner._most, active, most) == most) break;
                try { if (owner._gates.TryGetValue(id, out var gate)) await gate.Task.WaitAsync(cancellationToken); }
                catch (OperationCanceledException) { Interlocked.Increment(ref owner._canceled); throw; }
                finally { Interlocked.Decrement(ref owner._active); }
                return owner._snapshots.TryGetValue(id, out var snapshot) ? snapshot
                    : throw new InvalidDataException("The design preview has no usage for this account.");
            }
        }
    }

    private sealed class PreviewWslBridge : IWslCodexBridge
    {
        public Task<IReadOnlyList<string>> ListRunningDistributionsAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<string>>(["Ubuntu"]);
        public Task<WslCodexProbe> ProbeAsync(string distribution, WslCodexTarget? expected, CancellationToken token) => Task.FromResult(new WslCodexProbe(distribution, WslCodexAvailability.Ready,
            new(distribution, 1000, "user", "/home/user", "/home/user/.codex", "/usr/local/bin/codex", "codex-cli 0.153.2"), 0, "ready"));
        public Task<byte[]> ReadAuthAsync(WslCodexTarget target, CancellationToken token) => throw NoAccess("WSL sign-in files");
        public Task ReplaceAuthAsync(WslCodexTarget target, string digest, byte[] auth, CancellationToken token) => throw NoAccess("WSL sign-in files");
        public Task<bool> VerifyOfficialClientAsync(WslCodexTarget target, CancellationToken token) => throw NoAccess("WSL Codex");
        public Task<byte[]> LoginAsync(WslCodexTarget target, CancellationToken token, Action<Uri>? onAuthorizationUrl = null) => throw NoAccess("a WSL sign-in");
    }
}
