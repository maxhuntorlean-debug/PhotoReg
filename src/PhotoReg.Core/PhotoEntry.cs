namespace PhotoReg.Core;

public sealed record PhotoEntry(string FullPath)
{
    public string Name => Path.GetFileName(FullPath);
}
