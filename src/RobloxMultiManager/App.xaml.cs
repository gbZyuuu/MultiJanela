using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;

namespace RobloxMultiManager;

public partial class App : System.Windows.Application
{
    public App()
    {
        if (RobloxMultiManager.Core.CpuQuotaWatchdog.RunIfRequested(Environment.GetCommandLineArgs().Skip(1).ToArray()) is { } exitCode)
            Environment.Exit(exitCode);
    }

    private RobloxMultiManager.Core.ManagerInstanceCoordinator? _instances;
    private readonly List<IDisposable> _guardContinuity = [];

    protected override async void OnStartup(StartupEventArgs e)
    {
        RobloxMultiManager.Core.UiPreferences.Initialize();
        UiMotion.Initialize();
        base.OnStartup(e);
        EventManager.RegisterClassHandler(
            typeof(Window),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) =>
            {
                if (sender is Window window)
                {
                    WindowThemeService.ApplyDarkTitleBar(window);
                }
            }));

        try { if (InstallationRouting.Handle(e.Args)) return; }
        catch (Exception)
        {
            AppDialog.Show(null, RobloxMultiManager.Core.L.T("Não foi possível abrir a instalação do aplicativo. Os dados das contas foram preservados."), "MultiJanela", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown(); return;
        }
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _instances = new();
        ManagerOpeningWindow? opening = null;
        using var cancelled = new CancellationTokenSource();
        var committed = false;
        var legacy = false;
        var focusedPeer = false;
        Version? existingVersion = null;
        try
        {
            await RobloxMultiManager.Core.ManagerRestartWait.WaitAsync(e.Args, Environment.ProcessPath!, RetainNativeGuard, cancelled.Token);
            while (!_instances.TryAcquire())
            {
                if (!focusedPeer) { existingVersion = NativeManagerActivation.BringExistingToFront(); focusedPeer = true; }
                var action = legacy || committed ? "waiting" : await _instances.RequestAsync(
                    typeof(App).Assembly.GetName().Version!.ToString(),
                    () => { RetainNativeGuard(); return Task.CompletedTask; }, cancelled.Token);
                if (action == "activated") { Shutdown(); return; }
                committed |= action == "ready";
                if (action == "legacy" && existingVersion == typeof(App).Assembly.GetName().Version) { Shutdown(); return; }
                legacy |= action == "legacy" && (existingVersion is null || existingVersion < new Version(2, 5, 3, 0));
                opening ??= new ManagerOpeningWindow(cancelled);
                opening.SetStatus(action, legacy);
                if (!opening.IsVisible) opening.Show();
                if(action=="vault-incompatible")
                {AppDialog.Show(opening,RobloxMultiManager.Core.L.T("O cofre exige MultiJanela 2.9 ou superior. A versão atual foi preservada."),"MultiJanela",MessageBoxButton.OK,MessageBoxImage.Warning);Shutdown();return;}
                if (action == "save-failed")
                {
                    if (AppDialog.Show(opening,
                            RobloxMultiManager.Core.L.T("Não foi possível salvar o estado. Deseja tentar novamente? A versão atual continua aberta."),
                            "MultiJanela", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                    { Shutdown(); return; }
                }
                await Task.Delay(1500, cancelled.Token);
            }
            if (!await VaultUnlockWindow.UnlockIfNeededAsync(new RobloxMultiManager.Core.AppPaths())) { Shutdown(); return; }
            var window = new MainWindow();
            MainWindow = window;
            _instances.Listen(
                (request, token) => Dispatcher.InvokeAsync(() => window.PrepareManagerOpenAsync(request)).Task.Unwrap(),
                () => Dispatcher.InvokeAsync(window.CommitManagerHandoffAsync).Task.Unwrap(),
                () => Dispatcher.InvokeAsync(window.AbortManagerHandoff).Task);
            window.Show();
            opening?.Complete();
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            await window.StartupCompletion;
            ReleaseNativeGuard();
        }
        catch (OperationCanceledException) { Shutdown(); }
        catch (Exception)
        {
            AppDialog.Show(null, RobloxMultiManager.Core.L.T("Não foi possível abrir o gerenciador. A versão em execução e os jogos foram preservados."),
                "MultiJanela", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        RobloxMultiManager.Core.SecureJsonFiles.Lock();
        ReleaseNativeGuard();
        _instances?.Dispose();
        base.OnExit(e);
    }

    private void RetainNativeGuard()
    {
        foreach (var name in new[] { RobloxMultiManager.Core.MultiInstanceGuard.DefaultMutexName,
                     RobloxMultiManager.Core.MultiInstanceGuard.DefaultEventName })
        {
            try { _guardContinuity.Add(Mutex.OpenExisting(name)); }
            catch (WaitHandleCannotBeOpenedException)
            {
                try { _guardContinuity.Add(EventWaitHandle.OpenExisting(name)); }
                catch (WaitHandleCannotBeOpenedException) { }
            }
        }
    }
    private void ReleaseNativeGuard()
    { foreach (var handle in _guardContinuity) handle.Dispose(); _guardContinuity.Clear(); }
}

internal static class WindowThemeService
{
    private const int DwmUseImmersiveDarkModeBefore20H1 = 19;
    private const int DwmUseImmersiveDarkMode = 20;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        nint window,
        int attribute,
        ref int value,
        int valueSize);

    internal static void ApplyDarkTitleBar(Window window)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10))
        {
            return;
        }

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == nint.Zero)
        {
            return;
        }

        var enabled = AppearanceService.Current.Theme == "light" ? 0 : 1;
        if (DwmSetWindowAttribute(
                handle,
                DwmUseImmersiveDarkMode,
                ref enabled,
                sizeof(int)) != 0)
        {
            _ = DwmSetWindowAttribute(
                handle,
                DwmUseImmersiveDarkModeBefore20H1,
                ref enabled,
                sizeof(int));
        }
    }
}
