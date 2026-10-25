using System.Text.RegularExpressions;

namespace RobloxMultiManager.Core;

public sealed class RobloxAuthenticationFailureReader(string? robloxRoot = null)
{
    private readonly string _logs = Path.Combine(robloxRoot ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roblox"), "logs");
    private static readonly Regex Status = new(@"\bstatus\s*:\s*(?<code>[1-5]\d{2})\b", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));

    public bool WasRejected(DateTime processStartUtc)
    {
        if (processStartUtc == default || !Directory.Exists(_logs)) return false;
        try
        {
            // No attribution when concurrent client logs overlap this startup.
            var files = new DirectoryInfo(_logs).EnumerateFiles("*_last.log")
                .Where(f => !f.Name.EndsWith("_CrashHandler_last.log", StringComparison.OrdinalIgnoreCase)
                    && f.CreationTimeUtc >= processStartUtc.AddMilliseconds(-250)
                    && f.CreationTimeUtc <= processStartUtc.AddSeconds(2)).Take(2).ToArray();
            if (files.Length != 1) return false;
            using var stream = new FileStream(files[0].FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            const int maxBytes = 256 * 1024;
            if (stream.Length > maxBytes) stream.Seek(-maxBytes, SeekOrigin.End);
            using var reader = new StreamReader(stream);
            var rejected = false;
            while (reader.ReadLine() is { } line)
            {
                if (!line.Contains("https://auth.roblox.com/v1/authentication-ticket/redeem", StringComparison.OrdinalIgnoreCase)) continue;
                var status = Status.Match(line);
                if (status.Success) rejected = status.Groups["code"].Value == "403";
            }
            return rejected;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or RegexMatchTimeoutException) { }
        return false;
    }

}
