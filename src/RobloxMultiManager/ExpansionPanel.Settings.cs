using System.IO;
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
        private StackPanel BuildDiscovery()
        {
            var form=new StackPanel();form.Children.Add(Label("Jogadores, subplaces e região",21));form.Children.Add(Label("As consultas usam a conta selecionada e respeitam a visibilidade permitida pelo Roblox."));
            var user=Input(form,"ID do jogador");var destination=new TextBox{IsReadOnly=true,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,12)};
            form.Children.Add(ActionButton("Localizar jogador",async()=>await WorkAsync(async token=>
            {
                if(!long.TryParse(user.Text,out var id)||id<=0||Selected().FirstOrDefault() is not {} profile)throw new ArgumentException();using var api=await _owner.OpenAccountOperationsAsync(profile.Id,token);using var result=await api.PresenceAsync(id,token);
                var current=result.RootElement.GetProperty("userPresences").EnumerateArray().FirstOrDefault();var place=JsonNumber(current,"placeId");var server=JsonString(current,"gameId");
                destination.Text=place>0&&Guid.TryParse(server,out _)?$"https://www.roblox.com/games/start?placeId={place}&gameInstanceId={server}":L.T("A localização não está disponível para esta conta.");
            })));form.Children.Add(destination);form.Children.Add(ActionButton("Usar destino encontrado",()=>{if(RobloxLinkParser.TryParse(destination.Text,out var parsed,out _))_owner.UseLibraryDestination(parsed.LauncherUrl,false);return Task.CompletedTask;}));
            var universe=Input(form,"ID do universo");var places=new ListBox{Height=140};string? cursor=null;
            async Task LoadPlaces(bool more)=>await WorkAsync(async token=>{if(!long.TryParse(universe.Text,out var id)||id<=0||Selected().FirstOrDefault() is not {} profile)throw new ArgumentException();if(!more)cursor=null;using var api=await _owner.OpenAccountOperationsAsync(profile.Id,token);using var json=await api.SubplacesAsync(id,cursor,token);var entries=json.RootElement.GetProperty("data").EnumerateArray().Select(x=>new PlaceChoice(JsonNumber(x,"id"),JsonString(x,"name"))).Where(x=>x.Id>0).ToArray();places.ItemsSource=more?places.Items.OfType<PlaceChoice>().Concat(entries).DistinctBy(x=>x.Id).ToArray():entries;cursor=JsonNullableString(json.RootElement,"nextPageCursor");});
            var placeControls=new WrapPanel();placeControls.Children.Add(ActionButton("Carregar subplaces",()=>LoadPlaces(false)));placeControls.Children.Add(ActionButton("Carregar mais",()=>cursor is null?Task.CompletedTask:LoadPlaces(true)));form.Children.Add(placeControls);form.Children.Add(places);form.Children.Add(ActionButton("Usar subplace",()=>{if(places.SelectedItem is PlaceChoice selected)_owner.UseLibraryDestination($"https://www.roblox.com/games/{selected.Id}",false);return Task.CompletedTask;}));
            var placeInput=Input(form,"PlaceId");var serverInput=Input(form,"ID do servidor");var region=Label("Região aproximada");form.Children.Add(ActionButton("Consultar região",async()=>await WorkAsync(async token=>{if(!long.TryParse(placeInput.Text,out var place)||Selected().FirstOrDefault() is not {} profile)throw new ArgumentException();using var api=await _owner.OpenAccountOperationsAsync(profile.Id,token);using var anonymous=new System.Net.Http.HttpClient(new System.Net.Http.HttpClientHandler{AllowAutoRedirect=false});var result=await RobloxDiscoveryTools.RegionAsync(api,anonymous,place,serverInput.Text.Trim(),token);region.Text=result.ToString().Length==0?L.T("Região indisponível"):result.ToString();})));form.Children.Add(region);form.Children.Add(Label("A região é aproximada e exige uma consulta ao Roblox e a um serviço de geolocalização. Somente o IP público do servidor é enviado ao serviço externo."));return form;
        }
        private sealed record PlaceChoice(long Id,string Name){public override string ToString()=>$"{Name} · {Id}";}
        private sealed record AudioChoice(Guid? Id,string Name){public override string ToString()=>Id is null?L.T("Todas as contas · global"):Name;}
        private readonly SettingsSaveQueue _settingsSave = new();
        private StackPanel BuildAudio()
        {
            var form=new StackPanel();form.Children.Add(Label("Volume por cliente",21));
            var scope=new ComboBox{ItemsSource=new[]{new AudioChoice(null,"")}.Concat(_owner.Profiles.Select(p=>new AudioChoice(p.Id,p.PublicName))).ToArray(),SelectedIndex=0,Margin=new Thickness(0,0,0,12)};form.Children.Add(scope);
            var enabled=Toggle(form,"Ativar controle de volume",_owner._audioPreferences.Enabled);
            var mute=Toggle(form,"Silenciar janelas em segundo plano",_owner._audioPreferences.MuteBackground);
            var inherit=Toggle(form,"Usar volume global",true);
            var slider=new Slider{Minimum=0,Maximum=100,TickFrequency=1,IsSnapToTickEnabled=true,Margin=new Thickness(0,10,0,12)};form.Children.Add(slider);
            var value=Label("Volume");form.Children.Add(value);var status=Label("Alterações salvas automaticamente.");form.Children.Add(status);
            bool populating=false;
            void Populate()
            {
                if(scope.SelectedItem is not AudioChoice selected)return;
                populating=true;
                var own=_owner._audioPreferences.Accounts?.FirstOrDefault(a=>a.ProfileId==selected.Id);
                enabled.Visibility=mute.Visibility=selected.Id is null?Visibility.Visible:Visibility.Collapsed;
                inherit.Visibility=selected.Id is null?Visibility.Collapsed:Visibility.Visible;inherit.IsChecked=own is null;
                slider.Value=_owner._audioPreferences.ForAccount(selected.Id);slider.IsEnabled=selected.Id is null||own is not null;populating=false;
            }
            void Save()
            {
                if(populating||scope.SelectedItem is not AudioChoice selected)return;
                var volume=(int)slider.Value;var useGlobal=inherit.IsChecked==true;var active=enabled.IsChecked==true;var background=mute.IsChecked==true;
                _settingsSave.Enqueue("audio-"+selected.Id,async()=>
                {
                    try
                    {
                        var next=_owner._audioPreferences;
                        if(selected.Id is {} id)next=next with{Accounts=(next.Accounts??[]).Where(a=>a.ProfileId!=id).Concat(useGlobal?[]:new[]{new AccountVolume(id,volume)}).ToArray()};
                        else next=next with{Enabled=active,GlobalVolume=volume,MuteBackground=background};
                        await _owner.SaveAudioAsync(next);status.Text=L.T("Preferências salvas.");
                    }
                    catch(Exception){status.Text=L.T("Não foi possível salvar as preferências. Verifique o acesso à pasta local.");}
                });
            }
            slider.ValueChanged+=(_,_)=>value.Text=L.F("Volume: {0}%",(int)slider.Value);
            scope.SelectionChanged+=(_,_)=>Populate();
            inherit.Checked+=(_,_)=>{if(!populating&&scope.SelectedItem is AudioChoice{Id:not null}){populating=true;slider.Value=_owner._audioPreferences.GlobalVolume;populating=false;slider.IsEnabled=false;}};
            inherit.Unchecked+=(_,_)=>{if(!populating)slider.IsEnabled=true;};
            Populate();SettingsSaveQueue.Watch(form,Save,enabled,mute,inherit,slider);
            form.Children.Add(Label("Somente processos abertos e identificados pelo gerenciador recebem alterações. Desativar restaura os volumes anteriores quando eles não foram alterados por outro programa."));return form;
        }
        private StackPanel BuildVersions()
        {
            var form=new StackPanel();form.Children.Add(Label("Versão Roblox por conta",21));var profile=ProfilesChoice(form,_owner.Profiles);
            var versions=new ComboBox{Margin=new Thickness(0,0,0,12),DisplayMemberPath="Caption"};form.Children.Add(versions);
            var status=Label("Alterações salvas automaticamente.");bool populating=false;
            void SelectVersion(){populating=true;versions.SelectedItem=profile.SelectedItem is AccountProfile p?versions.Items.OfType<Choice<string?>>().FirstOrDefault(v=>v.Value==p.RobloxVersionId):null;populating=false;}
            async Task LoadVersions(){var installed=await Task.Run(()=>_owner._installationLocator.DiscoverInstalled());populating=true;versions.ItemsSource=new[]{new Choice<string?>(null,"Usar versão global")}.Concat(installed.Where(v=>v.AppearsSigned).Select(v=>new Choice<string?>(v.VersionId,v.DisplayName))).ToArray();SelectVersion();}
            form.Children.Add(ActionButton("Atualizar versões instaladas",LoadVersions));
            var loaded=false;form.Loaded+=async(_,_)=>{if(loaded)return;loaded=true;try{await LoadVersions();}catch(Exception){status.Text=L.T("Não foi possível carregar as versões instaladas.");}};
            profile.SelectionChanged+=(_,_)=>SelectVersion();
            versions.SelectionChanged+=(_,_)=>
            {
                if(populating||!form.IsLoaded||profile.SelectedItem is not AccountProfile p||versions.SelectedItem is not Choice<string?> version)return;
                _settingsSave.Enqueue("version-"+p.Id,async()=>{try{var current=_owner.Profiles.FirstOrDefault(x=>x.Id==p.Id);if(current is not null)await _owner.SetProfileVersionAsync(current,version.Value);status.Text=L.T("Preferências salvas.");}catch(Exception){status.Text=L.T("Não foi possível salvar as preferências. Verifique o acesso à pasta local.");}});
            };
            form.Children.Add(status);form.Children.Add(Label("O catálogo global continua disponível no botão Versões. A escolha individual é preservada para novas aberturas e reconexões; uma versão antiga pode ser recusada pelo Roblox."));return form;
        }
        private StackPanel BuildSecurity()
        {
            var form=new StackPanel();form.Children.Add(Label("Cofre e importação",21));form.Children.Add(Label("O cofre criptografa os arquivos JSON do gerenciador. Perfis de navegador continuam usando a proteção do Windows; logs não fazem parte do cofre."));form.Children.Add(Label("Nova senha do cofre"));var password=new PasswordBox{Margin=new Thickness(0,0,0,12),Padding=new Thickness(10)};password.SetResourceReference(BackgroundProperty,"SurfaceInset");password.SetResourceReference(ForegroundProperty,"TextPrimary");form.Children.Add(password);var idle=Input(form,"Bloquear após inatividade (min)","15",3);
            form.Children.Add(ActionButton("Ativar ou atualizar cofre",async()=>
            {
                if(!int.TryParse(idle.Text,out var minutes)||minutes is <1 or >240)throw new ArgumentException();var secret=password.Password;
                if(!Confirm(L.T("Configurar cofre"),L.T("Guarde sua senha. Os dados criptografados não podem ser recuperados sem ela. Continuar?")))return;
                await _owner.HomeGraphics.FlushAsync();await _owner.SaveProfilesAsync(true);await _owner.SaveSettingsAsync(true);var vault=new AccountVault(_owner._paths.Root);await WorkAsync(token=>_owner.MaintainStorageAsync(async()=>{if(vault.Exists)await Task.Run(()=>vault.ChangePasswordAsync(secret,minutes,token),token);else await Task.Run(()=>vault.CreateAsync(secret,minutes,token),token);},false));_owner._vaultIdleMinutes=minutes;password.Clear();
            }));form.Children.Add(ActionButton("Bloquear agora",()=>_owner.LockVaultAsync()));
            AccountImportEntry[] imported=[];var preview=new ListBox{Height=140};form.Children.Add(ActionButton("Escolher arquivo de contas",async()=>{var picker=new Microsoft.Win32.OpenFileDialog{Filter="JSON|*.json"};if(picker.ShowDialog(Window.GetWindow(this))!=true)return;if(new FileInfo(picker.FileName).Length>8*1024*1024)throw new InvalidDataException();imported=AccountImportPreview.Parse(await File.ReadAllTextAsync(picker.FileName));preview.ItemsSource=imported.Select(x=>x.Alias).ToArray();}));form.Children.Add(preview);form.Children.Add(ActionButton("Importar contas da prévia",async()=>{if(imported.Length==0)return;if(!Confirm(L.T("Importar contas"),L.F("Importar {0} conta(s)? Sessões serão verificadas; senhas não serão importadas.",imported.Length)))return;await WorkAsync(token=>_owner.ImportAccountsAsync(imported,token));imported=[];preview.ItemsSource=null;}));form.Children.Add(Label("Aceita listas JSON de perfis e AccountData.json em texto. Bancos criptografados de outros aplicativos exigem exportação pelo aplicativo de origem."));return form;
        }
    }
}
