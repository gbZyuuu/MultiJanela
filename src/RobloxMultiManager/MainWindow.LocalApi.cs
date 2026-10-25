using System.Security.Cryptography;
using RobloxMultiManager.Core;

namespace RobloxMultiManager;

public partial class MainWindow
{
    private LocalControlApi? _localApi;
    private LocalApiPanel? _localApiPanel;
    private LocalApiPreferences _apiPreferences = new();
    private bool _apiApplying;
    private PreferenceFile<LocalApiPreferences> ApiStore => new(_paths, "local-api.json", () => new(), LocalApiPreferences.Validate);
    private async Task InitializeLocalApiAsync()
    {
        try { _apiPreferences = await ApiStore.LoadAsync(); } catch (Exception) { _apiPreferences = new(); }
        if (_apiPreferences.Enabled)
        {
            try { await StartLocalApiAsync(_apiPreferences); }
            catch (Exception) { _apiPreferences = _apiPreferences with { Enabled = false }; }
        }
        _localApiPanel = new LocalApiPanel(_apiPreferences, SaveLocalApiAsync); LocalApiSettingsHost.Content = _localApiPanel;
    }
    private async Task<LocalApiPreferences> SaveLocalApiAsync(LocalApiPreferences preferences, bool rotate)
    {
        var previous = _apiPreferences; _apiApplying = true;
        try
        {
            if (rotate || string.IsNullOrEmpty(preferences.ProtectedToken)) preferences = preferences with { ProtectedToken = LocalSecretProtection.Protect(Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant()) };
            if (_localApi is not null) { await _localApi.DisposeAsync(); _localApi = null; }
            if (preferences.Enabled) await StartLocalApiAsync(preferences);
            await ApiStore.SaveAsync(preferences); _apiPreferences = preferences;
            return preferences;
        }
        catch
        {
            if (_localApi is not null) { await _localApi.DisposeAsync(); _localApi = null; }
            if (previous.Enabled) try { await StartLocalApiAsync(previous); } catch (Exception) { }
            throw;
        }
        finally { _apiApplying = false; }
    }
    private async Task StartLocalApiAsync(LocalApiPreferences preferences)
    {
        var host = new LocalControlApi();
        try
        {
            await host.StartAsync(preferences, LocalSecretProtection.Unprotect(preferences.ProtectedToken),
                () => Dispatcher.InvokeAsync(() => BuildApiSnapshot(preferences.IncludeNames)).Task,
                action => Dispatcher.InvokeAsync(() => DispatchApiAction(action, preferences.AllowRejoin)).Task);
            _localApi = host;
        }
        catch { await host.DisposeAsync(); throw; }
    }
    private ApiSnapshot BuildApiSnapshot(bool names)
    {
        if (_vaultLocked) return new(typeof(App).Assembly.GetName().Version!.ToString(3), true, [], [], []);
        return new(typeof(App).Assembly.GetName().Version!.ToString(3), !AutomationIdle || _automationCancellation is not null,
            Profiles.Select(p => new ApiAccount(p.Id, names && !DisplayPrivacy.Enabled ? p.Name : "Account " + p.Id.ToString("N")[..6], p.IsSignedIn)).ToArray(),
            Instances.Select(i =>
            {
                _resourceUsage.TryGetValue(i.Key, out var usage);
                return new ApiSession(i.Key, i.ProcessId, _launchOwnership.GetValueOrDefault(i.Key)?.ProfileId, i.FileVersion ?? i.VersionId ?? "",
                    usage?.ResidentBytes, usage?.DedicatedGpuBytes, usage?.CpuPercent);
            }).ToArray(),
            _workspaceLibrary.Presets.Select(p => new ApiPreset(p.Id, names && !DisplayPrivacy.Enabled ? p.Name : "Preset " + p.Id.ToString("N")[..6], p.Accounts.Length)).ToArray());
    }
    private bool DispatchApiAction(ApiAction action, bool rejoin)
    {
        if (_apiApplying || !AutomationIdle || _automationCancellation is not null) return false;
        if (action.PresetId is Guid id && _workspaceLibrary.Presets.All(p => p.Id != id)) return false;
        var recipe = new AutomationRecipe(1, Guid.NewGuid(), "API", action.Action == "rejoin" && rejoin, [new(action.Action, action.PresetId)]);
        try
        {
            _ = RunRecipeAsync(recipe).ContinueWith(_ => { }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            return true;
        }
        catch (Exception) { return false; }
    }
}
