using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RobloxMultiManager.Core;

public sealed record ManagerRelease(string Version, string Url, string Sha256, long Bytes);

public sealed class ManagerUpdateService(HttpClient http)
{
    public static readonly HashSet<string> PayloadFiles = new(StringComparer.Ordinal)
    { "RobloxMultiManager.exe", "README.md", "LICENSE", "NOTICE.md", "THIRD-PARTY-NOTICES.txt", "SHA256SUMS.txt" };
    public async Task<ManagerRelease> CheckAsync(CancellationToken token = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/gbZyuuu/MultiJanela/releases/latest");
        request.Headers.UserAgent.ParseAdd("MultiJanela-Updater/1.0");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > 1024 * 1024) throw new InvalidDataException();
        var text = await response.Content.ReadAsStringAsync(token); if (text.Length > 1024 * 1024) throw new InvalidDataException();
        using var json = JsonDocument.Parse(text);
        var tag = json.RootElement.GetProperty("tag_name").GetString() ?? "";
        if (!Regex.IsMatch(tag, "^v[0-9]+\\.[0-9]+\\.[0-9]+$") || !Version.TryParse(tag[1..], out _)) throw new InvalidDataException();
        var name = "MultiJanela-" + tag[1..] + "-win-x64.zip";
        var asset = json.RootElement.GetProperty("assets").EnumerateArray().FirstOrDefault(a => a.GetProperty("name").GetString() == name);
        if (asset.ValueKind != JsonValueKind.Object) throw new InvalidDataException();
        var url = asset.GetProperty("browser_download_url").GetString() ?? "";
        var digest = asset.GetProperty("digest").GetString() ?? "";
        var bytes = asset.GetProperty("size").GetInt64();
        if (url != $"https://github.com/gbZyuuu/MultiJanela/releases/download/{tag}/{name}"
            || !Regex.IsMatch(digest, "^sha256:[a-fA-F0-9]{64}$") || bytes is < 1024 or > 256 * 1024 * 1024) throw new InvalidDataException();
        return new(tag[1..], url, digest[7..].ToLowerInvariant(), bytes);
    }
    public async Task<string> DownloadAsync(ManagerRelease release, string cacheRoot, IProgress<double>? progress = null, CancellationToken token = default)
    {
        if (!Regex.IsMatch(release.Version, "^[0-9]+\\.[0-9]+\\.[0-9]+$") || !Version.TryParse(release.Version, out _)
            || !Regex.IsMatch(release.Sha256, "^[a-fA-F0-9]{64}$") || release.Bytes is < 1024 or > 256 * 1024 * 1024
            || release.Url != $"https://github.com/gbZyuuu/MultiJanela/releases/download/v{release.Version}/MultiJanela-{release.Version}-win-x64.zip") throw new ArgumentException();
        var root = Path.GetFullPath(cacheRoot); Directory.CreateDirectory(root);
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException();
        var directory = Path.Combine(root, release.Version); Directory.CreateDirectory(directory);
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException();
        var archive = Path.Combine(directory, "download.zip");
        RejectRedirects(archive);
        if (!File.Exists(archive) || new FileInfo(archive).Length != release.Bytes || !await HashMatchesAsync(archive, release.Sha256, token))
        {
            var temporary = archive + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using var response = await http.GetAsync(release.Url, HttpCompletionOption.ResponseHeadersRead, token); response.EnsureSuccessStatusCode();
                await using var input = await response.Content.ReadAsStreamAsync(token);
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    var buffer = new byte[65536]; long size = 0; int count; var reportedAt = Stopwatch.StartNew();
                    while ((count = await input.ReadAsync(buffer, token)) != 0)
                    {
                        if ((size += count) > release.Bytes || size > 256 * 1024 * 1024) throw new InvalidDataException();
                        await output.WriteAsync(buffer.AsMemory(0, count), token);
                        if (reportedAt.ElapsedMilliseconds >= 100 || size == release.Bytes) { progress?.Report((double)size / release.Bytes); reportedAt.Restart(); }
                    }
                    if (size != release.Bytes) throw new InvalidDataException();
                }
                if (!await HashMatchesAsync(temporary, release.Sha256, token)) throw new InvalidDataException(L.T("O checksum do download não corresponde à versão publicada."));
                File.Move(temporary, archive, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        var payload = Path.Combine(directory, "payload");
        RejectRedirects(payload);
        if (Directory.Exists(payload))
        {
            await VerifyPayloadAsync(payload, token);
            // A self-consistent cache is insufficient: bind every file to the verified release archive.
            using var cachedZip = ZipFile.OpenRead(archive);
            var verified = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in cachedZip.Entries.Where(e => !e.FullName.EndsWith('/')))
            {
                var parts = entry.FullName.Split('/');
                var name = parts.Length == 1 ? parts[0] : parts.Length == 2 && parts[0] == $"MultiJanela-{release.Version}-win-x64" ? parts[1] : "";
                if (!PayloadFiles.Contains(name) || !verified.Add(name)) throw new InvalidDataException();
                await using var source = entry.Open();
                var expected = Convert.ToHexString(await SHA256.HashDataAsync(source, token));
                if (!await HashMatchesAsync(Path.Combine(payload, name), expected, token)) throw new InvalidDataException();
            }
            if (!verified.SetEquals(PayloadFiles)) throw new InvalidDataException();
            VerifyVersion(payload, release.Version);
            return Path.Combine(payload, "RobloxMultiManager.exe");
        }
        var staging = Path.Combine(directory, "staging-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(staging);
        try
        {
            using var zip = ZipFile.OpenRead(archive);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long total = 0, extracted = 0;
            foreach (var entry in zip.Entries)
            {
                token.ThrowIfCancellationRequested();
                if (entry.FullName.EndsWith('/'))
                { if (entry.FullName != $"MultiJanela-{release.Version}-win-x64/") throw new InvalidDataException(); continue; }
                var parts = entry.FullName.Split('/');
                var name = parts.Length == 1 ? parts[0] : parts.Length == 2 && parts[0] == $"MultiJanela-{release.Version}-win-x64" ? parts[1] : "";
                if (!PayloadFiles.Contains(name) || !names.Add(name) || (total += entry.Length) > 512 * 1024 * 1024) throw new InvalidDataException();
                await using var source = entry.Open(); await using var target = new FileStream(Path.Combine(staging, name), FileMode.CreateNew, FileAccess.Write, FileShare.None);
                var buffer = new byte[65536]; int count;
                while ((count = await source.ReadAsync(buffer, token)) != 0)
                {
                    if ((extracted += count) > 512 * 1024 * 1024) throw new InvalidDataException();
                    await target.WriteAsync(buffer.AsMemory(0, count), token);
                }
            }
            if (!names.SetEquals(PayloadFiles)) throw new InvalidDataException();
            await VerifyPayloadAsync(staging, token);
            VerifyVersion(staging, release.Version);
            Directory.Move(staging, payload);
            return Path.Combine(payload, "RobloxMultiManager.exe");
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }
    public static async Task VerifyPayloadAsync(string directory, CancellationToken token = default)
    {
        RejectRedirects(directory);
        var entries = Directory.GetFileSystemEntries(directory);
        if (entries.Length != PayloadFiles.Count || entries.Any(p => !File.Exists(p) || !PayloadFiles.Contains(Path.GetFileName(p)) || (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0)) throw new InvalidDataException();
        if (entries.Sum(p => new FileInfo(p).Length) > 512 * 1024 * 1024) throw new InvalidDataException();
        var manifest = new Dictionary<string, string>(StringComparer.Ordinal);
        if (new FileInfo(Path.Combine(directory, "SHA256SUMS.txt")).Length > 8192) throw new InvalidDataException();
        foreach (var line in await File.ReadAllLinesAsync(Path.Combine(directory, "SHA256SUMS.txt"), token))
        {
            var match = Regex.Match(line, "^([a-fA-F0-9]{64})  ([^/\\\\]+)$");
            if (!match.Success || !manifest.TryAdd(match.Groups[2].Value, match.Groups[1].Value)) throw new InvalidDataException();
        }
        if (!manifest.Keys.ToHashSet().SetEquals(PayloadFiles.Where(n => n != "SHA256SUMS.txt"))) throw new InvalidDataException();
        foreach (var (name, hash) in manifest) if (!await HashMatchesAsync(Path.Combine(directory, name), hash, token)) throw new InvalidDataException();
        using var executable = SecureJsonFiles.OpenRead(Path.Combine(directory, "RobloxMultiManager.exe"));
        if (executable.ReadByte() != 'M' || executable.ReadByte() != 'Z') throw new InvalidDataException();
    }
    private static void VerifyVersion(string directory, string version)
    {
        if (!Version.TryParse(FileVersionInfo.GetVersionInfo(Path.Combine(directory, "RobloxMultiManager.exe")).FileVersion, out var actual)
            || actual.ToString(3) != version) throw new InvalidDataException();
    }
    internal static void RejectRedirects(string path)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException();
            current = Path.GetDirectoryName(current);
        }
    }
    private static async Task<bool> HashMatchesAsync(string file, string expected, CancellationToken token)
    {
        await using var stream = SecureJsonFiles.OpenRead(file);
        var hash = await SHA256.HashDataAsync(stream, token);
        return Convert.ToHexString(hash).Equals(expected, StringComparison.OrdinalIgnoreCase);
    }
}
