using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace RobloxMultiManager.Core;

/// <summary>
/// One published Roblox WindowsPlayer deployment.  <see cref="VersionId"/> is
/// always a validated <c>version-&lt;16 hex&gt;</c> identifier, so it is safe to
/// interpolate into a CDN path.  <see cref="Version"/> and
/// <see cref="DeployedAtUtc"/> are best effort: Roblox redacts the identifier in
/// its own DeployHistory, so historical rows can legitimately lack either field.
/// </summary>
public sealed record RobloxDeploymentEntry(
    string VersionId,
    string? Version,
    DateTime? DeployedAtUtc)
{
    public string VersionText => Version ?? RobloxMultiManager.Core.L.T("versão desconhecida");

    public string DeployedAtText => DeployedAtUtc is null
        ? RobloxMultiManager.Core.L.T("data desconhecida")
        : DeployedAtUtc.Value.ToLocalTime().ToString(
            "dd/MM/yyyy HH:mm",
            CultureInfo.InvariantCulture);
}

/// <summary>
/// Reads the public deployment index that mirrors Roblox's own DeployHistory and
/// restores the deployment identifiers Roblox now redacts.
/// </summary>
/// <remarks>
/// <para>
/// Roblox publishes <c>https://setup.rbxcdn.com/DeployHistory.txt</c>, but every
/// entry since roughly 2026-03-17 reads literally <c>version-hidden</c>, and the
/// official <c>clientsettings</c> endpoint returns only the single current
/// deployment.  Neither can enumerate history, so the newest-to-oldest list is
/// read from a mirror that polls <c>clientsettings</c> hourly and persists what
/// it observes.  Only the identifier list comes from the mirror; every download
/// still goes to Roblox's own CDN and is verified against Roblox's own manifest.
/// </para>
/// <para>
/// Two consequences are inherent rather than defects.  A deployment that was
/// never observed while it was live has no recorded identifier anywhere, so some
/// version strings resolve to nothing.  And a brand new deployment can be up to
/// one polling interval late here, which is why the caller merges the live
/// <see cref="RobloxVersionChecker"/> result into the list.
/// </para>
/// </remarks>
public sealed class RobloxDeploymentCatalog
{
    private const string HistoryUrl =
        "https://setup-rbxcdn.github.io/version-history-inverted/Windows/WindowsPlayer.json";
    private const string MetadataUrl =
        "https://setup-rbxcdn.github.io/hash-metadata.json";
    private const int MaxResponseBytes = 8 * 1024 * 1024;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(90);

    private readonly HttpClient _httpClient;
    private readonly object _cacheLock = new();
    private IReadOnlyList<RobloxDeploymentEntry>? _cachedEntries;
    private DateTime _cacheExpiryUtc;

    public RobloxDeploymentCatalog(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public string Source => "setup-rbxcdn.github.io";

    /// <summary>
    /// Every known WindowsPlayer deployment, newest first.
    /// Results are cached in-memory for <see cref="CacheTtl"/> so repeated
    /// drawer opens within the TTL skip the network entirely.
    /// </summary>
    public async Task<IReadOnlyList<RobloxDeploymentEntry>> GetWindowsPlayerAsync(
        CancellationToken cancellationToken = default)
    {
        // Return cached result if still valid. The lock is held only for the
        // dictionary lookup, not across the await, so concurrent callers do
        // not block each other during a cache hit.
        lock (_cacheLock)
        {
            if (_cachedEntries is not null && DateTime.UtcNow < _cacheExpiryUtc)
            {
                return _cachedEntries;
            }
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));

        var history = await GetStringAsync(HistoryUrl, timeout.Token)
            .ConfigureAwait(false);
        // The timestamp file only covers the redacted era.  A missing or broken
        // one must not lose the 1000-odd rows the history file already provided.
        string? metadata = null;
        try
        {
            metadata = await GetStringAsync(MetadataUrl, timeout.Token)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            metadata = null;
        }
        catch (InvalidDataException)
        {
            metadata = null;
        }

        var parsed = Parse(history, metadata);

        // Cache the successful result so subsequent opens within the TTL
        // skip both HTTP round-trips and the JSON parse entirely.
        lock (_cacheLock)
        {
            _cachedEntries = parsed;
            _cacheExpiryUtc = DateTime.UtcNow + CacheTtl;
        }

        return parsed;
    }

    /// <summary>
    /// Merges freshly observed deployments into a catalog snapshot.  The mirror
    /// refreshes on a timer, so the live endpoint can legitimately know about a
    /// deployment the catalog has not recorded yet.
    /// </summary>
    public static IReadOnlyList<RobloxDeploymentEntry> Merge(
        IEnumerable<RobloxDeploymentEntry> catalog,
        IEnumerable<RobloxDeploymentEntry> extra)
    {
        var merged = new Dictionary<string, RobloxDeploymentEntry>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var entry in catalog.Concat(extra))
        {
            if (!RobloxInstallationLocator.IsValidVersionId(entry.VersionId))
            {
                continue;
            }

            if (!merged.TryGetValue(entry.VersionId, out var existing))
            {
                merged[entry.VersionId] = entry;
                continue;
            }

            merged[entry.VersionId] = existing with
            {
                Version = existing.Version ?? entry.Version,
                DeployedAtUtc = existing.DeployedAtUtc ?? entry.DeployedAtUtc
            };
        }

        return Sort(merged.Values);
    }

    internal static IReadOnlyList<RobloxDeploymentEntry> Parse(
        string? historyJson,
        string? metadataJson)
    {
        var timestamps = ParseTimestamps(metadataJson);
        var entries = new Dictionary<string, RobloxDeploymentEntry>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var (versionId, version) in ParseHistory(historyJson))
        {
            timestamps.TryGetValue(versionId, out var deployedAt);
            entries[versionId] = new RobloxDeploymentEntry(
                versionId,
                version,
                deployedAt == default ? null : deployedAt);
        }

        // A timestamp can exist for an identifier the history file omits.
        foreach (var (versionId, deployedAt) in timestamps)
        {
            if (!entries.ContainsKey(versionId))
            {
                entries[versionId] = new RobloxDeploymentEntry(
                    versionId,
                    null,
                    deployedAt);
            }
        }

        return Sort(entries.Values);
    }

    private static IReadOnlyList<RobloxDeploymentEntry> Sort(
        IEnumerable<RobloxDeploymentEntry> entries) =>
        entries
            // Rows with a parsable version sort by version, newest first.  Rows
            // without one fall to the end rather than being dropped, because an
            // identifier with no recorded version is still downloadable.
            .OrderByDescending(entry =>
                RobloxVersionChecker.TryParseClientVersion(
                    entry.Version,
                    out var parsed)
                    ? parsed
                    : null)
            .ThenByDescending(entry => entry.DeployedAtUtc ?? DateTime.MinValue)
            .ThenBy(entry => entry.VersionId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static IEnumerable<(string VersionId, string? Version)> ParseHistory(
        string? json)
    {
        var result = new List<(string, string?)>();
        if (string.IsNullOrWhiteSpace(json))
        {
            return result;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return result;
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!RobloxInstallationLocator.IsValidVersionId(property.Name))
                {
                    continue;
                }

                // The identifier is what makes a row downloadable, so it is kept
                // even when the version alongside it is redacted or malformed.
                var version = property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString()
                    : null;
                result.Add((
                    property.Name,
                    RobloxVersionChecker.TryParseClientVersion(version, out _)
                        ? version
                        : null));
            }
        }
        catch (JsonException)
        {
            return result;
        }

        return result;
    }

    private static Dictionary<string, DateTime> ParseTimestamps(string? json)
    {
        var result = new Dictionary<string, DateTime>(
            StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(json))
        {
            return result;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("Windows", out var windows)
                || windows.ValueKind != JsonValueKind.Object
                || !windows.TryGetProperty("WindowsPlayer", out var player)
                || player.ValueKind != JsonValueKind.Object)
            {
                return result;
            }

            foreach (var property in player.EnumerateObject())
            {
                if (!RobloxInstallationLocator.IsValidVersionId(property.Name)
                    || property.Value.ValueKind != JsonValueKind.String
                    || !DateTimeOffset.TryParse(
                        property.Value.GetString(),
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal
                        | DateTimeStyles.AdjustToUniversal,
                        out var parsed))
                {
                    continue;
                }

                result[property.Name] = parsed.UtcDateTime;
            }
        }
        catch (JsonException)
        {
            return result;
        }

        return result;
    }

    private async Task<string> GetStringAsync(
        string url,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("RobloxMultiManager/1.5.0");

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength > MaxResponseBytes)
        {
            throw new InvalidDataException(
                RobloxMultiManager.Core.L.T("O índice de deployments retornou uma resposta grande demais."));
        }

        await using var stream = await response.Content
            .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)
                   .ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxResponseBytes)
            {
                throw new InvalidDataException(
                    RobloxMultiManager.Core.L.T("O índice de deployments excedeu o tamanho permitido."));
            }

            buffer.Write(chunk, 0, read);
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }
}
