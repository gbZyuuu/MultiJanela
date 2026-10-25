using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace RobloxWindowFixture;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var lifetimeSeconds = ReadInt(args, "--lifetime", 30, 5, 120);

        // A window that refuses to shrink is the case that used to escape the
        // screen: the window manager asks the window before resizing it, and WPF
        // answers WM_GETMINMAXINFO from MinWidth/MinHeight, exactly like a real
        // client with a minimum size. Without this the fixture accepts any cell and
        // the containment path is never exercised.
        var minimumWidth = ReadInt(args, "--min-width", 0, 0, 4000);
        var minimumHeight = ReadInt(args, "--min-height", 0, 0, 4000);

        var application = new Application
        {
            ShutdownMode = ShutdownMode.OnMainWindowClose
        };
        var window = new Window
        {
            Title = $"Roblox layout fixture — PID {Environment.ProcessId}",
            Width = 640,
            Height = 480,
            MinWidth = minimumWidth,
            MinHeight = minimumHeight,
            Left = 40,
            Top = 40,
            Opacity = 0.08,
            Background = Brushes.MidnightBlue,
            ShowInTaskbar = true,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Content = new System.Windows.Controls.TextBlock
            {
                Text = "Janela temporária de validação",
                Foreground = Brushes.White,
                FontSize = 22,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

        var timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(lifetimeSeconds)
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            window.Close();
        };
        timer.Start();
        application.Run(window);
    }

    private static int ReadInt(
        string[] args,
        string name,
        int fallback,
        int minimum,
        int maximum)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0
            && index + 1 < args.Length
            && int.TryParse(args[index + 1], out var parsed)
            ? Math.Clamp(parsed, minimum, maximum)
            : fallback;
    }
}
