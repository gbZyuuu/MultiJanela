using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using RobloxMultiManager.Core;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using TextBlock = System.Windows.Controls.TextBlock;
using StackPanel = System.Windows.Controls.StackPanel;
using Slider = System.Windows.Controls.Slider;

namespace RobloxMultiManager;

public partial class AvatarEditorPanel
{
    private sealed record AvatarCategory(int Type, string Key) { public string Name => L.T(Key); }
    private AvatarBody? _body;
    private readonly Dictionary<string, TextBox> _colors = [];
    private readonly Dictionary<string, Slider> _scales = [];
    private bool _buildingBody, _bodyDirty;
    private CancellationTokenSource? _previewCancellation;
    private DateTime _previewRetryAt, _lastRender;
    private readonly Dictionary<string, Model3D> _previewCache = [];
    private void InitializeStudio()
    {
        Category.ItemsSource = new AvatarCategory[] { new(0,"Todos"),new(41,"Cabelos"),new(8,"Chapéus"),new(11,"Camisas clássicas"),new(2,"Camisetas clássicas"),new(12,"Calças clássicas"),new(65,"Camisas"),new(64,"Camisetas"),new(66,"Calças"),new(67,"Jaquetas"),new(68,"Suéteres"),new(69,"Shorts"),new(72,"Vestidos e saias"),new(42,"Acessórios de rosto"),new(43,"Acessórios de pescoço"),new(44,"Acessórios de ombro"),new(45,"Acessórios frontais"),new(46,"Acessórios das costas"),new(47,"Acessórios de cintura"),new(17,"Cabeças"),new(79,"Cabeças dinâmicas"),new(18,"Rostos"),new(27,"Torsos"),new(28,"Braços direitos"),new(29,"Braços esquerdos"),new(30,"Pernas esquerdas"),new(31,"Pernas direitas") };
        Category.SelectedIndex = 0;
        SizeChanged += (_, _) => PreviewColumn.Width = new GridLength(Math.Clamp(ActualWidth * .30, 190, 310));
        L.LanguageChanged += StudioLanguageChanged;
        Unloaded += (_, _) => L.LanguageChanged -= StudioLanguageChanged;
        Loaded += (_, _) => { L.LanguageChanged -= StudioLanguageChanged; L.LanguageChanged += StudioLanguageChanged; };
    }
    private void StudioLanguageChanged(object? sender, EventArgs e)
    {
        Category.Items.Refresh();
        foreach (var panel in ColorsForm.Children.OfType<StackPanel>()) if (panel.Children[0] is TextBlock { Tag: string key } label) label.Text = L.T(key);
        foreach (var label in ScalesForm.Children.OfType<TextBlock>()) if (label.Tag is string key) label.Text = L.T(key);
    }
    private async void Category_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!_loaded || IsBusy || Category.SelectedItem is not AvatarCategory category) return;
        await WorkAsync(async token => { var page = await _service.GetAvatarInventoryAsync(token: token, assetType: category.Type); if (_disposed) return; _inventory.Clear(); foreach(var item in page.Items)_inventory.Add(item); _cursor=page.Cursor; Status.Text=""; });
    }
    private async Task LoadBodyAsync(CancellationToken token)
    {
        try
        {
            var bodyTask = _service.GetAvatarBodyAsync(token); var rulesTask = _service.GetAvatarRulesAsync(token); await Task.WhenAll(bodyTask, rulesTask); var body = await bodyTask; var rules = await rulesTask;
            if (_disposed) return; _buildingBody=true; _body=body; _colors.Clear(); _scales.Clear(); ColorsForm.Children.Clear(); ScalesForm.Children.Clear(); RigType.ItemsSource=new[]{"R6","R15"}; RigType.SelectedIndex=body.Type==1?0:1;
            var names=new Dictionary<string,string>{{"headColor","Cabeça"},{"torsoColor","Torso"},{"leftArmColor","Braço esquerdo"},{"rightArmColor","Braço direito"},{"leftLegColor","Perna esquerda"},{"rightLegColor","Perna direita"}};
            foreach(var item in body.Colors)
            {
                var panel=new StackPanel{Margin=new Thickness(0,0,12,12)};panel.Children.Add(new TextBlock{Tag=names.GetValueOrDefault(item.Key)??item.Key,Text=L.T(names.GetValueOrDefault(item.Key)??item.Key),Margin=new Thickness(0,0,0,5)});
                var box=new TextBox{Text=item.Value,MaxLength=7}; _colors[item.Key]=box;panel.Children.Add(box);
                var swatches=new System.Windows.Controls.WrapPanel{Margin=new Thickness(0,6,0,0)};
                foreach(var color in new[] { "#000000", "#FFFFFF" }.Concat(rules.Colors).Distinct(StringComparer.OrdinalIgnoreCase))
                {var swatch=new Button{Width=20,Height=20,MinWidth=0,MinHeight=0,Padding=new Thickness(0),Margin=new Thickness(0,0,3,3),Background=(SolidColorBrush)new BrushConverter().ConvertFromString(color)!,ToolTip=color};swatch.Click+=(_,_)=>box.Text=color;swatches.Children.Add(swatch);}
                panel.Children.Add(swatches);box.TextChanged+=(_,_)=>BodyChanged();ColorsForm.Children.Add(panel);
            }
            var scaleNames=new Dictionary<string,string>{{"height","Altura"},{"width","Largura"},{"head","Cabeça"},{"depth","Profundidade"},{"proportion","Proporções"},{"bodyType","Tipo do corpo"}};
            foreach(var item in rules.Scales)
            {
                if(!body.Scales.TryGetValue(item.Key,out var value))continue;
                var label=new TextBlock{Tag=scaleNames.GetValueOrDefault(item.Key)??item.Key,Text=L.T(scaleNames.GetValueOrDefault(item.Key)??item.Key)};ScalesForm.Children.Add(label);
                var slider=new Slider{Minimum=item.Value.Min,Maximum=item.Value.Max,Value=value,TickFrequency=item.Value.Increment>0?item.Value.Increment:.01,IsSnapToTickEnabled=true,Margin=new Thickness(0,8,12,14)};
                var key=item.Key;slider.ValueChanged+=(_,_)=>{label.Text=L.T(scaleNames.GetValueOrDefault(key)??key)+" · "+slider.Value.ToString("0.00");BodyChanged();};_scales[key]=slider;ScalesForm.Children.Add(slider);
            }
            _bodyDirty=false; ApplyBody.IsEnabled=false;
        }
        catch(OperationCanceledException) when(token.IsCancellationRequested){throw;}
        catch(Exception){_body=null;PreviewStatus.Text=L.T("Não foi possível carregar a prévia e o corpo. Seus itens continuam disponíveis.");}
        finally{_buildingBody=false;}
    }
    private AvatarBody? DraftBody()
    {
        if(_body is null||_colors.Any(p=>!System.Text.RegularExpressions.Regex.IsMatch(p.Value.Text,"^#[a-fA-F0-9]{6}$")))return null;
        var scales=new Dictionary<string,double>(_body.Scales);foreach(var pair in _scales)scales[pair.Key]=pair.Value.Value;
        return new(_colors.ToDictionary(p=>p.Key,p=>p.Value.Text.ToUpperInvariant()),scales,RigType.SelectedIndex==0?1:3);
    }
    private void Rig_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => BodyChanged();
    private string PreviewCaption => _dirty || _bodyDirty ? L.T("Prévia · alterações ainda não aplicadas") : L.T("Prévia do avatar atual");
    private void BodyChanged(){if(_buildingBody)return;_bodyDirty=true;ApplyBody.IsEnabled=!IsBusy&&DraftBody() is not null;QueuePreview();}
    private void SelectEditorTab(int index)
    {
        Equip.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        ItemsArea.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        WornArea.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        BodyArea.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
        NavigationState.SetActive(ItemsTab, index == 0); NavigationState.SetActive(WornTab, index == 1); NavigationState.SetActive(BodyTab, index == 2);
    }
    private void Items_Click(object sender,RoutedEventArgs e) => SelectEditorTab(0);
    private void Worn_Click(object sender,RoutedEventArgs e) => SelectEditorTab(1);
    private void Body_Click(object sender,RoutedEventArgs e) => SelectEditorTab(2);
    private async void ApplyBody_Click(object sender,RoutedEventArgs e)
    {
        if(IsBusy||!_bodyDirty||DraftBody() is not {} body)return;
        if(AppDialog.Show(Window.GetWindow(this),L.T("Aplicar as cores e proporções nesta conta?"),L.T("Corpo e cores"),MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)return;
        await WorkAsync(async token=>{await _service.ApplyBodyAsync(body,token);if(_disposed)return;_body=body;_bodyDirty=false;ApplyBody.IsEnabled=false;PreviewStatus.Text=PreviewCaption;Status.Text=L.T("Corpo e cores confirmados pelo Roblox.");AvatarChanged?.Invoke(this,EventArgs.Empty);});
    }
    private void ShowPreviewFallback() { Preview3D.Visibility=Visibility.Collapsed;Portrait.Visibility=Visibility.Visible;PreviewHint.Visibility=Visibility.Collapsed; }
    private void Preview_Click(object sender,RoutedEventArgs e)=>QueuePreview();
    private async void QueuePreview()
    {
        _previewCancellation?.Cancel();
        if(_disposed)return;
        if(DraftBody() is not {} body){PreviewStatus.Text=L.T("Confira as cores do corpo para gerar a prévia.");return;}
        PreviewStatus.Text=L.T("Carregando prévia do corpo inteiro...");
        if(DateTime.UtcNow<_previewRetryAt){PreviewStatus.Text=L.T("Prévia em espera. Tente novamente em alguns instantes.");return;}
        var ids=_equipped.Select(i=>i.Id).ToArray();
        var key=string.Join(',',ids.Order())+System.Text.Json.JsonSerializer.Serialize(body);
        if(_previewCache.TryGetValue(key,out var cached)){Preview3D.Visibility=Visibility.Visible;Preview3D.Show(cached);Portrait.Visibility=Visibility.Collapsed;PreviewHint.Visibility=Visibility.Visible;PreviewStatus.Text=PreviewCaption;return;}
        using var source=new CancellationTokenSource();_previewCancellation=source;
        try
        {
            if (_dirty || _bodyDirty) await Task.Delay(450,source.Token);if(!IsVisible)return;
            PreviewStatus.Text=L.T("Gerando prévia 3D...");
            for(var attempt=0;attempt<3;attempt++)
            {
                var remaining=TimeSpan.FromSeconds(3)-(DateTime.UtcNow-_lastRender); if(remaining>TimeSpan.Zero)await Task.Delay(remaining,source.Token);
                _lastRender=DateTime.UtcNow; var render = !_dirty && !_bodyDirty
                    ? await _service.GetCurrentAvatarRenderAsync(source.Token)
                    : await _service.RenderAvatarAsync(ids,body,source.Token);
                if(render.State=="Completed"&&render.Url is not null)
                {var model=await AvatarViewport.LoadAsync(render.Url,source.Token);source.Token.ThrowIfCancellationRequested();if(_disposed)return;if(_previewCache.Count>=4)_previewCache.Remove(_previewCache.Keys.First());_previewCache[key]=model;Preview3D.Visibility=Visibility.Visible;Preview3D.Show(model);Portrait.Visibility=Visibility.Collapsed;PreviewHint.Visibility=Visibility.Visible;PreviewStatus.Text=PreviewCaption;return;}
                if(render.State!="Pending"&&render.State!="InReview")break;
                if(attempt<2)await Task.Delay(TimeSpan.FromSeconds(2+attempt*2),source.Token);
            }
            ShowPreviewFallback(); PreviewStatus.Text=L.T("O Roblox ainda está preparando a prévia. Atualize a prévia mais tarde.");
        }
        catch(OperationCanceledException){}
        catch(RobloxApiException ex) when(ex.StatusCode==429){_previewRetryAt=DateTime.UtcNow+(ex.RetryAfter??TimeSpan.FromMinutes(1));if(!_disposed)PreviewStatus.Text=L.T("Prévia em espera. Tente novamente em alguns instantes.");}
        catch(Exception){if(!_disposed){ShowPreviewFallback();PreviewStatus.Text=L.T("3D indisponível. A imagem mostra o avatar atual, sem alterações pendentes.");}}
        finally{if(ReferenceEquals(_previewCancellation,source))_previewCancellation=null;}
    }
}
