using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace WeeklyUsageIndicator;

// Private to this process: the portable app never installs fonts on the user's PC.
internal static class AccountFonts
{
    private static readonly PrivateFontCollection Families = new();
    private static readonly List<IntPtr> Buffers = new();
    private static readonly List<IntPtr> Registrations = new();

    static AccountFonts()
    {
        foreach (var weight in new[] { "Regular", "SemiBold", "Bold" })
        {
            using var stream = typeof(AccountFonts).Assembly.GetManifestResourceStream($"WeeklyUsageIndicator.Assets.Fonts.Pretendard-{weight}.ttf")
                ?? throw new InvalidOperationException("The bundled UI font is missing.");
            using var data = new MemoryStream(); stream.CopyTo(data);
            var bytes = data.ToArray(); var memory = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, memory, bytes.Length);
            uint count = 0;
            var registration = AddFontMemResourceEx(memory, (uint)bytes.Length, IntPtr.Zero, ref count);
            if (registration == IntPtr.Zero) { Marshal.FreeHGlobal(memory); throw new InvalidOperationException("The UI font could not be loaded."); }
            // GDI (native controls) and GDI+ (FontFamily) both need the same faces.
            Families.AddMemoryFont(memory, bytes.Length);
            Buffers.Add(memory); Registrations.Add(registration);
        }
        // These three registrations and buffers intentionally live until process exit,
        // after all forms, labels and native font handles have finished using them.
    }

    internal static Font Create(float size, bool bold = false, bool semibold = false)
    {
        var name = semibold ? "Pretendard SemiBold" : "Pretendard";
        return new Font(Families.Families.Single(family => family.Name == name), size,
            bold && !semibold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Point);
    }

    [DllImport("gdi32.dll")]
    private static extern IntPtr AddFontMemResourceEx(IntPtr data, uint size, IntPtr reserved, ref uint count);
}
