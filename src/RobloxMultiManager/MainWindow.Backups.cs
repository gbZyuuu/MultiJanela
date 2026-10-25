using System.Diagnostics;
using System.Windows;
using RobloxMultiManager.Core;

namespace RobloxMultiManager;

public partial class MainWindow
{
    private BackupPanel? _backupPanel;
    private bool _storageMaintenance, _storageMaintenanceRequested;
    private void InitializeBackups()
    {
        _backupPanel = new BackupPanel(new EncryptedBackupService(_paths), MaintainStorageAsync);
        BackupSettingsHost.Content = _backupPanel;
    }
    private async Task MaintainStorageAsync(Func<Task> operation, bool restart)
    {
        if (_updatesPanel?.IsBusy == true || _apiApplying || _localApiPanel?.IsBusy == true || _automationCancellation is not null || _automationTickBusy || _automationPanel?.IsBusy == true || _workspacesPanel?.IsBusy == true || _gamesHub?.IsBusy == true || _appearancePanel?.IsBusy == true
            || DrawerHost.Visibility == Visibility.Visible || _storageMaintenanceRequested || _shutdownStarted || _handoffPrepared || _activeLaunches > 0
            || _launchBatchCancellation is not null || _launchingProfiles.Count > 0 || _reconnectCoordinator.HasWork
            || _reconnectInspections > 0 || !_reconnectPump.IsCompleted || _accountPortal is not null
            || OwnedWindows.Cast<Window>().Any(w => w.IsVisible))
            throw new ArgumentException(L.T("Conclua as operações e feche as páginas de contas antes de iniciar o backup."));
        _storageMaintenanceRequested = true; IsEnabled = false;
        var restoreStarted = false;
        try
        {
            await HomeCpuSlider.FlushAsync(finishInteraction: true); await HomeGraphics.FlushAsync(); await _cpuWork; await _preferencesWrite;
            await SaveProfilesAsync(throwOnError: true); await SaveSettingsAsync(throwOnError: true); await SaveOwnershipAsync(throwOnError: true);
            await SaveWorkspaceAsync();
            await _historyWrite;
            _storageMaintenance = true;
            await _profilesSaveGate.WaitAsync(); _profilesSaveGate.Release();
            await _settingsSaveGate.WaitAsync(); _settingsSaveGate.Release();
            await _ownershipSaveGate.WaitAsync(); _ownershipSaveGate.Release();
            restoreStarted = restart;
            await operation();
            if (restart)
            {
                using var current = Process.GetCurrentProcess();
                var next = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
                next.ArgumentList.Add("--restart-after"); next.ArgumentList.Add(current.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
                next.ArgumentList.Add(current.StartTime.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
                Process.Start(next);
                await ExitApplicationAsync(handoff: true);
            }
        }
        catch
        {
            if (restoreStarted)
            {
                // Avoid writing stale in-memory data over a restored or rolled-back store.
                AppDialog.Show(this, L.T("A restauração não terminou. Reinicie o gerenciador antes de continuar; o backup anterior foi preservado."),
                    L.T("Restaurar backup"), MessageBoxButton.OK, MessageBoxImage.Warning);
                await ExitApplicationAsync(handoff: true);
            }
            throw;
        }
        finally
        {
            if (!restoreStarted)
            {
                _storageMaintenance = _storageMaintenanceRequested = false; IsEnabled = true;
                ResumeHistoryWrites();
                await SaveProfilesAsync(); await SaveSettingsAsync(); await SaveOwnershipAsync();
            }
        }
    }
}
