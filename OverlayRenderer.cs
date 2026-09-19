using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace MicrosoftPurviewScreenGuard;

/// <summary>
/// Builds the overlay image once per monitor: white background with the picture centered, or, when the picture
/// is missing or unusable, a plain white overlay with one short line of text. Never contains document or label names.
/// </summary>
internal static class OverlayRenderer
{
    public const string PictureFileName = "blocked.png";

    /// <summary>The only text ever drawn by the app, and only when the picture cannot be used.</summary>
    public const string FallbackText = "Sensitive content hidden";

    /// <summary>The picture is scaled to fit inside this fraction of the screen width and height (aspect ratio kept).</summary>
    public const double MaxPictureFraction = 0.60;

    private static readonly Color Background = Color.White;

    /// <summary>
    /// Loads the picture, fully decoded and detached from the file. A truncated or corrupt file can open fine and only fail
    /// when it is drawn, so it is drawn once here. Never throws: any problem is returned as a reason.
    /// </summary>
    public static bool TryLoadPicture(string path, out Bitmap? picture, out string reason)
    {
        picture = null;
        reason = string.Empty;

        if (!File.Exists(path))
        {
            reason = $"file not found: {path}";
            return false;
        }

        try
        {
            byte[] bytes = File.ReadAllBytes(path);

            // GDI+ silently accepts a truncated or damaged PNG and shows half a picture, so check the file itself.
            string? damage = PngIntegrity.Check(bytes);
            if (damage is not null)
            {
                reason = damage;
                return false;
            }

            using var stream = new MemoryStream(bytes);
            using var raw = new Bitmap(stream);
            if (raw.Width < 1 || raw.Height < 1)
            {
                throw new InvalidDataException("image has no pixels");
            }

            var copy = new Bitmap(raw.Width, raw.Height, PixelFormat.Format32bppPArgb);
            try
            {
                using Graphics g = Graphics.FromImage(copy);
                g.Clear(Background); // flatten any transparency onto white
                g.DrawImage(raw, new Rectangle(0, 0, raw.Width, raw.Height), 0, 0, raw.Width, raw.Height, GraphicsUnit.Pixel);
            }
            catch
            {
                copy.Dispose();
                throw;
            }

            picture = copy;
            return true;
        }
        catch (Exception ex)
        {
            // GDI+ reports an unreadable image as ArgumentException, OutOfMemoryException or ExternalException.
            reason = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Renders the overlay for one monitor. Always returns a white bitmap of the requested size: if drawing the
    /// picture fails it falls back to the text, and if that fails too it stays plain white.
    /// </summary>
    public static Bitmap Render(Size size, Bitmap? picture, out string how)
    {
        var bitmap = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
        using Graphics g = Graphics.FromImage(bitmap);
        g.Clear(Background);
        how = "plain white";

        try
        {
            if (picture is not null)
            {
                DrawPicture(g, size, picture);
                how = "picture";
            }
            else
            {
                DrawFallbackText(g, size);
                how = "plain white + text";
            }
        }
        catch (Exception ex)
        {
            g.Clear(Background);
            try
            {
                DrawFallbackText(g, size);
                how = $"plain white + text (picture failed: {ex.GetType().Name})";
            }
            catch
            {
                g.Clear(Background);
                how = "plain white (drawing failed)";
            }
        }

        return bitmap;
    }

    /// <summary>Destination rectangle: as large as fits in 60% x 60% of the screen, aspect ratio kept, centered.</summary>
    internal static Rectangle PictureBounds(Size screen, Size picture)
    {
        double scale = Math.Min(
            MaxPictureFraction * screen.Width / picture.Width,
            MaxPictureFraction * screen.Height / picture.Height);

        int width = Math.Max(1, (int)Math.Round(picture.Width * scale));
        int height = Math.Max(1, (int)Math.Round(picture.Height * scale));
        return new Rectangle((screen.Width - width) / 2, (screen.Height - height) / 2, width, height);
    }

    private static void DrawPicture(Graphics g, Size size, Bitmap picture)
    {
        Rectangle target = PictureBounds(size, picture.Size);

        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.CompositingQuality = CompositingQuality.HighQuality;
        g.SmoothingMode = SmoothingMode.HighQuality;

        using var attributes = new ImageAttributes();
        attributes.SetWrapMode(WrapMode.TileFlipXY); // no dark or light fringe at the picture edge
        g.DrawImage(picture, target, 0, 0, picture.Width, picture.Height, GraphicsUnit.Pixel, attributes);
    }

    private static void DrawFallbackText(Graphics g, Size size)
    {
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        float fontPixels = Math.Max(24f, size.Height / 16f);
        float maxWidth = size.Width * 0.9f;

        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        Font font = new("Segoe UI", fontPixels, FontStyle.Bold, GraphicsUnit.Pixel);
        try
        {
            float measured = g.MeasureString(FallbackText, font).Width;
            if (measured > maxWidth)
            {
                Font smaller = new("Segoe UI", Math.Max(12f, fontPixels * maxWidth / measured), FontStyle.Bold, GraphicsUnit.Pixel);
                font.Dispose();
                font = smaller;
            }

            using var brush = new SolidBrush(Color.FromArgb(50, 50, 50));
            g.DrawString(FallbackText, font, brush, new RectangleF(0, 0, size.Width, size.Height), format);
        }
        finally
        {
            font.Dispose();
        }
    }
}
