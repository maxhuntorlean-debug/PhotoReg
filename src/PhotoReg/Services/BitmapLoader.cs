using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PhotoReg.Services;

public sealed record LoadedImage(BitmapSource Source, int OriginalWidth, int OriginalHeight);

public sealed class BitmapLoader : IDisposable
{
    private const int MaxEdge = 4096;

    private readonly BlockingCollection<Job> _full = new();
    private readonly BlockingCollection<Job> _thumbs = new();
    private readonly Thread _thread;

    public BitmapLoader()
    {
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "BitmapLoader",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public Task<LoadedImage> LoadFullAsync(string path, CancellationToken cancellationToken) =>
        Enqueue(path, thumb: false, cancellationToken).Full!;

    public Task<BitmapSource> LoadThumbAsync(string path, CancellationToken cancellationToken) =>
        Enqueue(path, thumb: true, cancellationToken).Thumb!;

    public static LoadedImage LoadForEdit(string path) => Decode(path, maxEdge: 0);

    public void Dispose()
    {
        try { _full.CompleteAdding(); } catch (InvalidOperationException) { }
        try { _thumbs.CompleteAdding(); } catch (InvalidOperationException) { }
    }

    private (Task<LoadedImage>? Full, Task<BitmapSource>? Thumb) Enqueue(string path, bool thumb, CancellationToken cancellationToken)
    {
        if (thumb)
        {
            var tcs = new TaskCompletionSource<BitmapSource>(TaskCreationOptions.RunContinuationsAsynchronously);
            _thumbs.Add(new Job(path, true, null, tcs, cancellationToken));
            return (null, tcs.Task);
        }

        var full = new TaskCompletionSource<LoadedImage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _full.Add(new Job(path, false, full, null, cancellationToken));
        return (full.Task, null);
    }

    private void Loop()
    {
        var queues = new[] { _full, _thumbs };
        while (true)
        {
            try
            {
                if (_full.TryTake(out var urgent))
                {
                    Run(urgent);
                    continue;
                }

                int which = BlockingCollection<Job>.TryTakeFromAny(queues, out var job, 40);
                if (which >= 0 && job is not null)
                    Run(job);
            }
            catch (InvalidOperationException)
            {
                break;
            }
            catch
            {
                // A bad file must not stop the loader.
            }
        }
    }

    private static void Run(Job job)
    {
        if (job.CancellationToken.IsCancellationRequested)
        {
            if (job.Thumb)
                job.ThumbTcs!.TrySetCanceled(job.CancellationToken);
            else
                job.FullTcs!.TrySetCanceled(job.CancellationToken);
            return;
        }

        try
        {
            if (job.Thumb)
                job.ThumbTcs!.TrySetResult(DecodeThumb(job.Path));
            else
                job.FullTcs!.TrySetResult(DecodeFull(job.Path));
        }
        catch (Exception ex)
        {
            if (job.Thumb)
                job.ThumbTcs!.TrySetException(ex);
            else
                job.FullTcs!.TrySetException(ex);
        }
    }

    private static LoadedImage DecodeFull(string path) => Decode(path, MaxEdge);

    private static BitmapSource DecodeThumb(string path) => Decode(path, 240).Source;

    private static LoadedImage Decode(string path, int maxEdge)
    {
        // BitmapImage.DecodePixelWidth applies the EXIF orientation on the wrong axis
        // and turns the photo. Pixels stay as stored; the camera tag is applied once.
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var decoder = BitmapDecoder.Create(
            stream,
            BitmapCreateOptions.IgnoreColorProfile,
            BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count == 0)
            throw new InvalidDataException("В файле нет изображения.");

        var frame = decoder.Frames[0];
        int orientation = ReadOrientation(frame);
        BitmapSource source = BitmapFrame.Create(frame);
        source = ApplyOrientation(source, orientation);
        int width = source.PixelWidth;
        int height = source.PixelHeight;
        source = ScaleDown(source, maxEdge);
        return new LoadedImage(Freeze(source), width, height);
    }

    private static BitmapSource ApplyOrientation(BitmapSource source, int orientation)
    {
        return orientation switch
        {
            2 => Transform(source, new ScaleTransform(-1, 1), source.PixelWidth, 0),
            3 => Transform(source, new RotateTransform(180), source.PixelWidth, source.PixelHeight),
            4 => Transform(source, new ScaleTransform(1, -1), 0, source.PixelHeight),
            5 => FlipHorizontal(Rotate(source, 270)),
            6 => Rotate(source, 90),
            7 => FlipHorizontal(Rotate(source, 90)),
            8 => Rotate(source, 270),
            _ => source,
        };
    }

    private static BitmapSource Rotate(BitmapSource source, int degrees)
    {
        double width = source.PixelWidth;
        double height = source.PixelHeight;
        return degrees switch
        {
            90 => Transform(source, new RotateTransform(90), height, 0),
            180 => Transform(source, new RotateTransform(180), width, height),
            270 => Transform(source, new RotateTransform(270), 0, width),
            _ => source,
        };
    }

    private static BitmapSource FlipHorizontal(BitmapSource source) =>
        Transform(source, new ScaleTransform(-1, 1), source.PixelWidth, 0);

    private static BitmapSource Transform(BitmapSource source, Transform transform, double offsetX, double offsetY)
    {
        var group = new TransformGroup();
        group.Children.Add(transform);
        if (Math.Abs(offsetX) > 0.5 || Math.Abs(offsetY) > 0.5)
            group.Children.Add(new TranslateTransform(offsetX, offsetY));

        var transformed = new TransformedBitmap(source, group);
        transformed.Freeze();
        return transformed;
    }

    private static BitmapSource ScaleDown(BitmapSource source, int maxEdge)
    {
        int longEdge = Math.Max(source.PixelWidth, source.PixelHeight);
        if (maxEdge <= 0 || longEdge <= maxEdge)
            return source;

        double scale = (double)maxEdge / longEdge;
        var scaled = new TransformedBitmap(source, new ScaleTransform(scale, scale));
        scaled.Freeze();
        return scaled;
    }

    private static BitmapSource Freeze(BitmapSource source)
    {
        if (source.CanFreeze && !source.IsFrozen)
            source.Freeze();
        return source;
    }

    private static int ReadOrientation(BitmapFrame frame)
    {
        try
        {
            if (frame.Metadata is BitmapMetadata meta && meta.ContainsQuery("System.Photo.Orientation"))
            {
                var value = meta.GetQuery("System.Photo.Orientation");
                return Convert.ToInt32(value, CultureInfo.InvariantCulture);
            }
        }
        catch
        {
            // Orientation is optional.
        }

        return 1;
    }

    private sealed record Job(
        string Path,
        bool Thumb,
        TaskCompletionSource<LoadedImage>? FullTcs,
        TaskCompletionSource<BitmapSource>? ThumbTcs,
        CancellationToken CancellationToken);
}
