using System.Text.Json;
using System.Text.RegularExpressions;

namespace RobloxMultiManager.Core;

/// <summary>
/// The Roblox account a running client is signed in as.
/// </summary>
/// <param name="IsExact">
/// <see langword="true"/> when the account came from an artifact Roblox names by
/// process id, so no correlation was involved.  <see langword="false"/> when it
/// was inferred by matching a log file to the process start time, which is a
/// heuristic and can be ambiguous when several clients start seconds apart.
/// </param>
public sealed record RobloxClientAccount(
    long UserId,
    string? Username,
    string? DisplayName,
    bool IsExact)
{
    public string Label => Username is { Length: > 0 }
        ? $"@{Username}"
        : $"ID {UserId}";
}

/// <summary>
/// Identifies which account an already-running Roblox client belongs to, without
/// touching the client itself.
/// </summary>
/// <remarks>
/// <para>
/// Two sources, measured on a real machine. <c>LocalStorage\memProfStorage&lt;pid&gt;.json</c>
/// is named by process id and carries <c>UserId</c>, so it needs no correlation at
/// all; it is preferred, but Roblox does not always leave one (only 2 of 16
/// observed sessions had one, and a short session wrote 32 bytes with no id).
/// The client log under <c>logs\</c> carries <c>UserId</c>, <c>UserName</c> and
/// <c>DisplayName</c> in its join payload, but nothing in the log names the
/// process, so tying a log to a pid relies on creation-time proximity.
/// </para>
/// <para>
/// One field is deliberately ignored: the <c>rbxuid=</c> value in the log's cookie
/// line is NOT the account that is playing.  It was measured reporting one account
/// while a different one was in the game, so using it would confidently show the
/// wrong name.
/// </para>
/// <para>
/// These files also contain authentication tickets and signatures.  Only the three
/// identity fields are extracted, by pattern; no other value is read out, kept or
/// returned.
/// </para>
/// </remarks>
public sealed class RobloxAccountResolver
{
    /// <summary>Matches the id in <c>memProfStorage&lt;pid&gt;.json</c>.</summary>
    private static readonly Regex StorageUserId = new(
        "\"(?:Debug)?UserId\"\\s*:\\s*\"(?<id>\\d{1,19})\"",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// The join payload's identity keys.  The URL is percent-encoded in the log,
    /// so <c>:</c> arrives as <c>%3a</c> and <c>,</c> as <c>%2c</c>.
    /// </summary>
    private static readonly Regex JoinPayloadIdentity = new(
        "\"UserId\"(?:%3a|:)(?<id>\\d{1,19})(?:%2c|,)"
        + "\"UserName\"(?:%3a|:)\"(?<user>[^\"%]{1,60})\"(?:%2c|,)"
        + "\"DisplayName\"(?:%3a|:)\"(?<display>[^\"%]{1,80})\"",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>The telemetry line, which carries the id but no name.</summary>
    private static readonly Regex JoinTelemetryUserId = new(
        "GameJoinLoadTime.*?\\buserid:(?<id>\\d{1,19})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private const int MaxLogBytes = 8 * 1024 * 1024;
    private static readonly TimeSpan LogStartTolerance = TimeSpan.FromSeconds(20);

    private readonly string _localStorageRoot;
    private readonly string _logsRoot;
    private readonly string _appStorageFile;
    private Dictionary<long, (string Username, string DisplayName)>? _knownAccounts;

    public RobloxAccountResolver(string? robloxLocalRootOverride = null)
    {
        var root = robloxLocalRootOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Roblox");
        _localStorageRoot = Path.Combine(root, "LocalStorage");
        _logsRoot = Path.Combine(root, "logs");
        _appStorageFile = Path.Combine(_localStorageRoot, "appStorage.json");
    }

    /// <summary>
    /// Resolves the account for one running client, or <see langword="null"/> when
    /// nothing on disk identifies it yet.
    /// </summary>
    /// <remarks>
    /// A client that has not joined a place yet legitimately has no identity on
    /// disk, so a null result is a "not yet", not a failure.
    /// </remarks>
    public RobloxClientAccount? Resolve(int processId, DateTime processStartUtc)
    {
        if (processId <= 0)
        {
            return null;
        }

        var exactId = ReadUserIdForProcess(processId);
        if (exactId is { } identified)
        {
            var (username, displayName) = LookupName(identified);
            return new RobloxClientAccount(
                identified,
                username,
                displayName,
                IsExact: true);
        }

        return ResolveFromLogs(processStartUtc);
    }

    /// <summary>
    /// Reads the account id from the per-process storage file Roblox names by pid.
    /// </summary>
    private long? ReadUserIdForProcess(int processId)
    {
        var path = Path.Combine(
            _localStorageRoot,
            $"memProfStorage{processId.ToString(
                System.Globalization.CultureInfo.InvariantCulture)}.json");
        var text = TryReadAllText(path, 4 * 1024 * 1024);
        if (text is null)
        {
            return null;
        }

        var match = StorageUserId.Match(text);
        return match.Success
               && long.TryParse(match.Groups["id"].Value, out var id)
               && id > 0
            ? id
            : null;
    }

    /// <summary>
    /// Finds the log written by the client that started at
    /// <paramref name="processStartUtc"/> and reads its join identity.
    /// </summary>
    private RobloxClientAccount? ResolveFromLogs(DateTime processStartUtc)
    {
        if (processStartUtc == default || !Directory.Exists(_logsRoot))
        {
            return null;
        }

        FileInfo[] candidates;
        try
        {
            candidates = new DirectoryInfo(_logsRoot)
                .GetFiles("*_last.log", SearchOption.TopDirectoryOnly);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        // Roblox creates the log a fraction of a second after the process starts.
        // A small negative tolerance absorbs clock skew between the two readings.
        var best = candidates
            .Where(file => file.CreationTimeUtc
                           >= processStartUtc - LogStartTolerance)
            .OrderBy(file => Math.Abs(
                (file.CreationTimeUtc - processStartUtc).TotalMilliseconds))
            .FirstOrDefault();
        if (best is null)
        {
            return null;
        }

        return ReadIdentityFromLog(best.FullName);
    }

    private RobloxClientAccount? ReadIdentityFromLog(string path)
    {
        long? telemetryId = null;
        try
        {
            // Shared read: the client keeps its own log open while it runs.
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var budget = MaxLogBytes;
            while (reader.ReadLine() is { } line)
            {
                budget -= line.Length + 1;
                if (budget <= 0)
                {
                    break;
                }

                // The join payload is the only place with all three fields. The
                // same line also holds the authentication ticket, which is why
                // only these capture groups are ever taken from it.
                var identity = JoinPayloadIdentity.Match(line);
                if (identity.Success
                    && long.TryParse(identity.Groups["id"].Value, out var id)
                    && id > 0)
                {
                    return new RobloxClientAccount(
                        id,
                        identity.Groups["user"].Value,
                        identity.Groups["display"].Value,
                        IsExact: false);
                }

                if (telemetryId is null)
                {
                    var telemetry = JoinTelemetryUserId.Match(line);
                    if (telemetry.Success
                        && long.TryParse(
                            telemetry.Groups["id"].Value,
                            out var fallbackId)
                        && fallbackId > 0)
                    {
                        telemetryId = fallbackId;
                    }
                }
            }
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        if (telemetryId is not { } resolved)
        {
            return null;
        }

        var (username, displayName) = LookupName(resolved);
        return new RobloxClientAccount(resolved, username, displayName, false);
    }

    /// <summary>
    /// Resolves a name for an id using the roster Roblox keeps of accounts that
    /// have signed in on this machine.
    /// </summary>
    private (string? Username, string? DisplayName) LookupName(long userId)
    {
        _knownAccounts ??= ReadKnownAccounts();
        return _knownAccounts.TryGetValue(userId, out var found)
            ? (found.Username, found.DisplayName)
            : (null, null);
    }

    private Dictionary<long, (string Username, string DisplayName)> ReadKnownAccounts()
    {
        var accounts = new Dictionary<long, (string, string)>();
        var text = TryReadAllText(_appStorageFile, 4 * 1024 * 1024);
        if (text is null)
        {
            return accounts;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return accounts;
            }

            // The current account sits at the top level; the roster of previous
            // ones is a JSON document stored inside a string field.
            AddCurrent(document.RootElement, accounts);
            if (document.RootElement.TryGetProperty(
                    "PreviousAccountsList",
                    out var rosterElement)
                && rosterElement.ValueKind == JsonValueKind.String
                && rosterElement.GetString() is { Length: > 0 } roster)
            {
                AddRoster(roster, accounts);
            }
        }
        catch (JsonException)
        {
            return accounts;
        }

        return accounts;
    }

    private static void AddCurrent(
        JsonElement root,
        Dictionary<long, (string, string)> accounts)
    {
        if (!root.TryGetProperty("UserId", out var idElement)
            || idElement.ValueKind != JsonValueKind.String
            || !long.TryParse(idElement.GetString(), out var userId)
            || userId <= 0)
        {
            return;
        }

        var username = ReadString(root, "Username");
        var displayName = ReadString(root, "DisplayName");
        if (username is not null)
        {
            accounts[userId] = (username, displayName ?? username);
        }
    }

    private static void AddRoster(
        string roster,
        Dictionary<long, (string, string)> accounts)
    {
        try
        {
            using var document = JsonDocument.Parse(roster);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            foreach (var entry in document.RootElement.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.Object
                    || !long.TryParse(entry.Name, out var userId)
                    || userId <= 0
                    || accounts.ContainsKey(userId))
                {
                    continue;
                }

                var username = ReadString(entry.Value, "username");
                var displayName = ReadString(entry.Value, "displayName");
                if (username is not null)
                {
                    accounts[userId] = (username, displayName ?? username);
                }
            }
        }
        catch (JsonException)
        {
            // A roster this app cannot parse simply yields no names.
        }
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
        && value.GetString() is { Length: > 0 } text
            ? text
            : null;

    private static string? TryReadAllText(string path, int maxBytes)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0 || info.Length > maxBytes)
            {
                return null;
            }

            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
