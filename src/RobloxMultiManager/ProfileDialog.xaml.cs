using System.Windows;
using RobloxMultiManager.Core;

namespace RobloxMultiManager;

public partial class ProfileDialog : Window
{
    private readonly Guid _id;
    private readonly AccountProfile? _original;

    public ProfileDialog(AccountProfile? profile = null)
    {
        InitializeComponent();
        _original = profile;
        _id = profile?.Id ?? Guid.NewGuid();
        NameInput.Text = profile?.Name ?? string.Empty;
        Loaded += (_, _) =>
        {
            NameInput.Focus();
            NameInput.SelectAll();
        };
    }

    public AccountProfile? Result { get; private set; }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var name = NameInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            AppDialog.Show(
                this,
                RobloxMultiManager.Core.L.T("Informe um nome para identificar a conta."),
                RobloxMultiManager.Core.L.T("Perfil"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        Result = _original is null
            ? AccountProfile.Create(name)
            : _original with { Name = name };
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
