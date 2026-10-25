using RobloxMultiManager.Core;

namespace RobloxMultiManager.Tests;

internal static partial class Program
{
    private static Task TestNativeSessionRecoveryAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "MultiJanela.SessionRecoveryTests", Guid.NewGuid().ToString("N"));
        var native = Path.Combine(root, "Roblox");
        var storage = Path.Combine(native, "LocalStorage");
        Directory.CreateDirectory(storage);
        var cookie = Path.Combine(storage, "RobloxCookies.dat");
        var settings = Path.Combine(storage, "appStorage.json");
        var manager = Path.Combine(root, "RobloxMultiManager", "profiles.json");
        Directory.CreateDirectory(Path.GetDirectoryName(manager)!);
        var bytes = new byte[] { 0, 255, 42, 19, 0, 128 };
        File.WriteAllBytes(cookie, bytes); File.WriteAllText(settings, "settings-fixture"); File.WriteAllText(manager, "profile-fixture");
        try
        {
            var busy = new RobloxLocalSessionRecovery(native, () => true).Repair();
            Assert(busy.Status == LocalSessionRecoveryStatus.ClientsRunning && File.ReadAllBytes(cookie).SequenceEqual(bytes), "live clients prevent session changes");
            Assert(!Directory.Exists(Path.Combine(native, "AuthSessionBackups")), "blocked repair creates no backup artifacts");
            var checks = 0;
            var race = new RobloxLocalSessionRecovery(native, () => ++checks == 2).Repair();
            Assert(race.Status == LocalSessionRecoveryStatus.ClientsRunning && File.Exists(cookie), "a client starting during preflight prevents repair");
            using (var held = new Mutex(true, @"Local\MultiJanela.NativeSessionRecovery"))
            {
                try
                {
                    var concurrent = Task.Run(() => new RobloxLocalSessionRecovery(native, () => false).Repair()).GetAwaiter().GetResult();
                    Assert(concurrent.Status == LocalSessionRecoveryStatus.Busy && File.Exists(cookie), "concurrent recovery cannot change the same session");
                }
                finally { held.ReleaseMutex(); }
            }
            var repair = new RobloxLocalSessionRecovery(native, () => false).Repair();
            Assert(repair.Status == LocalSessionRecoveryStatus.Repaired && !File.Exists(cookie), "explicit repair removes only the native session from its active location");
            Assert(repair.BackupDirectory is not null && File.ReadAllBytes(Path.Combine(repair.BackupDirectory, "RobloxCookies.dat")).SequenceEqual(bytes), "backup preserves the exact original bytes");
            Assert(File.ReadAllText(settings) == "settings-fixture" && File.ReadAllText(manager) == "profile-fixture", "native settings and manager profiles are preserved");
            Assert(new RobloxLocalSessionRecovery(native, () => false).Repair().Status == LocalSessionRecoveryStatus.AlreadyClean, "repeated repair does not overwrite backups");
            File.WriteAllBytes(cookie, [99]);
            var second = new RobloxLocalSessionRecovery(native, () => false).Repair();
            Assert(second.Status == LocalSessionRecoveryStatus.Repaired && second.BackupDirectory != repair.BackupDirectory, "later sessions get distinct backups");
            Assert(File.ReadAllBytes(Path.Combine(repair.BackupDirectory!, "RobloxCookies.dat")).SequenceEqual(bytes), "later recovery preserves the earlier original");
            var denied = new RobloxLocalSessionRecovery(native, () => throw new InvalidOperationException()).Repair();
            Assert(denied.Status == LocalSessionRecoveryStatus.Unavailable, "process-probe failures fail closed");
        }
        finally { Directory.Delete(root, true); }
        return Task.CompletedTask;
    }
}
