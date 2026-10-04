using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WeeklyUsageIndicator;

/// <summary>
/// Keeps Claude Desktop's sidebar pins and session groups in step between a session record and the copies this
/// utility made of it in other accounts. Desktop keeps both in its settings file under
/// <c>preferences.epitaxyPrefs</c>, keyed by list id (<c>code:local_…</c>): pins in one ordered list shared by every
/// account (<c>dframe-local-slice.pinnedOrder</c>, mirrored in <c>starred-local-code-sessions</c>), groups per account
/// (<c>dframe-group-scopes[account/org]</c>). A copy has its own list id, so without this its pins and groups looked
/// reset after an account switch. Runs only while Desktop is closed, changes only those entries, keeps every other
/// setting byte-for-byte in meaning, and skips a part whose shape differs from the reviewed one.
/// </summary>
internal static class ClaudeSidebarPrefs
{
    internal sealed record Link(string AccountA, string IdA, string AccountB, string IdB, string Key);
    internal sealed record Outcome(int Pinned, int Unpinned, int Grouped, int Ungrouped, int Moved, string? Skipped)
    {
        /// <summary>Every part was examined and written: nothing is left for a later run.</summary>
        internal bool Complete => Skipped is null or "no_config" or "no_sidebar";
    }
    private sealed record Side(bool Pinned, string? Group);
    private const string MirrorFile = "sidebar-mirror.json";
    private const string BackupFile = "claude_desktop_config.before-sidebar.json";
    private static readonly JsonSerializerOptions Output = new()
    { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver() };

    internal static string ListId(string file) => "code:" + (file.EndsWith(".json", StringComparison.Ordinal) ? file[..^5] : file);

    /// <summary>Every live pair of an original record and a copy this utility made of it.</summary>
    internal static IReadOnlyList<Link> Links(ClaudeShelfLedger ledger, string sessionsRoot)
    {
        var links = new List<Link>();
        foreach (var entry in ledger.Created.Where(entry => !entry.Pending && !entry.Retired))
        {
            if (entry.Source.Split('/') is not [var account, var org] || !Guid.TryParse(account, out _) || !Guid.TryParse(org, out _)
                || !entry.SourceFile.StartsWith("local_", StringComparison.Ordinal)
                || ClaudeShelfLedger.EntryPath(sessionsRoot, entry) is not { } copy || !File.Exists(copy)
                || !File.Exists(Path.Combine(sessionsRoot, account, org, entry.SourceFile))) continue;
            links.Add(new Link(entry.Source, ListId(entry.SourceFile), entry.Target, ListId(entry.File), entry.Target + "/" + entry.File));
        }
        return links;
    }

    /// <summary>
    /// Brings each pair to the same pin and group. The side that changed since the last sync wins; on the first sync,
    /// or when both changed, a pin on either side wins and the original's group wins. <paramref name="moved"/> maps a
    /// copy that was just archived to the newer record that replaced it; its pin and group move there.
    /// </summary>
    internal static Outcome Sync(string configPath, string stateDirectory, IReadOnlyList<Link> links,
        IReadOnlyDictionary<string, string> moved, Func<bool> desktopClosed)
    {
        byte[] original;
        JsonObject root;
        try
        {
            // Read directly: File.Exists also says false when access fails, and only a confirmed absence is done.
            original = File.ReadAllBytes(configPath);
            root = JsonNode.Parse(original) as JsonObject ?? throw new JsonException();
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        { return Skip("no_config"); }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        { return Skip("unreadable"); }
        // Only a missing section means there is nothing to keep in step; a section of another shape waits.
        if (root["preferences"] is null || root["preferences"] is JsonObject { } absent && absent["epitaxyPrefs"] is null)
            return Skip("no_sidebar");
        if (root["preferences"] is not JsonObject preferences || preferences["epitaxyPrefs"] is not JsonObject prefs)
            return Skip("shape");
        var pins = Pins.Read(prefs);
        var groups = Groups.Read(prefs);
        if (pins is null && groups is null) return Skip("shape");
        var mirror = ReadMirror(stateDirectory);
        int pinned = 0, unpinned = 0, grouped = 0, ungrouped = 0, movedCount = 0;

        foreach (var (from, to) in moved)
        {
            var changed = pins?.Move(from, to) ?? false;
            changed |= groups?.Move(from, to) ?? false;
            if (changed) movedCount++;
        }

        var next = new JsonObject();
        foreach (var link in links)
        {
            var a = new Side(pins?.Has(link.IdA) ?? false, groups?.NameOf(link.AccountA, link.IdA));
            var b = new Side(pins?.Has(link.IdB) ?? false, groups?.NameOf(link.AccountB, link.IdB));
            var last = mirror[link.Key] is JsonObject state
                ? new Side(state["pinned"] is JsonValue p && p.TryGetValue<bool>(out var flag) && flag,
                    state["group"] is JsonValue g && g.TryGetValue<string>(out var name) ? name : null)
                : null;
            var pin = last is null ? Resolve(a.Pinned, b.Pinned, false, false, a.Pinned || b.Pinned)
                : Resolve(a.Pinned, b.Pinned, last.Pinned, true, a.Pinned || b.Pinned);
            var group = Resolve(a.Group, b.Group, last?.Group, last is not null, last is null ? a.Group ?? b.Group : a.Group);
            if (pins is not null)
            {
                if (pins.Set(link.IdA, pin, link.IdB)) { if (pin) pinned++; else unpinned++; }
                if (pins.Set(link.IdB, pin, link.IdA)) { if (pin) pinned++; else unpinned++; }
            }
            if (groups is not null)
            {
                if (groups.Set(link.AccountA, link.IdA, group)) { if (group is null) ungrouped++; else grouped++; }
                if (groups.Set(link.AccountB, link.IdB, group)) { if (group is null) ungrouped++; else grouped++; }
            }
            next[link.Key] = new JsonObject
            {
                ["pinned"] = pins is null ? last?.Pinned ?? false : pin,
                ["group"] = groups is null ? last?.Group : group
            };
        }

        var skipped = pins is null ? "pins_shape" : groups is null ? "groups_shape" : null;
        if (pinned + unpinned + grouped + ungrouped + movedCount > 0)
        {
            var bytes = Encoding.UTF8.GetBytes(root.ToJsonString(Output).Replace("\r\n", "\n"));
            // The app may have started, or changed its settings, since they were read: then write nothing.
            if (!desktopClosed()) return Skip("desktop_running");
            try
            {
                if (!File.ReadAllBytes(configPath).AsSpan().SequenceEqual(original)) return Skip("changed");
                Directory.CreateDirectory(stateDirectory);
                File.WriteAllBytes(Path.Combine(stateDirectory, BackupFile), original);
                if (!Replace(configPath, bytes, () => desktopClosed() && File.ReadAllBytes(configPath).AsSpan().SequenceEqual(original)))
                    return Skip("changed");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return Skip("write_failed"); }
        }
        WriteMirror(stateDirectory, next);
        return new Outcome(pinned, unpinned, grouped, ungrouped, movedCount, skipped);
    }

    private static T Resolve<T>(T a, T b, T last, bool hasLast, T merged)
    {
        if (EqualityComparer<T>.Default.Equals(a, b)) return a;
        if (hasLast && EqualityComparer<T>.Default.Equals(a, last)) return b;
        if (hasLast && EqualityComparer<T>.Default.Equals(b, last)) return a;
        return merged;
    }

    private static Outcome Skip(string reason) => new(0, 0, 0, 0, 0, reason);

    /// <summary>Writes beside the file, then replaces it only if <paramref name="stillSafe"/> holds at the last moment.
    /// This narrows the window; it is not an exclusive lock against another writer.</summary>
    private static bool Replace(string path, byte[] bytes, Func<bool> stillSafe)
    {
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { stream.Write(bytes); stream.Flush(flushToDisk: true); }
            if (!stillSafe()) return false;
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static JsonObject ReadMirror(string directory)
    {
        try
        {
            var path = Path.Combine(directory, MirrorFile);
            return File.Exists(path) && JsonNode.Parse(File.ReadAllBytes(path))?["links"] is JsonObject links
                ? (JsonObject)links.DeepClone() : [];
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException) { return []; }
    }

    private static void WriteMirror(string directory, JsonObject links)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, MirrorFile);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(new JsonObject { ["version"] = 1, ["links"] = links }, Output));
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    /// <summary>The shared pin list and its starred mirror. Null when either has an unexpected shape.</summary>
    private sealed class Pins
    {
        private readonly JsonObject _prefs;
        private JsonArray? _order;
        private readonly JsonArray? _starred;

        private Pins(JsonObject prefs, JsonArray? order, JsonArray? starred) { _prefs = prefs; _order = order; _starred = starred; }

        internal static Pins? Read(JsonObject prefs)
        {
            JsonArray? order = null;
            if (prefs["dframe-local-slice"] is { } slice)
            {
                if (slice is not JsonObject sliceObject) return null;
                if (sliceObject["pinnedOrder"] is { } value)
                {
                    if (value is not JsonArray array || !AllStrings(array)) return null;
                    order = array;
                }
            }
            JsonArray? starred = null;
            if (prefs["starred-local-code-sessions"] is { } listed)
            {
                if (listed is not JsonArray array || !AllStrings(array)) return null;
                starred = array;
            }
            return new Pins(prefs, order, starred);
        }

        internal bool Has(string id) => _order is not null && IndexOf(_order, id) >= 0;

        /// <summary>Pins next to <paramref name="partner"/> or unpins. Returns whether anything changed.</summary>
        internal bool Set(string id, bool pinned, string partner)
        {
            if (Has(id) == pinned) return false;
            var plain = id["code:".Length..];
            if (pinned)
            {
                if (_order is null)
                {
                    var slice = _prefs["dframe-local-slice"] as JsonObject ?? (JsonObject)(_prefs["dframe-local-slice"] = new JsonObject());
                    _order = (JsonArray)(slice["pinnedOrder"] = new JsonArray());
                }
                var at = IndexOf(_order, partner);
                _order.Insert(at >= 0 ? at + 1 : _order.Count, id);
                if (_starred is not null && IndexOf(_starred, partner["code:".Length..]) >= 0 && IndexOf(_starred, plain) < 0)
                    _starred.Add(plain);
            }
            else
            {
                _order!.RemoveAt(IndexOf(_order, id));
                if (_starred is not null && IndexOf(_starred, plain) is var index and >= 0) _starred.RemoveAt(index);
            }
            return true;
        }

        internal bool Move(string from, string to)
        {
            var changed = false;
            foreach (var (list, a, b) in new[] { (_order, from, to), (_starred, from["code:".Length..], to["code:".Length..]) })
            {
                if (list is null || IndexOf(list, a) is not (var index and >= 0)) continue;
                if (IndexOf(list, b) >= 0) list.RemoveAt(index); else list[index] = b;
                changed = true;
            }
            return changed;
        }
    }

    /// <summary>Per-account groups: <c>{groups:[{id,name}], assignments:{listId: groupId}, order:{groupId:[listId]}}</c>.</summary>
    private sealed class Groups
    {
        private readonly JsonObject _prefs;
        private JsonObject? _scopes;

        private Groups(JsonObject prefs, JsonObject? scopes) { _prefs = prefs; _scopes = scopes; }

        internal static Groups? Read(JsonObject prefs)
        {
            if (prefs["dframe-group-scopes"] is not { } value) return new Groups(prefs, null);
            if (value is not JsonObject scopes) return null;
            foreach (var (_, scope) in scopes)
                if (!ValidScope(scope)) return null;
            return new Groups(prefs, scopes);
        }

        private static bool ValidScope(JsonNode? scope) =>
            scope is JsonObject value
            && (value["groups"] is null || value["groups"] is JsonArray groups && groups.All(group =>
                group is JsonObject entry && Text(entry, "id") is not null && Text(entry, "name") is not null))
            && (value["assignments"] is null || value["assignments"] is JsonObject assignments
                && assignments.All(pair => pair.Value is JsonValue item && item.TryGetValue<string>(out _)))
            && (value["order"] is null || value["order"] is JsonObject order
                && order.All(pair => pair.Value is JsonArray list && AllStrings(list)));

        internal string? NameOf(string account, string id)
        {
            if (_scopes?[account] is not JsonObject scope || scope["assignments"]?[id] is not JsonValue assigned
                || !assigned.TryGetValue<string>(out var groupId)) return null;
            return (scope["groups"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(group => Text(group, "id") == groupId) is { } found
                ? Text(found, "name") : null;
        }

        /// <summary>Puts <paramref name="id"/> in the group named <paramref name="name"/> in that account, creating the
        /// group there if needed, or takes it out of any group. Returns whether anything changed.</summary>
        internal bool Set(string account, string id, string? name)
        {
            if (NameOf(account, id) == name) return false;
            var scope = Scope(account, create: name is not null);
            if (scope is null) return false;
            var assignments = ChildObject(scope, "assignments");
            var order = ChildObject(scope, "order");
            foreach (var (_, list) in order) if (list is JsonArray members && IndexOf(members, id) is var index and >= 0) members.RemoveAt(index);
            if (name is null) { assignments.Remove(id); return true; }
            var groups = scope["groups"] as JsonArray ?? (JsonArray)(scope["groups"] = new JsonArray());
            var group = groups.OfType<JsonObject>().FirstOrDefault(entry => Text(entry, "name") == name);
            if (group is null) groups.Add(group = new JsonObject { ["id"] = "cg-" + Guid.NewGuid().ToString("D"), ["name"] = name });
            var groupId = Text(group, "id")!;
            assignments[id] = groupId;
            (order[groupId] as JsonArray ?? (JsonArray)(order[groupId] = new JsonArray())).Add(id);
            return true;
        }

        internal bool Move(string from, string to)
        {
            var changed = false;
            foreach (var (_, value) in _scopes ?? [])
            {
                if (value is not JsonObject scope || scope["assignments"] is not JsonObject assignments
                    || assignments[from] is not JsonValue assigned || !assigned.TryGetValue<string>(out var groupId)) continue;
                assignments.Remove(from);
                var order = scope["order"] as JsonObject;
                if (assignments[to] is null)
                {
                    assignments[to] = groupId;
                    if (order?[groupId] is JsonArray members && IndexOf(members, from) is var index and >= 0) members[index] = to;
                }
                else if (order is not null)
                    foreach (var (_, list) in order) if (list is JsonArray members && IndexOf(members, from) is var index and >= 0) members.RemoveAt(index);
                changed = true;
            }
            return changed;
        }

        private JsonObject? Scope(string account, bool create)
        {
            if (_scopes?[account] is JsonObject existing) return existing;
            if (!create) return null;
            _scopes ??= (JsonObject)(_prefs["dframe-group-scopes"] = new JsonObject());
            return (JsonObject)(_scopes[account] = new JsonObject { ["groups"] = new JsonArray(), ["assignments"] = new JsonObject(), ["order"] = new JsonObject() });
        }

        private static JsonObject ChildObject(JsonObject parent, string name) =>
            parent[name] as JsonObject ?? (JsonObject)(parent[name] = new JsonObject());
    }

    private static bool AllStrings(JsonArray array) => array.All(item => item is JsonValue value && value.TryGetValue<string>(out _));
    private static int IndexOf(JsonArray array, string text)
    {
        for (var index = 0; index < array.Count; index++)
            if (array[index] is JsonValue value && value.TryGetValue<string>(out var item) && item == text) return index;
        return -1;
    }
    private static string? Text(JsonObject node, string name) => node[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
