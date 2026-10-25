using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

namespace RobloxMultiManager.Core;

public enum LocalSessionRecoveryStatus { Repaired, ClientsRunning, AlreadyClean, Busy, UnsafePath, Unavailable }
public sealed record LocalSessionRecoveryResult(LocalSessionRecoveryStatus Status, string? BackupDirectory = null);

/// <summary>Explicit, reversible recovery of the native client's shared session file.</summary>
public sealed class RobloxLocalSessionRecovery(string? robloxRoot = null, Func<bool>? isClientRunning = null)
{
    private readonly string _root = Path.GetFullPath(robloxRoot ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roblox"));
    private readonly Func<bool> _isClientRunning = isClientRunning ?? AnyClientRunning;

    public LocalSessionRecoveryResult Repair()
    {
        Mutex? gate = null;
        var acquired = false;
        try
        {
            gate = new Mutex(false, @"Local\MultiJanela.NativeSessionRecovery");
            try { acquired = gate.WaitOne(TimeSpan.Zero); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) return new(LocalSessionRecoveryStatus.Busy);
            if (_isClientRunning()) return new(LocalSessionRecoveryStatus.ClientsRunning);
            var storage = Path.Combine(_root, "LocalStorage");
            var source = Path.Combine(storage, "RobloxCookies.dat");
            var backups = Path.Combine(_root, "AuthSessionBackups");
            if (IsLink(_root) || IsLink(storage) || IsLink(source) || IsLink(backups))
                return new(LocalSessionRecoveryStatus.UnsafePath);
            if (!File.Exists(source)) return new(LocalSessionRecoveryStatus.AlreadyClean);
            // Keep settings, installation/device identifiers, browser profiles and all
            // other native files untouched. Never read or log session contents.
            var backup = Path.Combine(backups, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture)
                + "-" + Guid.NewGuid().ToString("N"));
            if (!Path.GetFullPath(backup).StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return new(LocalSessionRecoveryStatus.UnsafePath);
            if (_isClientRunning()) return new(LocalSessionRecoveryStatus.ClientsRunning);
            Directory.CreateDirectory(backup);
            File.Move(source, Path.Combine(backup, "RobloxCookies.dat"), overwrite: false);
            return new(LocalSessionRecoveryStatus.Repaired, backup);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
        { return new(LocalSessionRecoveryStatus.Unavailable); }
        finally { if (acquired) gate!.ReleaseMutex(); gate?.Dispose(); }
    }

    private static bool IsLink(string path) => (File.Exists(path) || Directory.Exists(path))
        && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static bool AnyClientRunning()
    {
        foreach (var name in new[] { "RobloxPlayerBeta", "RobloxStudioBeta" })
        {
            var processes = Process.GetProcessesByName(name);
            try { if (processes.Length != 0) return true; }
            finally { foreach (var process in processes) process.Dispose(); }
        }
        return false;
    }
}
