using System.Windows;

namespace RobloxMultiManager;

/// <summary>
/// The app's own message dialog, replacing every native MessageBox.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Show"/> keeps MessageBox.Show's argument order and returns the same
/// <see cref="MessageBoxResult"/>, so swapping the roughly forty call sites was
/// mechanical: no wording, button choice or result comparison had to be rewritten,
/// which matters because several of those messages are the honest explanations of
/// measured failures and are easy to damage by retyping.
/// </para>
/// <para>
/// Only the button sets the app actually uses are implemented, and an unknown set
/// degrades to a single OK rather than throwing. A dialog that crashes while
/// reporting a problem would hide the problem.
/// </para>
/// </remarks>
public partial class AppDialog : Window
{
    private MessageBoxResult _result = MessageBoxResult.None;
    private MessageBoxResult _primaryResult = MessageBoxResult.OK;
    private MessageBoxResult _secondaryResult = MessageBoxResult.None;

    private AppDialog()
    {
        InitializeComponent();
        KeyDown += OnKeyDown;
    }

    /// <summary>
    /// Shows a themed dialog with the same signature as MessageBox.Show.
    /// </summary>
    public static MessageBoxResult Show(
        Window? owner,
        string message,
        string title,
        MessageBoxButton buttons = MessageBoxButton.OK,
        MessageBoxImage icon = MessageBoxImage.None)
    {
        var dialog = new AppDialog
        {
            // A dialog owned by a hidden or closing window cannot be shown, and the
            // main window hides to the tray, so ownership is only taken when the
            // owner can actually host it.
            Owner = owner is { IsLoaded: true, IsVisible: true } ? owner : null
        };
        if (dialog.Owner is null)
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        dialog.TitleText.Text = title;
        dialog.MessageText.Text = message;
        dialog.ApplyIcon(icon);
        dialog.ApplyButtons(buttons);
        dialog.ShowDialog();
        return dialog._result;
    }

    private void ApplyIcon(MessageBoxImage icon)
    {
        // Colour carries the severity, so the same glyph would still read correctly
        // for a colour-blind user through the title and wording.
        var (fill, border, stroke, glyph) = icon switch
        {
            MessageBoxImage.Error => ("DangerSurface", "DangerBorder", "Danger", "IconClose"),
            MessageBoxImage.Warning => ("WarningSurface", "Warning", "Warning", "IconInfo"),
            MessageBoxImage.Question => ("AccentSurface", "AccentBorder", "Accent", "IconInfo"),
            _ => ("AccentSurface", "AccentBorder", "Accent", "IconInfo")
        };

        // Fully qualified: WinForms is referenced too, so Brush is ambiguous.
        IconWell.Background = (System.Windows.Media.Brush)FindResource(fill);
        IconWell.BorderBrush = (System.Windows.Media.Brush)FindResource(border);
        IconGlyph.Stroke = (System.Windows.Media.Brush)FindResource(stroke);
        IconGlyph.Data = (System.Windows.Media.Geometry)FindResource(glyph);
    }

    private void ApplyButtons(MessageBoxButton buttons)
    {
        switch (buttons)
        {
            case MessageBoxButton.YesNo:
                PrimaryButton.Content = RobloxMultiManager.Core.L.T("Sim");
                _primaryResult = MessageBoxResult.Yes;
                SecondaryButton.Content = RobloxMultiManager.Core.L.T("Não");
                _secondaryResult = MessageBoxResult.No;
                SecondaryButton.Visibility = Visibility.Visible;
                _result = MessageBoxResult.No;
                break;

            case MessageBoxButton.OKCancel:
                PrimaryButton.Content = "OK";
                _primaryResult = MessageBoxResult.OK;
                SecondaryButton.Content = RobloxMultiManager.Core.L.T("Cancelar");
                _secondaryResult = MessageBoxResult.Cancel;
                SecondaryButton.Visibility = Visibility.Visible;
                _result = MessageBoxResult.Cancel;
                break;

            case MessageBoxButton.YesNoCancel:
                PrimaryButton.Content = RobloxMultiManager.Core.L.T("Sim");
                _primaryResult = MessageBoxResult.Yes;
                SecondaryButton.Content = RobloxMultiManager.Core.L.T("Não");
                _secondaryResult = MessageBoxResult.No;
                SecondaryButton.Visibility = Visibility.Visible;
                _result = MessageBoxResult.Cancel;
                break;

            default:
                PrimaryButton.Content = "OK";
                _primaryResult = MessageBoxResult.OK;
                SecondaryButton.Visibility = Visibility.Collapsed;
                _result = MessageBoxResult.OK;
                break;
        }
    }

    // Fully qualified: WinForms is also referenced, so KeyEventArgs is ambiguous.
    private void OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Escape)
        {
            return;
        }

        // Escape means "do not do it": the negative answer when there is one, and a
        // plain dismissal otherwise.
        _result = SecondaryButton.Visibility == Visibility.Visible
            ? _secondaryResult
            : _primaryResult;
        Close();
    }

    private void Primary_Click(object sender, RoutedEventArgs e)
    {
        _result = _primaryResult;
        Close();
    }

    private void Secondary_Click(object sender, RoutedEventArgs e)
    {
        _result = _secondaryResult;
        Close();
    }

    private void Dismiss_Click(object sender, RoutedEventArgs e)
    {
        _result = SecondaryButton.Visibility == Visibility.Visible
            ? _secondaryResult
            : _primaryResult;
        Close();
    }
}
