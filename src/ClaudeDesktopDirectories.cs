using System.Text.Json;

namespace WeeklyUsageIndicator;

internal sealed class ClaudeDesktopRecoveryRequiredException()
    : InvalidOperationException("Claude 앱의 계정 교체가 끝나지 않았습니다. 기존 파일을 보존한 채 복구를 기다립니다.");

/// <summary>
/// Moves whole live directories on one volume;
/// it never opens, copies or snapshots their contents. A route is not a verified Desktop login.
/// </summary>
internal sealed class ClaudeDesktopDirectories
{
    private readonly string _live;
    private readonly string _root;
    private readonly string _catalog;
    private readonly string _journal;
    private readonly Action _requireStopped;
    private readonly Action<string>? _checkpoint;

    internal ClaudeDesktopDirectories(string liveDirectory, string root, Action requireStopped,
        Action<string>? checkpoint = null)
    {
        _live = Path.TrimEndingDirectorySeparator(Path.GetFullPath(liveDirectory));
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (Within(_live, _root) || Within(_root, _live) || Path.GetPathRoot(_live) == _live
            || Path.GetPathRoot(_root) == _root) throw new ArgumentException("Desktop profile paths must be separate.");
        _catalog = Path.Combine(_root, "desktop-directories.json");
        _journal = Path.Combine(_root, "desktop-switch.json");
        _requireStopped = requireStopped ?? throw new ArgumentNullException(nameof(requireStopped));
        _checkpoint = checkpoint;
    }

    internal bool NeedsRecovery => File.Exists(_journal);

    internal string? CurrentProfileId
    {
        get
        {
            using var lease = Acquire();
            if (!File.Exists(_catalog)) return null;
            if (NeedsRecovery) throw new ClaudeDesktopRecoveryRequiredException();
            var catalog = Read<Catalog>(_catalog);
            ValidateId(catalog.Current);
            if (!catalog.Profiles.TryGetValue(catalog.Current, out var identity))
                throw new ClaudeDesktopRecoveryRequiredException();
            RequireIdentity(_live, identity);
            return catalog.Current;
        }
    }

    internal void RegisterOriginal(string profileId)
    {
        ValidateId(profileId);
        using var lease = Acquire();
        if (File.Exists(_catalog)) return;
        if (NeedsRecovery) throw new ClaudeDesktopRecoveryRequiredException();
        CheckDirectory(_live);
        CheckVolume();
        Write(_catalog, new Catalog(profileId,
            new Dictionary<string, WindowsFileIdentity> { [profileId] = ClaudeSharedSettings.Identify(_live) }));
    }

    internal void PrepareEmpty(string profileId)
    {
        ValidateId(profileId);
        using var lease = Acquire();
        if (NeedsRecovery) throw new ClaudeDesktopRecoveryRequiredException();
        var catalog = Read<Catalog>(_catalog);
        if (catalog.Profiles.ContainsKey(profileId)) return;
        var path = Parked(profileId);
        if (Directory.Exists(path) || File.Exists(path)) throw new ClaudeDesktopRecoveryRequiredException();
        ClaudeProfileStore.CreatePrivateDirectory(path);
        CheckVolume();
        catalog.Profiles.Add(profileId, ClaudeSharedSettings.Identify(path));
        Write(_catalog, catalog);
    }

    internal void Switch(string profileId)
    {
        ValidateId(profileId);
        using var lease = Acquire();
        if (NeedsRecovery) throw new ClaudeDesktopRecoveryRequiredException();
        var catalog = Read<Catalog>(_catalog);
        ValidateId(catalog.Current);
        if (catalog.Current == profileId) { RequireIdentity(_live, catalog.Profiles[profileId]); return; }
        if (!catalog.Profiles.TryGetValue(profileId, out var target)) throw new ClaudeDesktopRecoveryRequiredException();
        var previous = catalog.Profiles[catalog.Current];
        var oldPark = Parked(catalog.Current);
        var newPark = Parked(profileId);
        CheckVolume();
        _requireStopped();
        RequireIdentity(_live, previous);
        RequireIdentity(newPark, target);
        if (Directory.Exists(oldPark) || File.Exists(oldPark)) throw new ClaudeDesktopRecoveryRequiredException();
        Write(_journal, new Journal(catalog.Current, profileId, previous, target));
        _requireStopped();
        // Both resolved absolute paths have been checked against this adapter's explicit live/parked roots.
        Directory.Move(_live, oldPark);
        _checkpoint?.Invoke("original-parked");
        _requireStopped();
        RequireIdentity(newPark, target);
        if (Directory.Exists(_live) || File.Exists(_live)) throw new ClaudeDesktopRecoveryRequiredException();
        Directory.Move(newPark, _live);
        _checkpoint?.Invoke("target-live");
        RequireIdentity(_live, target);
        Write(_catalog, catalog with { Current = profileId });
        File.Delete(_journal);
    }

    internal void Recover()
    {
        using var lease = Acquire();
        if (!NeedsRecovery) return;
        var journal = Read<Journal>(_journal);
        ValidateId(journal.Before); ValidateId(journal.After);
        var catalog = Read<Catalog>(_catalog);
        if (!catalog.Profiles.TryGetValue(journal.Before, out var oldId) || oldId != journal.BeforeIdentity
            || !catalog.Profiles.TryGetValue(journal.After, out var newId) || newId != journal.AfterIdentity)
            throw new ClaudeDesktopRecoveryRequiredException();
        _requireStopped();
        CheckVolume();
        var oldPark = Parked(journal.Before);
        var newPark = Parked(journal.After);
        if (IsIdentity(_live, newId) && IsIdentity(oldPark, oldId) && !Directory.Exists(newPark))
            Write(_catalog, catalog with { Current = journal.After });
        else if (IsIdentity(_live, oldId) && !Directory.Exists(oldPark) && IsIdentity(newPark, newId))
            Write(_catalog, catalog with { Current = journal.Before });
        else if (!Directory.Exists(_live) && !File.Exists(_live)
                 && IsIdentity(oldPark, oldId) && IsIdentity(newPark, newId))
        {
            Directory.Move(oldPark, _live);
            RequireIdentity(_live, oldId);
            Write(_catalog, catalog with { Current = journal.Before });
        }
        else throw new ClaudeDesktopRecoveryRequiredException();
        // Unknown directories, including a concurrently recreated live directory, are never removed or moved.
        File.Delete(_journal);
    }

    private FileStream Acquire()
    {
        CheckAncestors(Path.GetDirectoryName(_root)!);
        ClaudeProfileStore.CreatePrivateDirectory(_root);
        ClaudeProfileStore.CreatePrivateDirectory(Path.Combine(_root, "profiles"));
        CheckDirectory(_root);
        try { return new FileStream(Path.Combine(_root, ".desktop-route.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new InvalidOperationException("다른 Claude 계정 작업이 진행 중입니다."); }
    }

    private void CheckVolume()
    {
        CheckDirectory(_root);
        var parent = Path.GetDirectoryName(_live)!;
        CheckDirectory(parent);
        if (ClaudeSharedSettings.Identify(parent).Volume != ClaudeSharedSettings.Identify(_root).Volume)
            throw new InvalidOperationException("Claude 앱 프로필은 같은 드라이브 안에서만 교체할 수 있습니다.");
    }
    private string Parked(string id)
    {
        ValidateId(id);
        var path = Path.GetFullPath(Path.Combine(_root, "profiles", id));
        if (!Within(path, _root)) throw new ClaudeDesktopRecoveryRequiredException();
        return path;
    }
    private static void ValidateId(string id)
    {
        if (id != "default" && !Guid.TryParseExact(id, "N", out _)) throw new ClaudeDesktopRecoveryRequiredException();
    }
    private static bool Within(string path, string root) => ClaudeProfileStore.PathsEqual(path, root)
        || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static void CheckAncestors(string path)
    {
        for (var current = new DirectoryInfo(path); current is not null; current = current.Parent)
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new ClaudeDesktopRecoveryRequiredException();
    }
    private static void CheckDirectory(string path)
    {
        if (!Directory.Exists(path)) throw new ClaudeDesktopRecoveryRequiredException();
        CheckAncestors(path);
    }
    private static bool IsIdentity(string path, WindowsFileIdentity identity)
    {
        if (!Directory.Exists(path)) return false;
        CheckDirectory(path);
        return ClaudeSharedSettings.Identify(path) == identity;
    }
    private static void RequireIdentity(string path, WindowsFileIdentity identity)
    { if (!IsIdentity(path, identity)) throw new ClaudeDesktopRecoveryRequiredException(); }

    private static T Read<T>(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length is <= 0 or > 64 * 1024
            || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new ClaudeDesktopRecoveryRequiredException();
        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<T>(stream) ?? throw new ClaudeDesktopRecoveryRequiredException();
        }
        catch (JsonException) { throw new ClaudeDesktopRecoveryRequiredException(); }
    }
    private static void Write<T>(string path, T value)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 4096, FileOptions.WriteThrough))
            { JsonSerializer.Serialize(stream, value); stream.Flush(true); }
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private sealed record Catalog(string Current, Dictionary<string, WindowsFileIdentity> Profiles);
    private sealed record Journal(string Before, string After,
        WindowsFileIdentity BeforeIdentity, WindowsFileIdentity AfterIdentity);
}
