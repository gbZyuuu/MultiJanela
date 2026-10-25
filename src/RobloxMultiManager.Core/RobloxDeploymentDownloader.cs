using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace RobloxMultiManager.Core;

/// <summary>One package record of an <c>rbxPkgManifest</c>.</summary>
/// <remarks>
/// Field order was confirmed by download: name, MD5 hex, size of the downloaded
/// blob, then the sum of the uncompressed lengths of the archive's entries.  The
/// third field equals the blob's <c>Content-Length</c> exactly, so it is the one
/// to use for progress and validation; the fourth is only good for a disk-space
/// estimate.
/// </remarks>
public sealed record RobloxPackageRecord(
    string Name,
    string Md5,
    long CompressedSize,
    long UncompressedSize)
{
    /// <summary>
    /// Whether this package is a client archive that must be extracted.
    /// </summary>
    /// <remarks>
    /// A manifest also lists <c>RobloxPlayerInstaller.exe</c>, which is the
    /// bootstrapper rather than client content: it is not an archive and its
    /// compressed and uncompressed sizes are equal.  It is not part of a version
    /// folder and is deliberately not downloaded.
    /// </remarks>
    public bool IsArchive =>
        Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
}

public sealed record RobloxDeploymentManifest(
    string VersionId,
    string Channel,
    IReadOnlyList<RobloxPackageRecord> Packages)
{
    public long DownloadSize =>
        Packages.Where(p => p.IsArchive).Sum(p => p.CompressedSize);

    public long InstalledSize =>
        Packages.Where(p => p.IsArchive).Sum(p => p.UncompressedSize);
}

public sealed record RobloxDownloadProgress(
    string Stage,
    int CompletedPackages,
    int TotalPackages,
    long ReceivedBytes,
    long TotalBytes)
{
    public double Fraction => TotalBytes <= 0
        ? 0
        : Math.Clamp((double)ReceivedBytes / TotalBytes, 0, 1);

    public string SizeText =>
        $"{ToMegabytes(ReceivedBytes)} / {ToMegabytes(TotalBytes)} MB";

    private static string ToMegabytes(long value) =>
        (value / 1024d / 1024d).ToString("0.0", CultureInfo.InvariantCulture);
}

public sealed class RobloxDeploymentDownloadException : Exception
{
    public RobloxDeploymentDownloadException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Downloads a Roblox deployment straight from Roblox's own CDN and extracts it
/// into a folder this app owns.
/// </summary>
/// <remarks>
/// <para>
/// The layout is Roblox's own: a manifest at
/// <c>{base}{versionId}-rbxPkgManifest.txt</c> lists packages, and each package
/// is a sibling blob at <c>{base}{versionId}-{package}</c>.  For the LIVE channel
/// the base is the CDN root; for any other channel it is
/// <c>channel/{lowercase}/</c>, and the two are not interchangeable — a LIVE
/// identifier under <c>channel/</c> and a non-LIVE identifier at the root both
/// return 403.  That bucket answers 403, not 404, for a key that does not exist.
/// </para>
/// <para>
/// Extraction targets a root under this app's own local data rather than
/// <c>%LOCALAPPDATA%\Roblox\Versions</c>, so Roblox's updater cannot delete or
/// replace a version the user pinned deliberately.  The Authenticode requirement
/// is unchanged: the extracted <c>RobloxPlayerBeta.exe</c> must still be trusted
/// and published by Roblox Corporation, and the download is discarded if it is
/// not.  The manifest MD5 is Roblox's own integrity field and is checked for
/// exactly that — it is not the security control and must not be treated as one.
/// </para>
/// </remarks>
public sealed class RobloxDeploymentDownloader
{
    private const string CdnRoot = "https://setup.rbxcdn.com/";
    private const string ClientExecutableName = "RobloxPlayerBeta.exe";
    private const string StagingFolderName = ".staging";

    /// <summary>
    /// Roblox writes this file into every version folder and the client reads it
    /// to locate its content.  Measured byte-identical across every installed
    /// version on disk: 144 bytes, UTF-8 with no BOM, LF endings, tab indent.
    /// </summary>
    private const string AppSettingsContent =
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n"
        + "<Settings>\n"
        + "\t<ContentFolder>content</ContentFolder>\n"
        + "\t<BaseUrl>http://www.roblox.com</BaseUrl>\n"
        + "</Settings>\n";

    /// <summary>
    /// Where each package's contents belong, relative to the version folder.
    /// An empty value means the version folder itself.
    /// </summary>
    private static readonly Dictionary<string, string> PackageDirectories =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["RobloxApp.zip"] = "",
            ["Libraries.zip"] = "",
            ["redist.zip"] = "",
            ["WebView2.zip"] = "",
            ["shaders.zip"] = "shaders",
            ["ssl.zip"] = "ssl",
            ["WebView2RuntimeInstaller.zip"] = "WebView2RuntimeInstaller",
            ["content-avatar.zip"] = @"content\avatar",
            ["content-configs.zip"] = @"content\configs",
            ["content-fonts.zip"] = @"content\fonts",
            ["content-models.zip"] = @"content\models",
            ["content-sky.zip"] = @"content\sky",
            ["content-sounds.zip"] = @"content\sounds",
            ["content-textures2.zip"] = @"content\textures",
            ["content-textures3.zip"] = @"PlatformContent\pc\textures",
            ["content-terrain.zip"] = @"PlatformContent\pc\terrain",
            ["content-platform-fonts.zip"] = @"PlatformContent\pc\fonts",
            ["content-platform-dictionaries.zip"] =
                @"PlatformContent\pc\shared_compression_dictionaries",
            ["extracontent-luapackages.zip"] = @"ExtraContent\LuaPackages",
            ["extracontent-translations.zip"] = @"ExtraContent\translations",
            ["extracontent-models.zip"] = @"ExtraContent\models",
            ["extracontent-textures.zip"] = @"ExtraContent\textures",
            ["extracontent-places.zip"] = @"ExtraContent\places"
        };

    private readonly HttpClient _httpClient;

    public RobloxDeploymentDownloader(
        HttpClient httpClient,
        string? versionsRootOverride = null)
    {
        _httpClient = httpClient;
        VersionsRoot = Path.GetFullPath(
            versionsRootOverride ?? DefaultVersionsRoot);
    }

    /// <summary>
    /// The app-owned root for downloaded versions.
    /// </summary>
    public static string DefaultVersionsRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RobloxMultiManager",
        "Versions");

    public string VersionsRoot { get; }

    public string GetVersionDirectory(string versionId)
    {
        if (!RobloxInstallationLocator.IsValidVersionId(versionId))
        {
            throw new ArgumentException(
                RobloxMultiManager.Core.L.T("Identificador de deployment inválido."),
                nameof(versionId));
        }

        return Path.Combine(VersionsRoot, versionId);
    }

    public string GetClientExecutablePath(string versionId) =>
        Path.Combine(GetVersionDirectory(versionId), ClientExecutableName);

    /// <summary>
    /// Normalizes a user-supplied deployment identifier, or returns
    /// <see langword="null"/> when it cannot be one.
    /// </summary>
    /// <remarks>
    /// The bare 16-hex form is accepted because that is frequently what ends up
    /// on the clipboard when a hash is copied out of a listing.  Anything that
    /// does not end up matching the strict identifier pattern is rejected, which
    /// is what keeps the value safe to interpolate into a CDN path.
    /// </remarks>
    public static string? NormalizeVersionId(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        if (text.Length == 16
            && !text.StartsWith("version-", StringComparison.OrdinalIgnoreCase))
        {
            text = "version-" + text;
        }

        return RobloxInstallationLocator.IsValidVersionId(text)
            ? text.ToLowerInvariant()
            : null;
    }

    public bool IsDownloaded(string versionId) =>
        RobloxInstallationLocator.IsValidVersionId(versionId)
        && File.Exists(GetClientExecutablePath(versionId));

    public async Task<RobloxDeploymentManifest> GetManifestAsync(
        string versionId,
        string? channel,
        CancellationToken cancellationToken = default)
    {
        var normalizedChannel = RobloxVersionChecker.NormalizeChannel(channel);
        if (!RobloxInstallationLocator.IsValidVersionId(versionId))
        {
            throw new RobloxDeploymentDownloadException(
                RobloxMultiManager.Core.L.T("Informe um hash no formato version- seguido de 16 caracteres ")
                + RobloxMultiManager.Core.L.T("hexadecimais."));
        }

        var url = BuildBaseUrl(normalizedChannel)
                  + versionId
                  + "-rbxPkgManifest.txt";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("RobloxMultiManager/1.5.0");
        using var response = await _httpClient.SendAsync(
            request,
            timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new RobloxDeploymentDownloadException(
                DescribeManifestFailure(
                    response.StatusCode,
                    versionId,
                    normalizedChannel));
        }

        var text = await response.Content
            .ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        return new RobloxDeploymentManifest(
            versionId,
            normalizedChannel,
            ParseManifest(text));
    }

    /// <summary>
    /// Downloads and extracts a deployment, returning the client path.
    /// </summary>
    public async Task<string> DownloadAsync(
        string versionId,
        string? channel,
        IProgress<RobloxDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var manifest = await GetManifestAsync(
            versionId,
            channel,
            cancellationToken).ConfigureAwait(false);
        var packages = manifest.Packages.Where(p => p.IsArchive).ToArray();
        if (packages.Length == 0)
        {
            throw new RobloxDeploymentDownloadException(
                RobloxMultiManager.Core.L.T("O manifesto deste deployment não lista nenhum pacote do ")
                + RobloxMultiManager.Core.L.T("cliente."));
        }

        // Fail before touching the network if a package has appeared that this
        // build does not know where to place.  Extracting it to the wrong folder
        // would produce a client that starts and then misbehaves, which is a far
        // worse outcome than refusing with the package name.
        var unknown = packages
            .Where(p => !PackageDirectories.ContainsKey(p.Name))
            .Select(p => p.Name)
            .ToArray();
        if (unknown.Length > 0)
        {
            throw new RobloxDeploymentDownloadException(
                RobloxMultiManager.Core.L.F("O deployment usa pacotes desconhecidos ({0}). ",string.Join(", ", unknown))
                + RobloxMultiManager.Core.L.T("Atualize o app antes de baixar esta versão."));
        }

        Directory.CreateDirectory(VersionsRoot);
        var staging = Path.Combine(
            VersionsRoot,
            StagingFolderName,
            versionId + "-" + Guid.NewGuid().ToString("N"));
        var target = GetVersionDirectory(versionId);
        var baseUrl = BuildBaseUrl(manifest.Channel);
        var totalBytes = packages.Sum(p => p.CompressedSize);
        var receivedBytes = 0L;

        try
        {
            Directory.CreateDirectory(staging);
            for (var index = 0; index < packages.Length; index++)
            {
                var package = packages[index];
                progress?.Report(new RobloxDownloadProgress(
                    RobloxMultiManager.Core.L.F("Baixando {0}",package.Name),
                    index,
                    packages.Length,
                    receivedBytes,
                    totalBytes));

                var blobPath = Path.Combine(staging, package.Name);
                var alreadyReceived = receivedBytes;
                await DownloadPackageAsync(
                    baseUrl + versionId + "-" + package.Name,
                    blobPath,
                    package,
                    bytes => progress?.Report(new RobloxDownloadProgress(
                        RobloxMultiManager.Core.L.F("Baixando {0}",package.Name),
                        index,
                        packages.Length,
                        alreadyReceived + bytes,
                        totalBytes)),
                    cancellationToken).ConfigureAwait(false);
                receivedBytes = alreadyReceived + package.CompressedSize;

                progress?.Report(new RobloxDownloadProgress(
                    RobloxMultiManager.Core.L.F("Extraindo {0}",package.Name),
                    index,
                    packages.Length,
                    receivedBytes,
                    totalBytes));
                ExtractPackage(
                    blobPath,
                    staging,
                    PackageDirectories[package.Name],
                    cancellationToken);
                File.Delete(blobPath);
            }

            progress?.Report(new RobloxDownloadProgress(
                RobloxMultiManager.Core.L.T("Finalizando instalação"),
                packages.Length,
                packages.Length,
                totalBytes,
                totalBytes));

            await SecureJsonFiles.WriteAllTextAsync(
                Path.Combine(staging, "AppSettings.xml"),
                AppSettingsContent,
                new UTF8Encoding(false),
                cancellationToken).ConfigureAwait(false);

            VerifyStagedClient(Path.Combine(staging, ClientExecutableName));

            if (Directory.Exists(target))
            {
                Directory.Delete(target, true);
            }

            Directory.Move(staging, target);
            return Path.Combine(target, ClientExecutableName);
        }
        catch
        {
            TryDeleteDirectory(staging);
            throw;
        }
        finally
        {
            TryRemoveEmptyStagingRoot();
        }
    }

    public void Delete(string versionId)
    {
        var directory = GetVersionDirectory(versionId);
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }

    internal static string BuildBaseUrl(string channel)
    {
        var normalized = RobloxVersionChecker.NormalizeChannel(channel);
        // NormalizeChannel restricts the value to [A-Za-z0-9_-], so it cannot
        // introduce a path segment here.
        return normalized.Equals("LIVE", StringComparison.OrdinalIgnoreCase)
            ? CdnRoot
            : CdnRoot + "channel/" + normalized.ToLowerInvariant() + "/";
    }

    internal static IReadOnlyList<RobloxPackageRecord> ParseManifest(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new RobloxDeploymentDownloadException(
                RobloxMultiManager.Core.L.T("O manifesto do deployment veio vazio."));
        }

        // The file is CRLF throughout, including a trailing CRLF.  Splitting on
        // "\n" alone leaves a stray CR on every field, which silently breaks both
        // the MD5 string comparison and the integer parses.
        var lines = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToArray();
        if (lines.Length == 0 || !lines[0].Equals("v0", StringComparison.Ordinal))
        {
            throw new RobloxDeploymentDownloadException(
                RobloxMultiManager.Core.L.T("O manifesto está em um formato não reconhecido."));
        }

        if ((lines.Length - 1) % 4 != 0)
        {
            throw new RobloxDeploymentDownloadException(
                RobloxMultiManager.Core.L.T("O manifesto do deployment está incompleto."));
        }

        var packages = new List<RobloxPackageRecord>();
        for (var index = 1; index < lines.Length; index += 4)
        {
            var name = lines[index];
            var md5 = lines[index + 1];
            if (!IsSafePackageName(name)
                || !IsHex(md5, 32)
                || !long.TryParse(
                    lines[index + 2],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var compressed)
                || !long.TryParse(
                    lines[index + 3],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var uncompressed))
            {
                throw new RobloxDeploymentDownloadException(
                    RobloxMultiManager.Core.L.T("O manifesto do deployment tem um registro inválido."));
            }

            packages.Add(new RobloxPackageRecord(
                name,
                md5,
                compressed,
                uncompressed));
        }

        return packages;
    }

    private async Task DownloadPackageAsync(
        string url,
        string destinationPath,
        RobloxPackageRecord package,
        Action<long> onProgress,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("RobloxMultiManager/1.5.0");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new RobloxDeploymentDownloadException(
                RobloxMultiManager.Core.L.F("Falha ao baixar {0} ",package.Name)
                + $"(HTTP {(int)response.StatusCode}).");
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        var written = 0L;
        await using (var source = await response.Content
                         .ReadAsStreamAsync(cancellationToken)
                         .ConfigureAwait(false))
        await using (var destination = new FileStream(
                         destinationPath,
                         FileMode.Create,
                         FileAccess.Write,
                         FileShare.None,
                         1024 * 1024,
                         useAsync: true))
        {
            var buffer = new byte[1024 * 1024];
            int read;
            var lastReported = 0L;
            while ((read = await source
                       .ReadAsync(buffer, cancellationToken)
                       .ConfigureAwait(false)) > 0)
            {
                written += read;
                if (written > package.CompressedSize)
                {
                    throw new RobloxDeploymentDownloadException(
                        RobloxMultiManager.Core.L.F("{0} veio maior do que o manifesto declara.",package.Name));
                }

                hash.AppendData(buffer, 0, read);
                await destination
                    .WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                    .ConfigureAwait(false);
                if (written - lastReported >= 512 * 1024)
                {
                    lastReported = written;
                    onProgress(written);
                }
            }
        }

        if (written != package.CompressedSize)
        {
            throw new RobloxDeploymentDownloadException(
                RobloxMultiManager.Core.L.F("{0} veio incompleto ",package.Name)
                + RobloxMultiManager.Core.L.F("({0} de {1} bytes).",written,package.CompressedSize));
        }

        var actual = Convert.ToHexString(hash.GetHashAndReset());
        if (!actual.Equals(package.Md5, StringComparison.OrdinalIgnoreCase))
        {
            throw new RobloxDeploymentDownloadException(
                RobloxMultiManager.Core.L.F("{0} não corresponde ao hash publicado pela Roblox.",package.Name));
        }

        onProgress(written);
    }

    internal static void ExtractPackage(
        string archivePath,
        string versionRoot,
        string relativeDirectory,
        CancellationToken cancellationToken = default)
    {
        var destinationRoot = Path.GetFullPath(
            relativeDirectory.Length == 0
                ? versionRoot
                : Path.Combine(versionRoot, relativeDirectory));
        Directory.CreateDirectory(destinationRoot);
        var boundary = destinationRoot.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var isDirectory = entry.FullName.EndsWith('/')
                              || entry.FullName.EndsWith('\\');

            // Measured on real packages: Roblox writes these archives with
            // BACKSLASH separators and prefixes every directory entry with one,
            // so content-avatar.zip contains "\" and "\abilities\" while its files
            // are "abilities\AbilityManagerActor.rbxm".  Path.Combine treats a
            // leading separator as rooted and DISCARDS the destination, which
            // would resolve "\abilities\" to the drive root, so the prefix has to
            // be stripped before combining.
            var relative = entry.FullName
                .Replace('/', Path.DirectorySeparatorChar)
                .TrimStart(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);
            if (relative.Length == 0)
            {
                // The archive's own root entry; the destination already exists.
                continue;
            }

            var fullPath = Path.GetFullPath(
                Path.Combine(destinationRoot, relative));
            if (!fullPath.StartsWith(boundary, StringComparison.OrdinalIgnoreCase))
            {
                throw new RobloxDeploymentDownloadException(
                    RobloxMultiManager.Core.L.T("Um pacote tentou gravar fora da pasta da versão."));
            }

            if (isDirectory)
            {
                Directory.CreateDirectory(fullPath);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            entry.ExtractToFile(fullPath, true);
        }
    }

    private static void VerifyStagedClient(string executablePath)
    {
        if (!File.Exists(executablePath))
        {
            throw new RobloxDeploymentDownloadException(
                RobloxMultiManager.Core.L.T("O download terminou sem RobloxPlayerBeta.exe. O deployment ")
                + RobloxMultiManager.Core.L.T("escolhido não parece ser um cliente Windows."));
        }

        var (publisher, appearsSigned) =
            RobloxInstallationLocator.VerifyRobloxSignature(executablePath, null);
        if (!appearsSigned)
        {
            throw new RobloxDeploymentDownloadException(
                RobloxMultiManager.Core.L.T("O executável baixado não tem uma assinatura Roblox confiável ")
                + RobloxMultiManager.Core.L.F("(publicador: {0}). O download foi ",publisher ?? RobloxMultiManager.Core.L.T("desconhecido"))
                + RobloxMultiManager.Core.L.T("descartado."));
        }
    }

    private static string DescribeManifestFailure(
        HttpStatusCode status,
        string versionId,
        string channel) =>
        // This bucket answers 403 for a key that does not exist, so a 403 here
        // means "no such deployment on this channel", not an access problem.
        status is HttpStatusCode.Forbidden or HttpStatusCode.NotFound
            ? RobloxMultiManager.Core.L.F("A Roblox não publica {0} no canal {1}. ",versionId,channel)
              + RobloxMultiManager.Core.L.T("Confira o hash e o canal.")
            : RobloxMultiManager.Core.L.F("A consulta do manifesto falhou (HTTP {0}).",(int)status);

    private static bool IsSafePackageName(string value) =>
        value.Length is > 0 and <= 128
        && value.All(character =>
            char.IsAsciiLetterOrDigit(character)
            || character is '-' or '_' or '.')
        && !value.Contains("..", StringComparison.Ordinal);

    private static bool IsHex(string value, int length) =>
        value.Length == length && value.All(char.IsAsciiHexDigit);

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
        catch (IOException)
        {
            // A partially written staging folder is not worth failing over.
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }
    }

    private void TryRemoveEmptyStagingRoot()
    {
        try
        {
            var root = Path.Combine(VersionsRoot, StagingFolderName);
            if (Directory.Exists(root)
                && !Directory.EnumerateFileSystemEntries(root).Any())
            {
                Directory.Delete(root);
            }
        }
        catch (IOException)
        {
            // Cosmetic cleanup only.
        }
        catch (UnauthorizedAccessException)
        {
            // Cosmetic cleanup only.
        }
    }
}
