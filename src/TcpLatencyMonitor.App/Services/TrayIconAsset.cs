namespace TcpLatencyMonitor.App.Services;

internal static class TrayIconAsset
{
    private const int IconSize = 16;

    public static string EnsureCreated()
    {
        Directory.CreateDirectory(AppPaths.DataDirectory);
        var path = Path.Combine(AppPaths.DataDirectory, "monitor.ico");
        if (File.Exists(path))
        {
            return path;
        }

        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);

        const int bitmapHeaderSize = 40;
        const int xorBytes = IconSize * IconSize * 4;
        const int maskStride = 4;
        const int maskBytes = maskStride * IconSize;
        const int imageBytes = bitmapHeaderSize + xorBytes + maskBytes;

        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)1);
        writer.Write((byte)IconSize);
        writer.Write((byte)IconSize);
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((ushort)1);
        writer.Write((ushort)32);
        writer.Write(imageBytes);
        writer.Write(22);

        writer.Write(bitmapHeaderSize);
        writer.Write(IconSize);
        writer.Write(IconSize * 2);
        writer.Write((ushort)1);
        writer.Write((ushort)32);
        writer.Write(0);
        writer.Write(xorBytes);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);

        for (var y = IconSize - 1; y >= 0; y--)
        {
            for (var x = 0; x < IconSize; x++)
            {
                var (red, green, blue, alpha) = Pixel(x, y);
                writer.Write(blue);
                writer.Write(green);
                writer.Write(red);
                writer.Write(alpha);
            }
        }

        writer.Write(new byte[maskBytes]);
        return path;
    }

    private static (byte Red, byte Green, byte Blue, byte Alpha) Pixel(int x, int y)
    {
        var bars = new[]
        {
            (X: 1, Height: 7, Color: (R: (byte)239, G: (byte)68, B: (byte)68)),
            (X: 4, Height: 10, Color: (R: (byte)249, G: (byte)115, B: (byte)22)),
            (X: 7, Height: 13, Color: (R: (byte)234, G: (byte)179, B: (byte)8)),
            (X: 10, Height: 10, Color: (R: (byte)132, G: (byte)204, B: (byte)22)),
            (X: 13, Height: 7, Color: (R: (byte)34, G: (byte)197, B: (byte)94))
        };

        foreach (var bar in bars)
        {
            var top = IconSize - 2 - bar.Height;
            if (x >= bar.X && x <= bar.X + 1 && y >= top && y <= IconSize - 3)
            {
                return (bar.Color.R, bar.Color.G, bar.Color.B, 255);
            }
        }

        return (0, 0, 0, 0);
    }
}
