using System.Windows;
using RobloxMultiManager.Core;

namespace RobloxMultiManager;

public partial class GraphicsPanel : System.Windows.Controls.UserControl
{
    private sealed record Scope(Guid? Id, string Name) { public override string ToString() => Name; }
    private readonly RobloxGraphicsSettingsService _service;
    private readonly IEnumerable<AccountProfile> _profiles;
    private readonly Func<GraphicsLaunchPolicy, Task>? _save;
    private GraphicsLaunchPolicy _policy;
    private bool _synchronizing;
    private readonly SettingsSaveQueue _autosave = new();
    public bool IsBusy => _autosave.IsBusy;
    public GraphicsPanel(RobloxGraphicsSettingsService service, IEnumerable<AccountProfile>? profiles = null, GraphicsLaunchPolicy? policy = null, Func<GraphicsLaunchPolicy, Task>? save = null)
    {
        _service = service; _profiles = profiles ?? []; _policy = GraphicsLaunchPolicy.Validate(policy ?? new()); _save = save;
        InitializeComponent(); RefreshScopes(); Enabled.IsChecked = _policy.Enabled;
        SettingsSaveQueue.Watch(this, SaveChanges, Enabled, UseGlobal, Fps, Quality);
    }
    private static readonly string[] DraftFields = ["Account", "Enabled", "UseGlobal", "Fps", "Quality"];
    internal Dictionary<string, string> CaptureDraft() => FormDrafts.Capture(this, DraftFields);
    internal void RestoreDraft(Dictionary<string, string>? draft) => FormDrafts.Restore(this, draft, DraftFields);
    public void RefreshLanguage() { RefreshScopes(); Status.Text = ""; }
    public void Synchronize(GraphicsLaunchPolicy policy) { if (IsBusy) return; _policy = policy; _synchronizing = true; Enabled.IsChecked = policy.Enabled; Populate(); }
    public void SelectAccount(Guid id) => Account.SelectedItem = Account.Items.OfType<Scope>().FirstOrDefault(item => item.Id == id);
    public void RefreshPrivacy() => RefreshScopes();
    private void RefreshScopes()
    {
        var id = (Account.SelectedItem as Scope)?.Id;
        Account.ItemsSource = new[] { new Scope(null, L.T("Todas as contas · global")) }.Concat(_profiles.Select(p => new Scope(p.Id, p.PublicName))).ToArray();
        Account.SelectedItem = ((Scope[])Account.ItemsSource).FirstOrDefault(s => s.Id == id) ?? Account.Items[0];
    }
    private void Account_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => Populate();
    private void Populate()
    {
        if (Fps is null || Account.SelectedItem is not Scope scope) return;
        _synchronizing = true;
        var own = scope.Id is Guid id ? _policy.Accounts!.FirstOrDefault(a => a.ProfileId == id) : null;
        UseGlobal.Visibility = scope.Id is null ? Visibility.Collapsed : Visibility.Visible;
        Enabled.Visibility = scope.Id is null ? Visibility.Visible : Visibility.Collapsed;
        UseGlobal.IsChecked = scope.Id is not null && own is null;
        var value = scope.Id is Guid profileId ? _policy.ForAccount(profileId) : new(_policy.GlobalFps, _policy.GlobalQuality);
        Fps.SelectedValue = value.FpsCap.ToString(); Quality.Value = value.Quality;
        _synchronizing = false; RefreshMode();
    }
    private void UseGlobal_Changed(object sender, RoutedEventArgs e)
    {
        if (_synchronizing || Fps is null) return;
        if (UseGlobal.IsChecked == true) { _synchronizing = true; Fps.SelectedValue = _policy.GlobalFps.ToString(); Quality.Value = _policy.GlobalQuality; _synchronizing = false; }
        RefreshMode();
    }
    private void RefreshMode()
    {
        var inherited = UseGlobal.Visibility == Visibility.Visible && UseGlobal.IsChecked == true;
        Fps.IsEnabled = Quality.IsEnabled = !inherited;
        Mode.Text = !_policy.Enabled ? L.T("Ative Aplicar ao abrir na tela inicial para usar os gráficos salvos.") : inherited ? L.T("Esta conta acompanha o padrão global.") : L.T("Os valores serão usados na próxima abertura pelo MultiJanela.");
    }
    private void Read_Click(object sender, RoutedEventArgs e)
    {
        try { var value = _service.ReadCurrent(); UseGlobal.IsChecked = false; Fps.SelectedValue = value.FpsCap.ToString(); Quality.Value = value.Quality; Status.Text = L.T("Preferências lidas. Salve para utilizar nas próximas aberturas."); }
        catch (Exception) { Status.Text = L.T("Não foi possível ler as preferências gráficas."); }
    }
    private void SaveChanges()
    {
        if (_synchronizing || !int.TryParse(Fps.SelectedValue?.ToString(), out var fps) || Account.SelectedItem is not Scope scope) return;
        var value = new RobloxGraphicsPreferences(fps, (int)Quality.Value);
        _policy = GraphicsLaunchPolicy.Validate((scope.Id is Guid id ? _policy.WithAccount(id, UseGlobal.IsChecked == true ? null : value) : _policy with { GlobalFps = fps, GlobalQuality = value.Quality }) with { Enabled = Enabled.IsChecked == true });
        var next = _policy; RefreshMode();
        _autosave.Enqueue("graphics", async () =>
        {
            try
            {
                if (_save is not null) await _save(next); else await _service.ApplyAsync(value);
                Status.Text = next.Enabled ? L.T("Gráficos salvos para as próximas aberturas.") : L.T("Gráficos salvos. A aplicação automática está desativada.");
            }
            catch (Exception) { Status.Text = L.T("Não foi possível salvar os gráficos. As preferências anteriores foram preservadas."); }
        });
    }
}
