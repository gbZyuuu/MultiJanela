using System.Net.Http;
using RobloxMultiManager.Core;

namespace RobloxMultiManager;

public partial class MainWindow
{
    private NotificationPreferences _notificationPreferences = new();
    private NotificationDispatcher? _notifications;
    private HttpClient? _notificationHttp;
    private NotificationsPanel? _notificationsPanel;
    private PreferenceFile<StoredNotificationPreferences> NotificationStore => new(_paths, "notifications.json", () => new(), p => p);
    private async Task InitializeNotificationsAsync()
    {
        try { _notificationPreferences = (await NotificationStore.LoadAsync()).ToRuntime(); } catch (Exception) { _notificationPreferences = new(); }
        _notificationHttp = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        _notifications = new NotificationDispatcher(_notificationHttp);
        _notificationsPanel = new NotificationsPanel(_notificationPreferences, async preferences =>
        { await NotificationStore.SaveAsync(StoredNotificationPreferences.FromRuntime(preferences)); _notificationPreferences = preferences; }, () => NotifyEvent("test"));
        NotificationsSettingsHost.Content = _notificationsPanel;
    }
    private void NotifyEvent(string kind)
    {
        if (_shutdownStarted || _storageMaintenanceRequested) return;
        if (_notificationPreferences.Desktop)
            _notifyIcon.ShowBalloonTip(3000, "MultiJanela", kind switch
            {
                "session-closed" => L.T("Uma sessão foi encerrada."),
                "launch-complete" => L.T("A sequência de abertura terminou."),
                "authentication" => L.T("Uma conta precisa de atenção no login."),
                _ => L.T("Notificações do MultiJanela ativadas.")
            }, System.Windows.Forms.ToolTipIcon.Info);
        _notifications?.Enqueue(_notificationPreferences, kind, Instances.Count);
    }
}
