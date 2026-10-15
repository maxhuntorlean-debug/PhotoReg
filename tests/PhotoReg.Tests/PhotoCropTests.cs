using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhotoReg.Services;
using Xunit;

namespace PhotoReg.Tests;

public class PhotoCropTests
{
    [Fact]
    public void ToPixels_maps_the_right_half()
    {
        var rect = PhotoCrop.ToPixels(new Rect(0.5, 0, 0.5, 1), 100, 40);
        Assert.Equal(50, rect.X);
        Assert.Equal(0, rect.Y);
        Assert.Equal(50, rect.Width);
        Assert.Equal(40, rect.Height);
    }

    [Fact]
    public void Crop_keeps_only_the_selected_side_and_saves_it()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                CropAndSave();
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

    private static void CropAndSave()
    {
        const int width = 80;
        const int height = 40;
        var bytes = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int index = (y * width + x) * 4;
                bool left = x < width / 2;
                bytes[index] = (byte)(left ? 0 : 255);
                bytes[index + 2] = (byte)(left ? 255 : 0);
                bytes[index + 3] = 255;
            }
        }

        var bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        bitmap.WritePixels(new Int32Rect(0, 0, width, height), bytes, width * 4, 0);
        bitmap.Freeze();

        var rect = PhotoCrop.ToPixels(new Rect(0.5, 0, 0.5, 1), width, height);
        var cropped = PhotoCrop.Crop(bitmap, rect);
        Assert.Equal(40, cropped.PixelWidth);
        Assert.Equal(40, cropped.PixelHeight);

        string path = Path.Combine(Path.GetTempPath(), "photoreg-crop-" + Guid.NewGuid().ToString("N") + ".jpg");
        try
        {
            PhotoCrop.Save(cropped, path);
            var reloaded = BitmapLoader.LoadForEdit(path);
            Assert.Equal(40, reloaded.Source.PixelWidth);
            Assert.Equal(40, reloaded.Source.PixelHeight);
            var pixel = new byte[4];
            var bgra = new FormatConvertedBitmap(reloaded.Source, PixelFormats.Bgra32, null, 0);
            bgra.CopyPixels(new Int32Rect(4, 4, 1, 1), pixel, 4, 0);
            Assert.True(pixel[0] > 200 && pixel[2] < 80, $"B{pixel[0]} R{pixel[2]}");
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
