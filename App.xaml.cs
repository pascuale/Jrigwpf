using System.Windows;
using Jrig.Audio;

namespace Jrig;

public partial class App : Application
{
    public App()
    {
        // Show a message instead of silently closing if something on the UI thread goes wrong, and log it.
        DispatcherUnhandledException += (_, e) =>
        {
            DiagLog.Error("Unhandled UI exception", e.Exception);
            MessageBox.Show(e.Exception.Message + "\n\nDetails are in the log:\n" + DiagLog.LogPath, "jrig", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            DiagLog.Error("Unhandled exception (app is closing)", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
            DiagLog.Error("Unobserved task exception", e.Exception);
    }
}
