using System.Windows;
using System.Windows.Threading;

using OmniRead.Localization;

namespace OmniRead;

public partial class App : Application {
    protected override void OnStartup(StartupEventArgs e) {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e) {
        MessageBox.Show(
            Localizer.Format("Error_Unexpected", e.Exception.Message),
            "OmniRead",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        e.Handled = true;
    }
}