using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Data;

using OmniRead.Localization;
using OmniRead.Services;

namespace OmniRead;

/// <summary>One line of the voice list.</summary>
public sealed record PiperVoiceRow(PiperVoice Voice, string Language, string Name, string Detail, string Action);

/// <summary>Installs the Piper engine and downloads or removes its public-domain and CC0 voices.</summary>
public partial class PiperWindow : Window {
    private readonly CancellationTokenSource _cancel = new();
    private bool _busy;

    public PiperWindow() {
        InitializeComponent();
        Refresh();
    }

    private void Refresh() {
        bool installed = Piper.EngineInstalled;
        EngineText.Text = installed ? Localizer.Format("Piper_EngineReady", Piper.Version) : Localizer.Get("Piper_EngineMissing");
        InstallButton.Visibility = installed ? Visibility.Collapsed : Visibility.Visible;
        UninstallButton.Visibility = installed ? Visibility.Visible : Visibility.Collapsed;
        // Languages in alphabetical order; within a language, from the lowest quality to the highest.
        List<PiperVoiceRow> rows = [.. Piper.Catalog
            .OrderBy(static voice => voice.LanguageName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(static voice => voice.QualityRank)
            .ThenBy(static voice => voice.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(static voice => new PiperVoiceRow(
                voice,
                voice.LanguageName,
                voice.ShortName,
                Describe(voice),
                Localizer.Get(voice.IsInstalled ? "Piper_Remove" : "Piper_Download")))];
        ListCollectionView view = new(rows);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(PiperVoiceRow.Language)));
        VoiceList.ItemsSource = view;
    }

    /// <summary>Licence, size or installed state, and anything stricter that comes with the voice it was trained from.</summary>
    private static string Describe(PiperVoice voice) {
        string detail = voice.IsInstalled
            ? Localizer.Format("Piper_VoiceInstalled", voice.License)
            : Localizer.Format("Piper_VoiceDetail", voice.License, Math.Round(voice.Bytes / 1048576.0));
        return (voice.Base == PiperBase.Free) ? detail : $"{detail} · {Localizer.Get($"Piper_Base_{voice.Base}")}";
    }

    /// <summary>Runs one download or install with the buttons disabled, reporting progress in the status line.</summary>
    private async Task RunAsync(Func<IProgress<string>, Task> work) {
        if (_busy) {
            return;
        }
        _busy = true;
        InstallButton.IsEnabled = false;
        UninstallButton.IsEnabled = false;
        VoiceList.IsEnabled = false;
        Progress<string> progress = new(message => StatusText.Text = message);
        try {
            await work(progress);
        }
        catch (OperationCanceledException) {
            // The window was closed part-way; an unfinished download is not counted as installed.
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException
            or UnauthorizedAccessException or Win32Exception) {
            StatusText.Text = Localizer.Format("Piper_Failed", ex.Message);
        }
        _busy = false;
        InstallButton.IsEnabled = true;
        UninstallButton.IsEnabled = true;
        VoiceList.IsEnabled = true;
        Refresh();
    }

    private void OnInstallClick(object sender, RoutedEventArgs e) {
        _ = RunAsync(progress => Piper.InstallEngineAsync(progress, _cancel.Token));
    }

    private void OnUninstallClick(object sender, RoutedEventArgs e) {
        // Yes removes everything, No keeps the downloaded voices for a later reinstall.
        MessageBoxResult answer = MessageBox.Show(
            this, Localizer.Get("Piper_UninstallQuestion"), Localizer.Get("Piper_UninstallEngine"), MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Cancel) {
            return;
        }
        _ = RunAsync(progress => {
            Piper.Uninstall(answer == MessageBoxResult.Yes);
            progress.Report(Localizer.Get("Piper_Uninstalled"));
            return Task.CompletedTask;
        });
    }

    private void OnVoiceActionClick(object sender, RoutedEventArgs e) {
        if (((FrameworkElement)sender).DataContext is not PiperVoiceRow row) {
            return;
        }
        if (row.Voice.IsInstalled) {
            _ = RunAsync(_ => {
                Piper.RemoveVoice(row.Voice);
                return Task.CompletedTask;
            });
        }
        else {
            _ = RunAsync(progress => Piper.DownloadVoiceAsync(row.Voice, progress, _cancel.Token));
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) {
        Close();
    }

    protected override void OnClosed(EventArgs e) {
        base.OnClosed(e);
        _cancel.Cancel();
    }
}