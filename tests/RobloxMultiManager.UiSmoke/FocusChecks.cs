using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using RobloxMultiManager;
using RobloxMultiManager.Core;
internal static partial class Program
{
    private static void RunFocusResourceChecks(MainWindow main, AppPaths paths, string output)
    {
        void Await(Task task) { PumpUntil(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
        Await((Task)InvokeResult(main, "InitializeGraphicsAsync")!);
        Func<GraphicsLaunchPolicy, GraphicsLaunchPolicy> add = p => p.WithAccount(main.Profiles[0].Id, new(120, 5));
        Await((Task)InvokeResult(main, "SaveGraphicsPolicyAsync", add)!);
        var quick = Get<GraphicsQuickControls>(main, "HomeGraphics");
        ((ComboBox)quick.FindName("Fps")).SelectedValue = "60"; ((ComboBox)quick.FindName("Quality")).SelectedValue = "3"; ((CheckBox)quick.FindName("Enabled")).IsChecked = true;
        Await(quick.FlushAsync());
        var data = (GraphicsLaunchPolicy)typeof(MainWindow).GetField("_graphicsLaunchPolicy", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
        Check(data.GlobalFps == 60 && data.GlobalQuality == 3 && data.Enabled && data.ForAccount(main.Profiles[0].Id) == new RobloxGraphicsPreferences(120, 5), "Visible home FPS/quality save globally without overwriting an individual override");
        Check(main.SavedAccounts[0].FpsLabel == "FPS 120" && main.SavedAccounts[1].FpsLabel == "FPS 60" && main.SavedAccounts[1].GraphicsSource == L.T("Gráficos globais"), "Each home account shows effective graphics and its global inheritance");
        var row = main.SavedAccounts[4]; var cpu = row.CpuLimitLabel;
        InvokeResult(main, "AccountGraphics_Click", new Button { DataContext = row }, new RoutedEventArgs());
        PumpUntil(() => Get<ContentControl>(main, "DrawerBody").Content is GraphicsPanel);
        var editor = (GraphicsPanel)Get<ContentControl>(main, "DrawerBody").Content;
        Check(((ComboBox)editor.FindName("Account")).SelectedIndex > 0 && ((CheckBox)editor.FindName("UseGlobal")).IsChecked == true, "The home account graphics action opens that closed account with inheritance visible");
        ((CheckBox)editor.FindName("UseGlobal")).IsChecked = false; ((ComboBox)editor.FindName("Fps")).SelectedValue = "165"; ((Slider)editor.FindName("Quality")).Value = 2;
        InvokeResult(editor, "SaveChanges"); PumpUntil(() => !editor.IsBusy);
        Check(row.FpsLabel == "FPS 165" && row.CpuLimitLabel == cpu && row.GraphicsSource == L.T("Gráficos desta conta"), "Saving account graphics updates its home row without changing CPU");
        HideDrawer(main);
        var isolated = new GraphicsQuickControls(); var saves = 0; var pending = new TaskCompletionSource();
        isolated.Synchronize(new(true, 60, 4)); isolated.SaveRequested = async (_, _, _) => { saves++; if (saves == 1) await pending.Task; };
        ((ComboBox)isolated.FindName("Fps")).SelectedValue = "120"; var first = isolated.FlushAsync();
        ((ComboBox)isolated.FindName("Fps")).SelectedValue = "240"; pending.SetResult(); Await(first);
        Check(saves == 2 && (string)((ComboBox)isolated.FindName("Fps")).SelectedValue == "240", "Rapid global graphics changes serialize and retain the newest input");
        isolated.SaveRequested = (_, _, _) => Task.FromException(new IOException("Fixture failure"));
        ((ComboBox)isolated.FindName("Fps")).SelectedValue = "30"; Await(isolated.FlushAsync());
        Check((string)((ComboBox)isolated.FindName("Fps")).SelectedValue == "240" && ((TextBlock)isolated.FindName("Status")).Text.Length > 0, "A failed graphics save restores the committed value and exposes an error");
    }
    private static void RunFocusPresentationChecks(MainWindow main, GamesHub games, AutomationPanel automation, Window host, FrameworkElement presented, string output)
    {
        void Page(string page) => Invoke(main, "ShowWorkspacePage", Enum.Parse(typeof(MainWindow).GetNestedType("WorkspacePage", BindingFlags.NonPublic)!, page));
        var appearanceType = typeof(MainWindow).Assembly.GetType("RobloxMultiManager.AppearanceService")!;
        Get<ContentControl>(main, "AutomationSettingsHost").Content = automation;
        Page("Settings"); var categories = Get<TabControl>(main, "SettingsCategories"); categories.SelectedIndex = 2;
        host.Width = 1000; host.Height = 800; Layout(presented, 1000, 800);
        var date = (DatePicker)automation.FindName("Date"); date.IsDropDownOpen = true; Layout(date, 200, 44);
        var popup = (Popup)date.Template.FindName("PART_Popup", date); var calendar = (Calendar)popup.Child;
        Layout(calendar, 330, 320); Render(calendar, 330, 320, Path.Combine(output, "calendar-dark.png"));
        Check(calendar.Background is SolidColorBrush b && b.Color.R < 100 && b.Color.G < 100 && b.Color.B < 100, "Calendar popup uses a dark background instead of white");
        var days = Descendants<CalendarDayButton>(calendar).ToArray();
        var monthHeader = Descendants<Button>(calendar).Single(b => b.Name == "PART_HeaderButton");
        Check(monthHeader.Content?.ToString()?.Contains(L.Culture.DateTimeFormat.GetMonthName(calendar.DisplayDate.Month), StringComparison.OrdinalIgnoreCase) == true, "Calendar month names follow the selected app language");
        Check(days.Length == 42 && days.All(d => d.Foreground is SolidColorBrush brush && brush.Color.A > 0), "Calendar day labels are visible in the themed native grid");
        var next = Descendants<Button>(calendar).Single(b => b.Name == "PART_NextButton"); var before = calendar.DisplayDate;
        next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(calendar.DisplayDate.Month != before.Month || calendar.DisplayDate.Year != before.Year, "Themed calendar retains functional month navigation");
        var chosen = before.AddDays(2); calendar.SelectedDate = chosen;
        Check(date.SelectedDate?.Date == chosen.Date, "Themed calendar selection still updates the schedule date"); date.IsDropDownOpen = false;
        foreach (var theme in new[] { "midnight", "forest", "light", "contrast" })
        {
            appearanceType.GetMethod("Apply")!.Invoke(null, new object[] { new AppearancePreferences(Theme: theme, TextScale: 150) });
            host.Width = 860; host.Height = 640; Layout(presented, 860, 640);
            foreach (TabItem item in categories.Items)
            {
                var border = (Border)item.Template.FindName("Root", item); var header = (FrameworkElement)border.Child;
                Check(new Rect(border.RenderSize).Contains(new Rect(header.TranslatePoint(new Point(), border), header.RenderSize)), $"Settings category header is not clipped in {theme} at150% text");
            }
        }
        appearanceType.GetMethod("Apply")!.Invoke(null, new object[] { new AppearancePreferences() });
        host.Width = 1180; host.Height = 800; Page("Games"); ((ListBox)games.FindName("Games")).SelectedIndex = 0;
        Layout(presented, 1180, 800);
        var detail = (Border)games.FindName("Inspector"); var list = (ListBox)games.FindName("Games");
        Check(detail.ActualWidth is >=700 and <=800 && ((FrameworkElement)games.FindName("BrowseSurface")).Effect is System.Windows.Media.Effects.BlurEffect && detail.DataContext is GameSummary && ((Image)games.FindName("GameImage")).GetBindingExpression(Image.SourceProperty) is not null, "Selecting a game opens a lateral overlay with the selected game's image binding");
        var game = (GameSummary)detail.DataContext;
        var photo = (Image)games.FindName("GameImage");
        detail.DataContext = game with { IconUrl = main.Profiles[0].AvatarUrl };
        PumpUntil(() => photo.Source is System.Windows.Media.Imaging.BitmapSource { PixelWidth: > 0 });
        Check(photo.Source is System.Windows.Media.Imaging.BitmapSource { PixelWidth: 96 }, "Game image binding decodes and displays the selected cover fixture");
        Check(((TextBox)games.FindName("PlaceIdValue")).Text == game.PlaceId.ToString() && ((TextBox)games.FindName("PlaceIdValue")).IsReadOnly && ((TextBox)games.FindName("UniverseIdValue")).Text == game.UniverseId.ToString(), "Game identifiers are complete, selectable values");
        var servers = (ListBox)games.FindName("Servers"); var source = (System.Collections.ObjectModel.ObservableCollection<PublicGameServer>)servers.ItemsSource;
        source.Add(new("e7e739c9-1700-49ec-8a23-aa70dc19cff5", 24, 25, 30)); Layout(presented, 1180, 800);var visibleServer=((PublicGameServer)servers.Items[0]).Id;
        Check(Descendants<TextBlock>(servers).Any(t => t.Text == visibleServer && t.TextWrapping == TextWrapping.Wrap), "Server IDs can wrap and be copied without ellipsis");
        var idText = Descendants<TextBlock>(servers).Single(t => t.Text == visibleServer);
        Check(idText.ActualHeight>=28,"Server cards reserve two readable lines for full IDs");
        Check(idText.Foreground is SolidColorBrush fg && fg.Color.R > 150, "Server ID text has explicit readable contrast on the dark theme");
        Render(presented, 1180, 800, Path.Combine(output, "game-detail.png"));
        InvokeResult(games, "CloseDetails_Click", games, new RoutedEventArgs());
        Check(list.Items.Count > 0, "Returning from game details preserves browsing results");
        Page("Settings"); categories.SelectedIndex = 2; Render(presented, 1180, 800, Path.Combine(output, "settings-automation.png"));
    }
}
