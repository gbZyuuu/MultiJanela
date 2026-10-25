using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RobloxMultiManager;
using RobloxMultiManager.Core;
internal static partial class Program
{
    private static void RunExpansionPresentationChecks(MainWindow main,Window host,FrameworkElement root,string output)
    {
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        var paths=new AppPaths(Path.Combine(output,"expansion-fixture-"+Guid.NewGuid().ToString("N")));paths.EnsureCreated();
        typeof(MainWindow).GetField("_paths",flags)!.SetValue(main,paths);typeof(MainWindow).GetField("_store",flags)!.SetValue(main,new LocalStore(paths));
        var page=Enum.Parse(typeof(MainWindow).GetNestedType("WorkspacePage",BindingFlags.NonPublic)!,"Tools");Invoke(main,"ShowWorkspacePage",page);
        var panel=(FrameworkElement)Get<ContentControl>(main,"ToolsPageHost").Content;Layout(root,1180,800);var tabs=Descendants<TabControl>(panel).Single();
        Check(tabs.Items.Count==9,"All nine tool categories have real content");
        var selector=(ListBox)panel.GetType().GetField("_accounts",flags)!.GetValue(panel)!;selector.SelectedItems.Add(main.Profiles[0]);selector.SelectedItems.Add(main.Profiles[2]);Check(selector.SelectedItems.Count==2,"Whole account rows support batch multiselection");
        foreach(var size in new[]{(1180,800),(860,640)})
        {
            host.Width=size.Item1;host.Height=size.Item2;Layout(root,size.Item1,size.Item2);
            for(int n=0;n<tabs.Items.Count;n++)
            {
                tabs.SelectedIndex=n;Layout(root,size.Item1,size.Item2);
                var content=(ScrollViewer)tabs.SelectedContent;
                Check(content.ActualHeight>120 && content.ActualWidth>500,"Tool form has usable scrolling space: "+n);
                Check(Descendants<ListBox>(content).All(list => list.Background is SolidColorBrush brush && brush.Color != Colors.White || list.Background is null), "Tool lists do not use the Windows white surface: " + n);
                Check(n==4 ? Descendants<Slider>(content).Any() && Descendants<CheckBox>(content).Count()>=3 : Descendants<Button>(content).Any(b=>b.MinHeight>=36),"Tool category exposes working action controls: "+n);
                if(size.Item1==1180)Render(root,size.Item1,size.Item2,Path.Combine(output,"tool-"+n+".png"));
            }
            var settings=Get<Button>(main,"SettingsButton");var icon=Descendants<System.Windows.Shapes.Path>(settings).Single();
            Check(new Rect(settings.RenderSize).Contains(new Rect(icon.TranslatePoint(new Point(),settings),icon.RenderSize)),"Settings icon is fully inside its button at "+size.Item1);
        }
        var capture=panel.GetType().GetMethod("CaptureDraft")!;var restore=panel.GetType().GetMethod("RestoreDraft")!;var draft=(Dictionary<string,string>)capture.Invoke(panel,null)!;draft["input:8:1"]="ram.log('retained fixture');";draft["tab"]="8";restore.Invoke(panel,new object[]{draft});
        var restored=(Dictionary<string,string>)capture.Invoke(panel,null)!;Check(restored["input:8:1"]==draft["input:8:1"]&&tabs.SelectedIndex==8&&selector.SelectedItems.Count==2,"Version transfer retains script drafts, selected accounts and tool category");
        var previous=L.Language;L.Language=previous=="en"?"pt-BR":"en";Layout(root,860,640);Check(((TabItem)tabs.Items[0]).Header?.ToString()==L.T("Lotes"),"Tools translate instantly on the existing UI");L.Language=previous;
        host.Width=1180;host.Height=800;tabs.SelectedIndex=0;Render(root,1180,800,Path.Combine(output,"tools.png"));
        var dispose=(ValueTask)panel.GetType().GetMethod("DisposeAsync")!.Invoke(panel,null)!;var task=dispose.AsTask();PumpUntil(()=>task.IsCompleted);task.GetAwaiter().GetResult();
    }
}
