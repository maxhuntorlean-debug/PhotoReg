using System.ComponentModel;
using System.IO;
using System.Windows.Media;

namespace PhotoReg.Models;

public sealed class PhotoItem : INotifyPropertyChanged
{
    private ImageSource? _thumbnail;

    public PhotoItem(string fullPath) => FullPath = fullPath;

    public string FullPath { get; }

    public string Name => Path.GetFileName(FullPath);

    public int LoadStamp { get; set; }

    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set
        {
            if (ReferenceEquals(_thumbnail, value))
                return;
            _thumbnail = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
