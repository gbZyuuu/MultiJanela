using System.Diagnostics;
using System.IO;
using System.Windows;
using RobloxMultiManager.Core;

namespace RobloxMultiManager;

internal static class InstallationRouting
{
    public static bool Handle(string[] arguments)
    {
        var current = Environment.ProcessPath;
        if (current is null) return false;
        var root = Path.GetFullPath(UserInstallationService.DefaultRoot);
        if (arguments.Contains("--install")) { Show(false); return true; }
        if (arguments.Contains("--uninstall") || arguments.Contains("--uninstall-helper"))
        {
            if (Path.GetFullPath(current).StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                var temporary = Path.Combine(Path.GetTempPath(), "MultiJanela-uninstall-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(temporary); var helper = Path.Combine(temporary, "RobloxMultiManager.exe");
                File.Copy(current, helper, false);
                var start = new ProcessStartInfo(helper) { UseShellExecute = false }; start.ArgumentList.Add("--uninstall-helper"); Process.Start(start);
                System.Windows.Application.Current.Shutdown(); return true;
            }
            Show(true); return true;
        }
        if (Path.GetFullPath(current).Equals(Path.Combine(root, "MultiJanela.exe"), StringComparison.OrdinalIgnoreCase))
        {
            var target = new UserInstallationService().ResolveCurrent();
            if (target is not null)
            {
                var start = new ProcessStartInfo(target) { UseShellExecute = false };
                foreach (var argument in arguments) start.ArgumentList.Add(argument);
                Process.Start(start); System.Windows.Application.Current.Shutdown(); return true;
            }
        }
        return false;
    }
    private static void Show(bool uninstall)
    {
        var app = System.Windows.Application.Current;
        app.ShutdownMode = ShutdownMode.OnMainWindowClose;
        var window = new InstallationWindow(uninstall); app.MainWindow = window; window.Show();
    }
}
