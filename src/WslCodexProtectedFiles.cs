using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace WeeklyUsageIndicator;

// Windows stores only CurrentUser-DPAPI envelopes. Linux writes have their own 0600 atomic path.
internal sealed class WslCodexProtectedFiles
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("WeeklyUsageIndicator.WslCodex.v1");
    private const int Maximum = 48 * 1024 * 1024;
    internal string Root { get; }
    internal WslCodexProtectedFiles(string root) => Root = Path.GetFullPath(root);

    internal IDisposable Acquire()
    {
        CodexAccountStore.RejectReparsePath(Root);
        var sid = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("Windows 사용자를 확인하지 못했습니다.");
        var security = new DirectorySecurity();
        security.SetOwner(sid);
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        var directory = new DirectoryInfo(Root);
        if (!directory.Exists) FileSystemAclExtensions.Create(directory, security);
        FileSystemAclExtensions.SetAccessControl(directory, security);
        var path = Resolve("store.lock");
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new AccountStoreBusyException(); }
    }

    internal bool Exists(string name) => File.Exists(Resolve(name));
    internal string[] Names(string pattern) => Directory.Exists(Root)
        ? Directory.GetFiles(Root, pattern).Select(Path.GetFileName).OfType<string>().ToArray() : [];

    internal T Read<T>(string name)
    {
        var path = Resolve(name);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is <= 0 or > Maximum) throw Corrupt();
        var encrypted = new byte[(int)stream.Length];
        stream.ReadExactly(encrypted);
        byte[]? plain = null;
        try
        {
            plain = Protect(encrypted, false);
            return JsonSerializer.Deserialize<T>(plain) ?? throw Corrupt();
        }
        catch (Exception e) when (e is JsonException or Win32Exception or CryptographicException) { throw Corrupt(); }
        finally { if (plain is not null) CryptographicOperations.ZeroMemory(plain); }
    }

    internal void Write<T>(string name, T value)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(value);
        var path = Resolve(name);
        var temporary = Resolve(".write-" + Guid.NewGuid().ToString("N"));
        try
        {
            if (plain.Length > Maximum) throw Corrupt();
            var encrypted = Protect(plain, true);
            var sid = WindowsIdentity.GetCurrent().User!;
            var security = new FileSecurity();
            security.SetOwner(sid);
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
            using (var stream = FileSystemAclExtensions.Create(new FileInfo(temporary), FileMode.CreateNew,
                FileSystemRights.FullControl, FileShare.None, 4096, FileOptions.WriteThrough, security))
            { stream.Write(encrypted); stream.Flush(true); }
            CodexAccountStore.RejectReparsePath(path);
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
            CodexAccountStore.RejectReparsePath(temporary);
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    internal void Delete(string name) => File.Delete(Resolve(name));

    private string Resolve(string name)
    {
        if (string.IsNullOrEmpty(name) || name != Path.GetFileName(name)) throw Corrupt();
        var path = Path.Combine(Root, name);
        CodexAccountStore.RejectReparsePath(path);
        return path;
    }

    private static InvalidOperationException Corrupt() => new("WSL 계정 보관함 또는 복구 기록을 읽을 수 없습니다. 원본은 보존했습니다.");
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref Blob data, string? description, ref Blob entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob result);
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref Blob data, IntPtr description, ref Blob entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob result);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr pointer);

    private static byte[] Protect(byte[] bytes, bool encrypt)
    {
        var input = new Blob { Length = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        var entropy = new Blob { Length = Entropy.Length, Data = Marshal.AllocHGlobal(Entropy.Length) };
        var result = new Blob();
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            Marshal.Copy(Entropy, 0, entropy.Data, Entropy.Length);
            var success = encrypt
                ? CryptProtectData(ref input, null, ref entropy, IntPtr.Zero, IntPtr.Zero, 1, out result)
                : CryptUnprotectData(ref input, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, 1, out result);
            if (!success) throw new Win32Exception(Marshal.GetLastWin32Error());
            var output = new byte[result.Length];
            Marshal.Copy(result.Data, output, 0, output.Length);
            return output;
        }
        finally
        {
            Zero(input); Marshal.FreeHGlobal(input.Data); Marshal.FreeHGlobal(entropy.Data);
            if (result.Data != IntPtr.Zero) { Zero(result); LocalFree(result.Data); }
        }
    }

    private static void Zero(Blob blob)
    {
        var empty = new byte[Math.Min(4096, blob.Length)];
        for (var offset = 0; offset < blob.Length; offset += empty.Length)
            Marshal.Copy(empty, 0, blob.Data + offset, Math.Min(empty.Length, blob.Length - offset));
    }
}
