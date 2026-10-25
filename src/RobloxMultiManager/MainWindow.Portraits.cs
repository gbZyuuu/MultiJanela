using RobloxMultiManager.Core;

namespace RobloxMultiManager;

public partial class MainWindow
{
    private readonly CancellationTokenSource _portraitRefreshLifetime = new();
    private readonly HashSet<long> _portraitRefreshPending = [];
    private bool _portraitRefreshHookAttached;
    private async Task RefreshAccountPortraitAsync(Guid profileId)
    {
        var profile = Profiles.FirstOrDefault(p => p.Id == profileId);
        if (profile?.RobloxUserId is not { } userId || !_portraitRefreshPending.Add(userId)) return;
        var token = _portraitRefreshLifetime.Token;
        try
        {
            // Thumbnail generation is asynchronous on Roblox; keep the old picture while waiting.
            foreach (var delay in new[] { 0, 4000, 12000 })
            {
                if (delay > 0) await Task.Delay(delay, token);
                token.ThrowIfCancellationRequested();
                var images = await _avatarService.FetchHeadshotsAsync([userId], token);
                if (!images.TryGetValue(userId, out var url) || url == profile.AvatarUrl) continue;
                var index = Profiles.ToList().FindIndex(p => p.Id == profileId);
                if (index < 0 || _shutdownStarted || _handoffPrepared) return;
                var selected = (ProfilesGrid.SelectedItem as AccountProfile)?.Id;
                var updated = Profiles[index] with { AvatarUrl = url };
                void Replace() { Profiles[index] = updated; if (selected == profileId) ProfilesGrid.SelectedItem = updated; }
                if (_accountPortal is { } portal) portal.UpdateProfilePortrait(updated, Replace); else Replace();
                await SaveProfilesAsync(); return;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* The equipped-item receipt remains authoritative if thumbnails are unavailable. */ }
        finally { _portraitRefreshPending.Remove(userId); }
    }
}
