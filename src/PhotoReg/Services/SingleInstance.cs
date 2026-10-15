using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;

namespace PhotoReg.Services;

internal static class SingleInstance
{
    private const string MutexName = @"Local\PhotoReg.Express";
    private const string PipeName = "PhotoReg.Express";

    public static bool TryBecomePrimary(out Mutex? mutex)
    {
        mutex = new Mutex(false, MutexName);
        try
        {
            if (mutex.WaitOne(0))
                return true;
        }
        catch (AbandonedMutexException)
        {
            return true;
        }

        mutex.Dispose();
        mutex = null;
        return false;
    }

    public static bool Send(string? path)
    {
        try
        {
            AllowSetForegroundWindow(-1);
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(2000);
            using var writer = new StreamWriter(client, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = true };
            writer.WriteLine(path ?? "");
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void Listen(Window window, Action<string?> onMessage)
    {
        var thread = new Thread(() =>
        {
            while (!window.Dispatcher.HasShutdownStarted)
            {
                try
                {
                    using var server = new NamedPipeServerStream(
                        PipeName,
                        PipeDirection.In,
                        1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.CurrentUserOnly);
                    server.WaitForConnection();
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    var line = reader.ReadLine();
                    window.Dispatcher.BeginInvoke(() => onMessage(line));
                }
                catch
                {
                    if (window.Dispatcher.HasShutdownStarted)
                        break;
                    Thread.Sleep(100);
                }
            }
        })
        {
            IsBackground = true,
            Name = "PhotoReg.Ipc",
        };
        thread.Start();
    }

    public static void BringToFront(Window window)
    {
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;
        window.Show();
        window.Activate();
        var handle = new WindowInteropHelper(window).Handle;
        if (handle != IntPtr.Zero)
            SetForegroundWindow(handle);
    }

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr windowHandle);
}
