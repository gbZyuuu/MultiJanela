using System.Collections.ObjectModel;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RobloxMultiManager.Core;
namespace RobloxMultiManager;

public partial class CommunitiesPanel : System.Windows.Controls.UserControl, IDisposable
{
    private readonly CommunityService _service;
    private readonly long _user;
    private readonly ObservableCollection<CommunityRow> _groups=[];
    private readonly ObservableCollection<CommunityMember> _members=[],_requests=[];
    private CommunityDetails? _details;
    private CancellationTokenSource? _operation;
    private bool _loaded,_disposed,_membersLoaded,_requestsLoaded;
    private string? _groupCursor,_memberCursor,_requestCursor;
    private string _query="";
    public bool IsBusy=>_operation is not null;
    public bool HasDraft=>_details is {} d&&(ShoutInput.Text!=d.Shout||DescriptionInput.Text!=d.Description);
    public event EventHandler? BusyChanged;
    public CommunitiesPanel(HttpClient http,long user)
    {
        _user=user;_service=new(http,user);InitializeComponent();Groups.ItemsSource=_groups;Members.ItemsSource=_members;Requests.ItemsSource=_requests;
        Loaded+=async(_,_)=>{L.LanguageChanged-=LanguageChanged;L.LanguageChanged+=LanguageChanged;if(!_loaded){_loaded=true;await RefreshAsync();}};
        Unloaded+=(_,_)=>{_operation?.Cancel();L.LanguageChanged-=LanguageChanged;};
        SizeChanged+=(_,_)=>ListColumn.Width=new GridLength(ActualWidth<760?180:230);
    }
    private void LanguageChanged(object? sender,EventArgs e)
    {
        if(_disposed)return;
        if(_details is {} d)
        {Summary.Text=L.F("{0:N0} membros · ID {1}",d.Members,d.Id)+(d.Locked?" · "+L.T("Comunidade bloqueada"):"");Owner.Text=L.F("Proprietário: {0}",d.Owner);}
        Status.Text="";
    }
    private bool DiscardDraft()=>!HasDraft||AppDialog.Show(Window.GetWindow(this),L.T("Descartar as alterações da comunidade?"),L.T("Comunidades"),MessageBoxButton.YesNo,MessageBoxImage.Question)==MessageBoxResult.Yes;
    public Task RefreshAsync()=>!DiscardDraft()?Task.CompletedTask:WorkAsync(async token=>
    {
        var rows=await _service.MineAsync(token);_query="";_groupCursor=null;MoreGroups.Visibility=Visibility.Collapsed;
        var selected=rows.FirstOrDefault(r=>r.Id==_details?.Id)??rows.FirstOrDefault();
        _groups.Clear();foreach(var row in rows)_groups.Add(row);Groups.SelectedItem=selected;
        Status.Text=L.F("{0} comunidade(s)",rows.Length);
        if(selected is not null)await LoadDetailsAsync(selected.Id,token);
    });
    private async Task LoadDetailsAsync(long id,CancellationToken token)
    {
        var details=await _service.DetailsAsync(id,token);var roles=await _service.RolesAsync(id,token);
        if(_disposed)return;_details=details;_members.Clear();_requests.Clear();_membersLoaded=_requestsLoaded=false;_memberCursor=_requestCursor=null;
        MoreMembers.Visibility=MoreRequests.Visibility=Visibility.Collapsed;
        Title.Text=details.Name;Summary.Text=L.F("{0:N0} membros · ID {1}",details.Members,id)+(details.Locked?" · "+L.T("Comunidade bloqueada"):"");
        Shout.Text=ShoutInput.Text=details.Shout;Description.Text=DescriptionInput.Text=details.Description;Owner.Text=L.F("Proprietário: {0}",details.Owner);
        Join.Visibility=!details.Joined&&!details.Pending&&!details.Locked?Visibility.Visible:Visibility.Collapsed;
        Leave.Visibility=details.Joined&&!details.IsOwner?Visibility.Visible:Visibility.Collapsed;
        CancelRequest.Visibility=details.Pending?Visibility.Visible:Visibility.Collapsed;
        MembersTab.IsEnabled=details.CanViewMembers;RequestsTab.Visibility=details.CanManageRequests?Visibility.Visible:Visibility.Collapsed;
        ManageTab.Visibility=details.CanShout||details.IsOwner?Visibility.Visible:Visibility.Collapsed;
        ShoutForm.Visibility=details.CanShout?Visibility.Visible:Visibility.Collapsed;DescriptionForm.Visibility=details.IsOwner?Visibility.Visible:Visibility.Collapsed;
        MemberActions.Visibility=details.CanChangeRank||details.CanRemove?Visibility.Visible:Visibility.Collapsed;
        RolePicker.Visibility=ChangeRole.Visibility=details.CanChangeRank?Visibility.Visible:Visibility.Collapsed;RemoveMember.Visibility=details.CanRemove?Visibility.Visible:Visibility.Collapsed;
        Roles.ItemsSource=roles.OrderByDescending(r=>r.Rank).ToArray();RolePicker.ItemsSource=roles.Where(r=>r.Rank>0&&r.Rank<details.Rank).ToArray();
        Tabs.SelectedIndex=0;Tabs.Visibility=Visibility.Visible;UiMotion.Reveal(Tabs);
    }
    private async void Group_Changed(object sender,SelectionChangedEventArgs e)
    {
        if(IsBusy||Groups.SelectedItem is not CommunityRow row||row.Id==_details?.Id)return;
        if(!DiscardDraft()){Groups.SelectedItem=_groups.FirstOrDefault(g=>g.Id==_details?.Id);return;}
        await WorkAsync(token=>LoadDetailsAsync(row.Id,token));
    }
    private async Task FindAsync(bool more=false)
    {
        if(!more&&!DiscardDraft())return;var query=more?_query:Search.Text.Trim();if(query.Length==0){await RefreshAsync();return;}
        await WorkAsync(async token=>
        {
            if(!more&&long.TryParse(query,out var id)&&id>0){await LoadDetailsAsync(id,token);return;}
            var page=await _service.SearchAsync(query,more?_groupCursor:null,token);if(!more)_groups.Clear();
            foreach(var row in page.Items)if(!_groups.Any(g=>g.Id==row.Id))_groups.Add(row);
            _query=query;_groupCursor=page.Cursor;MoreGroups.Visibility=page.Cursor is null?Visibility.Collapsed:Visibility.Visible;Status.Text=L.F("{0} comunidade(s)",_groups.Count);
        });
    }
    private async Task LoadMembersAsync(bool more,CancellationToken token)
    {
        if(_details is not {} d)return;var page=await _service.MembersAsync(d.Id,more?_memberCursor:null,token);if(!more)_members.Clear();
        foreach(var row in page.Items)if(!_members.Any(m=>m.Id==row.Id))_members.Add(row);_memberCursor=page.Cursor;_membersLoaded=true;MoreMembers.Visibility=page.Cursor is null?Visibility.Collapsed:Visibility.Visible;
    }
    private async Task LoadRequestsAsync(bool more,CancellationToken token)
    {
        if(_details is not {} d)return;var page=await _service.RequestsAsync(d.Id,more?_requestCursor:null,token);if(!more)_requests.Clear();
        foreach(var row in page.Items)if(!_requests.Any(m=>m.Id==row.Id))_requests.Add(row);_requestCursor=page.Cursor;_requestsLoaded=true;MoreRequests.Visibility=page.Cursor is null?Visibility.Collapsed:Visibility.Visible;
    }
    private async void Tab_Changed(object sender,SelectionChangedEventArgs e)
    {
        if(!ReferenceEquals(e.OriginalSource,Tabs)||IsBusy)return;
        if(Tabs.SelectedItem==MembersTab&&!_membersLoaded)await WorkAsync(token=>LoadMembersAsync(false,token));
        else if(Tabs.SelectedItem==RequestsTab&&!_requestsLoaded)await WorkAsync(token=>LoadRequestsAsync(false,token));
    }
    private bool Confirm(string message)=>!IsBusy&&AppDialog.Show(Window.GetWindow(this),message,L.T("Comunidades"),MessageBoxButton.YesNo,MessageBoxImage.Question)==MessageBoxResult.Yes;
    private async Task MembershipAsync(bool leave)
    {if(_details is not {} d||!Confirm(L.F("Confirmar alteração da participação em {0}?",d.Name)))return;await WorkAsync(async token=>{await _service.ChangeMembershipAsync(d.Id,leave,token);await LoadDetailsAsync(d.Id,token);Status.Text=_details!.Pending?L.T("Solicitação enviada. A comunidade pode exigir aprovação."):L.T("Participação confirmada pelo Roblox.");});}
    private async Task RespondAsync(bool accept,bool own=false)
    {
        var target=own?_user:(Requests.SelectedItem as CommunityMember)?.Id;if(_details is not {} d||target is null)return;
        if(!Confirm(L.T("Confirmar esta resposta à solicitação?")))return;
        await WorkAsync(async token=>{await _service.RespondRequestAsync(d.Id,target.Value,accept,token);if(own)await LoadDetailsAsync(d.Id,token);else await LoadRequestsAsync(false,token);Status.Text=L.T("Alteração confirmada pelo Roblox.");});
    }
    private async Task EditMemberAsync(bool remove)
    {
        if(_details is not {} d||Members.SelectedItem is not CommunityMember member||!remove&&RolePicker.SelectedItem is not CommunityRole)return;
        if(!Confirm(L.F("Alterar a participação de {0} em {1}?",member.Caption,d.Name)))return;
        await WorkAsync(async token=>{await _service.ChangeMemberAsync(d.Id,member.Id,remove?null:((CommunityRole)RolePicker.SelectedItem).Id,token);await LoadMembersAsync(false,token);Status.Text=L.T("Alteração confirmada pelo Roblox.");});
    }
    private async Task SaveTextAsync(bool description)
    {
        if(_details is not {} d||!Confirm(L.T("Publicar esta alteração na comunidade?")))return;
        var text=description?DescriptionInput.Text:ShoutInput.Text;
        await WorkAsync(async token=>{await _service.SetTextAsync(d.Id,text,description,token);await LoadDetailsAsync(d.Id,token);Status.Text=L.T("Alteração confirmada pelo Roblox.");});
    }
    private async Task WorkAsync(Func<CancellationToken,Task> action)
    {
        if(_disposed||IsBusy)return;using var cancel=new CancellationTokenSource();_operation=cancel;BusyChanged?.Invoke(this,EventArgs.Empty);Status.Text=L.T("Carregando...");Groups.IsEnabled=Tabs.IsEnabled=Find.IsEnabled=Mine.IsEnabled=Refresh.IsEnabled=false;
        try{await action(cancel.Token);if(Status.Text==L.T("Carregando..."))Status.Text="";}
        catch(OperationCanceledException){}
        catch(AccountVerificationRequiredException){Status.Text=L.T("O Roblox pediu uma verificação. Conclua-a na página oficial desta conta.");}
        catch(RobloxApiException e){Status.Text=e.Message;}
        catch(InvalidOperationException e){Status.Text=e.Message;}
        catch(Exception){Status.Text=L.T("Não foi possível carregar os dados desta conta. Tente atualizar novamente.");}
        finally{_operation=null;Groups.IsEnabled=Tabs.IsEnabled=Find.IsEnabled=Mine.IsEnabled=Refresh.IsEnabled=true;BusyChanged?.Invoke(this,EventArgs.Empty);}
    }
    private async void Refresh_Click(object s,RoutedEventArgs e)=>await RefreshAsync();
    private async void Mine_Click(object s,RoutedEventArgs e)=>await RefreshAsync();
    private async void Find_Click(object s,RoutedEventArgs e)=>await FindAsync();
    private async void MoreGroups_Click(object s,RoutedEventArgs e)=>await FindAsync(true);
    private async void Search_KeyDown(object s,System.Windows.Input.KeyEventArgs e){if(e.Key==Key.Enter)await FindAsync();}
    private async void MoreMembers_Click(object s,RoutedEventArgs e)=>await WorkAsync(t=>LoadMembersAsync(true,t));
    private async void MoreRequests_Click(object s,RoutedEventArgs e)=>await WorkAsync(t=>LoadRequestsAsync(true,t));
    private async void Join_Click(object s,RoutedEventArgs e)=>await MembershipAsync(false);
    private async void Leave_Click(object s,RoutedEventArgs e)=>await MembershipAsync(true);
    private async void CancelRequest_Click(object s,RoutedEventArgs e)=>await RespondAsync(false,true);
    private async void Accept_Click(object s,RoutedEventArgs e)=>await RespondAsync(true);
    private async void Decline_Click(object s,RoutedEventArgs e)=>await RespondAsync(false);
    private async void ChangeRole_Click(object s,RoutedEventArgs e)=>await EditMemberAsync(false);
    private async void RemoveMember_Click(object s,RoutedEventArgs e)=>await EditMemberAsync(true);
    private async void SaveShout_Click(object s,RoutedEventArgs e)=>await SaveTextAsync(false);
    private async void SaveDescription_Click(object s,RoutedEventArgs e)=>await SaveTextAsync(true);
    public void Dispose(){_disposed=true;_operation?.Cancel();L.LanguageChanged-=LanguageChanged;}
}