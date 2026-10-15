using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Microsoft.Win32;
using PhotoReg.Core;
using PhotoReg.Models;
using PhotoReg.Services;

namespace PhotoReg;

public partial class MainWindow : Window
{
    private readonly BitmapLoader _loader = new();
    private readonly ConcurrentDictionary<string, byte> _thumbRequested = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LoadedImage> _fullCache = new(StringComparer.OrdinalIgnoreCase);

    private List<PhotoItem> _items = [];
    private CancellationTokenSource? _loadCts;
    private ScrollViewer? _filmScroll;
    private string? _folder;
    private int _session;
    private int _generation;
    private int _index = -1;
    private bool _syncing;
    private double _zoom = 1;
    private double _panX;
    private double _panY;
    private double _layoutW;
    private double _layoutH;
    private double _layoutLeft;
    private double _layoutTop;
    private int _pixelW;
    private int _pixelH;
    private int _originalW;
    private int _originalH;
    private bool _panning;
    private Point _dragStart;
    private double _panDragX;
    private double _panDragY;
    private bool _cropping;
    private Rect _cropNorm = Rect.Empty;
    private Rect _cropCarry = Rect.Empty;
    private Rect _dragNorm;
    private Point _dragNormStart;
    private Point _cropDragStage;
    private bool _drawArmed;
    private CropDrag _cropDrag = CropDrag.None;
    private CropDrag _cropHover = CropDrag.None;
    private Canvas? _cropLayer;
    private Rectangle? _dimTop;
    private Rectangle? _dimBottom;
    private Rectangle? _dimLeft;
    private Rectangle? _dimRight;
    private Rectangle? _cropFrame;
    private readonly Rectangle[] _cropHandles = new Rectangle[8];
    private static readonly CropDrag[] HandleKinds =
    [
        CropDrag.NW, CropDrag.N, CropDrag.NE, CropDrag.E,
        CropDrag.SE, CropDrag.S, CropDrag.SW, CropDrag.W,
    ];

    public MainWindow()
    {
        InitializeComponent();
        ShowWelcome(
            "Экспресс-редактор",
            "Откройте одну фотографию. В ленте внизу будут остальные снимки из той же папки — листайте их стрелками ← и →, пробелом, колёсиком мыши или щелчком по ленте.");
    }

    public void OpenPath(string path)
    {
        try
        {
            ApplyCatalog(PhotoCatalogScanner.Scan(path));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "PhotoReg", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        int enabled = 0;
        DwmSetWindowAttribute(hwnd, 20, ref enabled, sizeof(int));
        DwmSetWindowAttribute(hwnd, 19, ref enabled, sizeof(int));
    }

    protected override void OnClosed(EventArgs e)
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loader.Dispose();
        base.OnClosed(e);
    }

    private void ApplyCatalog(PhotoCatalog catalog)
    {
        _session++;
        _thumbRequested.Clear();
        _loadCts?.Cancel();
        _fullCache.Clear();
        _folder = catalog.FolderPath;
        _items = catalog.Photos.Select(photo => new PhotoItem(photo.FullPath)).ToList();

        _syncing = true;
        Film.ItemsSource = _items;
        Film.SelectedIndex = -1;
        _syncing = false;

        if (_items.Count == 0)
        {
            _index = -1;
            ShowWelcome(
                "Нет изображений",
                "В этой папке нет файлов JPG, PNG, WEBP, TIFF и других поддерживаемых форматов.");
            return;
        }

        int index = catalog.SelectedIndex;
        if (index < 0 || index >= _items.Count)
            index = 0;
        ShowIndex(index);
    }

    private void ShowWelcome(string title, string body)
    {
        _index = -1;
        _pixelW = 0;
        _pixelH = 0;
        _originalW = 0;
        _originalH = 0;
        _zoom = 1;
        _panX = 0;
        _panY = 0;
        if (_cropping)
            ExitCropMode();
        PhotoView.Source = null;
        ErrorText.Visibility = Visibility.Collapsed;
        LoadingText.Visibility = Visibility.Collapsed;
        WelcomeTitle.Text = title;
        WelcomeBody.Text = body;
        WelcomeHint.Text = "Правый клик по файлу в проводнике → «Открыть в PhotoReg».";
        UpdateChrome();
    }

    private void ShowIndex(int index)
    {
        if (index < 0 || index >= _items.Count)
            return;

        if (_cropping)
        {
            _cropDrag = CropDrag.None;
            _drawArmed = false;
            _cropHover = CropDrag.None;
            if (StageHost.IsMouseCaptured)
                StageHost.ReleaseMouseCapture();
            if (_cropNorm.IsEmpty && _cropCarry.Width > 0 && _cropCarry.Height > 0)
                _cropNorm = _cropCarry;
        }

        _index = index;
        _zoom = 1;
        _panX = 0;
        _panY = 0;
        ErrorText.Visibility = Visibility.Collapsed;

        _syncing = true;
        Film.SelectedIndex = index;
        if (Film.SelectedItem != null)
            Film.ScrollIntoView(Film.SelectedItem);
        _syncing = false;

        LayoutPhoto();
        UpdateChrome();
        _ = LoadCurrentAsync();
    }

    private async Task LoadCurrentAsync()
    {
        if (_index < 0 || _index >= _items.Count)
            return;

        var item = _items[_index];
        int generation = ++_generation;
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = new CancellationTokenSource();
        var token = _loadCts.Token;

        if (_fullCache.TryGetValue(item.FullPath, out var cached))
        {
            ApplyBitmap(cached);
            PreloadNeighbors(token);
            return;
        }

        var loadTask = _loader.LoadFullAsync(item.FullPath, token);
        await Task.WhenAny(loadTask, Task.Delay(120));
        if (generation != _generation)
            return;
        if (!loadTask.IsCompleted)
            LoadingText.Visibility = Visibility.Visible;

        try
        {
            var image = await loadTask;
            if (generation != _generation)
                return;

            _fullCache[item.FullPath] = image;
            TrimCache();
            ApplyBitmap(image);
            PreloadNeighbors(token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            if (generation != _generation)
                return;

            _pixelW = 0;
            _pixelH = 0;
            _originalW = 0;
            _originalH = 0;
            PhotoView.Source = null;
            ErrorText.Visibility = Visibility.Visible;
            UpdateChrome();
        }
        finally
        {
            if (generation == _generation)
                LoadingText.Visibility = Visibility.Collapsed;
        }
    }

    private void PreloadNeighbors(CancellationToken token)
    {
        _ = PreloadAsync(_index - 1, token);
        _ = PreloadAsync(_index + 1, token);
    }

    private async Task PreloadAsync(int index, CancellationToken token)
    {
        if (index < 0 || index >= _items.Count || token.IsCancellationRequested)
            return;

        var path = _items[index].FullPath;
        if (_fullCache.ContainsKey(path))
            return;

        try
        {
            var image = await _loader.LoadFullAsync(path, token);
            if (token.IsCancellationRequested)
                return;
            _fullCache[path] = image;
            TrimCache();
        }
        catch
        {
            // The next visit will try again.
        }
    }

    private void TrimCache()
    {
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = _index - 1; i <= _index + 1; i++)
        {
            if (i >= 0 && i < _items.Count)
                keep.Add(_items[i].FullPath);
        }

        foreach (var path in _fullCache.Keys.Where(path => !keep.Contains(path)).ToList())
            _fullCache.Remove(path);
    }

    private void ApplyBitmap(LoadedImage image)
    {
        _pixelW = image.Source.PixelWidth;
        _pixelH = image.Source.PixelHeight;
        _originalW = image.OriginalWidth;
        _originalH = image.OriginalHeight;
        PhotoView.Source = image.Source;
        ErrorText.Visibility = Visibility.Collapsed;
        LayoutPhoto();
        UpdateChrome();
    }

    private void LayoutPhoto()
    {
        double stageW = StageHost.ActualWidth;
        double stageH = StageHost.ActualHeight;
        if (_pixelW <= 0 || _pixelH <= 0 || stageW < 2 || stageH < 2)
            return;

        double fit = Math.Min(stageW / _pixelW, stageH / _pixelH);
        _layoutW = _pixelW * fit * _zoom;
        _layoutH = _pixelH * fit * _zoom;
        double overflowX = Math.Max(0, (_layoutW - stageW) / 2);
        double overflowY = Math.Max(0, (_layoutH - stageH) / 2);
        _panX = Math.Clamp(_panX, -overflowX, overflowX);
        _panY = Math.Clamp(_panY, -overflowY, overflowY);
        PhotoView.Width = _layoutW;
        PhotoView.Height = _layoutH;
        _layoutLeft = (stageW - _layoutW) / 2 + _panX;
        _layoutTop = (stageH - _layoutH) / 2 + _panY;
        Canvas.SetLeft(PhotoView, _layoutLeft);
        Canvas.SetTop(PhotoView, _layoutTop);
        UpdateCropOverlay();
        UpdateCursor();
    }

    private void ZoomBy(double factor, Point cursor)
    {
        if (_pixelW <= 0 || _layoutW <= 0 || _layoutH <= 0)
            return;

        double relativeX = (cursor.X - _layoutLeft) / _layoutW;
        double relativeY = (cursor.Y - _layoutTop) / _layoutH;
        _zoom = Math.Clamp(_zoom * factor, 1, 12);

        double stageW = StageHost.ActualWidth;
        double stageH = StageHost.ActualHeight;
        double fit = Math.Min(stageW / _pixelW, stageH / _pixelH);
        double newW = _pixelW * fit * _zoom;
        double newH = _pixelH * fit * _zoom;
        _panX = cursor.X - relativeX * newW - (stageW - newW) / 2;
        _panY = cursor.Y - relativeY * newH - (stageH - newH) / 2;
        LayoutPhoto();
        UpdateChrome();
    }

    private void ResetZoom()
    {
        _zoom = 1;
        _panX = 0;
        _panY = 0;
        LayoutPhoto();
        UpdateChrome();
    }

    private void UpdateChrome()
    {
        bool has = _index >= 0 && _index < _items.Count;
        PrevButton.IsEnabled = has && _index > 0;
        NextButton.IsEnabled = has && _index < _items.Count - 1;
        PrevButton.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        NextButton.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        CropButton.IsEnabled = has;
        TitleBlock.Visibility = _cropping ? Visibility.Collapsed : Visibility.Visible;
        CropBar.Visibility = _cropping ? Visibility.Visible : Visibility.Collapsed;
        CropApplyButton.IsEnabled = _cropping && HasCropSelection();
        if (_cropping)
        {
            if (!HasCropSelection())
                CropHint.Text = "Проведите мышью, чтобы выделить участок";
            else
            {
                var rect = PhotoCrop.ToPixels(_cropNorm, CropPixelWidth, CropPixelHeight);
                CropHint.Text = $"{rect.Width} × {rect.Height} · потяните рамку";
            }
        }
        StageEmpty.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        StripPlaceholder.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ZoomChip.Visibility = has && _zoom > 1.001 ? Visibility.Visible : Visibility.Collapsed;
        if (_zoom > 1.001)
            ZoomChip.Content = $"{Math.Round(_zoom * 100)}%";

        if (!has)
        {
            Title = "PhotoReg";
            NameText.Text = "";
            MetaText.Text = "";
            UpdateCursor();
            return;
        }

        var item = _items[_index];
        Title = $"PhotoReg — {item.Name}";
        NameText.Text = item.Name;
        var meta = $"{_index + 1} из {_items.Count} · {FolderLabel(_folder)}";
        if (_originalW > 0 && _originalH > 0)
            meta += $" · {_originalW} × {_originalH}";
        if (_zoom > 1.001)
            meta += $" · {Math.Round(_zoom * 100)}%";
        MetaText.Text = meta;
        UpdateCursor();
    }

    private void UpdateCursor()
    {
        if (_cropping)
        {
            StageHost.Cursor = CursorFor(_cropDrag != CropDrag.None ? _cropDrag : _cropHover);
            return;
        }

        StageHost.Cursor = _panning ? Cursors.SizeAll : _zoom > 1.001 ? Cursors.Hand : Cursors.Arrow;
    }

    private void Move(int delta) => MoveTo(_index + delta);

    private void MoveTo(int index)
    {
        if (index < 0 || index >= _items.Count || index == _index)
            return;
        ShowIndex(index);
    }

    private void OpenDialog()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Открыть фотографию",
            Filter = "Изображения|*.jpg;*.jpeg;*.jpe;*.jfif;*.png;*.bmp;*.gif;*.tif;*.tiff;*.webp;*.heic;*.heif;*.avif;*.ico|Все файлы|*.*",
            CheckFileExists = true,
        };
        if (!string.IsNullOrEmpty(_folder))
            dialog.InitialDirectory = _folder;
        if (dialog.ShowDialog(this) == true)
            OpenPath(dialog.FileName);
    }

    private async Task RequestThumbAsync(PhotoItem item)
    {
        if (item.Thumbnail != null)
            return;
        if (!_thumbRequested.TryAdd(item.FullPath, 0))
            return;

        int session = _session;
        int stamp = item.LoadStamp;
        try
        {
            var thumbnail = await _loader.LoadThumbAsync(item.FullPath, CancellationToken.None);
            if (session != _session || stamp != item.LoadStamp)
                return;
            item.Thumbnail = thumbnail;
        }
        catch
        {
            // The tile stays empty when a file cannot be decoded.
        }
    }

    private void QueueThumb(object sender)
    {
        if (sender is FrameworkElement { DataContext: PhotoItem item })
            _ = RequestThumbAsync(item);
    }

    private void ScrollFilm(int delta)
    {
        _filmScroll ??= FindScrollViewer(Film);
        _filmScroll?.ScrollToHorizontalOffset(_filmScroll.HorizontalOffset - Math.Sign(delta) * 148);
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer viewer)
                return viewer;
            var nested = FindScrollViewer(child);
            if (nested != null)
                return nested;
        }

        return null;
    }

    private static bool IsOver(FrameworkElement element, MouseEventArgs e)
    {
        if (!element.IsVisible || element.ActualWidth <= 0 || element.ActualHeight <= 0)
            return false;

        var point = e.GetPosition(element);
        return point.X >= 0 && point.Y >= 0 && point.X <= element.ActualWidth && point.Y <= element.ActualHeight;
    }

    private static bool IsInsideButton(DependencyObject source)
    {
        while (true)
        {
            if (source is Button)
                return true;
            var parent = VisualTreeHelper.GetParent(source);
            if (parent == null)
                return false;
            source = parent;
        }
    }

    private static string FolderLabel(string? folder)
    {
        if (string.IsNullOrEmpty(folder))
            return "";

        var trimmed = folder.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        var name = System.IO.Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? folder : name;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _filmScroll = FindScrollViewer(Film);
        Focus();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_cropping)
        {
            if (e.Key == Key.Escape)
            {
                ExitCropMode();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Enter)
            {
                ApplyCrop();
                e.Handled = true;
                return;
            }

        }

        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            switch (e.Key)
            {
                case Key.O:
                    OpenDialog();
                    e.Handled = true;
                    return;
                case Key.D0:
                case Key.NumPad0:
                    ResetZoom();
                    e.Handled = true;
                    return;
                case Key.OemPlus:
                case Key.Add:
                    ZoomBy(1.12, new Point(StageHost.ActualWidth / 2, StageHost.ActualHeight / 2));
                    e.Handled = true;
                    return;
                case Key.OemMinus:
                case Key.Subtract:
                    ZoomBy(1 / 1.12, new Point(StageHost.ActualWidth / 2, StageHost.ActualHeight / 2));
                    e.Handled = true;
                    return;
            }
        }

        if (Keyboard.Modifiers != ModifierKeys.None)
            return;

        switch (e.Key)
        {
            case Key.Left:
                Move(-1);
                e.Handled = true;
                break;
            case Key.Right:
            case Key.Space:
                Move(1);
                e.Handled = true;
                break;
            case Key.Home:
                MoveTo(0);
                e.Handled = true;
                break;
            case Key.End:
                MoveTo(_items.Count - 1);
                e.Handled = true;
                break;
        }
    }

    private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.XButton1)
        {
            Move(-1);
            e.Handled = true;
        }
        else if (e.ChangedButton == MouseButton.XButton2)
        {
            Move(1);
            e.Handled = true;
        }
    }

    private void Window_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (IsOver(BottomBar, e))
        {
            ScrollFilm(e.Delta);
            e.Handled = true;
            return;
        }

        if (_cropping && IsOver(StageHost, e))
        {
            e.Handled = true;
            return;
        }

        if (!IsOver(StageHost, e) || _pixelW <= 0)
            return;

        ZoomBy(e.Delta > 0 ? 1.12 : 1 / 1.12, e.GetPosition(StageHost));
        e.Handled = true;
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } files)
            return;
        OpenPath(files[0]);
    }

    private void StageHost_SizeChanged(object sender, SizeChangedEventArgs e) => LayoutPhoto();

    private void StageHost_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && IsInsideButton(source))
            return;

        if (_cropping)
        {
            BeginCropDrag(e);
            return;
        }

        if (e.ClickCount >= 2)
        {
            ResetZoom();
            _panning = false;
            StageHost.ReleaseMouseCapture();
            e.Handled = true;
            return;
        }

        if (_zoom <= 1.001)
            return;

        _panning = true;
        _dragStart = e.GetPosition(StageHost);
        _panDragX = _panX;
        _panDragY = _panY;
        StageHost.CaptureMouse();
        UpdateCursor();
        e.Handled = true;
    }

    private void StageHost_MouseMove(object sender, MouseEventArgs e)
    {
        if (_cropping)
        {
            UpdateCropDrag(e.GetPosition(StageHost));
            return;
        }

        if (!_panning)
            return;

        var point = e.GetPosition(StageHost);
        _panX = _panDragX + (point.X - _dragStart.X);
        _panY = _panDragY + (point.Y - _dragStart.Y);
        LayoutPhoto();
    }

    private void StageHost_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_cropping && _cropDrag != CropDrag.None)
        {
            EndCropDrag();
            e.Handled = true;
            return;
        }

        if (!_panning)
            return;

        _panning = false;
        StageHost.ReleaseMouseCapture();
        UpdateCursor();
    }

    private void StageHost_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_cropping && _cropDrag != CropDrag.None)
            EndCropDrag();
        _panning = false;
        UpdateCursor();
    }

    private void Open_Click(object sender, RoutedEventArgs e) => OpenDialog();

    private void Default_Click(object sender, RoutedEventArgs e)
    {
        if (_index >= 0 && _index < _items.Count)
            ShellIntegration.PromptOpenWith(this, _items[_index].FullPath);
        else
            ShellIntegration.OpenDefaultAppsSettings();
    }

    private void Prev_Click(object sender, RoutedEventArgs e) => Move(-1);

    private void Next_Click(object sender, RoutedEventArgs e) => Move(1);

    private void ZoomChip_Click(object sender, RoutedEventArgs e) => ResetZoom();

    private int _cropSync;

    private void CropButton_Checked(object sender, RoutedEventArgs e)
    {
        if (_cropSync == 0)
            EnterCropMode();
    }

    private void CropButton_Unchecked(object sender, RoutedEventArgs e)
    {
        if (_cropSync == 0)
            ExitCropMode();
    }

    private void SetCropChecked(bool value)
    {
        _cropSync++;
        CropButton.IsChecked = value;
        _cropSync--;
    }

    private void CropCancel_Click(object sender, RoutedEventArgs e) => ExitCropMode();

    private void CropApply_Click(object sender, RoutedEventArgs e) => ApplyCrop();

    private void Film_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing)
            return;
        if (Film.SelectedIndex >= 0 && Film.SelectedIndex != _index)
            ShowIndex(Film.SelectedIndex);
    }

    private void Thumb_Loaded(object sender, RoutedEventArgs e) => QueueThumb(sender);

    private void Thumb_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e) => QueueThumb(sender);

    private int CropPixelWidth => _originalW > 0 ? _originalW : _pixelW;

    private int CropPixelHeight => _originalH > 0 ? _originalH : _pixelH;

    private void EnterCropMode()
    {
        if (_pixelW <= 0)
        {
            SetCropChecked(false);
            return;
        }

        if (_cropping)
            return;

        _cropping = true;
        _cropNorm = Rect.Empty;
        _cropCarry = Rect.Empty;
        _cropDrag = CropDrag.None;
        _drawArmed = false;
        ResetZoom();
        UpdateChrome();
    }

    private void ExitCropMode()
    {
        _cropping = false;
        _cropNorm = Rect.Empty;
        _cropCarry = Rect.Empty;
        SetCropChecked(false);
        _cropDrag = CropDrag.None;
        _drawArmed = false;
        if (StageHost.IsMouseCaptured)
            StageHost.ReleaseMouseCapture();
        UpdateCropOverlay();
        UpdateChrome();
    }

    private void ApplyCrop()
    {
        if (!_cropping || !HasCropSelection() || _index < 0 || _index >= _items.Count)
            return;

        var item = _items[_index];
        var wait = Mouse.OverrideCursor;
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            BitmapSource full = PhotoView.Source is BitmapSource shown
                && shown.PixelWidth == _originalW
                && shown.PixelHeight == _originalH
                ? shown
                : BitmapLoader.LoadForEdit(item.FullPath).Source;
            var rect = PhotoCrop.ToPixels(_cropNorm, full.PixelWidth, full.PixelHeight);
            var cropped = PhotoCrop.Crop(full, rect);
            PhotoCrop.Save(cropped, item.FullPath);
            _fullCache[item.FullPath] = new LoadedImage(cropped, cropped.PixelWidth, cropped.PixelHeight);
            item.LoadStamp++;
            item.Thumbnail = null;
            _thumbRequested.TryRemove(item.FullPath, out _);
            _ = RequestThumbAsync(item);
            _cropCarry = _cropNorm;
            _cropNorm = Rect.Empty;
            _cropDrag = CropDrag.None;
            _zoom = 1;
            _panX = 0;
            _panY = 0;
            ApplyBitmap(_fullCache[item.FullPath]);
            UpdateCropOverlay();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "PhotoReg", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            Mouse.OverrideCursor = wait;
        }
    }

    private bool HasCropSelection()
    {
        if (!_cropping || _cropNorm.IsEmpty || CropPixelWidth <= 0 || CropPixelHeight <= 0)
            return false;

        var rect = PhotoCrop.ToPixels(_cropNorm, CropPixelWidth, CropPixelHeight);
        if (rect.Width < 8 || rect.Height < 8)
            return false;
        return rect.X != 0 || rect.Y != 0 || rect.Width != CropPixelWidth || rect.Height != CropPixelHeight;
    }

    private void BeginCropDrag(MouseButtonEventArgs e)
    {
        var stage = e.GetPosition(StageHost);
        var hit = HitCrop(stage);
        if (hit == CropDrag.None)
            return;

        _cropDrag = hit;
        _cropDragStage = stage;
        _dragNormStart = ToNorm(stage);
        _dragNorm = _cropNorm;
        _drawArmed = hit == CropDrag.Draw;
        StageHost.CaptureMouse();
        UpdateCursor();
        e.Handled = true;
    }

    private void UpdateCropDrag(Point stage)
    {
        if (_cropDrag == CropDrag.None)
        {
            var hover = HitCrop(stage);
            if (hover != _cropHover)
            {
                _cropHover = hover;
                UpdateCursor();
            }
            return;
        }

        if (_drawArmed && (stage - _cropDragStage).Length < 4)
            return;

        _drawArmed = false;
        var norm = ToNorm(stage);
        switch (_cropDrag)
        {
            case CropDrag.Draw:
                _cropNorm = NormalizeRect(_dragNormStart, norm);
                break;
            case CropDrag.Move:
                MoveSelection(norm);
                break;
            default:
                ResizeSelection(norm);
                break;
        }

        UpdateCropOverlay();
        UpdateChrome();
    }

    private void EndCropDrag()
    {
        if (_cropDrag == CropDrag.Draw && _drawArmed)
            _cropNorm = _dragNorm.IsEmpty ? Rect.Empty : _dragNorm;
        else if (_cropDrag == CropDrag.Draw && !HasCropSelection())
            _cropNorm = Rect.Empty;

        _cropDrag = CropDrag.None;
        _drawArmed = false;
        _cropHover = CropDrag.None;
        _cropCarry = HasCropSelection() ? _cropNorm : Rect.Empty;
        if (StageHost.IsMouseCaptured)
            StageHost.ReleaseMouseCapture();
        UpdateCropOverlay();
        UpdateChrome();
    }

    private void MoveSelection(Point norm)
    {
        double width = _dragNorm.Width;
        double height = _dragNorm.Height;
        double x = Math.Clamp(_dragNorm.X + (norm.X - _dragNormStart.X), 0, Math.Max(0, 1 - width));
        double y = Math.Clamp(_dragNorm.Y + (norm.Y - _dragNormStart.Y), 0, Math.Max(0, 1 - height));
        _cropNorm = new Rect(x, y, width, height);
    }

    private void ResizeSelection(Point norm)
    {
        double minW = Math.Max(8.0 / Math.Max(CropPixelWidth, 1), 0.002);
        double minH = Math.Max(8.0 / Math.Max(CropPixelHeight, 1), 0.002);
        double left = _dragNorm.X;
        double top = _dragNorm.Y;
        double right = _dragNorm.Right;
        double bottom = _dragNorm.Bottom;

        if (_cropDrag is CropDrag.W or CropDrag.NW or CropDrag.SW)
            left = Math.Clamp(norm.X, 0, right - minW);
        if (_cropDrag is CropDrag.E or CropDrag.NE or CropDrag.SE)
            right = Math.Clamp(norm.X, left + minW, 1);
        if (_cropDrag is CropDrag.N or CropDrag.NW or CropDrag.NE)
            top = Math.Clamp(norm.Y, 0, bottom - minH);
        if (_cropDrag is CropDrag.S or CropDrag.SW or CropDrag.SE)
            bottom = Math.Clamp(norm.Y, top + minH, 1);

        _cropNorm = new Rect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    private CropDrag HitCrop(Point stage)
    {
        if (!_cropNorm.IsEmpty && _layoutW > 1)
        {
            var centers = HandleCenters();
            for (int i = 0; i < centers.Length; i++)
            {
                Vector delta = stage - centers[i];
                if (delta.Length <= 12)
                    return HandleKinds[i];
            }

            if (SelectionContains(stage))
                return CropDrag.Move;
        }

        return _layoutW > 1 ? CropDrag.Draw : CropDrag.None;
    }

    private bool SelectionContains(Point stage)
    {
        double x = _layoutLeft + _cropNorm.X * _layoutW;
        double y = _layoutTop + _cropNorm.Y * _layoutH;
        double w = _cropNorm.Width * _layoutW;
        double h = _cropNorm.Height * _layoutH;
        return stage.X >= x && stage.Y >= y && stage.X <= x + w && stage.Y <= y + h;
    }

    private Point[] HandleCenters()
    {
        double x = _layoutLeft + _cropNorm.X * _layoutW;
        double y = _layoutTop + _cropNorm.Y * _layoutH;
        double right = x + _cropNorm.Width * _layoutW;
        double bottom = y + _cropNorm.Height * _layoutH;
        double midX = (x + right) / 2;
        double midY = (y + bottom) / 2;
        return
        [
            new Point(x, y), new Point(midX, y), new Point(right, y), new Point(right, midY),
            new Point(right, bottom), new Point(midX, bottom), new Point(x, bottom), new Point(x, midY),
        ];
    }

    private Point ToNorm(Point stage)
    {
        double x = _layoutW > 1 ? (stage.X - _layoutLeft) / _layoutW : 0;
        double y = _layoutH > 1 ? (stage.Y - _layoutTop) / _layoutH : 0;
        return new Point(Math.Clamp(x, 0, 1), Math.Clamp(y, 0, 1));
    }

    private static Rect NormalizeRect(Point start, Point end)
    {
        double x = Math.Min(start.X, end.X);
        double y = Math.Min(start.Y, end.Y);
        return new Rect(x, y, Math.Abs(end.X - start.X), Math.Abs(end.Y - start.Y));
    }

    private static Cursor CursorFor(CropDrag drag) => drag switch
    {
        CropDrag.Move => Cursors.SizeAll,
        CropDrag.N or CropDrag.S => Cursors.SizeNS,
        CropDrag.E or CropDrag.W => Cursors.SizeWE,
        CropDrag.NE or CropDrag.SW => Cursors.SizeNESW,
        CropDrag.NW or CropDrag.SE => Cursors.SizeNWSE,
        _ => Cursors.Cross,
    };

    private void EnsureCropVisuals()
    {
        if (_cropLayer != null)
            return;

        _cropLayer = new Canvas { IsHitTestVisible = false };
        Panel.SetZIndex(_cropLayer, 2);
        _dimTop = DimRect();
        _dimBottom = DimRect();
        _dimLeft = DimRect();
        _dimRight = DimRect();
        _cropFrame = new Rectangle
        {
            Stroke = new SolidColorBrush(Color.FromRgb(15, 118, 110)),
            StrokeThickness = 2,
            Fill = Brushes.Transparent,
            IsHitTestVisible = false,
        };
        _cropLayer.Children.Add(_dimTop);
        _cropLayer.Children.Add(_dimBottom);
        _cropLayer.Children.Add(_dimLeft);
        _cropLayer.Children.Add(_dimRight);
        _cropLayer.Children.Add(_cropFrame);
        for (int i = 0; i < _cropHandles.Length; i++)
        {
            _cropHandles[i] = new Rectangle
            {
                Width = 10,
                Height = 10,
                Fill = Brushes.White,
                Stroke = new SolidColorBrush(Color.FromRgb(15, 118, 110)),
                StrokeThickness = 1,
                IsHitTestVisible = false,
            };
            _cropLayer.Children.Add(_cropHandles[i]);
        }

        Stage.Children.Add(_cropLayer);
    }

    private static Rectangle DimRect() => new()
    {
        Fill = new SolidColorBrush(Color.FromArgb(96, 28, 36, 48)),
        IsHitTestVisible = false,
    };

    private void UpdateCropOverlay()
    {
        if (!_cropping)
        {
            if (_cropLayer != null)
                _cropLayer.Visibility = Visibility.Collapsed;
            return;
        }

        EnsureCropVisuals();
        if (_cropLayer == null || _dimTop == null || _dimBottom == null || _dimLeft == null || _dimRight == null || _cropFrame == null)
            return;

        _cropLayer.Visibility = Visibility.Visible;
        Canvas.SetLeft(_cropLayer, _layoutLeft);
        Canvas.SetTop(_cropLayer, _layoutTop);
        _cropLayer.Width = Math.Max(0, _layoutW);
        _cropLayer.Height = Math.Max(0, _layoutH);

        bool show = !_cropNorm.IsEmpty && _cropNorm.Width > 0 && _cropNorm.Height > 0 && _layoutW > 1;
        if (!show)
        {
            HideCropShapes();
            return;
        }

        double x = _cropNorm.X * _layoutW;
        double y = _cropNorm.Y * _layoutH;
        double w = _cropNorm.Width * _layoutW;
        double h = _cropNorm.Height * _layoutH;
        Place(_dimTop, 0, 0, _layoutW, y);
        Place(_dimBottom, 0, y + h, _layoutW, Math.Max(0, _layoutH - (y + h)));
        Place(_dimLeft, 0, y, x, h);
        Place(_dimRight, x + w, y, Math.Max(0, _layoutW - (x + w)), h);
        Place(_cropFrame, x, y, w, h);

        var centers = new[]
        {
            new Point(x, y), new Point(x + w / 2, y), new Point(x + w, y), new Point(x + w, y + h / 2),
            new Point(x + w, y + h), new Point(x + w / 2, y + h), new Point(x, y + h), new Point(x, y + h / 2),
        };
        for (int i = 0; i < _cropHandles.Length; i++)
        {
            _cropHandles[i].Visibility = Visibility.Visible;
            Canvas.SetLeft(_cropHandles[i], centers[i].X - 5);
            Canvas.SetTop(_cropHandles[i], centers[i].Y - 5);
        }
    }

    private void HideCropShapes()
    {
        if (_dimTop == null || _dimBottom == null || _dimLeft == null || _dimRight == null || _cropFrame == null)
            return;

        _dimTop.Visibility = Visibility.Collapsed;
        _dimBottom.Visibility = Visibility.Collapsed;
        _dimLeft.Visibility = Visibility.Collapsed;
        _dimRight.Visibility = Visibility.Collapsed;
        _cropFrame.Visibility = Visibility.Collapsed;
        foreach (var handle in _cropHandles)
        {
            if (handle != null)
                handle.Visibility = Visibility.Collapsed;
        }
    }

    private static void Place(Rectangle shape, double x, double y, double width, double height)
    {
        shape.Visibility = Visibility.Visible;
        Canvas.SetLeft(shape, x);
        Canvas.SetTop(shape, y);
        shape.Width = Math.Max(0, width);
        shape.Height = Math.Max(0, height);
    }

    private enum CropDrag
    {
        None,
        Draw,
        Move,
        N,
        S,
        E,
        W,
        NE,
        NW,
        SE,
        SW,
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
