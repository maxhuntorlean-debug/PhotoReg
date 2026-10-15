namespace PhotoReg.Core;

public sealed record PhotoCatalog(string FolderPath, IReadOnlyList<PhotoEntry> Photos, int SelectedIndex);

public static class PhotoCatalogScanner
{
    private static readonly HashSet<string> ExtensionSet = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jpe", ".jfif",
        ".png", ".bmp", ".gif",
        ".tif", ".tiff",
        ".webp", ".heic", ".heif", ".avif",
        ".ico",
    };

    public static IReadOnlySet<string> Extensions => ExtensionSet;

    public static bool IsImage(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Length > 0 && ExtensionSet.Contains(extension);
    }

    public static PhotoCatalog Scan(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new FileNotFoundException("Не указан файл.");

        path = Path.GetFullPath(path);

        string folder;
        string? preferred = null;

        if (Directory.Exists(path))
        {
            folder = path;
        }
        else if (File.Exists(path))
        {
            folder = Path.GetDirectoryName(path) ?? path;
            if (IsImage(path))
                preferred = path;
        }
        else
        {
            throw new FileNotFoundException($"Не найден файл или папка: {path}");
        }

        var files = new List<string>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                if (IsImage(file))
                    files.Add(file);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            files.Clear();
        }

        if (preferred != null && !files.Exists(file => PathsEqual(file, preferred)))
            files.Add(preferred);

        files.Sort((a, b) => NaturalStringComparer.Instance.Compare(Path.GetFileName(a), Path.GetFileName(b)));

        int index = files.Count == 0 ? -1 : 0;
        if (preferred != null && files.Count > 0)
        {
            int found = files.FindIndex(file => PathsEqual(file, preferred));
            index = found >= 0 ? found : 0;
        }

        var photos = files.Select(file => new PhotoEntry(file)).ToArray();
        return new PhotoCatalog(folder, photos, index);
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
}
