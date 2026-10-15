using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using PhotoReg.Services;

namespace PhotoReg;

public partial class App : Application
{
    private Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        SetCurrentProcessExplicitAppUserModelID("PhotoReg.Express");

        DispatcherUnhandledException += (_, args) =>
        {
            LogCrash(args.Exception);
            MessageBox.Show(args.Exception.Message, "PhotoReg", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => LogCrash(args.ExceptionObject);

        if (e.Args.Any(arg => string.Equals(arg, "--unregister", StringComparison.OrdinalIgnoreCase)))
        {
            try { ShellIntegration.Unregister(); } catch { }
            Shutdown();
            return;
        }

        if (!SingleInstance.TryBecomePrimary(out _mutex))
        {
            var existingPath = FirstPath(e.Args);
            if (!SingleInstance.Send(existingPath))
            {
                MessageBox.Show(
                    "PhotoReg уже запущен, но не отвечает. Закройте его и откройте фотографию ещё раз.",
                    "PhotoReg",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }

            Shutdown();
            return;
        }

        var exe = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(exe))
        {
            try { ShellIntegration.Register(exe); } catch { }
        }

        var window = new MainWindow();
        MainWindow = window;
        SingleInstance.Listen(window, line =>
        {
            SingleInstance.BringToFront(window);
            if (!string.IsNullOrWhiteSpace(line))
                window.OpenPath(line);
        });
        window.Show();

        var path = FirstPath(e.Args);
        if (path != null)
            window.OpenPath(path);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_mutex != null)
        {
            try { _mutex.ReleaseMutex(); } catch { }
            _mutex.Dispose();
        }

        base.OnExit(e);
    }

    private static string? FirstPath(IEnumerable<string> args)
    {
        foreach (var arg in args)
        {
            if (string.IsNullOrWhiteSpace(arg) || arg.StartsWith('-'))
                continue;
            return arg;
        }

        return null;
    }

    private static void LogCrash(object error)
    {
        try
        {
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "photoreg-crash.txt"), error.ToString());
        }
        catch
        {
            // Crash logging is best-effort.
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
}
