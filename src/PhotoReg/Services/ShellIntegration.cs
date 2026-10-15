using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;
using PhotoReg.Core;

namespace PhotoReg.Services;

internal static class ShellIntegration
{
    private const string VerbName = "PhotoReg";
    private const int AssocChanged = 0x08000000;

    public static void Register(string exePath)
    {
        var command = $"\"{exePath}\" \"%1\"";
        using (var marker = Registry.CurrentUser.OpenSubKey($@"Software\Classes\SystemFileAssociations\.jpg\shell\{VerbName}\command"))
        {
            if (string.Equals(marker?.GetValue("") as string, command, StringComparison.OrdinalIgnoreCase))
                return;
        }

        foreach (var extension in PhotoCatalogScanner.Extensions)
        {
            using var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\SystemFileAssociations\{extension}\shell\{VerbName}");
            key.SetValue("", "Открыть в PhotoReg");
            key.SetValue("Icon", $"\"{exePath}\",0");
            key.SetValue("Position", "Top");
            using var commandKey = key.CreateSubKey("command");
            commandKey.SetValue("", command);
        }

        using (var app = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Applications\PhotoReg.exe"))
        {
            app.SetValue("FriendlyAppName", "PhotoReg");
            using (var commandKey = app.CreateSubKey(@"shell\open\command"))
                commandKey.SetValue("", command);
            using (var icon = app.CreateSubKey("DefaultIcon"))
                icon.SetValue("", $"\"{exePath}\",0");
            using var types = app.CreateSubKey("SupportedTypes");
            foreach (var extension in PhotoCatalogScanner.Extensions)
                types.SetValue(extension, "");
        }

        SHChangeNotify(AssocChanged, 0, IntPtr.Zero, IntPtr.Zero);
    }

    public static void Unregister()
    {
        foreach (var extension in PhotoCatalogScanner.Extensions)
        {
            try
            {
                Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\SystemFileAssociations\{extension}\shell\{VerbName}", throwOnMissingSubKey: false);
            }
            catch (ArgumentException)
            {
                // The key is already gone.
            }
        }

        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\Applications\PhotoReg.exe", throwOnMissingSubKey: false);
        }
        catch (ArgumentException)
        {
            // The key is already gone.
        }

        SHChangeNotify(AssocChanged, 0, IntPtr.Zero, IntPtr.Zero);
    }

    public static void PromptOpenWith(Window owner, string filePath)
    {
        var info = new OpenAsInfo
        {
            FileName = filePath,
            Flags = 0x1 | 0x2 | 0x4,
        };
        int result = SHOpenWithDialog(new WindowInteropHelper(owner).Handle, ref info);
        if (result < 0)
            OpenDefaultAppsSettings();
    }

    public static void OpenDefaultAppsSettings()
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:defaultapps")
        {
            UseShellExecute = true,
        });
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHOpenWithDialog(IntPtr owner, ref OpenAsInfo info);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenAsInfo
    {
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? FileName;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? ClassName;

        public int Flags;
    }
}
