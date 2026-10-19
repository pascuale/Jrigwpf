using System.Windows;

namespace Jrig;

public partial class App : Application
{
    public App()
    {
        // Show a message instead of silently closing if something on the UI thread goes wrong.
        DispatcherUnhandledException += (_, e) =>
        {
            MessageBox.Show(e.Exception.Message, "jrig", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        };
    }
}
