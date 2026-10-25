using System.Text.RegularExpressions;

namespace RobloxMultiManager.Core;

public sealed record RobloxSessionEvidence(string? FailureKind, long? PlaceId, string? GameInstanceId);

public sealed class RobloxSessionLogReader
{
    private readonly string _logsRoot;
    private static readonly Regex User = new(@"(?:""UserId""(?:%3a|:)\s*|\buserid:)(?<id>\d{1,19})", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
    private static readonly Regex Join = new(@"Joining game '(?<job>[0-9a-fA-F-]{36})' place (?<place>\d{1,19})\b", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
    public RobloxSessionLogReader(string? root = null) => _logsRoot = Path.Combine(root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roblox"), "logs");
    public RobloxSessionEvidence? Read(DateTime startedUtc, long? userId)
    {
        if (userId is not > 0 || !Directory.Exists(_logsRoot)) return null;
        try
        {
            var matches = new List<FileInfo>();
            foreach (var file in new DirectoryInfo(_logsRoot).EnumerateFiles("*_last.log"))
            {
                if (file.CreationTimeUtc < startedUtc.AddSeconds(-20) || file.CreationTimeUtc > startedUtc.AddSeconds(90)) continue;
                var head = ReadRegion(file.FullName, tail: false);
                var identities = User.Matches(head).Select(m => long.TryParse(m.Groups["id"].Value, out var id) ? id : 0)
                    .Where(id => id > 0).Distinct().Take(2).ToArray();
                if (identities.Length == 1 && identities[0] == userId) matches.Add(file);
                if (matches.Count > 1) return null; // Ambiguity must not send an account to another session's server.
            }
            if (matches.Count != 1) return null;
            var text = ReadRegion(matches[0].FullName, tail: true);
            var joins = Join.Matches(text);
            long? place = null; string? job = null;
            if (joins.Count > 0 && long.TryParse(joins[^1].Groups["place"].Value, out var parsed) && parsed > 0)
            { place = parsed; if (Guid.TryParseExact(joins[^1].Groups["job"].Value, "D", out var guid) && guid != Guid.Empty) job = guid.ToString("D"); }
            var kind = text.Contains("updateRequired TRUE", StringComparison.Ordinal) && text.Contains("Update thread: start", StringComparison.Ordinal)
                ? "self-update" : text.Contains("RBXCRASH", StringComparison.Ordinal) ? "crash" : null;
            return new(kind, place, job);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or RegexMatchTimeoutException) { return null; }
    }
    private static string ReadRegion(string path, bool tail)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        const int cap = 512 * 1024;
        if (tail && stream.Length > cap) stream.Seek(-cap, SeekOrigin.End);
        var bytes = new byte[(int)Math.Min(cap, stream.Length - stream.Position)];
        var count = stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
        return System.Text.Encoding.UTF8.GetString(bytes, 0, count);
    }
}

public static class ReconnectTargetPolicy
{
    public static RobloxLaunchTarget? Resolve(string? originalUrl, RobloxSessionEvidence? evidence)
    {
        RobloxLaunchTarget? original = null;
        if (originalUrl is not null && !RobloxLinkParser.TryParse(originalUrl, out original, out _)) original = null;
        // Private invitations must retain their authorization and original destination.
        if (original?.Kind == RobloxLaunchTargetKind.PrivateServer) return original;
        if (evidence?.PlaceId is > 0)
        {
            var url = $"https://www.roblox.com/games/start?placeId={evidence.PlaceId}";
            if (evidence.GameInstanceId is { } job) url += "&gameInstanceId=" + Uri.EscapeDataString(job);
            return RobloxLinkParser.Parse(url);
        }
        return original;
    }
    public static RobloxLaunchTarget? WithoutServer(RobloxLaunchTarget? target) =>
        target is { Kind: RobloxLaunchTargetKind.Game, PlaceId: > 0, GameInstanceId: not null }
            ? RobloxLinkParser.Parse(target.PlaceId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)) : target;
}
