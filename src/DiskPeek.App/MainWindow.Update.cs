using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DiskPeek.Core;

namespace DiskPeek.App;

public partial class MainWindow
{
    private const string LatestInstaller = "https://github.com/GoByeBye/DiskPeek/releases/latest/download/DiskPeek-setup-win-x64.exe";
    private const string ReleasePage = "https://github.com/GoByeBye/DiskPeek/releases/latest";
    private readonly SettingsStore settingsStore = new();
    private readonly CancellationTokenSource updateLifetime = new();
    private UpdateService? updater;
    private UpdateDialog? updateDialog;
    private DispatcherTimer? updateTimer;
    private DispatcherTimer? updateDialogTimer;
    private CancellationTokenSource? updateOperation;
    private bool updateDownloading;
    private bool updateApplying;
    private bool exportActive;
    private int updateProgress;
    private string updateMessage = "Checks use the stable DiskPeek releases on GitHub. Downloads and installation always require your approval.";
    private string? settingsWarning;
    private string? updateInitializationError;

    private void InitializeUpdates()
    {
        try { updater = new UpdateService(new VelopackUpdateBackend()); }
        catch (Exception ex) { updateInitializationError = ex.Message; }
        settingsWarning = settingsStore.LoadError == null ? null : "Saved preferences could not be read. Defaults apply for this session.";
        updateTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(1) };
        updateTimer.Tick += (_, _) => BackgroundUpdateCheck();
        updateTimer.Start();
        RefreshUpdateUi();
        _ = InitialUpdateCheckAsync();
    }

    private async Task InitialUpdateCheckAsync()
    {
        try { await Task.Delay(TimeSpan.FromSeconds(3), updateLifetime.Token); BackgroundUpdateCheck(); }
        catch (OperationCanceledException) { }
    }
    private void BackgroundUpdateCheck()
    {
        if (closing || updater?.IsInstalled != true || updateOperation != null ||
            !UpdateService.ShouldCheck(settingsStore.Settings.AutomaticUpdateChecks, settingsStore.Settings.LastUpdateCheckUtc, DateTime.UtcNow)) return;
        _ = CheckUpdatesAsync(false, updateLifetime.Token);
    }

    private async Task CheckUpdatesAsync(bool manual, CancellationToken callerToken)
    {
        if (updater?.IsInstalled != true || updateOperation != null) return;
        var cancellation = updateOperation = CancellationTokenSource.CreateLinkedTokenSource(updateLifetime.Token, callerToken);
        cancellation.CancelAfter(TimeSpan.FromSeconds(30));
        settingsStore.Settings.LastUpdateCheckUtc = DateTime.UtcNow;
        SaveSettings();
        if (manual) updateMessage = "Checking stable releases…";
        RefreshUpdateUi();
        try
        {
            await updater.CheckAsync(cancellation.Token);
            if (closing) return;
            updateMessage = updater.Available == null ? "You have the latest available stable version." : "An update is available. Download it when convenient; installation requires a separate restart confirmation.";
        }
        catch (OperationCanceledException)
        {
            if (manual && !closing) updateMessage = callerToken.IsCancellationRequested ? "Update check cancelled." : "The update check timed out. Try again when your connection is available.";
        }
        catch (Exception ex)
        {
            // Automatic checks never interrupt scanning or show an offline/error popup.
            if (manual && !closing) updateMessage = "Could not check for updates. " + ex.Message;
        }
        finally
        {
            if (updateOperation == cancellation) updateOperation = null;
            cancellation.Dispose();
            if (!closing) RefreshUpdateUi();
        }
    }

    private void Updates_Click(object sender, RoutedEventArgs e)
    {
        if (updateDialog != null) { updateDialog.Activate(); return; }
        updateDialog = new UpdateDialog(this);
        updateDialog.Automatic.IsChecked = settingsStore.Settings.AutomaticUpdateChecks;
        updateDialog.Automatic.Click += (_, _) =>
        {
            if (updateDialog == null) return;
            settingsStore.Settings.AutomaticUpdateChecks = updateDialog.Automatic.IsChecked == true;
            SaveSettings();
            RefreshUpdateUi();
            if (settingsStore.Settings.AutomaticUpdateChecks) BackgroundUpdateCheck();
        };
        updateDialog.Check.Click += async (_, _) =>
        {
            if (updateDialog != null) await CheckUpdatesAsync(true, updateDialog.Lifetime.Token);
        };
        updateDialog.Primary.Click += UpdatePrimary_Click;
        updateDialog.Cancel.Click += (_, _) =>
        {
            if (updateOperation != null) updateOperation.Cancel();
            else updateDialog?.Close();
        };
        updateDialog.Releases.Click += (_, _) => OpenUpdateLink(ReleasePage);
        updateDialog.ClosingRequested += () => updateOperation?.Cancel();
        updateDialogTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        updateDialogTimer.Tick += (_, _) => RefreshUpdateUi();
        updateDialogTimer.Start();
        RefreshUpdateUi();
        updateDialog.ShowDialog();
        updateDialogTimer.Stop();
        updateDialogTimer = null;
        updateDialog = null;
    }

    private async void UpdatePrimary_Click(object sender, RoutedEventArgs e)
    {
        if (updateOperation != null || updateApplying || updateDialog == null) return;
        if (updater?.IsInstalled != true) { OpenUpdateLink(LatestInstaller); return; }
        if (updater.Available == null) { await CheckUpdatesAsync(true, updateDialog.Lifetime.Token); return; }
        if (!updater.DownloadReady)
        {
            var cancellation = updateOperation = CancellationTokenSource.CreateLinkedTokenSource(updateLifetime.Token, updateDialog.Lifetime.Token);
            updateDownloading = true;
            updateProgress = 0;
            updateMessage = "Downloading and verifying the update. You can cancel and keep using DiskPeek.";
            RefreshUpdateUi();
            IProgress<int> progress = new Progress<int>(value => { if (updateOperation == cancellation && !closing) { updateProgress = Math.Clamp(value, 0, 100); RefreshUpdateUi(); } });
            try
            {
                await updater.DownloadAsync(value => progress.Report(value), cancellation.Token);
                updateMessage = "Download verified. Select Update & restart when you are ready. Closing or restarting normally will not install it.";
            }
            catch (OperationCanceledException) { updateMessage = "Download cancelled. No update will be applied automatically."; }
            catch (Exception ex) { updateMessage = "The update could not be downloaded or verified. " + ex.Message; }
            finally
            {
                updateDownloading = false;
                if (updateOperation == cancellation) updateOperation = null;
                cancellation.Dispose();
                if (!closing) RefreshUpdateUi();
            }
            return;
        }
        if (IsScanning || exportActive) { updateMessage = "Finish or stop the scan and wait for CSV exports before restarting. Close this window to return to the scan."; RefreshUpdateUi(); return; }
        string prompt = result == null ? "Install the downloaded update and restart DiskPeek now?" : "Install the downloaded update and restart DiskPeek now?\n\nThe current scan results are kept only in memory and will be lost. Cancel and export them first if you want to keep them.";
        if (MessageBox.Show(updateDialog, prompt, "Update and restart", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
        try
        {
            if (IsScanning || exportActive) throw new InvalidOperationException("A scan or export is still running.");
            updateApplying = true;
            RefreshUpdateUi();
            SaveSettings();
            updater.Apply(IsScanning, exportActive, confirmed: true);
        }
        catch (Exception ex) { updateApplying = false; updateMessage = "The update could not be applied. " + ex.Message; RefreshUpdateUi(); }
    }

    private void RefreshUpdateUi()
    {
        bool available = updater?.Available != null;
        UpdatesButton.Content = available ? "Update " + updater!.Available!.TargetFullRelease.Version : "Updates";
        UpdatesButton.SetResourceReference(Control.ForegroundProperty, available ? "AccentBrush" : "TextBrush");
        UpdatesButton.ToolTip = available ? "A stable update is available. Review it before downloading or restarting." : "Check for updates and manage daily update checks";
        if (updateDialog == null) return;
        string current = updater?.CurrentVersion?.ToString() ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "unknown";
        bool installed = updater?.IsInstalled == true;
        bool busy = updateOperation != null || updateApplying;
        updateDialog.Heading.Text = installed ? available ? "Update " + updater!.Available!.TargetFullRelease.Version + " available" : "DiskPeek " + current : updateInitializationError == null ? "Updates for the installed app" : "Updater unavailable";
        updateDialog.Message.Text = installed ? updateMessage : updateInitializationError == null
            ? "You're running the portable or development app. Install DiskPeek to enable in-app updates and daily update notifications. The installer works for your Windows user account. Portable files are never overwritten by this updater."
            : "The updater could not be initialized. You can get the latest installer from the official GitHub release. " + updateInitializationError;
        updateDialog.Details.Text = installed
            ? $"Installed {current} · stable Windows x64 channel" + (available ? $"\nDownload: {FormatUtil.Bytes(updater!.Available!.TargetFullRelease.Size)} · verified before installation" : "")
            : $"Current version: {current}. Get the latest installer from the official GitHub release.";
        updateDialog.SaveWarning.Text = settingsWarning ?? "";
        updateDialog.Primary.Content = !installed ? "Get Windows installer" : updater!.DownloadReady ? "Update & restart" : available ? "Download update" : "Check for updates";
        updateDialog.Primary.IsEnabled = !busy && (!installed || !updater!.DownloadReady || (!IsScanning && !exportActive));
        updateDialog.Primary.ToolTip = installed && updater!.DownloadReady && (IsScanning || exportActive) ? "Finish or stop the scan and wait for exports before updating." : null;
        updateDialog.Check.Visibility = installed && available ? Visibility.Visible : Visibility.Collapsed;
        updateDialog.Check.IsEnabled = !busy;
        updateDialog.Cancel.Content = updateOperation != null ? "Cancel" : "Close";
        updateDialog.Cancel.IsEnabled = !updateApplying;
        updateDialog.Progress.Visibility = updateOperation != null ? Visibility.Visible : Visibility.Collapsed;
        updateDialog.Progress.IsIndeterminate = !updateDownloading;
        updateDialog.Progress.Value = updateProgress;
    }

    private void SaveSettings()
    {
        settingsWarning = settingsStore.TrySave(out _) ? null : "Preferences could not be saved. Your choices apply to this session.";
    }
    private void OpenUpdateLink(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { updateMessage = "Could not open the browser. " + ex.Message; RefreshUpdateUi(); }
    }
    private void StopUpdateChecks()
    {
        updateTimer?.Stop();
        updateDialogTimer?.Stop();
        updateLifetime.Cancel();
        updateOperation?.Cancel();
    }
}
