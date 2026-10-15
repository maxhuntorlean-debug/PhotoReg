using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PhotoReg.Services;

public static class PhotoCrop
{
    public static Int32Rect ToPixels(Rect normalized, int pixelWidth, int pixelHeight)
    {
        if (pixelWidth <= 0 || pixelHeight <= 0 || normalized.IsEmpty || normalized.Width <= 0 || normalized.Height <= 0)
            return new Int32Rect(0, 0, 0, 0);

        int x = (int)Math.Floor(normalized.X * pixelWidth);
        int y = (int)Math.Floor(normalized.Y * pixelHeight);
        int right = (int)Math.Ceiling((normalized.X + normalized.Width) * pixelWidth);
        int bottom = (int)Math.Ceiling((normalized.Y + normalized.Height) * pixelHeight);
        x = Math.Clamp(x, 0, Math.Max(0, pixelWidth - 1));
        y = Math.Clamp(y, 0, Math.Max(0, pixelHeight - 1));
        right = Math.Clamp(right, Math.Min(pixelWidth, x + 1), pixelWidth);
        bottom = Math.Clamp(bottom, Math.Min(pixelHeight, y + 1), pixelHeight);
        return new Int32Rect(x, y, right - x, bottom - y);
    }

    public static BitmapSource Crop(BitmapSource source, Int32Rect rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
            throw new ArgumentException("Участок слишком маленький.");
        if (rect.X < 0 || rect.Y < 0 || rect.X + rect.Width > source.PixelWidth || rect.Y + rect.Height > source.PixelHeight)
            throw new ArgumentOutOfRangeException(nameof(rect), "Участок выходит за границы фотографии.");

        var cropped = new CroppedBitmap(source, rect);
        cropped.Freeze();
        return cropped;
    }

    public static void Save(BitmapSource source, string path)
    {
        var encoder = CreateEncoder(path);
        var format = encoder is PngBitmapEncoder ? PixelFormats.Bgra32 : PixelFormats.Bgr24;
        BitmapSource frameSource = source.Format == format
            ? source
            : Freeze(new FormatConvertedBitmap(source, format, null, 0));
        encoder.Frames.Add(BitmapFrame.Create(frameSource));

        string directory = Path.GetDirectoryName(path) ?? "";
        string temp = Path.Combine(directory, Path.GetFileName(path) + ".photoreg.tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                encoder.Save(stream);
            File.Move(temp, path, overwrite: true);
        }
        catch (NotSupportedException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new IOException($"Не удалось записать обрезанную фотографию. {ex.Message}", ex);
        }
        finally
        {
            if (File.Exists(temp))
            {
                try { File.Delete(temp); } catch { }
            }
        }
    }

    private static BitmapEncoder CreateEncoder(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" or ".jpe" or ".jfif" => new JpegBitmapEncoder { QualityLevel = 92 },
            ".png" => new PngBitmapEncoder(),
            ".bmp" => new BmpBitmapEncoder(),
            ".tif" or ".tiff" => new TiffBitmapEncoder(),
            ".gif" => new GifBitmapEncoder(),
            var extension => throw new NotSupportedException(
                $"Формат {extension} нельзя перезаписать. Обрезка не сохранена."),
        };
    }

    private static BitmapSource Freeze(BitmapSource source)
    {
        if (source.CanFreeze && !source.IsFrozen)
            source.Freeze();
        return source;
    }
}
