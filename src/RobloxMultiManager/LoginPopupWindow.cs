using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using RobloxMultiManager.Core;

namespace RobloxMultiManager;

/// <summary>A real child WebView preserving window.opener and the account profile.</summary>
internal sealed class LoginPopupWindow : Window
{
    private readonly WebView2 _browser = new();
    private readonly TextBlock _host = new() { Margin = new Thickness(12, 8, 12, 8) };
    private bool _closed;

    public LoginPopupWindow(Window owner)
    {
        Owner = owner;
        Title = L.T("Verificação do Roblox");
        Width = 700; Height = 650; MinWidth = 400; MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        // A regular resizable window keeps the close button available even when
        // an external verification page fails to load.
        Style = null;
        var panel = new DockPanel();
        DockPanel.SetDock(_host, Dock.Top);
        panel.Children.Add(_host);
        panel.Children.Add(_browser);
        Content = panel;
        Closed += (_, _) => { _closed = true; _browser.Dispose(); };
    }

    public async Task<CoreWebView2?> InitializeAsync(
        CoreWebView2Environment environment,
        EventHandler<CoreWebView2NewWindowRequestedEventArgs> popupHandler,
        Action<string> diagnostic)
    {
        await _browser.EnsureCoreWebView2Async(environment);
        if (_closed) return null;
        var core = _browser.CoreWebView2;
        AccountLoginWindow.ConfigureLoginBrowser(core);
        core.NavigationStarting += (_, e) =>
        {
            if (!LoginBrowserPolicy.IsLoginPopup(e.Uri))
            {
                e.Cancel = true;
                _host.Text = L.T("Destino externo bloqueado.");
                diagnostic("popup-origin-blocked");
                return;
            }
            _host.Text = Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri)
                ? uri.Host : "";
        };
        core.WebResourceResponseReceived += (_, e) =>
        {
            if (LoginBrowserPolicy.DescribeHttpFailure(e.Request.Uri, e.Response.StatusCode) is { } failure)
                diagnostic(failure);
        };
        core.NewWindowRequested += popupHandler;
        core.WindowCloseRequested += (_, _) => Close();
        // Do not Navigate here. The opener must supply the initial document via
        // NewWindowRequested.NewWindow while its deferral is still held.
        return core;
    }
}
