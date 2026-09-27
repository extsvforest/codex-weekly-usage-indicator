using System.Drawing.Drawing2D;

namespace WeeklyUsageIndicator;

// Small, immutable tiles from the approved paper study. Decode each resource
// once; painting never generates noise, accesses disk, or starts an animation.
internal static class AccountPaperTexture
{
    internal static readonly Bitmap Grain = Load("paper-grain.png");
    private static readonly Bitmap Tooth = Load("paper-tooth.png");
    private static readonly Bitmap Fiber = Load("paper-fiber.png");

    private static Bitmap Load(string name)
    {
        using var stream = typeof(AccountPaperTexture).Assembly.GetManifestResourceStream($"WeeklyUsageIndicator.Assets.Paper.{name}")
            ?? throw new InvalidOperationException("The bundled paper texture is missing.");
        using var image = Image.FromStream(stream);
        return new Bitmap(image);
    }

    internal static void Fill(Graphics graphics, GraphicsPath shape, Color color, bool dark = false)
    {
        using var fill = new SolidBrush(color); graphics.FillPath(fill, shape);
        using var grain = new TextureBrush(dark ? Fiber : Tooth, WrapMode.Tile);
        graphics.FillPath(grain, shape);
    }

    internal static void Fill(Graphics graphics, Rectangle bounds, Color color)
    {
        using var fill = new SolidBrush(color); graphics.FillRectangle(fill, bounds);
        using var grain = new TextureBrush(Tooth, WrapMode.Tile); graphics.FillRectangle(grain, bounds);
    }
}
