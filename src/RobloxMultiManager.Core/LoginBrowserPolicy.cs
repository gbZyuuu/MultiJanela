namespace RobloxMultiManager.Core;

/// <summary>Navigation rules and intentionally credential-free login diagnostics.</summary>
public static class LoginBrowserPolicy
{
    public static bool IsRobloxPage(string? value) =>
        IsBlank(value) || TryHttps(value, out var uri) && IsDomain(uri.Host, "roblox.com");

    // Roblox documents these two providers for verification. They may open a
    // child window, but never become destinations of the main credential page.
    public static bool IsLoginPopup(string? value) =>
        IsRobloxPage(value) || TryHttps(value, out var uri)
        && (IsDomain(uri.Host, "arkoselabs.com") || IsDomain(uri.Host, "funcaptcha.com"));

    public static string? DescribeHttpFailure(string? value, int statusCode)
    {
        if (statusCode is < 400 or > 599 || !IsLoginPopup(value)
            || !TryHttps(value, out var uri)) return null;
        // Never include path, query, fragment, request/response body or headers.
        return $"{uri.IdnHost} · HTTP {statusCode}";
    }

    private static bool IsBlank(string? value) =>
        string.Equals(value, "about:blank", StringComparison.OrdinalIgnoreCase);

    private static bool TryHttps(string? value, out Uri uri)
    {
        uri = null!;
        return Uri.TryCreate(value, UriKind.Absolute, out uri!)
            && uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo);
    }

    private static bool IsDomain(string host, string domain) =>
        host.Equals(domain, StringComparison.OrdinalIgnoreCase)
        || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);
}
