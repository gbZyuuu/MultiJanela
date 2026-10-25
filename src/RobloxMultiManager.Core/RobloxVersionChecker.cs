using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RobloxMultiManager.Core;

public sealed class RobloxVersionChecker
{
    private static readonly Regex ChannelPattern =
        new("^[A-Za-z0-9_-]{1,64}$", RegexOptions.Compiled);
    private static readonly Regex VersionPattern =
        new("^\\d+\\.\\d+\\.\\d+\\.\\d+$", RegexOptions.Compiled);
    private static readonly Uri OfficialBaseUri =
        new("https://clientsettingscdn.roblox.com/");

    private readonly HttpClient _httpClient;

    public RobloxVersionChecker(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<RobloxRemoteVersionInfo> GetLatestAsync(
        string? channel,
        CancellationToken cancellationToken = default)
    {
        var normalizedChannel = NormalizeChannel(channel);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        var relative =
            $"v2/client-version/WindowsPlayer/channel/{Uri.EscapeDataString(normalizedChannel)}";
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri(OfficialBaseUri, relative));
        request.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("RobloxMultiManager/1.6.0");

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(
            timeout.Token).ConfigureAwait(false);
        if (!TryParseResponse(
                json,
                normalizedChannel,
                DateTime.UtcNow,
                out var result))
        {
            throw new InvalidDataException(
                RobloxMultiManager.Core.L.T("O endpoint oficial retornou uma versão em formato inesperado."));
        }

        return result;
    }

    public static string BuildRddUrl(
        string? channel,
        string? versionId = null)
    {
        var normalizedChannel = NormalizeChannel(channel);
        var url =
            "https://rdd.whatexpsare.online/?binaryType=WindowsPlayer"
            + $"&channel={Uri.EscapeDataString(normalizedChannel)}";
        if (RobloxInstallationLocator.IsValidVersionId(versionId))
        {
            url += $"&version={Uri.EscapeDataString(versionId!)}";
        }

        return url;
    }

    internal static bool TryParseResponse(
        string? json,
        string channel,
        DateTime checkedAtUtc,
        out RobloxRemoteVersionInfo result)
    {
        result = new RobloxRemoteVersionInfo(
            string.Empty,
            string.Empty,
            string.Empty,
            checkedAtUtc,
            string.Empty);
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("version", out var versionElement)
                || versionElement.ValueKind != JsonValueKind.String
                || !root.TryGetProperty(
                    "clientVersionUpload",
                    out var versionIdElement)
                || versionIdElement.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var version = versionElement.GetString();
            var versionId = versionIdElement.GetString();
            if (version is null
                || !VersionPattern.IsMatch(version)
                || !RobloxInstallationLocator.IsValidVersionId(versionId))
            {
                return false;
            }

            result = new RobloxRemoteVersionInfo(
                NormalizeChannel(channel),
                version,
                versionId!,
                DateTime.SpecifyKind(checkedAtUtc, DateTimeKind.Utc),
                "clientsettingscdn.roblox.com");
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Parses a Roblox client version into a comparable value.
    /// </summary>
    /// <remarks>
    /// Accepts both the dotted form and the comma-spaced form the PE version
    /// resource carries (<c>0, 733, 0, 7330989</c>).  Callers must use this
    /// rather than <c>FileVersionInfo.FilePrivatePart</c>: the fourth component
    /// of a real Roblox build exceeds 16 bits (measured 7330989, which the Win32
    /// field truncates to 56493), so the numeric properties compare wrongly
    /// while the string does not.
    /// </remarks>
    public static bool TryParseClientVersion(string? value, out Version? version)
    {
        version = null;
        var text = value?
            .Replace(", ", ".", StringComparison.Ordinal)
            .Trim();
        if (string.IsNullOrEmpty(text) || !VersionPattern.IsMatch(text))
        {
            return false;
        }

        return Version.TryParse(text, out version);
    }

    public static string NormalizeChannel(string? value)
    {
        var normalized = string.IsNullOrWhiteSpace(value)
            ? "LIVE"
            : value.Trim();
        if (normalized.Equals("production", StringComparison.OrdinalIgnoreCase))
        {
            return "LIVE";
        }

        if (!ChannelPattern.IsMatch(normalized))
        {
            throw new ArgumentException(RobloxMultiManager.Core.L.T("Canal Roblox inválido."), nameof(value));
        }

        return normalized.Equals("live", StringComparison.OrdinalIgnoreCase)
            ? "LIVE"
            : normalized;
    }
}
