using System.Windows;
using RobloxMultiManager.Core;

namespace RobloxMultiManager;

public partial class WorkspacesPanel : System.Windows.Controls.UserControl
{
    private Dictionary<string, string>? _pendingDraft;
    private static readonly string[] DraftFields = ["Presets", "Groups", "Account", "PresetName", "GroupName", "Notes", "Tags", "Fields", "CollectionsTabs", "PresetDestination", "PresetCpu", "PresetDelay"];
    internal Dictionary<string, string> CaptureDraft()
    { var data = FormDrafts.Capture(this, DraftFields); data["preset-accounts"] = string.Join(",", _presetAccounts); data["group-accounts"] = string.Join(",", _groupAccounts); return data; }
    internal void RestoreDraft(Dictionary<string, string>? draft) { _pendingDraft = draft; ApplyDraft(); }
    private void ApplyDraft() { if (!IsLoaded || _pendingDraft is null) return; FormDrafts.Restore(this, _pendingDraft, DraftFields);
        Guid[] Parse(string key) => (_pendingDraft.GetValueOrDefault(key) ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Where(s => Guid.TryParse(s, out _)).Select(Guid.Parse).Take(500).ToArray();
        if (_pendingDraft.ContainsKey("preset-accounts")) _presetAccounts = Parse("preset-accounts");
        if (_pendingDraft.ContainsKey("group-accounts")) _groupAccounts = Parse("group-accounts"); _pendingDraft = null; RefreshAccountCaptions(); }
    private readonly WorkspaceLibraryStore _store;
    private readonly Func<string, Guid, Guid[], LaunchPreset> _capture;
    private readonly IEnumerable<AccountProfile> _profiles;
    private Guid[] _presetAccounts = [], _groupAccounts = [];
    private readonly Action<LaunchPreset> _apply;
    private readonly Func<Guid[]> _selection;
    private readonly Action<Guid[]> _useGroup;
    private readonly Action<WorkspaceLibrary> _changed;
    private WorkspaceLibrary _data = WorkspaceLibrary.Empty;
    private bool _loaded;
    public bool IsBusy { get; private set; }
    public void RefreshPrivacy()
    {
        foreach (var control in new System.Windows.UIElement[] { Notes, Tags, Fields, PresetName, PresetDestination, GroupName, Presets, Groups })
            control.Visibility = DisplayPrivacy.Enabled ? Visibility.Hidden : Visibility.Visible;
    }
    public void RefreshLanguage() { Status.Text = ""; RefreshAccountCaptions(); }
    public WorkspacesPanel(WorkspaceLibraryStore store, IEnumerable<AccountProfile> profiles, Func<string, Guid, Guid[], LaunchPreset> capture,
        Action<LaunchPreset> apply, Func<Guid[]> selection, Action<Guid[]> useGroup, Action<WorkspaceLibrary> changed)
    {
        _store = store; _profiles = profiles; _capture = capture; _apply = apply; _selection = selection; _useGroup = useGroup; _changed = changed;
        InitializeComponent(); Account.ItemsSource = profiles;
        Loaded += async (_, _) => { if (!_loaded) await RunAsync(async () => { _data = await _store.LoadAsync(); _loaded = true; Refresh(); _presetAccounts = _groupAccounts = _selection().ToArray(); RefreshAccountCaptions(); }, success: false); ApplyDraft(); RefreshPrivacy(); };
    }
    private async Task RunAsync(Func<Task> action, bool success = true)
    {
        if (IsBusy) return;
        IsBusy = true;
        try { await action(); if (success) Status.Text = L.T("Alterações salvas."); }
        catch (ArgumentException ex) { Status.Text = ex.Message; }
        catch (Exception) { Status.Text = L.T("Não foi possível salvar ou carregar. Verifique os dados e o armazenamento local."); }
        finally { IsBusy = false; }
    }
    private void Refresh()
    {
        var preset = (Presets.SelectedItem as LaunchPreset)?.Id; var group = (Groups.SelectedItem as AccountGroup)?.Id;
        Presets.ItemsSource = _data.Presets; Groups.ItemsSource = _data.Groups;
        Presets.SelectedItem = _data.Presets.FirstOrDefault(p => p.Id == preset); Groups.SelectedItem = _data.Groups.FirstOrDefault(g => g.Id == group);
        NoPresets.Visibility = _data.Presets.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        NoGroups.Visibility = _data.Groups.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _changed(_data); RefreshAccountCaptions();
        if (Account.SelectedItem is null) Account.SelectedIndex = 0;
    }
    private void NewPreset_Click(object sender, RoutedEventArgs e)
    { Presets.SelectedItem = null; PresetName.Clear(); PresetDestination.Clear(); PresetDelay.Text = "5"; PresetCpu.Text = "100"; _presetAccounts = _selection().ToArray(); RefreshAccountCaptions(); Status.Text = L.T("Dê um nome, escolha as contas e salve o perfil."); PresetName.Focus(); }
    private void NewGroup_Click(object sender, RoutedEventArgs e)
    { Groups.SelectedItem = null; GroupName.Clear(); _groupAccounts = _selection().ToArray(); RefreshAccountCaptions(); Status.Text = L.T("Dê um nome, escolha as contas e salve o grupo."); GroupName.Focus(); }
    private async void SavePreset_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (!_loaded) return;
        var id = (Presets.SelectedItem as LaunchPreset)?.Id ?? Guid.NewGuid();
        if (string.IsNullOrWhiteSpace(PresetName.Text)) throw new ArgumentException(L.T("Digite um nome para o perfil."));
        if (_presetAccounts.Length == 0) throw new ArgumentException(L.T("Escolha pelo menos uma conta."));
        if (!int.TryParse(PresetDelay.Text, out var delay) || delay is < 1 or > 120 || !int.TryParse(PresetCpu.Text, out var cpu) || cpu is < 1 or > 100) throw new ArgumentException(L.T("Use intervalo de 1 a 120 segundos e CPU de 1 a 100%."));
        var preset = _capture(PresetName.Text.Trim(), id, _presetAccounts) with { Destination = string.IsNullOrWhiteSpace(PresetDestination.Text) ? "https://www.roblox.com/home" : PresetDestination.Text.Trim(), DelaySeconds = delay, GlobalCpuPercent = cpu };
        _data = await _store.UpdateAsync(d => d with { Presets = d.Presets.Where(p => p.Id != id).Append(preset).ToArray() });
        Refresh(); Presets.SelectedItem = _data.Presets.Single(p => p.Id == id);
    });
    private void Preset_Selected(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    { if (Presets.SelectedItem is LaunchPreset preset) { PresetName.Text = preset.Name; PresetDestination.Text = preset.Destination == "https://www.roblox.com/home" ? "" : preset.Destination; PresetDelay.Text = preset.DelaySeconds.ToString(); PresetCpu.Text = preset.GlobalCpuPercent.ToString(); _presetAccounts = preset.Accounts.ToArray(); RefreshAccountCaptions(); } DeletePreset.IsEnabled = LoadPreset.IsEnabled = Presets.SelectedItem is not null; }
    private void Group_Selected(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    { if (Groups.SelectedItem is AccountGroup group) { GroupName.Text = group.Name; _groupAccounts = group.Accounts.ToArray(); RefreshAccountCaptions(); } DeleteGroup.IsEnabled = UseGroup.IsEnabled = Groups.SelectedItem is not null; }
    private void LoadPreset_Click(object sender, RoutedEventArgs e)
    {
        if (Presets.SelectedItem is not LaunchPreset preset || IsBusy) return;
        if (AppDialog.Show(Window.GetWindow(this), L.F("Carregar ‘{0}’ com {1} conta(s) e suas preferências de abertura?", preset.Name, preset.Accounts.Length),
                L.T("Carregar perfil"), MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes) _apply(preset);
    }
    private async void DeletePreset_Click(object sender, RoutedEventArgs e)
    {
        if (Presets.SelectedItem is not LaunchPreset selected || !ConfirmDelete(selected.Name)) return;
        await RunAsync(async () => { _data = await _store.UpdateAsync(d => d with { Presets = d.Presets.Where(p => p.Id != selected.Id).ToArray() }); Refresh(); });
    }
    private async void SaveGroup_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (!_loaded) return;
        var id = (Groups.SelectedItem as AccountGroup)?.Id ?? Guid.NewGuid();
        if (string.IsNullOrWhiteSpace(GroupName.Text)) throw new ArgumentException(L.T("Digite um nome para o grupo."));
        var group = new AccountGroup(id, GroupName.Text.Trim(), _groupAccounts);
        if (group.Accounts.Length == 0) throw new ArgumentException(L.T("Escolha pelo menos uma conta."));
        _data = await _store.UpdateAsync(d => d with { Groups = d.Groups.Where(g => g.Id != id).Append(group).ToArray() });
        Refresh(); Groups.SelectedItem = _data.Groups.Single(g => g.Id == id);
    });
    private void UseGroup_Click(object sender, RoutedEventArgs e) { if (Groups.SelectedItem is AccountGroup group && !IsBusy) _useGroup(group.Accounts); }
    private async void DeleteGroup_Click(object sender, RoutedEventArgs e)
    {
        if (Groups.SelectedItem is not AccountGroup selected || !ConfirmDelete(selected.Name)) return;
        await RunAsync(async () => { _data = await _store.UpdateAsync(d => d with { Groups = d.Groups.Where(g => g.Id != selected.Id).ToArray() }); Refresh(); });
    }
    private bool ConfirmDelete(string name) => AppDialog.Show(Window.GetWindow(this), L.F("Excluir ‘{0}’?", name), L.T("Excluir"), MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
    private void Account_Selected(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        var item = _data.Metadata.FirstOrDefault(m => m.ProfileId == (Account.SelectedItem as AccountProfile)?.Id);
        Notes.Text = item?.Notes ?? ""; Tags.Text = item is null ? "" : string.Join(", ", item.Tags);
        Fields.Text = item?.Fields is null ? "" : string.Join(Environment.NewLine, item.Fields.Select(f => f.Key + "=" + f.Value));
    }
    private async void SaveMetadata_Click(object sender, RoutedEventArgs e)
    {
        if (Account.SelectedItem is not AccountProfile account) return;
        await RunAsync(async () =>
        {
            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in Fields.Text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split('=', 2, StringSplitOptions.TrimEntries);
                if (parts.Length != 2 || !fields.TryAdd(parts[0], parts[1])) throw new ArgumentException(L.T("Use um campo por linha, no formato nome=valor, sem nomes repetidos."));
            }
            var metadata = new AccountMetadata(account.Id, Notes.Text.Trim(), Tags.Text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), fields);
            _data = await _store.UpdateAsync(d => d with { Metadata = d.Metadata.Where(m => m.ProfileId != account.Id).Append(metadata).ToArray() });
            _changed(_data);
        });
    }
    private void RefreshAccountCaptions()
    {
        string Caption(Guid[] ids) => ids.Length == 0 ? L.T("Nenhuma conta escolhida.") : L.F("{0} conta(s): {1}", ids.Length,
            string.Join(", ", _profiles.Where(p => ids.Contains(p.Id)).Select(p => p.PublicName)));
        PresetAccountsText.Text = Caption(_presetAccounts); GroupAccountsText.Text = Caption(_groupAccounts);
    }
    private Guid[]? PickAccounts(Guid[] initial)
    {
        var picker = new QueueAccountsPanel(_profiles, new HashSet<Guid>());
        foreach (var choice in picker.Choices) choice.IsChosen = initial.Contains(choice.Profile.Id);
        var dialog = new Window { Owner = Window.GetWindow(this), Title = L.T("Escolher contas"), Width = 620, Height = 650, MinWidth = 480, MinHeight = 420, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = picker };
        dialog.SetResourceReference(StyleProperty, "AppWindowStyle"); Guid[]? selected = null;
        picker.AddRequested += (_, _) => { selected = picker.SelectedProfiles.Select(p => p.Id).ToArray(); dialog.Close(); };
        picker.CancelRequested += (_, _) => dialog.Close(); dialog.ShowDialog(); return selected;
    }
    private void PickPresetAccounts_Click(object sender, RoutedEventArgs e) { if (PickAccounts(_presetAccounts) is { } ids) { _presetAccounts = ids; RefreshAccountCaptions(); } }
    private void PickGroupAccounts_Click(object sender, RoutedEventArgs e) { if (PickAccounts(_groupAccounts) is { } ids) { _groupAccounts = ids; RefreshAccountCaptions(); } }

}
