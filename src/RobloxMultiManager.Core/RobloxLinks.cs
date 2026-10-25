using System.Globalization;
using System.Text;

namespace RobloxMultiManager.Core;

public enum RobloxLaunchTargetKind
{
    Game,
    PrivateServer
}

/// <summary>
/// A validated, official Roblox destination.  The original input is never
/// retained; only the minimum launcher URL needed by the official client is
/// kept in memory for the duration of the launch.
/// </summary>
public sealed record RobloxLaunchTarget(
    RobloxLaunchTargetKind Kind,
    long? PlaceId,
    string LauncherUrl,
    string? PrivateServerCode = null,
    string? GameInstanceId = null)
{
    public string LogKind => Kind == RobloxLaunchTargetKind.PrivateServer
        ? "private-server"
        : "game";

    public string SafeSummary => PlaceId is > 0
        ? $"{LogKind};placeId={PlaceId.Value.ToString(CultureInfo.InvariantCulture)}"
        : LogKind;
}

public static class RobloxLinkParser
{
    private const int MaxInputLength = 2048;
    private const int MaxCodeLength = 256;

    public static bool TryParse(
        string? value,
        out RobloxLaunchTarget target,
        out string error)
    {
        target = null!;
        error = string.Empty;
        var text = value?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            error = RobloxMultiManager.Core.L.T("Cole um link de jogo ou de servidor privado.");
            return false;
        }

        if (text.Length > MaxInputLength)
        {
            error = RobloxMultiManager.Core.L.T("O link é grande demais para ser processado.");
            return false;
        }

        if (long.TryParse(
                text,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var numericPlaceId)
            && numericPlaceId > 0)
        {
            target = BuildGameTarget(numericPlaceId, null);
            return true;
        }

        // A mistyped place id (stray space, comma, hyphen, trailing text) misses the
        // numeric branch above and used to land on the HTTPS message, which pointed the
        // user at links when the real problem was the digits they just typed.  Anything
        // without a path or scheme separator cannot be a URL, so treat it as a place id
        // attempt and say what is actually wrong with it.
        if (!text.Contains('/') && !text.Contains(':') && text.Any(char.IsDigit))
        {
            error = text.All(char.IsDigit)
                ? RobloxMultiManager.Core.L.T("O ID do jogo precisa ser um número inteiro positivo válido.")
                : RobloxMultiManager.Core.L.T("Use só os dígitos do ID do jogo, sem espaços nem pontuação. Exemplo: 2753915549.");
            return false;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || !IsOfficialRobloxLink(uri))
        {
            error = RobloxMultiManager.Core.L.T("Use um link HTTPS oficial de www.roblox.com.");
            return false;
        }

        if (!TryReadQuery(uri.Query, out var query))
        {
            error = RobloxMultiManager.Core.L.T("A consulta do link não pôde ser validada.");
            return false;
        }

        var path = Uri.UnescapeDataString(uri.AbsolutePath).Trim('/');
        var segments = path.Split(
            '/',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (segments.Length == 1
            && segments[0].Equals("share", StringComparison.OrdinalIgnoreCase))
        {
            if (!query.TryGetValue("type", out var type)
                || !query.TryGetValue("code", out var shareCode)
                || !IsSafePrivateServerCode(shareCode))
            {
                error = RobloxMultiManager.Core.L.T("O link compartilhado não contém um código válido.");
                return false;
            }

            var encodedCode = Uri.EscapeDataString(shareCode);
            if (type.Equals("Server", StringComparison.OrdinalIgnoreCase))
            {
                target = new RobloxLaunchTarget(
                    RobloxLaunchTargetKind.PrivateServer,
                    null,
                    $"https://www.roblox.com/share?code={encodedCode}&type=Server",
                    shareCode);
                return true;
            }

            if (type.Equals("Experience", StringComparison.OrdinalIgnoreCase))
            {
                target = new RobloxLaunchTarget(
                    RobloxLaunchTargetKind.Game,
                    null,
                    $"https://www.roblox.com/share?code={encodedCode}&type=Experience");
                return true;
            }

            error = RobloxMultiManager.Core.L.T("O link compartilhado deve ser de um jogo ou servidor privado.");
            return false;
        }

        if (segments.Length < 2
            || !segments[0].Equals("games", StringComparison.OrdinalIgnoreCase))
        {
            error = RobloxMultiManager.Core.L.T("O link deve apontar para um jogo ou servidor privado do Roblox.");
            return false;
        }

        long placeId;
        if (segments[1].Equals("start", StringComparison.OrdinalIgnoreCase))
        {
            if (!query.TryGetValue("placeId", out var placeText)
                || !long.TryParse(
                    placeText,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out placeId)
                || placeId <= 0)
            {
                error = RobloxMultiManager.Core.L.T("O link de jogo não contém um PlaceId válido.");
                return false;
            }
        }
        else if (!long.TryParse(
                     segments[1],
                     NumberStyles.None,
                     CultureInfo.InvariantCulture,
                     out placeId)
                 || placeId <= 0)
        {
            error = RobloxMultiManager.Core.L.T("O link de jogo não contém um PlaceId válido.");
            return false;
        }

        query.TryGetValue("privateserverlinkcode", out var privateCode);
        if (privateCode is not null && !IsSafePrivateServerCode(privateCode))
        {
            error = RobloxMultiManager.Core.L.T("O código do servidor privado não pôde ser validado.");
            return false;
        }

        query.TryGetValue("gameInstanceId", out var job);
        if (job is not null && (!Guid.TryParseExact(job, "D", out var parsedJob) || parsedJob == Guid.Empty))
        { error = L.T("O identificador do servidor não é válido."); return false; }
        target = BuildGameTarget(placeId, privateCode, job);
        return true;
    }

    public static RobloxLaunchTarget Parse(string value)
    {
        if (!TryParse(value, out var target, out var error))
        {
            throw new ArgumentException(error, nameof(value));
        }

        return target;
    }

    private static RobloxLaunchTarget BuildGameTarget(
        long placeId,
        string? privateServerCode, string? gameInstanceId = null)
    {
        var builder = new StringBuilder()
            .Append("https://www.roblox.com/games/start?placeId=")
            .Append(placeId.ToString(CultureInfo.InvariantCulture));
        var kind = RobloxLaunchTargetKind.Game;
        if (privateServerCode is not null)
        {
            kind = RobloxLaunchTargetKind.PrivateServer;
            builder.Append("&privateServerLinkCode=")
                .Append(Uri.EscapeDataString(privateServerCode));
        }

        if (gameInstanceId is not null) builder.Append("&gameInstanceId=").Append(Uri.EscapeDataString(gameInstanceId));
        return new RobloxLaunchTarget(
            kind,
            placeId,
            builder.ToString(),
            privateServerCode, gameInstanceId);
    }

    private static bool IsOfficialRobloxLink(Uri uri) =>
        uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
        && uri.IsDefaultPort
        && string.IsNullOrEmpty(uri.UserInfo)
        && string.IsNullOrEmpty(uri.Fragment)
        && (uri.Host.Equals("www.roblox.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("roblox.com", StringComparison.OrdinalIgnoreCase));

    private static bool TryReadQuery(
        string queryText,
        out Dictionary<string, string> query)
    {
        query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var text = queryText.TrimStart('?');
        if (string.IsNullOrEmpty(text))
        {
            return true;
        }

        foreach (var pair in text.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            if (separator <= 0 || separator == pair.Length - 1)
            {
                return false;
            }

            try
            {
                var key = Uri.UnescapeDataString(pair[..separator].Replace('+', ' '));
                var value = Uri.UnescapeDataString(pair[(separator + 1)..].Replace('+', ' '));
                if (string.IsNullOrWhiteSpace(key)
                    || key.Length > 80
                    || value.Length > MaxCodeLength)
                {
                    return false;
                }

                if (!query.TryAdd(key, value))
                {
                    return false;
                }
            }
            catch (UriFormatException)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsSafePrivateServerCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > MaxCodeLength)
        {
            return false;
        }

        return value.All(character =>
            char.IsLetterOrDigit(character)
            || character is '-' or '_' or '.' or '~');
    }
}
