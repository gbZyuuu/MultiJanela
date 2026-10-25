using SelectionMode=System.Windows.Controls.SelectionMode;
using System.IO;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using RobloxMultiManager.Core;
using Button=System.Windows.Controls.Button;
using TextBox=System.Windows.Controls.TextBox;
using ComboBox=System.Windows.Controls.ComboBox;
using ListBox=System.Windows.Controls.ListBox;
using CheckBox=System.Windows.Controls.CheckBox;
namespace RobloxMultiManager;
public partial class MainWindow
{
    private sealed partial class ExpansionPanel
    {
        private sealed record Choice<T>(T Value,string Key){public string Caption=>L.T(Key);public override string ToString()=>Caption;}
        private readonly ComboBox _avatarChoice=new(){DisplayMemberPath=nameof(AvatarDefinition.Name),Margin=new Thickness(0,0,0,12)};
        private readonly ComboBox _recordingChoice=new(){DisplayMemberPath=nameof(MacroRecording.Name),Margin=new Thickness(0,0,0,12)};
        private StackPanel BuildBatches()
        {
            var form=new StackPanel();form.Children.Add(Label("Amizades e comunidades em lote",21));form.Children.Add(Label("Selecione as contas acima, escolha a ação e confira a prévia antes de executar."));
            var actions=new ComboBox{ItemsSource=new[]{new Choice<AccountBatchAction>(AccountBatchAction.RequestFriendship,"Enviar solicitação de amizade"),new Choice<AccountBatchAction>(AccountBatchAction.AcceptFriendship,"Aceitar solicitação de amizade"),new Choice<AccountBatchAction>(AccountBatchAction.JoinCommunity,"Entrar na comunidade"),new Choice<AccountBatchAction>(AccountBatchAction.LeaveCommunity,"Sair da comunidade")},SelectedIndex=0,Margin=new Thickness(0,0,0,12)};form.Children.Add(actions);
            var target=Input(form,"ID do usuário ou da comunidade");var results=new ObservableCollection<string>();var list=new ListBox{ItemsSource=results,MinHeight=120,MaxHeight=240};form.Children.Add(ActionButton("Conferir e executar lote",async()=>
            {
                var profiles=Selected();if(profiles.Length==0||!long.TryParse(target.Text,out var id)||id<=0)throw new ArgumentException();var action=((Choice<AccountBatchAction>)actions.SelectedItem).Value;
                if(!Confirm(L.T("Confirmar lote"),L.F("Executar {0} em {1} conta(s), para o ID {2}?",((Choice<AccountBatchAction>)actions.SelectedItem).Caption,profiles.Length,id)))return;
                results.Clear();await WorkAsync(token=>new AccountBatchRunner().RunAsync(new(action,profiles.Select(p=>p.Id).ToArray(),id),_owner.OpenAccountOperationsAsync,result=>ReportBatch(results,profiles,result),token));
            }));form.Children.Add(list);form.Children.Add(Label("Verificações são concluídas nas páginas oficiais. Um pedido enviado não significa amizade aceita ou ingresso aprovado."));return form;
        }
        private static void ReportBatch(ObservableCollection<string> results,AccountProfile[] profiles,AccountOperationResult row)
        {var index=Array.FindIndex(profiles,p=>p.Id==row.ProfileId);if(index<0)return;while(results.Count<profiles.Length)results.Add("");results[index]=L.F("{0}: {1}",profiles[index].PublicName,OperationText(row.State));}
        private static string OperationText(AccountOperationState state)=>state switch
        {
            AccountOperationState.Pending=>L.T("Na fila"),AccountOperationState.Running=>L.T("Em execução"),AccountOperationState.Completed=>L.T("Pedido confirmado pelo Roblox"),AccountOperationState.PendingApproval=>L.T("Aguardando aprovação"),AccountOperationState.VerificationRequired=>L.T("Verificação necessária"),AccountOperationState.Cancelled=>L.T("Cancelado"),_=>L.T("Não confirmado")
        };
        private StackPanel BuildAvatars()
        {
            var form=new StackPanel();form.Children.Add(Label("Monte e distribua um visual",21));var search=Input(form,"Buscar itens oficiais gratuitos");var catalog=new ListBox{SelectionMode=SelectionMode.Multiple,DisplayMemberPath=nameof(CatalogAsset.Name),Height=180};string? cursor=null;
            async Task Search(bool more)
            {
                await WorkAsync(async token=>
                {
                    if(!more)cursor=null;using var api=new RobloxAccountOperations(new System.Net.Http.HttpClient(new System.Net.Http.HttpClientHandler{AllowAutoRedirect=false}),1);
                    var url="https://catalog.roblox.com/v2/search/items/details?Category=1&CreatorName=Roblox&MinPrice=0&MaxPrice=0&Limit=30&Keyword="+Uri.EscapeDataString(search.Text.Trim())+(cursor is null?"":"&Cursor="+Uri.EscapeDataString(cursor));
                    using var json=await api.SendAsync(System.Net.Http.HttpMethod.Get,url,null,token);var rows=RobloxDiscoveryTools.ReadFreeCatalog(json.RootElement);catalog.ItemsSource=(more?catalog.Items.OfType<CatalogAsset>().Concat(rows):rows).DistinctBy(x=>x.Id).ToArray();cursor=JsonNullableString(json.RootElement,"nextPageCursor");
                });
            }
            var controls=new WrapPanel();controls.Children.Add(ActionButton("Buscar catálogo",()=>Search(false)));controls.Children.Add(ActionButton("Carregar mais",()=>cursor is null?Task.CompletedTask:Search(true)));form.Children.Add(controls);form.Children.Add(catalog);
            var name=Input(form,"Nome do visual","",80);form.Children.Add(ActionButton("Salvar visual selecionado",async()=>{var item=RobloxAccountOperations.ValidateAvatar(new(Guid.NewGuid(),name.Text.Trim(),catalog.SelectedItems.Cast<CatalogAsset>().Select(x=>x.Id).ToArray()));_avatars=_avatars.Append(item).ToArray();await _avatarStore.SaveAsync(_avatars);RefreshLibraries();_avatarChoice.SelectedItem=item;}));
            var inventory=new ListBox{SelectionMode=SelectionMode.Multiple,Height=160,DisplayMemberPath="Name"};string? inventoryCursor=null;Guid? inventoryOwner=null;
            async Task LoadInventory(bool more)=>await WorkAsync(async token=>
            {
                var account=Selected().FirstOrDefault()??throw new ArgumentException();if(!more||inventoryOwner!=account.Id){inventoryCursor=null;inventory.ItemsSource=null;inventoryOwner=account.Id;}
                using var api=await _owner.OpenAccountOperationsAsync(account.Id,token);using var json=await api.InventoryAsync(inventoryCursor,token);
                var entries=json.RootElement.GetProperty("data").EnumerateArray().Select(x=>new CatalogAsset(JsonNumber(x,"assetId"),JsonString(x,"name"),0,0,0)).Where(x=>x.Id>0).ToArray();
                inventory.ItemsSource=(more?inventory.Items.OfType<CatalogAsset>().Concat(entries):entries).DistinctBy(x=>x.Id).ToArray();inventoryCursor=JsonNullableString(json.RootElement,"nextPageCursor");
            });
            var inventoryControls=new WrapPanel();inventoryControls.Children.Add(ActionButton("Carregar inventário da primeira conta selecionada",()=>LoadInventory(false)));inventoryControls.Children.Add(ActionButton("Mais itens do inventário",()=>inventoryCursor is null?Task.CompletedTask:LoadInventory(true)));form.Children.Add(inventoryControls);form.Children.Add(inventory);
            form.Children.Add(ActionButton("Salvar visual do inventário",async()=>{var item=RobloxAccountOperations.ValidateAvatar(new(Guid.NewGuid(),name.Text.Trim(),inventory.SelectedItems.Cast<CatalogAsset>().Select(x=>x.Id).ToArray()));_avatars=_avatars.Append(item).ToArray();await _avatarStore.SaveAsync(_avatars);RefreshLibraries();_avatarChoice.SelectedItem=item;}));
            form.Children.Add(ActionButton("Salvar avatar atual da primeira conta selecionada",async()=>await WorkAsync(async token=>{var account=Selected().FirstOrDefault()??throw new ArgumentException();using var api=await _owner.OpenAccountOperationsAsync(account.Id,token);var item=await api.CurrentAvatarAsync(name.Text.Trim(),token);_avatars=_avatars.Append(item).ToArray();await _avatarStore.SaveAsync(_avatars);RefreshLibraries();_avatarChoice.SelectedItem=item;})));
            form.Children.Add(Label("Visuais salvos"));form.Children.Add(_avatarChoice);var results=new ObservableCollection<string>();
            form.Children.Add(ActionButton("Distribuir para as contas selecionadas",async()=>
            {
                var accounts=Selected();if(accounts.Length==0||_avatarChoice.SelectedItem is not AvatarDefinition avatar)throw new ArgumentException();if(!Confirm(L.T("Distribuir visual"),L.F("Aplicar {0}, com {1} item(ns), em {2} conta(s)? Somente itens oficiais gratuitos poderão ser obtidos.",avatar.Name,avatar.AssetIds.Length,accounts.Length)))return;
                results.Clear();await WorkAsync(token=>new AccountBatchRunner().RunAsync(new(AccountBatchAction.ApplyAvatar,accounts.Select(p=>p.Id).ToArray(),Avatar:avatar),_owner.OpenAccountOperationsAsync,row=>ReportBatch(results,accounts,row),token));
            }));form.Children.Add(new ListBox{ItemsSource=results,MinHeight=100,MaxHeight=200});return form;
        }
        private sealed record SessionChoice(RobloxInstanceInfo Session){public override string ToString()=>L.F("{0} · PID {1}",Session.ProfileText,Session.ProcessId);}
        private StackPanel BuildMacros()
        {
            var form=new StackPanel();form.Children.Add(Label("Gravações e TinyTask",21));form.Children.Add(Label("A reprodução usa uma janela por vez. Mudar o foco interrompe a entrada; Parar solta as teclas pressionadas."));
            var reference=new ComboBox{Margin=new Thickness(0,0,0,12)};void Refresh()=>reference.ItemsSource=_owner.Instances.Where(i=>_owner._launchOwnership.ContainsKey(i.Key)&&i.HasWindow).Select(i=>new SessionChoice(i)).ToArray();reference.DropDownOpened+=(_,_)=>Refresh();Refresh();form.Children.Add(Label("Janela de referência do TinyTask"));form.Children.Add(reference);
            var editor=Input(form,"Editor da gravação",JsonSerializer.Serialize(new MacroRecording(Guid.NewGuid(),L.T("Minha gravação"),[new("keyDown",87),new("wait",Milliseconds:100),new("keyUp",87)]),new JsonSerializerOptions{WriteIndented=true}),1024*1024);editor.AcceptsReturn=true;editor.VerticalContentAlignment=VerticalAlignment.Top;editor.FontFamily=new System.Windows.Media.FontFamily("Consolas");editor.TextWrapping=TextWrapping.Wrap;editor.Height=200;editor.VerticalScrollBarVisibility=ScrollBarVisibility.Auto;
            form.Children.Add(ActionButton("Importar TinyTask",async()=>
            {
                if(reference.SelectedItem is not SessionChoice selected)throw new ArgumentException();var picker=new Microsoft.Win32.OpenFileDialog{Filter="TinyTask|*.rec"};if(picker.ShowDialog(Window.GetWindow(this))!=true)return;
                var file=new FileInfo(picker.FileName);if(file.Length>8*1024*1024)throw new InvalidDataException();var area=new NativeMacroTarget(selected.Session).Bounds();var macro=TinyTaskImporter.Import(await File.ReadAllBytesAsync(file.FullName),area,Path.GetFileNameWithoutExtension(file.Name));editor.Text=JsonSerializer.Serialize(macro,new JsonSerializerOptions{WriteIndented=true});
            }));form.Children.Add(ActionButton("Salvar gravação",async()=>{var macro=MacroRecording.Validate(JsonSerializer.Deserialize<MacroRecording>(editor.Text,new JsonSerializerOptions{PropertyNameCaseInsensitive=true})??throw new InvalidDataException());_recordings=_recordings.Where(x=>x.Id!=macro.Id).Append(macro).ToArray();await _macroStore.SaveAsync(_recordings);RefreshLibraries();_recordingChoice.SelectedItem=macro;}));form.Children.Add(_recordingChoice);
            _recordingChoice.SelectionChanged+=(_,_)=>{if(_recordingChoice.SelectedItem is MacroRecording item)editor.Text=JsonSerializer.Serialize(item,new JsonSerializerOptions{WriteIndented=true});};
            form.Children.Add(ActionButton("Reproduzir nas contas selecionadas",async()=>
            {
                var ids=Selected().Select(p=>p.Id).ToHashSet();var sessions=_owner.Instances.Where(i=>_owner._launchOwnership.TryGetValue(i.Key,out var own)&&ids.Contains(own.ProfileId)&&i.HasWindow).ToArray();if(sessions.Length==0)throw new ArgumentException();var macro=MacroRecording.Validate(JsonSerializer.Deserialize<MacroRecording>(editor.Text,new JsonSerializerOptions{PropertyNameCaseInsensitive=true})??throw new InvalidDataException());if(!Confirm(L.T("Reproduzir gravação"),L.F("Reproduzir {0} em {1} janela(s)? O foco e o mouse serão utilizados.",macro.Name,sessions.Length)))return;await WorkAsync(async token=>{foreach(var session in sessions)await MacroPlayback.PlayAsync(macro,new NativeMacroTarget(session),token);});
            }));return form;
        }
    }
}
