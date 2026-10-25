using System.Collections.ObjectModel;
using System.IO;
using System.Collections.Concurrent;
using System.Security.Cryptography;
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
        private readonly WrapPanel _nexusElements=new();
        private readonly ConcurrentDictionary<string,string> _elementValues=new();
        private CheckBox? _allowNexusCode;
        private sealed record SavedScript(Guid Id,string Name,string Code){public override string ToString()=>Name;}
        private sealed record BridgeSettings(int Port=5242,string ProtectedKey="",bool AllowExecute=false);
        public ValueTask StopBridgeAsync()=>_nexus.DisposeAsync();
        private StackPanel BuildNexus()
        {
            var form=new StackPanel();form.Children.Add(Label("Bridge Nexus",21));form.Children.Add(Label("Conexões locais autenticadas. O cliente deve informar nome, ID Roblox e a chave. Nenhum código é injetado pelo gerenciador."));var port=Input(form,"Porta Nexus","5242",5);_allowNexusCode=Toggle(form,"Autorizar envio de código aos clientes conectados");
            var store=new PreferenceFile<BridgeSettings>(_owner._paths,"nexus.json",()=>new(),p=>p.Port is >=1024 and <=65535&&p.ProtectedKey.Length<32768?p:throw new InvalidDataException());
            form.Loaded+=async(_,_)=>{try{var p=await store.LoadAsync();port.Text=p.Port.ToString();_nexusKey=p.ProtectedKey.Length==0?null:LocalSecretProtection.Unprotect(p.ProtectedKey);_allowNexusCode.IsChecked=p.AllowExecute;}catch(Exception){_status.Text=L.T("Não foi possível carregar as preferências Nexus.");}};
            form.Children.Add(ActionButton("Iniciar bridge",async()=>await WorkAsync(async token=>
            {
                if(!int.TryParse(port.Text,out var number))throw new ArgumentException();_nexusKey??=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));await _nexus.DisposeAsync();
                await _nexus.StartAsync(number,_nexusKey,()=>Dispatcher.Invoke(()=>_owner.Profiles.Where(p=>p.RobloxUserId is >0&&p.RobloxUsername is not null).Select(p=>new NexusIdentity(p.Id,p.RobloxUserId!.Value,p.RobloxUsername!)).ToArray()));
                await store.SaveAsync(new(number,LocalSecretProtection.Protect(_nexusKey),_allowNexusCode.IsChecked==true));_status.Text=L.T("Bridge iniciada em 127.0.0.1. A chave é necessária para conectar.");
            })));form.Children.Add(ActionButton("Copiar chave Nexus",()=>{if(_nexusKey is not null)System.Windows.Clipboard.SetText(_nexusKey);return Task.CompletedTask;}));form.Children.Add(ActionButton("Parar bridge",()=>WorkAsync(async _=>await _nexus.DisposeAsync())));
            var command=Input(form,"Comando para os clientes selecionados","rejoin",65536);command.AcceptsReturn=true;command.Height=90;command.TextWrapping=TextWrapping.Wrap;
            form.Children.Add(ActionButton("Enviar comando Nexus",async()=>await WorkAsync(async token=>
            {
                var profiles=Selected().Select(p=>p.Id).ToArray();if(profiles.Length==0)throw new ArgumentException();if(!Confirm(L.T("Enviar comando"),L.F("Enviar o comando para {0} cliente(s) selecionado(s)?",profiles.Length)))return;
                var count=await _nexus.SendAsync(profiles,command.Text,_allowNexusCode.IsChecked==true,token);_status.Text=L.F("Enviado para {0} conexão(ões).",count);
            })));form.Children.Add(new ListBox{ItemsSource=_events,Height=140});form.Children.Add(_nexusElements);return form;
        }
        private void AddNexusElement(NexusElement element)
        {
            if(_nexusElements.Children.Count>=100||!System.Text.RegularExpressions.Regex.IsMatch(element.Name,"^[A-Za-z0-9_. -]{1,80}$"))return;
            var key=element.ProfileId+":"+element.Name;
            if(_elementValues.ContainsKey(key))return;_elementValues[key]=element.Content;
            if(element.Kind=="CreateButton")
            {
                var button=new Button{Content=element.Content,Margin=new Thickness(4),MinHeight=36};button.Click+=async(_,_)=>{if(Selected().Any(p=>p.Id==element.ProfileId))try{await _nexus.SendAsync([element.ProfileId],"ButtonClicked:"+element.Name,false,CancellationToken.None);}catch(Exception){_status.Text=L.T("O cliente não está conectado.");}};_nexusElements.Children.Add(button);
            }
            else if(element.Kind=="CreateLabel")_nexusElements.Children.Add(new TextBlock{Text=element.Content,Margin=new Thickness(4),TextWrapping=TextWrapping.Wrap});
            else
            {var box=new TextBox{Text=element.Content,MaxLength=2048,MinWidth=140,Margin=new Thickness(4)};box.TextChanged+=(_,_)=>_elementValues[key]=box.Text;_nexusElements.Children.Add(box);}
        }
        private StackPanel BuildScripts()
        {
            var form=new StackPanel();form.Children.Add(Label("Scripts JavaScript",21));form.Children.Add(Label("Ambiente isolado, sem rede, arquivos ou sessões do navegador. Tempo máximo de 30 segundos. API: ram.accounts(), ram.log(), ram.wait(), ram.arrange() e ram.nexus()."));
            var store=new PreferenceFile<SavedScript[]>(_owner._paths,"scripts.json",()=>[],data=>data.Length<=100&&data.All(x=>x.Id!=Guid.Empty&&x.Name.Length is >0 and <=80&&x.Code.Length<=65536)?data:throw new InvalidDataException());SavedScript[] saved=[];var list=new ComboBox{Margin=new Thickness(0,0,0,12)};form.Children.Add(list);var name=Input(form,"Nome do script","",80);var editor=Input(form,"Código JavaScript","const accounts = await ram.accounts();\nram.log(accounts.length);",65536);editor.Height=220;editor.AcceptsReturn=true;editor.VerticalContentAlignment=VerticalAlignment.Top;editor.FontFamily=new System.Windows.Media.FontFamily("Consolas");editor.AcceptsTab=true;editor.TextWrapping=TextWrapping.Wrap;editor.VerticalScrollBarVisibility=ScrollBarVisibility.Auto;
            list.SelectionChanged+=(_,_)=>{if(list.SelectedItem is SavedScript script){name.Text=script.Name;editor.Text=script.Code;}};
            form.Loaded+=async(_,_)=>{try{saved=await store.LoadAsync();list.ItemsSource=saved;}catch(Exception){_status.Text=L.T("Não foi possível carregar os scripts.");}};
            form.Children.Add(ActionButton("Salvar script",async()=>{if(string.IsNullOrWhiteSpace(name.Text))throw new ArgumentException();var item=new SavedScript((list.SelectedItem as SavedScript)?.Id??Guid.NewGuid(),name.Text.Trim(),editor.Text);saved=saved.Where(s=>s.Id!=item.Id).Append(item).ToArray();await store.SaveAsync(saved);list.ItemsSource=saved;list.SelectedItem=item;}));
            var actions=Toggle(form,"Autorizar ações de janelas e Nexus para este script");var output=new ObservableCollection<string>();form.Children.Add(ActionButton("Executar script",async()=>
            {
                var selected=Selected();var ids=selected.Select(p=>p.Id).ToArray();if(!Confirm(L.T("Executar script"),L.F("Executar o script com {0} conta(s) no escopo?",ids.Length)))return;output.Clear();
                await WorkAsync(token=>SandboxedScriptRuntime.RunAsync(_owner._paths,editor.Text,async(action,args,cancel)=>
                {
                    cancel.ThrowIfCancellationRequested();
                    if(action=="accounts")return selected.Select(p=>new{id=p.Id,name=p.PublicName,ready=p.IsSignedIn}).ToArray();
                    if(action=="wait"){var delay=args[0].GetInt32();if(delay is <0 or >10000)throw new ArgumentException();await Task.Delay(delay,cancel);return null;}
                    if(actions.IsChecked!=true)throw new InvalidOperationException();
                    if(action=="arrange"){var windows=_owner.Instances.Where(i=>_owner._launchOwnership.TryGetValue(i.Key,out var own)&&ids.Contains(own.ProfileId)).ToArray();var monitor=_owner._settings.SelectedMonitor;await Task.Run(()=>_owner._layout.ArrangeWithReport(windows,monitor),cancel);return true;}
                    if(action=="nexus")return await _nexus.SendAsync(ids,args[0].GetString()??"",_allowNexusCode?.IsChecked==true,cancel);
                    throw new ArgumentException();
                },text=>{if(output.Count>=100)output.RemoveAt(0);output.Add(text);},token));
            }));form.Children.Add(new ListBox{ItemsSource=output,MinHeight=100,MaxHeight=160});return form;
        }
    }
}
