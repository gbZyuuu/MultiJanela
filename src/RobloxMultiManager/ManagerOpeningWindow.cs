using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using RobloxMultiManager.Core;

namespace RobloxMultiManager;

internal sealed class ManagerOpeningWindow : Window
{
    private bool _completed;
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 16 };
    public ManagerOpeningWindow(CancellationTokenSource cancellation)
    {
        Title = "MultiJanela " + typeof(App).Assembly.GetName().Version!.ToString(3);
        Width = 540; Height = 300; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        SetResourceReference(StyleProperty, "AppWindowStyle");
        var content = new StackPanel { Margin = new Thickness(28) };
        content.Children.Add(new TextBlock { Text = Title, FontSize = 25, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 18) });
        content.Children.Add(_status);
        var show = new System.Windows.Controls.Button { Content = L.T("Mostrar versão em execução"), Margin = new Thickness(0, 20, 0, 0), HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
        show.Click += (_, _) => NativeManagerActivation.BringExistingToFront();
        content.Children.Add(show);
        Content = content;
        Closed += (_, _) => { if (!_completed) cancellation.Cancel(); };
    }
    public void SetStatus(string action, bool legacy) => _status.Text = legacy
        ? L.T("A versão anterior ainda está em execução. Encerre-a pelo menu Sair quando terminar suas operações; esta janela abrirá a nova versão automaticamente. Os jogos continuarão abertos.")
        : action == "save-failed"
            ? L.T("Não foi possível salvar o estado da versão atual. Ela continua aberta; verifique as permissões de armazenamento e tente novamente.")
            : L.T("Preparando a troca de versão. Conclua as operações pendentes e feche os painéis abertos para continuar. Seus jogos continuarão abertos.");
    public void Complete() { _completed = true; Close(); }
}

internal static class NativeManagerActivation
{
    private delegate bool EnumWindowsCallback(nint handle, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsCallback callback, nint parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint handle, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint handle, System.Text.StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(nint handle, int command);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint handle);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint handle);
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(uint processId);

    internal static Version? BringExistingToFront()
    {
        var current = Environment.ProcessId;
        var peers = new HashSet<uint>();
        var versions = new Dictionary<uint, Version?>();
        using var self = Process.GetCurrentProcess();
        foreach (var process in Process.GetProcessesByName("RobloxMultiManager"))
        {
            using (process)
            {
                try
                {
                    if (process.Id != current && process.SessionId == self.SessionId)
                    {
                        peers.Add((uint)process.Id);
                        var file = process.MainModule?.FileName;
                        versions[(uint)process.Id] = file is not null && Version.TryParse(FileVersionInfo.GetVersionInfo(file).FileVersion, out var version) ? version : null;
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }
        Version? existingVersion = null;
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var pid);
            if (!peers.Contains(pid)) return true;
            var title = new System.Text.StringBuilder(128);
            GetWindowText(window, title, title.Capacity);
            if (title.ToString() != "MultiJanela") return true;
            existingVersion = versions.GetValueOrDefault(pid);
            AllowSetForegroundWindow(pid);
            ShowWindowAsync(window, IsIconic(window) ? 9 : 5);
            SetForegroundWindow(window);
            return false;
        }, 0);
        return existingVersion;
    }
}
