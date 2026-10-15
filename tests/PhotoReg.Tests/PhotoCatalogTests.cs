using System.IO;
using PhotoReg.Core;
using Xunit;

namespace PhotoReg.Tests;

public class PhotoCatalogTests
{
    [Fact]
    public void Orders_numbers_by_value()
    {
        var names = new[] { "img10.jpg", "img2.jpg", "img1.jpg", "img02.jpg" };
        Array.Sort(names, NaturalStringComparer.Instance);
        Assert.Equal(new[] { "img1.jpg", "img2.jpg", "img02.jpg", "img10.jpg" }, names);
    }

    [Fact]
    public void Orders_russian_names_with_numbers()
    {
        var names = new[] { "фото10.jpg", "фото2.jpg", "фото1.jpg" };
        Array.Sort(names, NaturalStringComparer.Instance);
        Assert.Equal(new[] { "фото1.jpg", "фото2.jpg", "фото10.jpg" }, names);
    }

    [Fact]
    public void Scan_selects_opened_file_and_lists_folder_in_natural_order()
    {
        using var temp = new TempFolder();
        temp.Touch("b.jpg");
        temp.Touch("a10.jpg");
        temp.Touch("a2.png");
        temp.Touch("notes.txt");
        temp.Touch("a2.jpg");
        Directory.CreateDirectory(Path.Combine(temp.Path, "sub"));
        File.WriteAllBytes(Path.Combine(temp.Path, "sub", "z.jpg"), []);

        var catalog = PhotoCatalogScanner.Scan(temp.Touch("a10.jpg"));

        Assert.Equal(new[] { "a2.jpg", "a2.png", "a10.jpg", "b.jpg" }, catalog.Photos.Select(photo => photo.Name).ToArray());
        Assert.Equal(2, catalog.SelectedIndex);
        Assert.Equal(temp.Path, catalog.FolderPath);
    }

    [Fact]
    public void Scan_directory_starts_at_first_photo()
    {
        using var temp = new TempFolder();
        temp.Touch("c.jpg");
        temp.Touch("a.jpg");

        var catalog = PhotoCatalogScanner.Scan(temp.Path);

        Assert.Equal("a.jpg", catalog.Photos[0].Name);
        Assert.Equal(0, catalog.SelectedIndex);
    }

    [Fact]
    public void Scan_non_image_opens_folder_photos()
    {
        using var temp = new TempFolder();
        temp.Touch("a.jpg");
        var notes = temp.Touch("notes.txt");

        var catalog = PhotoCatalogScanner.Scan(notes);

        Assert.Equal(["a.jpg"], catalog.Photos.Select(photo => photo.Name).ToArray());
        Assert.Equal(0, catalog.SelectedIndex);
    }

    [Fact]
    public void Scan_missing_path_throws()
    {
        var missing = Path.Combine(Path.GetTempPath(), "photoreg-missing-" + Guid.NewGuid().ToString("N"), "a.jpg");
        Assert.Throws<FileNotFoundException>(() => PhotoCatalogScanner.Scan(missing));
    }

    private sealed class TempFolder : IDisposable
    {
        public TempFolder()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "photoreg-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string Touch(string name)
        {
            var fullPath = System.IO.Path.Combine(Path, name);
            File.WriteAllBytes(fullPath, []);
            return fullPath;
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
