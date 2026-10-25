using System.Diagnostics;

namespace RobloxMultiManager.Core;

public static class RobloxClientExecutableInspector
{
    public static bool TryInspect(
        string? executablePath,
        out RobloxClientExecutableMetadata metadata,
        string? versionsRootOverride = null)
    {
        metadata = new RobloxClientExecutableMetadata(
            string.Empty,
            string.Empty,
            null,
            RobloxMultiManager.Core.L.T("indisponível"));
        try
        {
            if (string.IsNullOrWhiteSpace(executablePath)
                || !File.Exists(executablePath))
            {
                return false;
            }

            var fullPath = Path.GetFullPath(executablePath);
            if (!Path.GetFileName(fullPath).Equals(
                    "RobloxPlayerBeta.exe",
                    StringComparison.OrdinalIgnoreCase)
                || (File.GetAttributes(fullPath)
                    & FileAttributes.ReparsePoint) != 0)
            {
                return false;
            }

            // A version this app downloaded lives under its own root, so both are
            // accepted here.  An explicit override still means "only this root",
            // which keeps fixture-driven callers isolated.
            (string Root, string Label)[] roots = versionsRootOverride is not null
                ? [(Path.GetFullPath(versionsRootOverride), "Versions")]
                :
                [
                    (Path.GetFullPath(Path.Combine(
                            Environment.GetFolderPath(
                                Environment.SpecialFolder.LocalApplicationData),
                            "Roblox",
                            "Versions")),
                        "Versions"),
                    (Path.GetFullPath(
                            RobloxInstallationLocator.ManagedVersionsRoot),
                        @"RobloxMultiManager\Versions")
                ];

            var directory = new DirectoryInfo(
                Path.GetDirectoryName(fullPath)!);
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0
                || !RobloxInstallationLocator.IsValidVersionId(directory.Name))
            {
                return false;
            }

            var parent = Path.GetFullPath(
                directory.Parent?.FullName ?? string.Empty);
            string? matchedLabel = null;
            foreach (var candidate in roots)
            {
                var prefix = candidate.Root.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
                if (fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(
                        parent,
                        candidate.Root,
                        StringComparison.OrdinalIgnoreCase))
                {
                    matchedLabel = candidate.Label;
                    break;
                }
            }

            if (matchedLabel is null)
            {
                return false;
            }

            var fileVersion = FileVersionInfo
                .GetVersionInfo(fullPath)
                .FileVersion?
                .Replace(", ", ".", StringComparison.Ordinal)
                .Trim();
            metadata = new RobloxClientExecutableMetadata(
                fullPath,
                directory.Name,
                string.IsNullOrWhiteSpace(fileVersion)
                    ? null
                    : fileVersion,
                Path.Combine(
                    matchedLabel,
                    directory.Name,
                    "RobloxPlayerBeta.exe"));
            return true;
        }
        catch
        {
            return false;
        }
    }
}
