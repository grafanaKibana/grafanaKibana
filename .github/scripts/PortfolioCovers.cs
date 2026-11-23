using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

internal static class PortfolioCovers
{
    private const string Raw = "https://raw.githubusercontent.com/grafanaKibana/";
    // The Ubuntu runner supplies libgd through its PHP GD extension.
    private const string Codec = "libgd.so.3";

    internal static async Task RefreshAsync(HttpClient http)
    {
        foreach (string theme in new[] { "light", "dark" })
        {
            string name = theme == "light" ? "hero.png" : "hero-dark.png";
            using Stream reference = await http.GetStreamAsync(
                $"{Raw}devbook.zip/HEAD/docs/assets/product-captures/{name}");
            byte[] header = new byte[24];
            await reference.ReadExactlyAsync(header);
            (int referenceWidth, int referenceHeight) = Dimensions(header);
            byte[] png = await http.GetByteArrayAsync(
                $"{Raw}web-portfolio-v2/HEAD/public/projects/web-portfolio-v2/{name}");
            byte[] jpeg = Crop(png, (double)referenceWidth / referenceHeight);
            string destination = $"assets/featured/portfolio-{theme}.jpg";
            await File.WriteAllBytesAsync(destination + ".tmp", jpeg);
            File.Move(destination + ".tmp", destination, overwrite: true);
        }
    }

    private static (int Width, int Height) Dimensions(ReadOnlySpan<byte> png)
    {
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (png.Length < 24 || !png[..8].SequenceEqual(signature)
            || !png[12..16].SequenceEqual("IHDR"u8))
        {
            throw new InvalidDataException("Expected a PNG header.");
        }

        int width = BinaryPrimitives.ReadInt32BigEndian(png[16..]);
        int height = BinaryPrimitives.ReadInt32BigEndian(png[20..]);
        return width > 0 && height > 0
            ? (width, height)
            : throw new InvalidDataException("Invalid PNG dimensions.");
    }

    private static byte[] Crop(byte[] png, double ratio)
    {
        (int sourceWidth, int sourceHeight) = Dimensions(png);
        int width = (int)Math.Min(sourceWidth, Math.Round(sourceHeight * ratio, MidpointRounding.AwayFromZero));
        int height = (int)Math.Min(sourceHeight, Math.Round(width / ratio, MidpointRounding.AwayFromZero));
        using Image source = gdImageCreateFromPngPtr(png.Length, png);
        using Image crop = gdImageCreateTrueColor(width, height);
        if (source.IsInvalid || crop.IsInvalid)
        {
            throw new InvalidDataException("Could not decode or crop the portfolio cover.");
        }

        gdImageCopy(crop, source, 0, 0, (sourceWidth - width) / 2, 0, width, height);
        nint buffer = gdImageJpegPtr(crop, out int length, 75);
        if (buffer == 0)
        {
            throw new InvalidDataException("Could not encode the portfolio cover.");
        }

        try
        {
            byte[] jpeg = new byte[length];
            Marshal.Copy(buffer, jpeg, 0, length);
            return jpeg;
        }
        finally
        {
            gdFree(buffer);
        }
    }

    private sealed class Image() : SafeHandleZeroOrMinusOneIsInvalid(ownsHandle: true)
    {
        protected override bool ReleaseHandle()
        {
            gdImageDestroy(handle);
            return true;
        }
    }

    [DllImport(Codec)]
    private static extern Image gdImageCreateFromPngPtr(int length, byte[] png);

    [DllImport(Codec)]
    private static extern Image gdImageCreateTrueColor(int width, int height);

    [DllImport(Codec)]
    private static extern void gdImageCopy(Image destination, Image source, int x, int y,
        int sourceX, int sourceY, int width, int height);

    [DllImport(Codec)]
    private static extern nint gdImageJpegPtr(Image image, out int length, int quality);

    [DllImport(Codec)]
    private static extern void gdImageDestroy(nint image);

    [DllImport(Codec)]
    private static extern void gdFree(nint buffer);
}
