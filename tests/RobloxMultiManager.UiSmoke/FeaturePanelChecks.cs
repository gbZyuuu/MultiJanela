using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RobloxMultiManager;
using RobloxMultiManager.Core;

internal static partial class Program
{
    private static void RunFeaturePanelChecks(MainWindow main, string output)
    {
        var paths = new AppPaths(Path.Combine(output, "new-feature-fixtures")); paths.EnsureCreated();
        void Await(Task task) { PumpUntil(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
        void Load(FrameworkElement control) => control.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        var games = new GameLibraryStore(paths); var game = new GameSummary(12, 34, "Fixture adventure", "Explore with your accounts in a shared session.");
        Await(games.SaveAsync([new(game, false, DateTime.UtcNow, [])]));
        using var http = new System.Net.Http.HttpClient(new OfflineHandler());
        var used = ""; var hub = new GamesHub(new RobloxGameService(http), games, (link, _) => used = link, () => 3);
        Load(hub); PumpUntil(() => !hub.IsBusy);
        var list = (ListBox)hub.FindName("Games"); list.SelectedIndex = 0;
        InvokeResult(hub, "UseGame_Click", hub, new RoutedEventArgs()); Check(used == game.Url, "Game card selects the real launch destination");
        InvokeResult(hub, "Favorite_Click", hub, new RoutedEventArgs()); PumpUntil(() => !hub.IsBusy);
        Await(Task.WhenAll(hub.RecordLaunchAsync(34), hub.RecordLaunchAsync(34)));
        var bookmarks = games.LoadAsync(); Await(bookmarks);
        Check(bookmarks.Result.Length == 1 && bookmarks.Result[0].Favorite && bookmarks.Result[0].LastPlayedUtc != default, "Concurrent game history preserves favorites and avoids duplicate bookmarks");
        var serverList = (ListBox)hub.FindName("Servers");
        ((System.Collections.ObjectModel.ObservableCollection<PublicGameServer>)serverList.ItemsSource).Add(new(Guid.NewGuid().ToString(), 2, 12, 20));
        ((ComboBox)hub.FindName("Preference")).SelectedIndex = 1;
        Check(serverList.Items.Count == 0 && !((Button)hub.FindName("MoreButton")).IsEnabled, "Changing server sort clears stale results and pagination");
        var workspaceStore = new WorkspaceLibraryStore(paths);
        var workspaces = new WorkspacesPanel(workspaceStore, main.Profiles, (name, id, ids) => new(id, name, ids, "https://www.roblox.com/home", 5, 100, -1, false), _ => { }, () => [main.Profiles[0].Id], _ => { }, _ => { });
        Load(workspaces); PumpUntil(() => !workspaces.IsBusy);
        ((TextBox)workspaces.FindName("Notes")).Text = "Unsaved fixture note";
        var draft = InvokeResult(workspaces, "CaptureDraft");
        var formType = typeof(MainWindow).Assembly.GetType("RobloxMultiManager.FormDrafts")!;
        ((TextBox)workspaces.FindName("Notes")).Clear();
        formType.GetMethod("Restore", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [workspaces, draft, new[] { "Notes" }]);
        Check(((TextBox)workspaces.FindName("Notes")).Text == "Unsaved fixture note", "Workspace form draft preserves unsaved text");
        var appearance = new AppearancePanel(new AppearancePreferencesStore(paths));
        var automation = new AutomationPanel(new PreferenceFile<AutomationPreferences>(paths, "automation.json", () => AutomationPreferences.Empty, AutomationPreferences.Validate), () => [], _ => Task.CompletedTask, () => { });
        var panels = new Dictionary<string, FrameworkElement>
        {
            ["games"] = hub, ["workspaces"] = workspaces, ["appearance"] = appearance, ["automation"] = automation,
            ["notifications"] = new NotificationsPanel(new(), _ => Task.CompletedTask, () => { }),
            ["api"] = new LocalApiPanel(new(), (value, _) => Task.FromResult(value)),
            ["backup"] = new BackupPanel(new EncryptedBackupService(paths), (operation, _) => operation()),
            ["tuning"] = new ProcessTuningPanel(new(), _ => Task.CompletedTask, () => Task.FromResult(0)),
            ["updates"] = new UpdatesPanel(new ManagerUpdateService(http), Path.Combine(paths.Root, "Updates"))
        };
        var appearanceType = typeof(MainWindow).Assembly.GetType("RobloxMultiManager.AppearanceService")!;
        void Theme(string theme, int size) => appearanceType.GetMethod("Apply")!.Invoke(null, [new AppearancePreferences(theme, "#B9ADFF", size)]);
        try
        {
            foreach (var (theme, scale) in new[] { ("graphite", 100), ("light", 125), ("contrast", 150), ("forest", 100) })
            {
                Theme(theme, scale);
                foreach (var (name, panel) in panels)
                {
                    var frame = new Border { Padding = new Thickness(18), Child = panel };
                    frame.SetResourceReference(Border.BackgroundProperty, "WindowBackground");
                    System.Windows.Documents.TextElement.SetForeground(frame, (Brush)Application.Current.Resources["TextPrimary"]);
                    Render(frame, 760, 650, Path.Combine(output, $"feature-{name}-{theme}-{scale}.png"));
                    Check(panel.ActualWidth <= 724.5, $"{name}: fits compact viewport with {theme} / {scale}% text");
                    var buttons = Descendants<Button>(panel).Where(b => b.ActualWidth > 0 && b.Visibility == Visibility.Visible).ToArray();
                    Check(buttons.All(b => b.ActualHeight >= b.FontSize), $"{name}: buttons accommodate enlarged text");
                    foreach (var button in buttons.Where(b => b.Content is string && double.IsNaN(b.Width)))
                    {
                        var text = new FormattedText((string)button.Content, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                            new Typeface(button.FontFamily, button.FontStyle, button.FontWeight, button.FontStretch), button.FontSize, Brushes.Black, 1);
                        Check(button.ActualWidth + 2 >= text.Width + button.Padding.Left + button.Padding.Right, $"{name}: action text fits: {button.Content}");
                    }
                    frame.Child = null;
                }
            }
            Get<TextBox>(main, "AccountSearchInput").Clear(); Invoke(main, "ApplyAccountFilter");
            main.Instances.Clear();
            for (var index = 0; index < 3; index++)
            {
                var profile = main.Profiles[index];
                main.Instances.Add(new RobloxInstanceInfo(51000 + index, DateTime.UtcNow.AddMinutes(-index * 5), (nint)(index + 1), "Fixture", RobloxInstanceState.Running, true,
                    ProfileName: profile.Name, FileVersion: "0.735.3.0", AccountUserId: profile.RobloxUserId, AccountUsername: profile.RobloxUsername, AccountIsExact: true));
            }
            Invoke(main, "RefreshWorkspaceAccounts");
            foreach (var scale in new[] { 100, 125, 150 })
            {
                Theme("graphite", scale);
                Invoke(main, "ComfortAccounts_Click", main, new RoutedEventArgs());
                Render((FrameworkElement)main.Content, 860, 640, Path.Combine(output, $"home-compact-text-{scale}.png"));
                Check(Get<ListBox>(main, "SavedAccountsList").ActualWidth > 200 && Get<ListBox>(main, "UnifiedAccountsList").ActualWidth > 350,
                    $"Home keeps accounts and sessions separate at {scale}% text in minimum viewport");
                var root = (FrameworkElement)main.Content;
                Check(root.ActualWidth <= 860.5, $"Enlarged text does not force the window past its viewport at {scale}%");
                foreach (var button in Descendants<Button>(root).Where(b => b.ToolTip?.ToString() == L.T("Minimizar") || b.ToolTip?.ToString() == L.T("Maximizar ou restaurar")))
                {
                    Console.WriteLine($"[CHROME] {button.ToolTip}: {button.RenderSize}, opacity={button.Opacity}, parent={((FrameworkElement)button.Parent).Visibility}, drawer={Get<Border>(main, "DrawerHost").Visibility}");
                    Check(button.ActualWidth >= 30 && button.ActualHeight >= 20 && new Rect(root.RenderSize).Contains(new Rect(button.TranslatePoint(new Point(), root), button.RenderSize)), $"Window controls remain visible at {scale}%");
                }
                var cpuText = (TextBlock)Get<CpuGlobalSlider>(main, "HomeCpuSlider").FindName("ValueText");
                var readout = new FormattedText(cpuText.Text, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                    new Typeface(cpuText.FontFamily, cpuText.FontStyle, cpuText.FontWeight, cpuText.FontStretch), cpuText.FontSize, Brushes.Black, 1);
                Check(cpuText.ActualWidth + 1 >= readout.Width, $"CPU percentage is fully visible at {scale}% text");
            }
            // Presentation-host QA: show only the already-populated fixture visual tree,
            // never MainWindow itself (its production Loaded handler must not run).
            Theme("graphite", 150);
            var presented = (FrameworkElement)main.Content;
            var previousContext = presented.ReadLocalValue(FrameworkElement.DataContextProperty);
            main.Content = null; presented.DataContext = main;
            var host = new Window { Content = presented, Width = 860, Height = 640, WindowStyle = WindowStyle.None,
                ShowActivated = false, ShowInTaskbar = false, Left = -10000, Top = -10000 };
            var rendered = false; host.ContentRendered += (_, _) => rendered = true;
            try
            {
                host.Show(); PumpUntil(() => rendered);
                Render(presented, 860, 640, Path.Combine(output, "home-presented-150.png"));
                var minimize = Descendants<Button>(presented).First(b => b.ToolTip?.ToString() == L.T("Minimizar"));
                var glyph = Descendants<System.Windows.Shapes.Path>(minimize).Single();
                Console.WriteLine($"[PRESENTED CHROME] position={minimize.TranslatePoint(new Point(), presented)}, glyph={glyph.RenderSize}, stroke={glyph.Stroke}, geometry={glyph.Data}");
                Check(glyph.ActualWidth >= 9 && glyph.Stroke is SolidColorBrush { Color.A: > 0 } && glyph.Data is not null,
                    "Window chrome has a drawable glyph in an isolated presentation source");
            }
            finally
            {
                host.Content = null; host.Close(); main.Content = presented;
                if (previousContext == DependencyProperty.UnsetValue) presented.ClearValue(FrameworkElement.DataContextProperty); else presented.SetValue(FrameworkElement.DataContextProperty, previousContext);
            }
            var original = L.Language; L.Language = original == "en" ? "pt-BR" : "en";
            Layout(hub, 760, 650);
            Check(Descendants<TextBlock>(hub).Any(t => t.Text == L.T("Jogos")), "New game page changes language without reconstruction");
            Check(Descendants<Button>(automation).Any(b => b.Content?.ToString() == L.T("Salvar agendamento")), "Automation actions change language immediately");
            L.Language = original;
        }
        finally { Theme("graphite", 100); hub.Dispose(); }
    }
}
