using SelectionMode=System.Windows.Controls.SelectionMode;
using System.IO;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using RobloxMultiManager.Core;
using Button=System.Windows.Controls.Button;
using TextBox=System.Windows.Controls.TextBox;
using ComboBox=System.Windows.Controls.ComboBox;
using ListBox=System.Windows.Controls.ListBox;
using CheckBox=System.Windows.Controls.CheckBox;
using TabControl=System.Windows.Controls.TabControl;
namespace RobloxMultiManager;
public partial class MainWindow
{
    private sealed partial class ExpansionPanel:System.Windows.Controls.UserControl,IAsyncDisposable
    {
        private readonly MainWindow _owner;
        private readonly TabControl _toolTabs=new();
        private readonly ListBox _accounts=new(){SelectionMode=SelectionMode.Multiple,DisplayMemberPath=nameof(AccountProfile.PublicName),MaxHeight=110,MinHeight=70};
        private readonly TextBlock _status=new(){TextWrapping=TextWrapping.Wrap};
        private readonly ObservableCollection<string> _events=[];
        private CancellationTokenSource? _stop;private Task _work=Task.CompletedTask;
        public bool IsBusy=>!_work.IsCompleted || _settingsSave.IsBusy;
        private readonly NexusBridge _nexus=new();
        private string? _nexusKey;
        private MacroRecording[] _recordings=[];private AvatarDefinition[] _avatars=[];
        private readonly PreferenceFile<MacroRecording[]> _macroStore;
        private readonly PreferenceFile<AvatarDefinition[]> _avatarStore;
        public ExpansionPanel(MainWindow owner)
        {
            _owner=owner;_accounts.ItemsSource=owner.Profiles;
            var tiles=new FrameworkElementFactory(typeof(SessionTilePanel));tiles.SetValue(SessionTilePanel.MinimumItemWidthProperty,180d);tiles.SetValue(SessionTilePanel.ItemHeightProperty,60d);_accounts.ItemsPanel=new ItemsPanelTemplate(tiles);_accounts.SetValue(System.Windows.Controls.ScrollViewer.CanContentScrollProperty,true);
            _accounts.SetResourceReference(StyleProperty,"CardListStyle");
            _macroStore=new(owner._paths,"macros.json",()=>[],data=>{if(data.Length>200)throw new InvalidDataException();foreach(var item in data)MacroRecording.Validate(item);return data;});
            _avatarStore=new(owner._paths,"avatars.json",()=>[],data=>{if(data.Length>200)throw new InvalidDataException();foreach(var item in data)RobloxAccountOperations.ValidateAvatar(item);return data;});
            var root=new Grid();root.RowDefinitions.Add(new(){Height=GridLength.Auto});root.RowDefinitions.Add(new(){Height=GridLength.Auto});root.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});root.RowDefinitions.Add(new(){Height=GridLength.Auto});
            var heading=new DockPanel();var stopButton=ActionButton("Parar operação",()=>{_stop?.Cancel();return Task.CompletedTask;});DockPanel.SetDock(stopButton,Dock.Right);heading.Children.Add(stopButton);heading.Children.Add(Label("Ferramentas",26));root.Children.Add(heading);
            var selection=new Expander{Margin=new Thickness(0,4,0,12),IsExpanded=false};selection.SetResourceReference(StyleProperty,"StudioExpanderStyle");var summary=new TextBlock();
            void UpdateSummary()=>summary.Text=L.F("Contas selecionadas: {0}",_accounts.SelectedItems.Count);
            _accounts.SelectionChanged+=(_,_)=>UpdateSummary();selection.Header=summary;UpdateSummary();
            EventHandler language=(_,_)=>{UpdateSummary();foreach(var combo in VisualCombos(this))combo.Items.Refresh();};
            Loaded+=(_,_)=>L.LanguageChanged+=language;Unloaded+=(_,_)=>L.LanguageChanged-=language;
            var selectionBody=new StackPanel();var choose=new WrapPanel();choose.Children.Add(ActionButton("Selecionar todas",()=>{_accounts.SelectAll();return Task.CompletedTask;}));choose.Children.Add(ActionButton("Limpar seleção",()=>{_accounts.UnselectAll();return Task.CompletedTask;}));selectionBody.Children.Add(choose);selectionBody.Children.Add(_accounts);selection.Content=selectionBody;Grid.SetRow(selection,1);root.Children.Add(selection);
            var tabs=_toolTabs;tabs.SetResourceReference(StyleProperty,"StudioTabsStyle");AddTab(tabs,"Lotes",BuildBatches());AddTab(tabs,"Avatares",BuildAvatars());AddTab(tabs,"Gravações",BuildMacros());AddTab(tabs,"Servidores",BuildDiscovery());AddTab(tabs,"Áudio",BuildAudio());AddTab(tabs,"Versão por conta",BuildVersions());AddTab(tabs,"Cofre e importação",BuildSecurity());AddTab(tabs,"Nexus",BuildNexus());AddTab(tabs,"Scripts",BuildScripts());Grid.SetRow(tabs,2);root.Children.Add(tabs);
            _status.SetResourceReference(TextBlock.ForegroundProperty,"TextMuted");_status.Margin=new Thickness(0,10,0,0);Grid.SetRow(_status,3);root.Children.Add(_status);Content=root;
            Loaded+=async(_,_)=>{try{_recordings=await _macroStore.LoadAsync();_avatars=await _avatarStore.LoadAsync();RefreshLibraries();}catch(Exception){_status.Text=L.T("Não foi possível carregar a biblioteca. Os arquivos foram preservados.");}};
            _nexus.Log+=(id,text)=>Dispatcher.BeginInvoke(()=>{if(_events.Count>=200)_events.RemoveAt(0);_events.Add(text);});
            _nexus.ReadText=(id,name)=>_elementValues.GetValueOrDefault(id+":"+name);
            _nexus.Element+=element=>Dispatcher.BeginInvoke(()=>AddNexusElement(element));
        }
        private static IEnumerable<TextBox> DraftInputs(DependencyObject root)
        {foreach(var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>()){if(child is TextBox box&&!box.IsReadOnly)yield return box;else foreach(var nested in DraftInputs(child))yield return nested;}}
        public Dictionary<string,string> CaptureDraft()
        {
            var result=new Dictionary<string,string>{{"tab",_toolTabs.SelectedIndex.ToString()},{"selected-accounts",string.Join(",",Selected().Select(p=>p.Id))}};
            for(var tab=0;tab<_toolTabs.Items.Count;tab++){var n=0;foreach(var input in DraftInputs((DependencyObject)((TabItem)_toolTabs.Items[tab]).Content))result[$"input:{tab}:{n++}"]=input.Text;}return result;
        }
        public void RestoreDraft(Dictionary<string,string>? draft)
        {
            if(draft is null)return;_accounts.UnselectAll();var selected=(draft.GetValueOrDefault("selected-accounts")??"").Split(',').ToHashSet(StringComparer.OrdinalIgnoreCase);foreach(var profile in _owner.Profiles)if(selected.Contains(profile.Id.ToString()))_accounts.SelectedItems.Add(profile);
            for(var tab=0;tab<_toolTabs.Items.Count;tab++){var n=0;foreach(var input in DraftInputs((DependencyObject)((TabItem)_toolTabs.Items[tab]).Content))if(draft.TryGetValue($"input:{tab}:{n++}",out var text)&&text.Length<=input.MaxLength)input.Text=text;}
            if(int.TryParse(draft.GetValueOrDefault("tab"),out var index)&&index>=0&&index<_toolTabs.Items.Count)_toolTabs.SelectedIndex=index;
        }
        private static IEnumerable<System.Windows.Controls.ComboBox> VisualCombos(DependencyObject root)
        {for(var n=0;n<System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);n++){var child=System.Windows.Media.VisualTreeHelper.GetChild(root,n);if(child is System.Windows.Controls.ComboBox combo)yield return combo;foreach(var nested in VisualCombos(child))yield return nested;}}
        private static void Translate(FrameworkElement control,DependencyProperty property,string key)=>control.SetBinding(property,new System.Windows.Data.Binding(nameof(TranslationSource.Language)){Source=TranslationSource.Instance,Converter=new TrExtension(key),Mode=BindingMode.OneWay});
        private static TextBlock Label(string key,double size=14){var label=new TextBlock{TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,8,8)};Translate(label,TextBlock.TextProperty,key);label.SetResourceReference(TextBlock.FontSizeProperty,"Font"+size.ToString(System.Globalization.CultureInfo.InvariantCulture));return label;}
        private Button ActionButton(string key,Func<Task> action)
        {var button=new Button{Margin=new Thickness(0,0,8,8),MinHeight=38,HorizontalAlignment=System.Windows.HorizontalAlignment.Left,Padding=new Thickness(16,0,16,0)};Translate(button,Button.ContentProperty,key);button.SetResourceReference(StyleProperty,"SecondaryButtonStyle");button.Click+=async(_,_)=>{try{await action();}catch(OperationCanceledException){_status.Text=L.T("Operação cancelada.");}catch(Exception){_status.Text=L.T("A operação não foi confirmada. Verifique os dados ou atualize o login.");}};return button;}
        private static TextBox Input(StackPanel panel,string label,string value="",int max=2048)
        {panel.Children.Add(Label(label));var box=new TextBox{Text=value,MaxLength=max,Margin=new Thickness(0,0,0,12)};panel.Children.Add(box);return box;}
        private static CheckBox Toggle(StackPanel panel,string label,bool value=false)
        {var box=new CheckBox{IsChecked=value,Margin=new Thickness(0,0,0,12)};Translate(box,CheckBox.ContentProperty,label);panel.Children.Add(box);return box;}
        private static ComboBox ProfilesChoice(StackPanel panel,IEnumerable<AccountProfile> profiles)
        {var box=new ComboBox{ItemsSource=profiles,DisplayMemberPath=nameof(AccountProfile.PublicName),SelectedIndex=0,Margin=new Thickness(0,0,0,12)};panel.Children.Add(box);return box;}
        private static void AddTab(TabControl tabs,string key,UIElement content)
        {var tab=new TabItem();Translate(tab,TabItem.HeaderProperty,key);var card=new Border{Child=content,MaxWidth=980,HorizontalAlignment=System.Windows.HorizontalAlignment.Stretch};card.SetResourceReference(StyleProperty,"StudioCardStyle");tab.Content=new ScrollViewer{Content=card,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Padding=new Thickness(0,12,12,0)};tabs.Items.Add(tab);}
        private static long JsonNumber(JsonElement value,string name)=>value.ValueKind==JsonValueKind.Object&&value.TryGetProperty(name,out var item)&&item.TryGetInt64(out var number)?number:0;
        private static string JsonString(JsonElement value,string name)=>JsonNullableString(value,name)??"";
        private static string? JsonNullableString(JsonElement value,string name)=>value.ValueKind==JsonValueKind.Object&&value.TryGetProperty(name,out var item)&&item.ValueKind==JsonValueKind.String?item.GetString():null;
        private AccountProfile[] Selected()=>_accounts.SelectedItems.Cast<AccountProfile>().ToArray();
        private async Task WorkAsync(Func<CancellationToken,Task> action)
        {
            if(IsBusy||_owner._vaultLocked||_owner._storageMaintenanceRequested)throw new InvalidOperationException();
            _stop=new();var stop=_stop;_status.Text=L.T("Executando...");try{_work=action(stop.Token);await _work;_status.Text=L.T("Operação concluída. Confira os resultados.");}
            finally{_stop=null;stop.Dispose();}
        }
        private bool Confirm(string title,string details)
        {var names=string.Join(", ",Selected().Take(20).Select(p=>p.PublicName));return AppDialog.Show(Window.GetWindow(this),details+(names.Length==0?"":"\n\n"+names),title,MessageBoxButton.YesNo,MessageBoxImage.Question)==MessageBoxResult.Yes;}
        private void RefreshLibraries(){_recordingChoice.ItemsSource=_recordings;_avatarChoice.ItemsSource=_avatars;}
        public async ValueTask DisposeAsync(){_stop?.Cancel();try{await _work;}catch(Exception){}await _nexus.DisposeAsync();}
    }
}
