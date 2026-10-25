using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace RobloxMultiManager.Core;

public sealed record BackupInventory(int Files, long Bytes, bool IncludesBrowserProfiles);

/// <summary>Encrypted bounded backups; account-browser portability remains Windows/device dependent.</summary>
public sealed class EncryptedBackupService(AppPaths paths)
{
    private const int MaximumBytes = 256 * 1024 * 1024;
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("MJBK0001");
    private static readonly HashSet<string> Files = ["profiles.json", "settings.json", "ownership.json", "saved-destinations.json",
        "ui-preferences.json", "manager-workspace.json", "game-library.json", "workspaces.json", "appearance.json", "automation.json", "performance.json", "notifications.json", "session-history.json", "local-api.json", "graphics-policy.json", "audio.json", "macros.json", "avatars.json", "scripts.json", "nexus.json"];

    public async Task<BackupInventory> ExportAsync(string target, string password, bool includeBrowsers, CancellationToken token = default)
    {
        ValidatePassword(password);
        var root = Path.GetFullPath(paths.Root);
        VerifyNoRedirect(root);
        var selected = Directory.Exists(root) ? Directory.EnumerateFiles(root).Where(p => Files.Contains(Path.GetFileName(p))).ToList() : [];
        if (includeBrowsers && Directory.Exists(paths.BrowserProfilesRoot))
        {
            // Browser databases can be locked. Fail without producing a partial backup.
            selected.AddRange(Directory.EnumerateFiles(paths.BrowserProfilesRoot, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false })
                .Where(p => !Path.GetRelativePath(paths.BrowserProfilesRoot, p).Split(Path.DirectorySeparatorChar).Any(s => s is "Cache" or "Code Cache" or "GPUCache" or "Crashpad")));
        }
        if (selected.Count == 0) throw new InvalidDataException(L.T("Não há dados salvos para incluir no backup."));
        long total = 0;
        using var archive = new MemoryStream();
        using (var zip = new ZipArchive(archive, ZipArchiveMode.Create, true))
        {
            foreach (var file in selected)
            {
                token.ThrowIfCancellationRequested();
                VerifyNoRedirect(Path.GetDirectoryName(file)!);
                var info = new FileInfo(file);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || (total += info.Length) > MaximumBytes) throw new InvalidDataException();
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (!Allowed(relative)) throw new InvalidDataException();
                var entry = zip.CreateEntry(relative, CompressionLevel.Fastest);
                await using var input = SecureJsonFiles.OpenRead(file);
                await using var output = entry.Open();
                await input.CopyToAsync(output, token);
                if (archive.Length > MaximumBytes) throw new InvalidDataException();
            }
        }
        var plain = archive.ToArray();
        try
        {
            var encrypted = await Task.Run(() => Encrypt(plain, password), token);
            var targetPath = Path.GetFullPath(target);
            if (targetPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && !targetPath.StartsWith(Path.Combine(root, "Backups") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException(L.T("Salve o backup fora dos arquivos de configuração."));
            await WriteAtomicAsync(targetPath, encrypted, token);
            return new(selected.Count, total, includeBrowsers);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
            if (archive.TryGetBuffer(out var buffer)) CryptographicOperations.ZeroMemory(buffer.AsSpan());
        }
    }

    public async Task<BackupInventory> InspectAsync(string file, string password, CancellationToken token = default)
    {
        var plain = await ReadAsync(file, password, token);
        try
        {
            using var zip = new ZipArchive(new MemoryStream(plain), ZipArchiveMode.Read);
            return Inspect(zip);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public async Task RestoreAsync(string file, string password, CancellationToken token = default)
    {
        var plain = await ReadAsync(file, password, token);
        try
        {
            using var zip = new ZipArchive(new MemoryStream(plain), ZipArchiveMode.Read);
            var inventory = Inspect(zip);
            var backupRoot = Path.Combine(paths.Root, "Backups"); Directory.CreateDirectory(backupRoot);
            var safety = Path.Combine(backupRoot, "before-restore-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".mjb");
            await ExportAsync(safety, password, inventory.IncludesBrowserProfiles, token);
            var staging = Path.Combine(paths.Root, "restore-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            try
            {
                // Validate and stage every entry before modifying current data.
                long stagedBytes = 0;
                foreach (var entry in zip.Entries)
                {
                    token.ThrowIfCancellationRequested();
                    var destination = Path.GetFullPath(Path.Combine(staging, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
                    if (!destination.StartsWith(staging + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException();
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    await using var source = entry.Open();
                    await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    var buffer = new byte[65536]; int count;
                    while ((count = await source.ReadAsync(buffer, token)) != 0)
                    {
                        if ((stagedBytes += count) > MaximumBytes) throw new InvalidDataException();
                        await output.WriteAsync(buffer.AsMemory(0, count), token);
                    }
                }
                await ValidateConfigurationAsync(staging, zip.Entries.Select(e => e.FullName));
                // Cancellation is no longer accepted after the validated replacement begins.
                var replaced = new List<(string Destination, string? Original)>();
                try
                {
                    foreach (var entry in zip.Entries)
                    {
                        var relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                        var destination = Path.Combine(paths.Root, relative);
                        var parent = Path.GetDirectoryName(destination)!;
                        VerifyNoRedirect(parent);
                        if (File.Exists(destination) && (File.GetAttributes(destination) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException();
                        Directory.CreateDirectory(parent);
                        string? original = null;
                        if (File.Exists(destination))
                        {
                            original = Path.Combine(staging, "__rollback", relative);
                            Directory.CreateDirectory(Path.GetDirectoryName(original)!);
                            File.Copy(destination, original, false);
                        }
                        var staged = Path.Combine(staging, relative);
                        if (SecureJsonFiles.Managed(destination))
                        {
                            var bytes = await File.ReadAllBytesAsync(staged);
                            var encrypted = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
                            try { await SecureJsonFiles.WriteAllBytesAsync(encrypted, bytes); File.Move(encrypted, destination, true); }
                            finally { CryptographicOperations.ZeroMemory(bytes); if (File.Exists(encrypted)) File.Delete(encrypted); }
                        }
                        else File.Move(staged, destination, true);
                        replaced.Add((destination, original));
                    }
                }
                catch
                {
                    foreach (var item in replaced.AsEnumerable().Reverse())
                        if (item.Original is not null) File.Move(item.Original, item.Destination, true);
                        else File.Delete(item.Destination);
                    throw;
                }
            }
            finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    private static async Task ValidateConfigurationAsync(string staging, IEnumerable<string> entries)
    {
        var options = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var paths = new AppPaths(staging);
        foreach (var name in entries.Where(n => !n.Contains('/')))
        {
            var path = Path.Combine(staging, name);
            if (new FileInfo(path).Length > (name=="manager-workspace.json"?8:2) * 1024 * 1024) throw new InvalidDataException();
            var text = await SecureJsonFiles.ReadAllTextAsync(path);
            using var json = System.Text.Json.JsonDocument.Parse(text);
            var array = name is "profiles.json" or "ownership.json" or "saved-destinations.json" or "session-history.json" or "macros.json" or "avatars.json" or "scripts.json";
            if (json.RootElement.ValueKind != (array ? System.Text.Json.JsonValueKind.Array : System.Text.Json.JsonValueKind.Object)) throw new InvalidDataException();
            switch (name)
            {
                case "profiles.json":
                    var profiles = System.Text.Json.JsonSerializer.Deserialize<AccountProfile[]>(text, options);
                    if (profiles is null || profiles.Length > 2000 || profiles.Any(p => p is null || string.IsNullOrWhiteSpace(p.Name)) || profiles.Select(p => p.Id).Distinct().Count() != profiles.Length) throw new InvalidDataException();
                    break;
                case "audio.json": AudioPreferences.Validate(System.Text.Json.JsonSerializer.Deserialize<AudioPreferences>(text, options) ?? throw new InvalidDataException()); break;
                case "macros.json":
                    var macros = System.Text.Json.JsonSerializer.Deserialize<MacroRecording[]>(text, options) ?? throw new InvalidDataException();
                    if (macros.Length > 200) throw new InvalidDataException(); foreach (var macro in macros) MacroRecording.Validate(macro); break;
                case "avatars.json":
                    var avatars = System.Text.Json.JsonSerializer.Deserialize<AvatarDefinition[]>(text, options) ?? throw new InvalidDataException();
                    if (avatars.Length > 200) throw new InvalidDataException(); foreach (var avatar in avatars) RobloxAccountOperations.ValidateAvatar(avatar); break;
                case "scripts.json":
                    if (json.RootElement.GetArrayLength() > 100) throw new InvalidDataException();
                    foreach (var script in json.RootElement.EnumerateArray())
                        if (!script.TryGetProperty("Id", out var sid) || !Guid.TryParse(sid.GetString(), out _) || !script.TryGetProperty("Name",out var sn) || sn.GetString()?.Length is not (>0 and <=80) || !script.TryGetProperty("Code",out var sc) || sc.GetString()?.Length is not (>=0 and <=65536)) throw new InvalidDataException(); break;
                case "settings.json": if (System.Text.Json.JsonSerializer.Deserialize<AppSettings>(text, options) is null) throw new InvalidDataException(); break;
                case "ownership.json": if (System.Text.Json.JsonSerializer.Deserialize<ProcessOwnershipEntry[]>(text, options) is not { } owners || owners.Any(o => o is null)) throw new InvalidDataException(); break;
                case "game-library.json": await new GameLibraryStore(paths).LoadAsync(); break;
                case "workspaces.json": await new WorkspaceLibraryStore(paths).LoadAsync(); break;
                case "appearance.json": await new AppearancePreferencesStore(paths).LoadAsync(); break;
                case "graphics-policy.json": GraphicsLaunchPolicy.Validate(System.Text.Json.JsonSerializer.Deserialize<GraphicsLaunchPolicy>(text, options) ?? throw new InvalidDataException()); break;
                case "automation.json": AutomationPreferences.Validate(System.Text.Json.JsonSerializer.Deserialize<AutomationPreferences>(text, options) ?? throw new InvalidDataException()); break;
                case "session-history.json": await new SessionHistoryStore(paths).LoadAsync(); break;
                case "local-api.json": LocalApiPreferences.Validate(System.Text.Json.JsonSerializer.Deserialize<LocalApiPreferences>(text, options) ?? throw new InvalidDataException()); break;
            }
        }
    }
    private static BackupInventory Inspect(ZipArchive zip)
    {
        if (zip.Entries.Count is < 1 or > 10000) throw new InvalidDataException();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long size = 0;
        foreach (var entry in zip.Entries)
        {
            if (!Allowed(entry.FullName) || !names.Add(entry.FullName) || entry.Length > MaximumBytes || (size += entry.Length) > MaximumBytes) throw new InvalidDataException();
        }
        return new(zip.Entries.Count, size, names.Any(n => n.StartsWith("WebViewProfiles/", StringComparison.Ordinal)));
    }
    private static bool Allowed(string name)
    {
        if (name.Contains('\\') || name.Contains(':') || name.StartsWith('/') || name.Split('/').Any(s => s is "" or "." or "..")) return false;
        var segments = name.Split('/');
        if (segments.Any(s => s.EndsWith('.') || s.EndsWith(' ') || s.Any(char.IsControl)
            || new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }.Contains(s.Split('.')[0], StringComparer.OrdinalIgnoreCase))) return false;
        return segments.Length == 1 ? Files.Contains(name)
            : segments.Length >= 3 && segments[0] == "WebViewProfiles" && Guid.TryParseExact(segments[1], "N", out _);
    }
    private void VerifyNoRedirect(string directory)
    {
        var root = Path.GetFullPath(paths.Root);
        var current = new DirectoryInfo(directory);
        while (current.FullName.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException();
            if (current.FullName.Equals(root, StringComparison.OrdinalIgnoreCase)) break;
            current = current.Parent ?? throw new InvalidDataException();
        }
    }
    private static void ValidatePassword(string password)
    { if (password.Length is < 12 or > 256) throw new ArgumentException(L.T("Use uma senha de backup entre 12 e 256 caracteres.")); }
    private static byte[] Encrypt(byte[] plain, string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16); var nonce = RandomNumberGenerator.GetBytes(12);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, 600000, HashAlgorithmName.SHA256, 32);
        try
        {
            var result = new byte[Magic.Length + 16 + 12 + 16 + plain.Length]; Magic.CopyTo(result, 0); salt.CopyTo(result, 8); nonce.CopyTo(result, 24);
            using var cipher = new AesGcm(key, 16); cipher.Encrypt(nonce, plain, result.AsSpan(52), result.AsSpan(36, 16), Magic);
            return result;
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }
    private static async Task<byte[]> ReadAsync(string file, string password, CancellationToken token)
    {
        ValidatePassword(password);
        if (new FileInfo(file).Length is < 52 or > MaximumBytes + 52) throw new InvalidDataException();
        var encrypted = await SecureJsonFiles.ReadAllBytesAsync(file, token);
        if (!encrypted.AsSpan(0, 8).SequenceEqual(Magic)) throw new InvalidDataException();
        return await Task.Run(() =>
        {
            var key = Rfc2898DeriveBytes.Pbkdf2(password, encrypted.AsSpan(8, 16), 600000, HashAlgorithmName.SHA256, 32);
            try
            {
                var plain = new byte[encrypted.Length - 52];
                using var cipher = new AesGcm(key, 16);
                try { cipher.Decrypt(encrypted.AsSpan(24, 12), encrypted.AsSpan(52), encrypted.AsSpan(36, 16), plain, Magic); return plain; }
                catch { CryptographicOperations.ZeroMemory(plain); throw; }
            }
            finally { CryptographicOperations.ZeroMemory(key); }
        }, token);
    }
    private static async Task WriteAtomicAsync(string target, byte[] content, CancellationToken token)
    {
        ManagerUpdateService.RejectRedirects(Path.GetDirectoryName(Path.GetFullPath(target))!);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target))!);
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await SecureJsonFiles.WriteAllBytesAsync(temporary, content, token); File.Move(temporary, target, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
