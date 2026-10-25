using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using RobloxMultiManager.Core;

namespace RobloxMultiManager;

public sealed class QueueAccountChoice(AccountProfile profile, bool alreadyAdded) : INotifyPropertyChanged
{
    public AccountProfile Profile { get; } = profile;
    public bool CanChoose => Profile.IsSignedIn && !alreadyAdded;
    public string Status => alreadyAdded ? RobloxMultiManager.Core.L.T("Na sequência") : Profile.IsSignedIn ? RobloxMultiManager.Core.L.T("Pronta") : RobloxMultiManager.Core.L.T("Faça login");
    private bool _chosen;
    public bool IsChosen
    {
        get => _chosen;
        set
        {
            var next = value && CanChoose;
            if (_chosen == next) return;
            _chosen = next;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChosen)));
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public partial class QueueAccountsPanel : System.Windows.Controls.UserControl
{
    public IReadOnlyList<QueueAccountChoice> Choices { get; }
    public event EventHandler? AddRequested;
    public event EventHandler? CancelRequested;
    private readonly ListCollectionView _view;
    private readonly DispatcherTimer _searchTimer;
    private string _query = string.Empty;
    public QueueAccountsPanel(IEnumerable<AccountProfile> profiles, IReadOnlySet<Guid> existing)
    {
        Choices = profiles.Select(profile => new QueueAccountChoice(profile, existing.Contains(profile.Id))).ToArray();
        InitializeComponent();
        _view = new ListCollectionView(Choices.ToArray()) { Filter = Matches };
        AccountsList.ItemsSource = _view;
        foreach (var choice in Choices) choice.PropertyChanged += (_, _) => UpdateSelection();
        _searchTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(160) };
        _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); ApplySearch(); };
        Unloaded += (_, _) => _searchTimer.Stop();
        Loaded += (_, _) => SearchInput.Focus();
        UpdateSelection();
    }
    public AccountProfile[] SelectedProfiles => Choices.Where(choice => choice.IsChosen).Select(choice => choice.Profile).ToArray();
    private bool Matches(object value) => value is QueueAccountChoice row &&
        (_query.Length == 0 || row.Profile.Name.Contains(_query, StringComparison.OrdinalIgnoreCase)
            || row.Profile.AccountDisplay.Contains(_query, StringComparison.OrdinalIgnoreCase)
            || (row.Profile.RobloxUserId?.ToString().Contains(_query, StringComparison.Ordinal) ?? false));
    private void Search_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_searchTimer is null) return;
        _searchTimer.Stop(); _searchTimer.Start();
    }
    private void ApplySearch()
    {
        _query = SearchInput.Text.Trim(); _view.Refresh();
        EmptyText.Visibility = _view.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
    }
    private void UpdateSelection()
    {
        var count = Choices.Count(choice => choice.IsChosen);
        SelectionText.Text = RobloxMultiManager.Core.L.F("{0} selecionada(s)",count);
        AddButton.Content = count == 1 ? RobloxMultiManager.Core.L.T("Adicionar 1 conta") : RobloxMultiManager.Core.L.F("Adicionar {0} contas",count);
        AddButton.IsEnabled = count > 0;
    }
    private void SelectVisible_Click(object sender, RoutedEventArgs e)
    {
        _searchTimer.Stop(); ApplySearch();
        foreach (QueueAccountChoice choice in _view) choice.IsChosen = true;
    }
    private void Add_Click(object sender, RoutedEventArgs e) { if (SelectedProfiles.Length > 0) AddRequested?.Invoke(this, EventArgs.Empty); }
    private void Cancel_Click(object sender, RoutedEventArgs e) => CancelRequested?.Invoke(this, EventArgs.Empty);
}
