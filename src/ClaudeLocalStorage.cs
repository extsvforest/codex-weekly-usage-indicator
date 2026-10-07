using System.Buffers.Binary;
using System.Text;

namespace WeeklyUsageIndicator;

/// <summary>
/// Reads and appends to the Chromium localStorage LevelDB that Claude Desktop's web UI uses
/// (<c>%APPDATA%\Claude\Local Storage\leveldb</c>). The sidebar keeps pins and groups there; the settings file is only
/// a mirror the UI rewrites on start. Writes happen only while Desktop is closed and the database is not locked: the
/// whole directory is backed up, one write batch is appended to the live log, and the result is read back. Anything
/// unexpected stops before writing; a failed read-back restores the backup.
/// </summary>
internal sealed class ClaudeLocalStorage(string directory, string origin = "https://claude.ai")
{
    private const int BlockSize = 32768;
    private const int HeaderSize = 7;
    private const ulong TableMagic = 0xdb4775248b80fb57;
    private const int MaxBackups = 3;
    internal string Directory { get; } = directory;

    /// <param name="Clean">Every live log ends on a complete record; otherwise the engine would drop or repair the
    /// tail on open, and nothing is written.</param>
    /// <param name="Version">The raw <c>VERSION</c> entry Chromium checks before using the database.</param>
    internal sealed record Snapshot(IReadOnlyDictionary<string, string?> Values, ulong LastSequence, string LogPath,
        bool Clean, string? Version);

    /// <summary>Test seam: runs after the batch is appended and before it is read back.</summary>
    internal Action? AfterAppend { get; set; }

    /// <summary>The newest value of each key (null when absent or deleted) and the database's last sequence number.</summary>
    /// <param name="whileOpen">Read alongside a running app: open files with full sharing. Only for read-only callers;
    /// a torn tail or a table replaced underneath throws, and the caller retries.</param>
    internal Snapshot Read(IReadOnlyCollection<string> keys, bool whileOpen = false)
    {
        byte[] ReadAll(string path) => whileOpen ? ReadShared(path) : File.ReadAllBytes(path);
        var wanted = keys.ToDictionary(key => Key(key), key => key, new BytesComparer());
        wanted[Encoding.ASCII.GetBytes("VERSION")] = "\0VERSION";
        var latest = new Dictionary<string, (ulong Sequence, byte[]? Value)>(StringComparer.Ordinal);
        void Offer(byte[] userKey, ulong sequence, byte[]? value)
        {
            if (!wanted.TryGetValue(userKey, out var name)) return;
            if (!latest.TryGetValue(name, out var current) || current.Sequence < sequence) latest[name] = (sequence, value);
        }

        // CURRENT names the manifest and must end with a newline, as LevelDB requires.
        var currentText = Encoding.UTF8.GetString(ReadAll(Path.Combine(Directory, "CURRENT")));
        if (!currentText.EndsWith('\n')) throw new InvalidDataException("CURRENT does not end with a newline.");
        var current = currentText[..^1];
        if (!current.StartsWith("MANIFEST-", StringComparison.Ordinal) || current.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidDataException("Unexpected CURRENT file.");
        var (lastSequence, logNumber, liveTables) = ReadManifest(ReadAll(Path.Combine(Directory, current)));
        foreach (var number in liveTables)
        {
            var path = Path.Combine(Directory, number.ToString("D6") + ".ldb");
            if (!File.Exists(path)) path = Path.Combine(Directory, number.ToString("D6") + ".sst");
            foreach (var (internalKey, value) in ReadTable(ReadAll(path)))
            {
                if (internalKey.Length < 8) throw new InvalidDataException("Short internal key.");
                var tag = BinaryPrimitives.ReadUInt64LittleEndian(internalKey.AsSpan(internalKey.Length - 8));
                var sequence = tag >> 8;
                lastSequence = Math.Max(lastSequence, sequence);
                Offer(internalKey[..^8], sequence, (tag & 0xff) == 1 ? value : null);
            }
        }
        var logs = System.IO.Directory.EnumerateFiles(Directory, "*.log")
            .Select(path => (Path: path, Number: ulong.TryParse(Path.GetFileNameWithoutExtension(path), out var n) ? n : 0))
            .Where(log => log.Number >= logNumber).OrderBy(log => log.Number).ToArray();
        if (logs.Length == 0) throw new InvalidDataException("No live log.");
        var clean = true;
        foreach (var (path, _) in logs)
        {
            var (batches, complete) = ParseLog(ReadAll(path));
            clean &= complete;
            foreach (var batch in batches) lastSequence = Math.Max(lastSequence, ReadBatch(batch, Offer));
        }
        var values = keys.ToDictionary(key => key, key => latest.TryGetValue(key, out var found) && found.Value is { } bytes
            ? DecodeValue(bytes) : null, StringComparer.Ordinal);
        var version = latest.TryGetValue("\0VERSION", out var raw) && raw.Value is { } versionBytes ? Encoding.ASCII.GetString(versionBytes) : null;
        return new Snapshot(values, lastSequence, logs[^1].Path, clean, version);
    }

    /// <summary>Appends one batch putting <paramref name="values"/>. Returns false, writing nothing, when the database
    /// is in use or changed since <paramref name="basis"/>; throws when the read-back fails after restoring the backup.</summary>
    internal bool Write(Snapshot basis, IReadOnlyDictionary<string, string> values, string backupRoot, Func<bool> desktopClosed)
    {
        if (values.Count == 0) return true;
        using var guard = TryLock();
        if (guard is null || !desktopClosed()) return false;
        var now = Read(values.Keys.ToArray());
        if (now.LastSequence != basis.LastSequence || now.LogPath != basis.LogPath || !now.Clean || now.Version != SupportedVersion)
            return false;
        // A copy of the whole directory stays available for manual recovery; failures below undo only the append.
        Backup(backupRoot);
        var sequence = basis.LastSequence + 1;
        var batch = new MemoryStream();
        Span<byte> header = stackalloc byte[12];
        BinaryPrimitives.WriteUInt64LittleEndian(header, sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], (uint)values.Count);
        batch.Write(header);
        foreach (var (key, value) in values)
        {
            batch.WriteByte(1);
            WriteSlice(batch, Key(key));
            WriteSlice(batch, EncodeValue(value));
        }
        if (!desktopClosed()) return false;
        var originalLength = new FileInfo(basis.LogPath).Length;
        try
        {
            using (var stream = new FileStream(basis.LogPath, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough))
            {
                if (stream.Length != originalLength) throw new InvalidDataException("The log changed.");
                stream.Write(Frame(batch.ToArray(), stream.Length));
                stream.Flush(flushToDisk: true);
            }
            AfterAppend?.Invoke();
            var check = Read(values.Keys.ToArray());
            if (!check.Clean || check.LastSequence != sequence + (ulong)values.Count - 1
                || values.Any(pair => check.Values[pair.Key] != pair.Value))
                throw new InvalidDataException("Read-back did not match.");
            return true;
        }
        catch
        {
            // Only the log grew: cut it back to its original length. Tables, manifest and CURRENT were never touched.
            using (var stream = new FileStream(basis.LogPath, FileMode.Open, FileAccess.Write, FileShare.Read))
            {
                if (stream.Length > originalLength) { stream.SetLength(originalLength); stream.Flush(flushToDisk: true); }
            }
            throw;
        }
    }

    /// <summary>The localStorage schema version Chromium writes and this reader was checked against.</summary>
    internal const string SupportedVersion = "1";

    internal static string BackupsFolder(string root) => Path.Combine(root, "localstorage-backups");

    private FileStream? TryLock()
    {
        try { return new FileStream(Path.Combine(Directory, "LOCK"), FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private void Backup(string root)
    {
        var folder = BackupsFolder(root);
        var target = Path.Combine(folder, DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssfff"));
        System.IO.Directory.CreateDirectory(target);
        foreach (var file in System.IO.Directory.EnumerateFiles(Directory))
            if (!Path.GetFileName(file).Equals("LOCK", StringComparison.OrdinalIgnoreCase))
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        foreach (var old in System.IO.Directory.EnumerateDirectories(folder).Order(StringComparer.Ordinal).SkipLast(MaxBackups))
            try { System.IO.Directory.Delete(old, recursive: true); } catch (IOException) { }
    }

    private byte[] Key(string key)
    {
        var prefix = Encoding.UTF8.GetBytes("_" + origin);
        var encoded = EncodeValue(key);
        var bytes = new byte[prefix.Length + 1 + encoded.Length];
        prefix.CopyTo(bytes, 0);
        encoded.CopyTo(bytes, prefix.Length + 1);
        return bytes;
    }

    /// <summary>Chromium stores Latin-1 strings with a 1 prefix and anything else as UTF-16LE with a 0 prefix.</summary>
    internal static byte[] EncodeValue(string text)
    {
        if (text.All(character => character <= 'ÿ'))
        {
            var latin = new byte[text.Length + 1];
            latin[0] = 1;
            for (var index = 0; index < text.Length; index++) latin[index + 1] = (byte)text[index];
            return latin;
        }
        var wide = Encoding.Unicode.GetBytes(text);
        var bytes = new byte[wide.Length + 1];
        wide.CopyTo(bytes, 1);
        return bytes;
    }

    internal static string DecodeValue(byte[] bytes) => bytes.Length == 0 ? throw new InvalidDataException("Empty value.")
        : bytes[0] switch
        {
            1 => Encoding.Latin1.GetString(bytes, 1, bytes.Length - 1),
            0 => Encoding.Unicode.GetString(bytes, 1, bytes.Length - 1),
            _ => throw new InvalidDataException("Unknown value encoding.")
        };

    // ---- LevelDB formats -------------------------------------------------------------------------------------------

    private static byte[] ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static (ulong LastSequence, ulong LogNumber, IReadOnlyList<ulong> Tables) ReadManifest(byte[] manifest)
    {
        ulong lastSequence = 0, logNumber = 0;
        bool hasLog = false, hasNext = false, hasSequence = false;
        var tables = new HashSet<ulong>();
        var (edits, complete) = ParseLog(manifest);
        if (!complete) throw new InvalidDataException("The manifest ends mid-record.");
        foreach (var edit in edits)
        {
            var reader = new Reader(edit);
            while (!reader.End)
            {
                switch (reader.Varint())
                {
                    case 1: reader.Slice(); break; // comparator
                    case 2: logNumber = reader.Varint(); hasLog = true; break;
                    case 3: reader.Varint(); hasNext = true; break; // next file
                    case 4: lastSequence = Math.Max(lastSequence, reader.Varint()); hasSequence = true; break;
                    case 5: reader.Varint(); reader.Slice(); break; // compact pointer
                    case 6: reader.Varint(); tables.Remove(reader.Varint()); break; // deleted file
                    case 7: reader.Varint(); tables.Add(reader.Varint()); reader.Varint(); reader.Slice(); reader.Slice(); break;
                    case 9: reader.Varint(); break; // previous log
                    default: throw new InvalidDataException("Unknown manifest field.");
                }
            }
        }
        // LevelDB refuses to open without these.
        if (!hasLog || !hasNext || !hasSequence) throw new InvalidDataException("The manifest lacks required fields.");
        return (lastSequence, logNumber, tables.Order().ToArray());
    }

    /// <summary>Returns the highest sequence number in the batch, reporting each record.</summary>
    private static ulong ReadBatch(byte[] batch, Action<byte[], ulong, byte[]?> offer)
    {
        if (batch.Length < 12) throw new InvalidDataException("Short batch.");
        var sequence = BinaryPrimitives.ReadUInt64LittleEndian(batch);
        var count = BinaryPrimitives.ReadUInt32LittleEndian(batch.AsSpan(8));
        var reader = new Reader(batch, 12);
        for (var index = 0u; index < count; index++)
        {
            var type = reader.Byte();
            var key = reader.Slice();
            if (type == 1) offer(key, sequence + index, reader.Slice());
            else if (type == 0) offer(key, sequence + index, null);
            else throw new InvalidDataException("Unknown batch record.");
        }
        if (!reader.End) throw new InvalidDataException("Trailing batch bytes.");
        return count == 0 ? sequence : sequence + count - 1;
    }

    internal static IEnumerable<byte[]> ReadLog(byte[] data, bool strict) => ParseLog(data).Records;

    /// <summary>Reassembles log records the way LevelDB's reader does. Corruption (a bad checksum, a record longer than
    /// its block, a fragment out of order) throws. <c>Complete</c> is false when the file ends inside a record.</summary>
    internal static (List<byte[]> Records, bool Complete) ParseLog(byte[] data)
    {
        var records = new List<byte[]>();
        var pending = new MemoryStream();
        var inFragment = false;
        var position = 0;
        while (position < data.Length)
        {
            var left = BlockSize - position % BlockSize;
            if (left < HeaderSize)
            {
                // Block trailer: must be zero padding.
                if (data.AsSpan(position, Math.Min(left, data.Length - position)).ContainsAnyExcept((byte)0))
                    throw new InvalidDataException("Bad block trailer.");
                position += left;
                continue;
            }
            if (position + HeaderSize > data.Length) return (records, false);
            var crc = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(position));
            var length = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(position + 4));
            var type = data[position + 6];
            if (HeaderSize + length > left) throw new InvalidDataException("Record longer than its block.");
            if (type == 0 && length == 0) { position += left; continue; } // preallocated space
            if (position + HeaderSize + length > data.Length) return (records, false);
            if (Mask(Crc32C.Compute(data.AsSpan(position + 6, 1 + length))) != crc) throw new InvalidDataException("Log checksum mismatch.");
            var payload = data.AsSpan(position + HeaderSize, length);
            position += HeaderSize + length;
            switch (type)
            {
                case 1:
                    if (inFragment) throw new InvalidDataException("Full record inside a fragmented one.");
                    records.Add(payload.ToArray()); break;
                case 2:
                    if (inFragment) throw new InvalidDataException("First fragment inside a fragmented one.");
                    pending.SetLength(0); pending.Write(payload); inFragment = true; break;
                case 3:
                    if (!inFragment) throw new InvalidDataException("Middle fragment without a first.");
                    pending.Write(payload); break;
                case 4:
                    if (!inFragment) throw new InvalidDataException("Last fragment without a first.");
                    pending.Write(payload); records.Add(pending.ToArray()); pending.SetLength(0); inFragment = false; break;
                default: throw new InvalidDataException("Unknown log record type.");
            }
        }
        return (records, !inFragment);
    }

    /// <summary>Frames <paramref name="payload"/> as log records starting at <paramref name="offset"/> in the file.</summary>
    internal static byte[] Frame(byte[] payload, long offset)
    {
        var output = new MemoryStream();
        var position = offset;
        var index = 0;
        var first = true;
        do
        {
            var left = (int)(BlockSize - position % BlockSize);
            if (left < HeaderSize)
            {
                output.Write(new byte[left]);
                position += left;
                left = BlockSize;
            }
            var available = left - HeaderSize;
            var fragment = Math.Min(available, payload.Length - index);
            var last = index + fragment == payload.Length;
            byte type = first && last ? (byte)1 : first ? (byte)2 : last ? (byte)4 : (byte)3;
            var record = new byte[HeaderSize + fragment];
            record[6] = type;
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(4), (ushort)fragment);
            payload.AsSpan(index, fragment).CopyTo(record.AsSpan(HeaderSize));
            BinaryPrimitives.WriteUInt32LittleEndian(record, Mask(Crc32C.Compute(record.AsSpan(6, 1 + fragment))));
            output.Write(record);
            position += record.Length;
            index += fragment;
            first = false;
        } while (index < payload.Length);
        return output.ToArray();
    }

    private static IEnumerable<(byte[] Key, byte[] Value)> ReadTable(byte[] data)
    {
        if (data.Length < 48 || BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(data.Length - 8)) != TableMagic)
            throw new InvalidDataException("Not a table.");
        var footer = new Reader(data[^48..]);
        footer.Varint(); footer.Varint(); // metaindex handle
        var indexOffset = footer.Varint(); var indexSize = footer.Varint();
        foreach (var (_, handle) in Block(TableBlock(data, indexOffset, indexSize)))
        {
            var reader = new Reader(handle);
            var offset = reader.Varint(); var size = reader.Varint();
            foreach (var entry in Block(TableBlock(data, offset, size))) yield return entry;
        }
    }

    private static byte[] TableBlock(byte[] data, ulong offset, ulong size)
    {
        if (offset + size + 5 > (ulong)data.Length) throw new InvalidDataException("Block out of range.");
        var raw = data.AsSpan((int)offset, (int)size);
        var crc = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan((int)(offset + size) + 1));
        if (Mask(Crc32C.Compute(data.AsSpan((int)offset, (int)size + 1))) != crc) throw new InvalidDataException("Table checksum mismatch.");
        return data[(int)(offset + size)] switch
        {
            0 => raw.ToArray(),
            1 => Snappy.Decompress(raw),
            _ => throw new InvalidDataException("Unknown block compression.")
        };
    }

    private static List<(byte[] Key, byte[] Value)> Block(byte[] block)
    {
        var restarts = BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(block.Length - 4));
        var end = block.Length - 4 - 4 * (int)restarts;
        var reader = new Reader(block[..end]);
        var key = Array.Empty<byte>();
        var entries = new List<(byte[], byte[])>();
        while (!reader.End)
        {
            var shared = (int)reader.Varint(); var unshared = (int)reader.Varint(); var valueLength = (int)reader.Varint();
            var next = new byte[shared + unshared];
            key.AsSpan(0, shared).CopyTo(next);
            reader.Bytes(unshared).CopyTo(next.AsSpan(shared));
            key = next;
            entries.Add((key, reader.Bytes(valueLength).ToArray()));
        }
        return entries;
    }

    private static void WriteSlice(Stream stream, byte[] bytes)
    {
        var length = (ulong)bytes.Length;
        while (length >= 0x80) { stream.WriteByte((byte)(length | 0x80)); length >>= 7; }
        stream.WriteByte((byte)length);
        stream.Write(bytes);
    }

    private static uint Mask(uint crc) => ((crc >> 15) | (crc << 17)) + 0xa282ead8;

    private sealed class Reader(byte[] data, int position = 0)
    {
        private int _position = position;
        internal bool End => _position >= data.Length;
        internal byte Byte() => _position < data.Length ? data[_position++] : throw new InvalidDataException("Truncated.");
        internal ulong Varint()
        {
            ulong result = 0;
            for (var shift = 0; shift < 64; shift += 7)
            {
                var value = Byte();
                result |= (ulong)(value & 0x7f) << shift;
                if (value < 0x80) return result;
            }
            throw new InvalidDataException("Bad varint.");
        }
        internal ReadOnlySpan<byte> Bytes(int count)
        {
            if (count < 0 || _position + count > data.Length) throw new InvalidDataException("Truncated.");
            var span = data.AsSpan(_position, count);
            _position += count;
            return span;
        }
        internal byte[] Slice() => Bytes((int)Varint()).ToArray();
    }

    private sealed class BytesComparer : IEqualityComparer<byte[]>
    {
        public bool Equals(byte[]? x, byte[]? y) => x is not null && y is not null && x.AsSpan().SequenceEqual(y);
        public int GetHashCode(byte[] value)
        {
            var hash = new HashCode();
            hash.AddBytes(value);
            return hash.ToHashCode();
        }
    }
}

internal static class Crc32C
{
    private static readonly uint[] Table = Enumerable.Range(0, 256).Select(index =>
    {
        var crc = (uint)index;
        for (var bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0x82F63B78u : crc >> 1;
        return crc;
    }).ToArray();

    internal static uint Compute(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in data) crc = Table[(crc ^ value) & 0xff] ^ (crc >> 8);
        return ~crc;
    }
}

internal static class Snappy
{
    internal static byte[] Decompress(ReadOnlySpan<byte> input)
    {
        var position = 0;
        ulong length = 0;
        for (var shift = 0; ; shift += 7)
        {
            if (position >= input.Length || shift > 35) throw new InvalidDataException("Bad snappy length.");
            var value = input[position++];
            length |= (ulong)(value & 0x7f) << shift;
            if (value < 0x80) break;
        }
        var output = new byte[length];
        var written = 0;
        while (position < input.Length)
        {
            var tag = input[position++];
            int count, offset;
            switch (tag & 3)
            {
                case 0:
                    count = tag >> 2;
                    if (count >= 60)
                    {
                        var size = count - 59;
                        count = 0;
                        for (var index = 0; index < size; index++) count |= input[position++] << (8 * index);
                    }
                    count++;
                    input.Slice(position, count).CopyTo(output.AsSpan(written));
                    position += count; written += count;
                    continue;
                case 1: count = ((tag >> 2) & 7) + 4; offset = ((tag >> 5) << 8) | input[position++]; break;
                case 2: count = (tag >> 2) + 1; offset = input[position] | input[position + 1] << 8; position += 2; break;
                default: count = (tag >> 2) + 1; offset = BinaryPrimitives.ReadInt32LittleEndian(input.Slice(position)); position += 4; break;
            }
            if (offset <= 0 || offset > written || written + count > output.Length) throw new InvalidDataException("Bad snappy copy.");
            for (var index = 0; index < count; index++, written++) output[written] = output[written - offset];
        }
        if (written != output.Length) throw new InvalidDataException("Snappy length mismatch.");
        return output;
    }
}
