using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace WeeklyUsageIndicator;

internal sealed class ClaudeSettingsConflictException()
    : InvalidOperationException("이 Claude 프로필의 설정이 따로 변경되었습니다. 기존 설정을 보존했습니다.");

internal sealed record WindowsFileIdentity(uint Volume, uint High, uint Low);
internal sealed record SettingsFileStamp(WindowsFileIdentity Identity, long Length, long WrittenUtcTicks);

/// <summary>Links settings by file identity, never reading or copying settings values or authentication.</summary>
internal sealed class ClaudeSharedSettings(ClaudeProfileStore store)
{
    internal void Prepare(ClaudeProfile profile)
    {
        store.ValidateProfile(profile);
        if (profile.IsDefault) return;
        var source = Path.Combine(store.DefaultConfigDirectory, "settings.json");
        var target = Path.Combine(profile.ConfigDirectory, "settings.json");
        var record = Path.Combine(profile.ConfigDirectory, ".indicator-settings-link.json");
        if (!File.Exists(source))
        {
            if (File.Exists(record)) throw new ClaudeSettingsConflictException();
            return;
        }
        var sourceStamp = Stamp(source);
        if (File.Exists(target))
        {
            var targetStamp = Stamp(target);
            if (targetStamp.Identity == sourceStamp.Identity) { Save(record, sourceStamp); return; }
            var previous = Read(record);
            // File ID alone misses in-place edits after the two names split. Preserve those edits too.
            if (previous is null || targetStamp != previous) throw new ClaudeSettingsConflictException();
            // Keep the detached file rather than deleting it. Move fails if another writer owns it.
            var detached = Path.Combine(profile.ConfigDirectory,
                "settings.detached-" + Guid.NewGuid().ToString("N") + ".json");
            File.Move(target, detached, overwrite: false);
            try { Create(source, target, record); }
            catch
            {
                if (!File.Exists(target)) File.Move(detached, target, overwrite: false);
                throw;
            }
            return;
        }
        if (File.Exists(record)) throw new ClaudeSettingsConflictException();
        Create(source, target, record);
    }

    private static void Create(string source, string target, string record)
    {
        if (!CreateHardLink(target, source, IntPtr.Zero))
            throw new IOException("Claude 설정을 연결하지 못했습니다. 기존 파일은 보존했습니다.",
                new Win32Exception(Marshal.GetLastWin32Error()));
        var shared = Stamp(target);
        if (shared.Identity != Stamp(source).Identity) throw new ClaudeSettingsConflictException();
        Save(record, shared);
    }

    private static SettingsFileStamp Stamp(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new ClaudeSettingsConflictException();
        var info = new FileInfo(path);
        return new SettingsFileStamp(Identify(path), info.Length, info.LastWriteTimeUtc.Ticks);
    }

    internal static WindowsFileIdentity Identify(string path)
    {
        // Zero data-access rights: this handle is used only for filesystem metadata.
        using var handle = CreateFile(path, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var info)) throw new Win32Exception();
        return new WindowsFileIdentity(info.Volume, info.IndexHigh, info.IndexLow);
    }

    private static SettingsFileStamp? Read(string path)
    {
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length is <= 0 or > 4096
            || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new ClaudeSettingsConflictException();
        try { return JsonSerializer.Deserialize<SettingsFileStamp>(File.ReadAllText(path))
                ?? throw new ClaudeSettingsConflictException(); }
        catch (JsonException) { throw new ClaudeSettingsConflictException(); }
    }

    private static void Save(string path, SettingsFileStamp stamp)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 4096, FileOptions.WriteThrough))
            { JsonSerializer.Serialize(stream, stamp); stream.Flush(true); }
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct FileInformation
    {
        public uint Attributes; public System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateHardLink(string link, string existing, IntPtr security);
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandle(
        SafeFileHandle handle, out FileInformation information);
}
