using System.Text;

using OmniRead.Localization;
using OmniRead.Services;

namespace OmniRead;

public static class Program {
    [STAThread]
    public static void Main(string[] args) {
        // Needed for legacy code pages (Windows-1252, Windows-1251, ...) in old text and FB2 files.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        // The language has to be chosen before any window is created.
        Localizer.Apply(SettingsStore.Load().Language);
        App app = new();
        app.InitializeComponent();
        app.Run(new MainWindow(args));
    }
}