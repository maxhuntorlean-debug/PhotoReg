using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhotoReg.Services;
using Xunit;

namespace PhotoReg.Tests;

public class OrientationTests
{
    [Theory]
    [InlineData(1, false)]
    [InlineData(6, true)]
    [InlineData(8, true)]
    public void Camera_orientation_is_applied_once(int orientation, bool portrait)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                AssertOrientation(orientation, portrait);
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        if (error != null)
            ExceptionDispatchInfo.Capture(error).Throw();
    }

    private static void AssertOrientation(int orientation, bool portrait)
    {
        string path = Path.Combine(Path.GetTempPath(), "photoreg-orient-" + Guid.NewGuid().ToString("N") + ".jpg");
        try
        {
            WriteLandscape(path, orientation);
            using var loader = new BitmapLoader();
            var image = loader.LoadFullAsync(path, CancellationToken.None).GetAwaiter().GetResult();
            var pixels = ToBgra(image.Source);

            if (portrait)
            {
                Assert.True(pixels.PixelHeight > pixels.PixelWidth, $"{pixels.PixelWidth}x{pixels.PixelHeight}");
            }
            else
            {
                Assert.Equal(200, pixels.PixelWidth);
                Assert.Equal(100, pixels.PixelHeight);
            }

            var left = ColorAt(pixels, 4, pixels.PixelHeight / 2);
            var right = ColorAt(pixels, pixels.PixelWidth - 5, pixels.PixelHeight / 2);
            var top = ColorAt(pixels, pixels.PixelWidth / 2, 4);
            var bottom = ColorAt(pixels, pixels.PixelWidth / 2, pixels.PixelHeight - 5);

            switch (orientation)
            {
                case 1:
                    Assert.True(IsRed(left), Format(left));
                    Assert.True(IsBlue(right), Format(right));
                    break;
                case 6:
                    Assert.True(IsRed(top), Format(top));
                    Assert.True(IsBlue(bottom), Format(bottom));
                    break;
                case 8:
                    Assert.True(IsBlue(top), Format(top));
                    Assert.True(IsRed(bottom), Format(bottom));
                    break;
            }
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    private static void WriteLandscape(string path, int orientation)
    {
        const int width = 200;
        const int height = 100;
        var bytes = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int index = (y * width + x) * 4;
                bool left = x < width / 2;
                bytes[index] = (byte)(left ? 0 : 255);
                bytes[index + 1] = 0;
                bytes[index + 2] = (byte)(left ? 255 : 0);
                bytes[index + 3] = 255;
            }
        }

        var bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        bitmap.WritePixels(new Int32Rect(0, 0, width, height), bytes, width * 4, 0);
        var metadata = new BitmapMetadata("jpg");
        metadata.SetQuery("/app1/ifd/{ushort=274}", (ushort)orientation);
        var frame = BitmapFrame.Create(bitmap, null, metadata, null);
        var encoder = new JpegBitmapEncoder { QualityLevel = 95 };
        encoder.Frames.Add(frame);
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static BitmapSource ToBgra(BitmapSource source)
    {
        if (source.Format == PixelFormats.Bgra32)
            return source;

        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        converted.Freeze();
        return converted;
    }

    private static (byte B, byte G, byte R) ColorAt(BitmapSource source, int x, int y)
    {
        var bytes = new byte[4];
        source.CopyPixels(new Int32Rect(x, y, 1, 1), bytes, 4, 0);
        return (bytes[0], bytes[1], bytes[2]);
    }

    private static bool IsRed((byte B, byte G, byte R) color) => color.R > 200 && color.B < 40;

    private static bool IsBlue((byte B, byte G, byte R) color) => color.B > 200 && color.R < 40;

    private static string Format((byte B, byte G, byte R) color) => $"B{color.B} G{color.G} R{color.R}";
}
