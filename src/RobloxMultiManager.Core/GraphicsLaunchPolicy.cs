namespace RobloxMultiManager.Core;

public sealed record AccountGraphicsOverride(Guid ProfileId, int Fps, int Quality);
public sealed record GraphicsLaunchPolicy(bool Enabled = false, int GlobalFps = -1, int GlobalQuality = 0, AccountGraphicsOverride[]? Accounts = null)
{
    public static GraphicsLaunchPolicy Validate(GraphicsLaunchPolicy value)
    {
        static bool Valid(int fps, int quality) => fps is -1 or 30 or 60 or 120 or 144 or 165 or 240 && quality is >= 0 and <= 10;
        var accounts = value.Accounts ?? [];
        if (!Valid(value.GlobalFps, value.GlobalQuality) || accounts.Length > 2000 || accounts.Any(a => a is null || a.ProfileId == Guid.Empty || !Valid(a.Fps, a.Quality))
            || accounts.Select(a => a.ProfileId).Distinct().Count() != accounts.Length) throw new InvalidDataException();
        return value with { Accounts = accounts };
    }
    public RobloxGraphicsPreferences ForAccount(Guid id)
    {
        var item = Accounts?.FirstOrDefault(a => a.ProfileId == id);
        return item is null ? new(GlobalFps, GlobalQuality) : new(item.Fps, item.Quality);
    }
    public GraphicsLaunchPolicy WithAccount(Guid id, RobloxGraphicsPreferences? value) => Validate(this with
    { Accounts = (Accounts ?? []).Where(a => a.ProfileId != id).Concat(value is null ? [] : new[] { new AccountGraphicsOverride(id, value.FpsCap, value.Quality) }).ToArray() });
}
