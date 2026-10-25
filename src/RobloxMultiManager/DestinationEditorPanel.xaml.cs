using System.Windows;
using RobloxMultiManager.Core;

namespace RobloxMultiManager;

public partial class DestinationEditorPanel : System.Windows.Controls.UserControl
{
    private readonly Guid? _id;
    private readonly Func<SavedLaunchDestination, Task> _save;
    public bool IsSaving { get; private set; }
    public event EventHandler? Completed;
    public event EventHandler? CancelRequested;
    public DestinationEditorPanel(SavedLaunchDestination? original, string initialLink, Func<SavedLaunchDestination, Task> save)
    {
        _id = original?.Id; _save = save;
        InitializeComponent();
        NameInput.Text = original?.Name ?? string.Empty;
        LinkInput.Text = original?.Link ?? initialLink;
        Loaded += (_, _) => { NameInput.Focus(); NameInput.SelectAll(); };
    }
    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (IsSaving) return;
        if (!SavedLaunchDestination.TryCreate(NameInput.Text, LinkInput.Text, _id, out var destination, out var error))
        { ErrorText.Text = error; return; }
        IsSaving = true;
        SaveButton.IsEnabled = CancelButton.IsEnabled = false;
        ErrorText.Text = string.Empty;
        try { await _save(destination); Completed?.Invoke(this, EventArgs.Empty); }
        catch (Exception) { ErrorText.Text = RobloxMultiManager.Core.L.T("Não foi possível salvar neste computador. Tente novamente."); }
        finally { IsSaving = false; SaveButton.IsEnabled = CancelButton.IsEnabled = true; }
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) { if (!IsSaving) CancelRequested?.Invoke(this, EventArgs.Empty); }
}
