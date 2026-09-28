using System.IO;
using System.Net.Http;
using System.Windows;
using Sysoptimizer.Services;

namespace Sysoptimizer;

/// <summary>"Update available" — and, after an update has landed, the same window as a read-only "What's new".</summary>
public partial class UpdateDialog : Window
{
    private readonly Updater.Release _release;
    private readonly CancellationTokenSource _cancel = new();

    public UpdateDialog(Window owner, Updater.Release release, bool notesOnly = false)
    {
        InitializeComponent();
        Owner = owner;
        _release = release;
        SourceInitialized += (_, _) => WindowGlass.EnableAcrylic(this);
        Notes.Text = release.Notes.Trim() == "" ? "No release notes." : release.Notes.Trim();

        if (notesOnly)
        {
            Title = "What's new";
            Heading.Text = $"Sysoptimizer {release.Version}";
            Sub.Text = "Updated on this machine.";
            UpdateButton.Visibility = Visibility.Collapsed;
            LaterButton.Content = "Close";
        }
        else
        {
            Heading.Text = $"Sysoptimizer {release.Version} is available";
            Sub.Text = $"You have {Updater.Current}. The installer is {CleanupService.FormatSize(release.AssetSize)}.";
        }
        Closed += (_, _) => _cancel.Cancel();
    }

    private void Later_Click(object sender, RoutedEventArgs e) => Close();

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        UpdateButton.IsEnabled = LaterButton.IsEnabled = false;
        Progress.Visibility = Visibility.Visible;
        Status.Text = "Downloading...";
        try
        {
            var progress = new Progress<double>(p => { Progress.Value = p; Status.Text = $"Downloading... {p * 100:0}%"; });
            var installer = await Updater.Download(_release, progress, _cancel.Token);
            Status.Text = "Installing — Sysoptimizer will restart.";
            Updater.InstallAndRestart(installer);
            System.Windows.Application.Current.Shutdown(); // the installer replaces our files; nothing of ours may stay
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            Progress.Visibility = Visibility.Collapsed;
            Status.Text = ex.Message;
            UpdateButton.IsEnabled = LaterButton.IsEnabled = true;
        }
    }
}
