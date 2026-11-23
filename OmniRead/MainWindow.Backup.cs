using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;

using Microsoft.Win32;

using OmniRead.Localization;
using OmniRead.Services;

using MessageBox = HandyControl.Controls.MessageBox;

namespace OmniRead;

/// <summary>Backing up what the reader remembers, and restoring it.</summary>
public partial class MainWindow {
    private string BackupFilter => $"{Localizer.Get("Backup_FileType")}|*{Backup.Extension}";

    private void OnBackupClick(object sender, RoutedEventArgs e) {
        SaveFileDialog dialog = new() {
            Title = Localizer.Get("Backup_Create"),
            Filter = BackupFilter,
            FileName = $"OmniRead {DateTime.Now:yyyy-MM-dd}{Backup.Extension}",
            AddExtension = true
        };
        if (dialog.ShowDialog(this) != true) {
            return;
        }
        // The backup is made from the files on disk, so they are brought up to date first.
        SaveProgress();
        SettingsStore.Save(_settings);
        AnnotationStore.Save();
        try {
            Backup.Create(dialog.FileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException) {
            MessageBox.Show(this, Localizer.Format("Backup_Failed", ex.Message), "OmniRead", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        MessageBox.Show(this, Localizer.Format("Backup_Done", dialog.FileName), "OmniRead", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnRestoreClick(object sender, RoutedEventArgs e) {
        OpenFileDialog dialog = new() { Title = Localizer.Get("Backup_Restore"), Filter = BackupFilter };
        if ((dialog.ShowDialog(this) != true)
            || (MessageBox.Show(this, Localizer.Get("Backup_RestoreQuestion"), "OmniRead", MessageBoxButton.OKCancel, MessageBoxImage.Question)
                != MessageBoxResult.OK)) {
            return;
        }
        StopReading();
        try {
            Backup.Restore(dialog.FileName);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException) {
            MessageBox.Show(this, Localizer.Format("Backup_Failed", ex.Message), "OmniRead", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        // From here on this window's own state must not be written over what was restored.
        SettingsStore.Frozen = true;
        try {
            if (Environment.ProcessPath is string program) {
                Process.Start(new ProcessStartInfo(program) { UseShellExecute = false });
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) {
            MessageBox.Show(this, Localizer.Get("Backup_RestartYourself"), "OmniRead", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        Close();
    }
}