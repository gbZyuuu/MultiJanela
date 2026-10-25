using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace RobloxMultiManager.Core;

public sealed record UserInstallation(string Version, string Slot);

public sealed class UserInstallationService
{
    public static string DefaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "MultiJanela");
    private readonly Action<string, string, bool> _integrate;
    private readonly Action _removeIntegration;
    public string Root { get; }
    public UserInstallationService(string? root = null, Action<string, string, bool>? integrate = null, Action? removeIntegration = null)
    { Root = Path.GetFullPath(root ?? DefaultRoot); _integrate = integrate ?? IntegrateWindows; _removeIntegration = removeIntegration ?? (() => RemoveWindowsIntegration(Root)); }
    public async Task<string> InstallAsync(string source, bool desktopShortcut)
    {
        source = Path.GetFullPath(source);
        await ManagerUpdateService.VerifyPayloadAsync(source);
        var executable = Path.Combine(source, "RobloxMultiManager.exe");
        var version = FileVersionInfo.GetVersionInfo(executable).FileVersion;
        if (!System.Version.TryParse(version, out var parsed)) throw new InvalidDataException();
        ManagerUpdateService.RejectRedirects(Root); Directory.CreateDirectory(Root); VerifyDirectory(Root);
        var pointer = Path.Combine(Root, "current.json");
        var launcher = Path.Combine(Root, "MultiJanela.exe");
        var marker = Path.Combine(Root, "installation.json");
        if (File.Exists(launcher) && !File.Exists(pointer) && !File.Exists(marker)) throw new InvalidOperationException(L.T("A pasta de instalação já contém arquivos não gerenciados. Nenhum arquivo foi substituído."));
        if (File.Exists(marker)) VerifyMarker(marker);
        else await SecureJsonFiles.WriteAllTextAsync(marker, "{\"owner\":\"MultiJanela\",\"version\":1}");
        var versions = Path.Combine(Root, "versions"); Directory.CreateDirectory(versions); VerifyDirectory(versions);
        await using var executableStream = SecureJsonFiles.OpenRead(executable);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(executableStream))[..8].ToLowerInvariant();
        var slot = parsed.ToString() + "-" + hash; var target = Path.Combine(versions, slot);
        if (!Directory.Exists(target))
        {
            var staging = Path.Combine(versions, "staging-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(staging);
            try
            {
                foreach (var name in ManagerUpdateService.PayloadFiles) File.Copy(Path.Combine(source, name), Path.Combine(staging, name), false);
                await ManagerUpdateService.VerifyPayloadAsync(staging); Directory.Move(staging, target);
            }
            finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
        }
        else { VerifyDirectory(target); await ManagerUpdateService.VerifyPayloadAsync(target); }
        foreach (var name in ManagerUpdateService.PayloadFiles)
        {
            await using var original = SecureJsonFiles.OpenRead(Path.Combine(source, name));
            await using var installed = SecureJsonFiles.OpenRead(Path.Combine(target, name));
            if (!(await SHA256.HashDataAsync(original)).SequenceEqual(await SHA256.HashDataAsync(installed))) throw new InvalidDataException();
        }
        if (!File.Exists(launcher)) File.Copy(executable, launcher, false);
        _integrate(launcher, parsed.ToString(3), desktopShortcut);
        var temporary = pointer + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await SecureJsonFiles.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new UserInstallation(parsed.ToString(), slot))); File.Move(temporary, pointer, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return launcher;
    }
    public string? ResolveCurrent()
    {
        var pointer = Path.Combine(Root, "current.json"); if (!File.Exists(pointer)) return null;
        VerifyDirectory(Root);
        VerifyMarker(Path.Combine(Root, "installation.json"));
        if (new FileInfo(pointer).Length > 4096 || (File.GetAttributes(pointer) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException();
        var data = JsonSerializer.Deserialize<UserInstallation>(File.ReadAllText(pointer));
        if (data is null || !Regex.IsMatch(data.Slot, "^[0-9]+\\.[0-9]+\\.[0-9]+(?:\\.[0-9]+)?-[a-fA-F0-9]{8}$")) throw new InvalidDataException();
        var directory = Path.Combine(Root, "versions", data.Slot); VerifyDirectory(Path.Combine(Root, "versions")); VerifyDirectory(directory);
        var executable = Path.Combine(directory, "RobloxMultiManager.exe");
        if (!File.Exists(executable) || (File.GetAttributes(executable) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException();
        return executable;
    }
    public Task<bool> UninstallAsync(int currentProcessId) => Task.Run(() =>
    {
        if (!Directory.Exists(Root)) return true;
        VerifyDirectory(Root);
        var marker = Path.Combine(Root, "installation.json");
        if (!File.Exists(marker) || new FileInfo(marker).Length > 4096 || (File.GetAttributes(marker) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException(L.T("A pasta não contém uma instalação gerenciada. Nenhum arquivo foi removido."));
        VerifyMarker(marker);
        foreach (var name in new[] { "RobloxMultiManager", "MultiJanela" })
            foreach (var process in Process.GetProcessesByName(name))
                using (process)
                {
                    if (process.Id == currentProcessId) continue;
                    try
                    {
                        var file = process.MainModule?.FileName;
                        if (file is not null && Path.GetFullPath(file).StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException(L.T("Saia do gerenciador pelo menu antes de desinstalar. Os jogos podem continuar abertos."));
                    }
                    catch (System.ComponentModel.Win32Exception) { }
                }
        var versions = Path.Combine(Root, "versions");
        if (Directory.Exists(versions))
        {
            VerifyDirectory(versions);
            foreach (var directory in Directory.GetDirectories(versions))
            {
                if (!Regex.IsMatch(Path.GetFileName(directory), "^[0-9]+\\.[0-9]+\\.[0-9]+(?:\\.[0-9]+)?-[a-fA-F0-9]{8}$")) continue;
                VerifyDirectory(directory);
                foreach (var file in Directory.GetFiles(directory))
                    if (ManagerUpdateService.PayloadFiles.Contains(Path.GetFileName(file)) && (File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0) File.Delete(file);
                if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
            }
            if (!Directory.EnumerateFileSystemEntries(versions).Any()) Directory.Delete(versions);
        }
        foreach (var name in new[] { "MultiJanela.exe", "current.json", "installation.json" })
        {
            var file = Path.Combine(Root, name);
            if (File.Exists(file) && (File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0) File.Delete(file);
        }
        _removeIntegration();
        var empty = !Directory.EnumerateFileSystemEntries(Root).Any(); if (empty) Directory.Delete(Root);
        return empty;
    });
    private static void VerifyDirectory(string path)
    { ManagerUpdateService.RejectRedirects(path); if (!Directory.Exists(path)) throw new InvalidDataException(); }
    private static void VerifyMarker(string marker)
    {
        ManagerUpdateService.RejectRedirects(marker);
        if (!File.Exists(marker) || new FileInfo(marker).Length > 4096) throw new InvalidDataException();
        using var json = JsonDocument.Parse(File.ReadAllText(marker));
        if (!json.RootElement.TryGetProperty("owner", out var owner) || owner.GetString() != "MultiJanela"
            || !json.RootElement.TryGetProperty("version", out var version) || version.GetInt32() != 1) throw new InvalidDataException();
    }
    private static void IntegrateWindows(string launcher, string version, bool desktop)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\MultiJanela");
        key.SetValue("DisplayName", "MultiJanela"); key.SetValue("DisplayVersion", version); key.SetValue("Publisher", "MultiJanela");
        key.SetValue("InstallLocation", Path.GetDirectoryName(launcher)!); key.SetValue("DisplayIcon", launcher);
        key.SetValue("UninstallString", "\"" + launcher + "\" --uninstall"); key.SetValue("NoModify", 1); key.SetValue("NoRepair", 1);
        var type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException();
        dynamic shell = Activator.CreateInstance(type)!;
        try
        {
            var locations = new List<string> { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "MultiJanela.lnk") };
            if (desktop) locations.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "MultiJanela.lnk"));
            foreach (var location in locations)
            {
                dynamic shortcut = shell.CreateShortcut(location);
                try { shortcut.TargetPath = launcher; shortcut.WorkingDirectory = Path.GetDirectoryName(launcher); shortcut.IconLocation = launcher; shortcut.Description = "MultiJanela"; shortcut.Save(); }
                finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shortcut); }
            }
        }
        finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
    }
    private static void RemoveWindowsIntegration(string root)
    {
        const string registration = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\MultiJanela";
        using (var key = Registry.CurrentUser.OpenSubKey(registration))
            if (key?.GetValue("InstallLocation") is string location && Path.GetFullPath(location).Equals(root, StringComparison.OrdinalIgnoreCase))
                Registry.CurrentUser.DeleteSubKeyTree(registration, false);
        foreach (var location in new[] { Environment.GetFolderPath(Environment.SpecialFolder.Programs), Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory) })
        {
            var shortcut = Path.Combine(location, "MultiJanela.lnk"); if (!File.Exists(shortcut)) continue;
            var type = Type.GetTypeFromProgID("WScript.Shell"); if (type is null) continue;
            dynamic shell = Activator.CreateInstance(type)!;
            try
            {
                dynamic link = shell.CreateShortcut(shortcut);
                try { if (Path.GetFullPath((string)link.TargetPath).Equals(Path.Combine(root, "MultiJanela.exe"), StringComparison.OrdinalIgnoreCase)) File.Delete(shortcut); }
                finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(link); }
            }
            finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
        }
    }
}
