using RobloxMultiManager.Core;

namespace RobloxMultiManager.Tests;

internal static partial class Program
{
    private static async Task TestManagerActivationAsync()
    {
        if (ManagerInstanceCoordinator.IsDifferentVersion("2.5.3.0", "2.5.3.0")
            || !ManagerInstanceCoordinator.IsDifferentVersion("2.5.4.0", "2.5.3.0")
            || !ManagerInstanceCoordinator.IsDifferentVersion("2.5.2.0", "2.5.3.0")
            || ManagerInstanceCoordinator.IsDifferentVersion("invalid", "2.5.3.0"))
            throw new Exception("Activation version policy failed.");
        var publicOne = ManagerInstanceCoordinator.CreateRequest("1.0.0.0");
        Assert(publicOne.EffectiveVersion == "1.0.0.0" && Version.Parse(publicOne.Version) >= new Version(2,9,0,0), "Public v1 preserves the vault handoff capability for older managers");
        Assert(!ManagerInstanceCoordinator.IsDifferentVersion(publicOne.EffectiveVersion, "1.0.0.0"), "Opening the same public version activates the current manager");
        var name = @"Local\MultiJanela.ActivationFixture." + Guid.NewGuid().ToString("N");
        using var ready = new ManualResetEventSlim();
        using var stop = new ManualResetEventSlim();
        var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var aborted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var busy = true;
        var activations = 0;
        Exception? workerFailure = null;
        var owner = new Thread(() =>
        {
            try
            {
                using var coordinator = new ManagerInstanceCoordinator(name);
                if (!coordinator.TryAcquire()) throw new Exception("Fixture primary lease unavailable.");
                coordinator.Listen((request, _) =>
                {
                    if (!ManagerInstanceCoordinator.IsDifferentVersion(request.Version, "2.5.3.0"))
                    { Interlocked.Increment(ref activations); return Task.FromResult(new ManagerOpenReply("activated")); }
                    return Task.FromResult(new ManagerOpenReply(busy ? "busy" : "ready"));
                }, () => { committed.TrySetResult(); stop.Set(); return Task.CompletedTask; },
                   () => { aborted.TrySetResult(); return Task.CompletedTask; });
                ready.Set();
                stop.Wait(TimeSpan.FromSeconds(30));
            }
            catch (Exception ex) { workerFailure = ex; ready.Set(); }
        }) { IsBackground = true };
        owner.Start();
        try
        {
            if (!ready.Wait(TimeSpan.FromSeconds(5)) || workerFailure is not null) throw new Exception("Fixture owner failed.", workerFailure);
            using var client = new ManagerInstanceCoordinator(name);
            if (client.TryAcquire()) throw new Exception("Two manager writers acquired the same lease.");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var same = await client.RequestAsync("2.5.3.0", () => throw new Exception("Same version cannot commit."), timeout.Token);
            if (same != "activated" || activations != 1 || committed.Task.IsCompleted) throw new Exception("Same version did not activate in place.");
            var waiting = await client.RequestAsync("2.5.4.0", () => throw new Exception("Busy owner cannot commit."), timeout.Token);
            if (waiting != "busy" || committed.Task.IsCompleted) throw new Exception("Pending work was interrupted.");
            busy = false;
            try
            {
                await client.RequestAsync("2.5.4.0", () => throw new IOException("Fixture client cancelled before commit."), timeout.Token);
                throw new Exception("Fixture abort was swallowed.");
            }
            catch (IOException) { }
            await aborted.Task.WaitAsync(timeout.Token);
            if (committed.Task.IsCompleted) throw new Exception("An unacknowledged transfer closed its owner.");
            var bridgeCreated = false;
            var transferred = await client.RequestAsync("2.5.4.0", () => { bridgeCreated = true; return Task.CompletedTask; }, timeout.Token);
            await committed.Task.WaitAsync(timeout.Token);
            owner.Join(5000);
            if (transferred != "ready" || !bridgeCreated || owner.IsAlive || workerFailure is not null)
                throw new Exception("Acknowledged version transfer failed.", workerFailure);
            if (!client.TryAcquire()) throw new Exception("The replacement could not acquire the released lease.");
        }
        finally { stop.Set(); owner.Join(5000); }

        using var absent = new ManagerInstanceCoordinator(@"Local\MultiJanela.AbsentFixture." + Guid.NewGuid().ToString("N"));
        if (await absent.RequestAsync("2.5.3.0", () => Task.CompletedTask, CancellationToken.None) != "legacy")
            throw new Exception("Legacy-owner fallback failed.");
    }

    private static async Task TestManagerWorkspaceAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "MultiJanela.WorkspaceFixture." + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ManagerWorkspaceStore(root);
            if (await store.LoadAsync() is not null) throw new Exception("Missing workspace should not invent state.");
            var account = Guid.NewGuid();
            var destination = Guid.NewGuid();
            var state = new ManagerWorkspaceState([account], "https://www.roblox.com/games/12345/Fixture", destination,
                account, "fixture", "Launch", 980, 710, true, ["123:456"], [new(account, 2, DateTime.UtcNow)]);
            await store.SaveAsync(state);
            var restored = await store.LoadAsync();
            if (restored is null || restored.Sequence.Single() != account || restored.Destination != state.Destination
                || restored.SavedDestination != destination || restored.SelectedAccount != account || restored.Search != "fixture"
                || restored.Page != "Launch" || restored.Width != 980 || !restored.Maximized
                || restored.SelectedSessions.Single() != "123:456") throw new Exception("Workspace progress was not preserved.");
            var reconnect = new ReconnectCoordinator(_ => false, _ => false, (_, _) => Task.FromResult(false));
            reconnect.ImportBudgets(restored.ReconnectBudgets!);
            if (reconnect.Attempts(account) != 2 || reconnect.ExportBudgets().Length != 1) throw new Exception("Reconnect retry budget lost.");
            await store.SaveAsync(state with { Destination = "67890", Sequence = [] });
            if ((await store.LoadAsync())?.Destination != "67890" || Directory.GetFiles(root, "*.tmp").Length != 0)
                throw new Exception("Workspace replacement was incomplete.");
            await File.WriteAllTextAsync(Path.Combine(root, "manager-workspace.json"), "invalid");
            if (await store.LoadAsync() is not null) throw new Exception("Corrupt workspace should fall back safely.");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
