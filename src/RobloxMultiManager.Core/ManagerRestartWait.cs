using System.Diagnostics;

namespace RobloxMultiManager.Core;

public static class ManagerRestartWait
{
    public static async Task WaitAsync(string[] arguments, string executable, Action retainGuard, CancellationToken token)
    {
        var argument = Array.IndexOf(arguments, "--restart-after");
        if (argument < 0 || argument + 2 >= arguments.Length || !int.TryParse(arguments[argument + 1], out var pid)
            || !long.TryParse(arguments[argument + 2], out var started) || pid == Environment.ProcessId) return;
        Process prior;
        try { prior = Process.GetProcessById(pid); }
        catch (ArgumentException) { return; }
        using (prior)
        {
            try
            {
                if (prior.HasExited || prior.StartTime.ToUniversalTime().Ticks != started || prior.SessionId != Process.GetCurrentProcess().SessionId
                    || !string.Equals(prior.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase)) return;
            }
            catch (InvalidOperationException) { return; }
            retainGuard();
            await prior.WaitForExitAsync(token).WaitAsync(TimeSpan.FromSeconds(45), token);
        }
    }
}
