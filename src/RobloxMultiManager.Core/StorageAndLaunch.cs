using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace RobloxMultiManager.Core;

public sealed class AppPaths
{
    public AppPaths(string? rootOverride = null)
    {
        Root = rootOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RobloxMultiManager");
        ProfilesFile = Path.Combine(Root, "profiles.json");
        SettingsFile = Path.Combine(Root, "settings.json");
        OwnershipFile = Path.Combine(Root, "ownership.json");
        BrowserProfilesRoot = Path.Combine(Root, "WebViewProfiles");
        LogsRoot = Path.Combine(Root, "Logs");
    }

    public string Root { get; }
    public string ProfilesFile { get; }
    public string SettingsFile { get; }
    public string OwnershipFile { get; }
    public string BrowserProfilesRoot { get; }
    public string LogsRoot { get; }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(BrowserProfilesRoot);
        Directory.CreateDirectory(LogsRoot);
    }

    public string BrowserProfilePath(AccountProfile profile) =>
        Path.Combine(BrowserProfilesRoot, profile.Id.ToString("N"));
}

public sealed class LocalStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly AppPaths _paths;

    public LocalStore(AppPaths paths)
    {
        _paths = paths;
        _paths.EnsureCreated();
    }

    public async Task<IReadOnlyList<AccountProfile>> LoadProfilesAsync()
    {
        if (!File.Exists(_paths.ProfilesFile))
        {
            var defaults = Enumerable.Range(1, 5)
                .Select(i => AccountProfile.Create(RobloxMultiManager.Core.L.F("Conta {0}",i), "https://www.roblox.com/home"))
                .ToArray();
            await SaveProfilesAsync(defaults).ConfigureAwait(false);
            return defaults;
        }

        try
        {
            await using var stream = SecureJsonFiles.OpenRead(_paths.ProfilesFile);
            return await JsonSerializer.DeserializeAsync<List<AccountProfile>>(stream, JsonOptions)
                       .ConfigureAwait(false)
                   ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public Task SaveProfilesAsync(IEnumerable<AccountProfile> profiles) =>
        SaveAtomicAsync(_paths.ProfilesFile, profiles.ToArray());

    public async Task<AppSettings> LoadSettingsAsync()
    {
        if (!File.Exists(_paths.SettingsFile))
        {
            var defaults = new AppSettings();
            await SaveSettingsAsync(defaults).ConfigureAwait(false);
            return defaults;
        }

        try
        {
            await using var stream = SecureJsonFiles.OpenRead(_paths.SettingsFile);
            return await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonOptions)
                       .ConfigureAwait(false)
                   ?? new AppSettings();
        }
        catch (JsonException)
        {
            return new AppSettings();
        }
    }

    public Task SaveSettingsAsync(AppSettings settings) =>
        SaveAtomicAsync(_paths.SettingsFile, settings);

    /// <summary>
    /// Reads the persisted process-to-profile attribution.
    /// </summary>
    /// <remarks>
    /// A missing or corrupt file is not an error: attribution is a convenience,
    /// and losing it must never stop the app from starting.
    /// </remarks>
    public async Task<IReadOnlyList<ProcessOwnershipEntry>> LoadOwnershipAsync()
    {
        if (!File.Exists(_paths.OwnershipFile))
        {
            return [];
        }

        try
        {
            await using var stream = SecureJsonFiles.OpenRead(_paths.OwnershipFile);
            return await JsonSerializer
                       .DeserializeAsync<List<ProcessOwnershipEntry>>(
                           stream,
                           JsonOptions)
                       .ConfigureAwait(false)
                   ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
        catch (IOException)
        {
            return [];
        }
    }

    public Task SaveOwnershipAsync(IEnumerable<ProcessOwnershipEntry> entries) =>
        SaveAtomicAsync(_paths.OwnershipFile, entries.ToArray());

    private static async Task SaveAtomicAsync<T>(string path, T value)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try{await SecureJsonFiles.WriteAllTextAsync(temp,JsonSerializer.Serialize(value,JsonOptions)).ConfigureAwait(false);File.Move(temp,path,true);}
        finally{if(File.Exists(temp))File.Delete(temp);}
    }
}

public sealed class RobloxInstallationLocator
{
    private static readonly Regex QuotedCommand =
        new("^\\s*\"(?<path>[^\"]+\\.exe)\"", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex VersionIdPattern =
        new("^version-[a-f0-9]{16}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly string _versionsRoot;
    private readonly string? _managedRoot;
    private readonly Func<string?> _protocolCommandProvider;
    private readonly Func<string?> _environmentVersionProvider;
    private readonly Func<string?> _channelProvider;
    private readonly Func<string, string?, (string? Publisher, bool AppearsSigned)>
        _signatureVerifier;

    public RobloxInstallationLocator(
        string? versionsRootOverride = null,
        Func<string?>? protocolCommandProvider = null,
        Func<string?>? environmentVersionProvider = null,
        Func<string?>? channelProvider = null,
        Func<string, string?, (string? Publisher, bool AppearsSigned)>?
            signatureVerifier = null,
        string? managedRootOverride = null)
    {
        _versionsRoot = Path.GetFullPath(versionsRootOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Roblox",
            "Versions"));
        // An explicit official root means a caller is pointing the locator at a
        // fixture, so the real app-owned root must stay out of the results unless
        // that caller asked for one too.
        _managedRoot = managedRootOverride is not null
            ? Path.GetFullPath(managedRootOverride)
            : versionsRootOverride is null
                ? Path.GetFullPath(ManagedVersionsRoot)
                : null;
        _protocolCommandProvider = protocolCommandProvider ?? ReadProtocolCommand;
        _environmentVersionProvider =
            environmentVersionProvider ?? ReadEnvironmentVersion;
        _channelProvider = channelProvider ?? ReadCurrentChannel;
        _signatureVerifier = signatureVerifier ?? VerifyRobloxSignature;
    }

    /// <summary>
    /// Root for versions this app downloaded itself.  Kept out of
    /// <c>%LOCALAPPDATA%\Roblox\Versions</c> so Roblox's own updater cannot
    /// delete or replace a version the user pinned on purpose.
    /// </summary>
    public static string ManagedVersionsRoot =>
        RobloxDeploymentDownloader.DefaultVersionsRoot;

    public RobloxInstallationInfo Locate() => Resolve(null);

    public RobloxInstallationInfo Resolve(string? selectedVersionId) => Inspect(selectedVersionId).Effective;

    // A single discovery/signature pass supplies both the effective client and
    // the browser/report rows. Callers can run this I/O snapshot off the UI thread.
    public (RobloxInstallationInfo Effective, IReadOnlyList<RobloxInstalledVersionInfo> Installed) Inspect(string? selectedVersionId)
    {
        var protocol = _protocolCommandProvider();
        var environmentVersion = _environmentVersionProvider();
        var channel = NormalizeChannel(_channelProvider());
        var installed = DiscoverInstalled(protocol, environmentVersion);

        return (ResolveSnapshot(selectedVersionId, protocol, channel, installed), installed);
    }

    private RobloxInstallationInfo ResolveSnapshot(string? selectedVersionId, string? protocol,
        string channel, IReadOnlyList<RobloxInstalledVersionInfo> installed)
    {

        if (!string.IsNullOrWhiteSpace(selectedVersionId))
        {
            if (!IsValidVersionId(selectedVersionId))
            {
                return NotFound(
                    protocol,
                    RobloxMultiManager.Core.L.T("Seleção de versão inválida"),
                    selectedVersionId,
                    channel);
            }

            var selected = installed.FirstOrDefault(item =>
                item.VersionId.Equals(
                    selectedVersionId,
                    StringComparison.OrdinalIgnoreCase));
            return selected is null
                ? NotFound(
                    protocol,
                    RobloxMultiManager.Core.L.T("Versão selecionada não está instalada"),
                    selectedVersionId,
                    channel)
                : !selected.AppearsSigned
                    ? NotFound(
                        protocol,
                        RobloxMultiManager.Core.L.T("A assinatura Roblox da versão selecionada não é válida"),
                        selectedVersionId,
                        channel)
                : ToInstallation(
                    selected,
                    protocol,
                    RobloxMultiManager.Core.L.T("Versão selecionada"),
                    channel);
        }

        var trusted = installed.Where(item => item.AppearsSigned).ToArray();
        var automatic = trusted.FirstOrDefault(item => item.IsProtocolTarget)
                        ?? trusted.FirstOrDefault(item => item.IsEnvironmentCurrent)
                        ?? trusted.FirstOrDefault();
        if (automatic is not null)
        {
            var source = automatic.IsProtocolTarget
                ? RobloxMultiManager.Core.L.T("Protocolo roblox-player")
                : automatic.IsEnvironmentCurrent
                    ? RobloxMultiManager.Core.L.T("Ambiente Roblox")
                    : RobloxMultiManager.Core.L.T("Pasta Versions");
            return ToInstallation(automatic, protocol, source, channel);
        }

        return NotFound(protocol, RobloxMultiManager.Core.L.T("Não encontrado"), null, channel);
    }

    public IReadOnlyList<RobloxInstalledVersionInfo> DiscoverInstalled()
    {
        var protocol = _protocolCommandProvider();
        var environmentVersion = _environmentVersionProvider();
        return DiscoverInstalled(protocol, environmentVersion);
    }

    private IReadOnlyList<RobloxInstalledVersionInfo> DiscoverInstalled(
        string? protocol,
        string? environmentVersion)
    {
        var protocolPath = CanonicalizeExistingPath(ExtractExecutablePath(protocol));
        var runningPaths = Process.GetProcessesByName("RobloxPlayerBeta")
            .Select(TryGetProcessPath)
            .Where(path => path is not null)
            .Select(CanonicalizeExistingPath)
            .Where(path => path is not null)
            .Select(path => path!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new List<RobloxInstalledVersionInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // The official root is scanned first so that a version present in both
        // roots is reported at the location Roblox itself maintains.
        ScanRoot(
            _versionsRoot,
            isManaged: false,
            protocolPath,
            environmentVersion,
            runningPaths,
            seen,
            result);
        if (_managedRoot is not null
            && !_managedRoot.Equals(
                _versionsRoot,
                StringComparison.OrdinalIgnoreCase))
        {
            ScanRoot(
                _managedRoot,
                isManaged: true,
                protocolPath,
                environmentVersion,
                runningPaths,
                seen,
                result);
        }

        return result
            // Ordered by the client version, newest first.  Ordering by the file
            // timestamp instead placed a freshly written older build above a newer
            // one, which is what pushed the version being looked for out of view.
            .OrderByDescending(item =>
                RobloxVersionChecker.TryParseClientVersion(
                    item.FileVersion,
                    out var parsed)
                    ? parsed
                    : null)
            .ThenByDescending(item => item.LastWriteTimeUtc)
            .ThenBy(item => item.VersionId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void ScanRoot(
        string root,
        bool isManaged,
        string? protocolPath,
        string? environmentVersion,
        HashSet<string> runningPaths,
        HashSet<string> seen,
        List<RobloxInstalledVersionInfo> result)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        try
        {
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            {
                return;
            }
        }
        catch (IOException)
        {
            return;
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        try
        {
            foreach (var directoryPath in Directory.EnumerateDirectories(
                         root,
                         "version-*",
                         SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var directory = new DirectoryInfo(directoryPath);
                    if ((directory.Attributes & FileAttributes.ReparsePoint) != 0
                        || !IsValidVersionId(directory.Name)
                        || seen.Contains(directory.Name))
                    {
                        continue;
                    }

                    var executablePath = Path.GetFullPath(Path.Combine(
                        directory.FullName,
                        "RobloxPlayerBeta.exe"));
                    if (!IsPathInsideRoot(executablePath, root)
                        || !File.Exists(executablePath)
                        || (File.GetAttributes(executablePath)
                            & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }

                    var file = new FileInfo(executablePath);
                    var version = FileVersionInfo.GetVersionInfo(executablePath);
                    var (publisher, appearsSigned) =
                        _signatureVerifier(executablePath, version.CompanyName);
                    seen.Add(directory.Name);
                    result.Add(new RobloxInstalledVersionInfo(
                        directory.Name,
                        executablePath,
                        NormalizeFileVersion(version.FileVersion),
                        file.LastWriteTimeUtc,
                        publisher,
                        appearsSigned,
                        protocolPath is not null
                        && executablePath.Equals(
                            protocolPath,
                            StringComparison.OrdinalIgnoreCase),
                        directory.Name.Equals(
                            environmentVersion,
                            StringComparison.OrdinalIgnoreCase),
                        runningPaths.Contains(executablePath),
                        isManaged));
                }
                catch (IOException)
                {
                    // The updater may replace one candidate while the catalog is read.
                }
                catch (UnauthorizedAccessException)
                {
                    // Keep every other readable official candidate.
                }
                catch (System.Security.Cryptography.CryptographicException)
                {
                    // Keep every other readable official candidate.
                }
            }
        }
        catch (IOException)
        {
            // A root that cannot be enumerated contributes nothing; the other one
            // must still be reported.
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }
    }

    public string GetCurrentChannel() => NormalizeChannel(_channelProvider());

    public static bool IsValidVersionId(string? value) =>
        value is not null && VersionIdPattern.IsMatch(value);

    private RobloxInstallationInfo ToInstallation(
        RobloxInstalledVersionInfo installed,
        string? protocol,
        string source,
        string channel) =>
        new(
            installed.ExecutablePath,
            RedactProtocolCommand(protocol),
            installed.FileVersion,
            installed.Publisher,
            installed.AppearsSigned,
            source,
            installed.VersionId,
            channel);

    private static RobloxInstallationInfo NotFound(
        string? protocol,
        string source,
        string? selectedVersionId,
        string channel) =>
        new(
            null,
            RedactProtocolCommand(protocol),
            null,
            null,
            false,
            source,
            selectedVersionId,
            channel);

    private static bool IsPathInsideRoot(string path, string root)
    {
        var rootWithSeparator = root.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(
            rootWithSeparator,
            StringComparison.OrdinalIgnoreCase);
    }

    internal static (string? Publisher, bool AppearsSigned) VerifyRobloxSignature(
        string path,
        string? fallbackPublisher)
    {
        try
        {
            using var certificate = new X509Certificate2(
                X509Certificate.CreateFromSignedFile(path));
            var publisher = certificate.GetNameInfo(
                X509NameType.SimpleName,
                false);
            return (
                publisher,
                publisher.Equals(
                    "Roblox Corporation",
                    StringComparison.OrdinalIgnoreCase)
                && AuthenticodeVerifier.IsTrusted(path));
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return (fallbackPublisher, false);
        }
    }

    private static string? CanonicalizeExistingPath(string? path)
    {
        try
        {
            return path is not null && File.Exists(path)
                ? Path.GetFullPath(path)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? NormalizeFileVersion(string? value) =>
        value?.Replace(", ", ".", StringComparison.Ordinal).Trim();

    private static string NormalizeChannel(string? value)
    {
        try
        {
            return RobloxVersionChecker.NormalizeChannel(value);
        }
        catch (ArgumentException)
        {
            return "LIVE";
        }
    }

    private static string? ReadProtocolCommand()
    {
        foreach (var root in new[] { Registry.CurrentUser, Registry.ClassesRoot })
        {
            try
            {
                using var key = root.OpenSubKey(@"Software\Classes\roblox-player\shell\open\command")
                                ?? root.OpenSubKey(@"roblox-player\shell\open\command");
                if (key?.GetValue(null) is string value && !string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
            catch (UnauthorizedAccessException)
            {
                // Continue to filesystem discovery.
            }
        }

        return null;
    }

    private static string? ReadEnvironmentVersion()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\ROBLOX Corporation\Environments\roblox-player");
            return key?.GetValue("version") as string;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? ReadCurrentChannel()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\ROBLOX Corporation\Environments\RobloxPlayer\Channel");
            return key?.GetValue("www.roblox.com") as string;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? ExtractExecutablePath(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        var match = QuotedCommand.Match(command);
        if (match.Success)
        {
            return Environment.ExpandEnvironmentVariables(match.Groups["path"].Value);
        }

        var exeIndex = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exeIndex < 0)
        {
            return null;
        }

        return Environment.ExpandEnvironmentVariables(command[..(exeIndex + 4)].Trim());
    }

    private static string? RedactProtocolCommand(string? command)
    {
        var path = ExtractExecutablePath(command);
        return path is null ? null : $"\"{path}\" %1";
    }

    private static string? TryGetProcessPath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch
        {
            return null;
        }
        finally
        {
            process.Dispose();
        }
    }
}

public sealed class BrowserProfileLauncher
{
    private readonly AppPaths _paths;
    private readonly string? _edgePath;

    public BrowserProfileLauncher(AppPaths paths)
    {
        _paths = paths;
        _edgePath = LocateEdge();
    }

    public bool IsAvailable => _edgePath is not null;
    public string? EdgePath => _edgePath;

    public Process LaunchLogin(AccountProfile profile) =>
        Launch(profile, "https://www.roblox.com/login");

    public Process LaunchGame(AccountProfile profile)
    {
        var url = NormalizeRobloxUrl(profile.GameUrl);
        return Launch(profile, BuildOfficialLaunchUrl(url));
    }

    public Process LaunchHome(AccountProfile profile) =>
        Launch(profile, NormalizeRobloxUrl(profile.GameUrl));

    private Process Launch(AccountProfile profile, string url)
    {
        if (_edgePath is null)
        {
            throw new InvalidOperationException(
                RobloxMultiManager.Core.L.T("Microsoft Edge não foi encontrado. Instale ou repare o Edge."));
        }

        var profilePath = _paths.BrowserProfilePath(profile);
        Directory.CreateDirectory(profilePath);

        var start = new ProcessStartInfo
        {
            FileName = _edgePath,
            UseShellExecute = false
        };
        start.ArgumentList.Add($"--user-data-dir={profilePath}");
        start.ArgumentList.Add("--no-first-run");
        start.ArgumentList.Add("--new-window");
        start.ArgumentList.Add(url);

        return Process.Start(start)
               ?? throw new InvalidOperationException(RobloxMultiManager.Core.L.T("O Edge não iniciou."));
    }

    public static string NormalizeRobloxUrl(string value)
    {
        var text = value.Trim();
        if (long.TryParse(text, out var placeId) && placeId > 0)
        {
            return $"https://www.roblox.com/games/{placeId}";
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !(uri.Host.Equals("roblox.com", StringComparison.OrdinalIgnoreCase)
                 || uri.Host.EndsWith(".roblox.com", StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException(
                RobloxMultiManager.Core.L.T("Use um PlaceId ou uma URL HTTPS oficial do domínio roblox.com."));
        }

        return uri.ToString();
    }

    public static string BuildOfficialLaunchUrl(string url)
    {
        var uri = new Uri(url);
        var match = Regex.Match(
            uri.AbsolutePath,
            "^/games/(?<id>\\d+)",
            RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return url;
        }

        return $"https://www.roblox.com/games/start?placeId={match.Groups["id"].Value}";
    }

    private static string? LocateEdge()
    {
        var candidates = new[]
        {
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "Edge", "Application", "msedge.exe")
        };

        return candidates.FirstOrDefault(File.Exists);
    }
}
