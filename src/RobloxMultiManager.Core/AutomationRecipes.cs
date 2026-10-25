using System.Text.Json;

namespace RobloxMultiManager.Core;

public sealed record AutomationStep(string Action, Guid? PresetId = null, int Seconds = 0);
public sealed record AutomationRecipe(int Version, Guid Id, string Name, bool AllowRejoin, AutomationStep[] Steps);
public sealed record ScheduledManagerAction(Guid Id, string Name, string Action, Guid? PresetId, DateTime NextRunUtc,
    int IntervalMinutes = 0, bool Enabled = false, bool AllowRejoin = false);
public sealed record AutomationPreferences(ScheduledManagerAction[] Schedules, AutomationRecipe[] Recipes)
{
    public static AutomationPreferences Empty => new([], []);
    public static AutomationPreferences Validate(AutomationPreferences data)
    {
        if (data.Schedules is null || data.Recipes is null || data.Schedules.Length > 100 || data.Recipes.Length > 100
            || data.Schedules.Any(s => s is null) || data.Recipes.Any(r => r is null)) throw new InvalidDataException();
        foreach (var schedule in data.Schedules)
            if (schedule.Id == Guid.Empty || string.IsNullOrWhiteSpace(schedule.Name) || schedule.Name.Length > 80
                || schedule.Action is not ("launch" or "rejoin" or "arrange" or "pulse")
                || schedule.Action is "launch" or "rejoin" or "pulse" && schedule.PresetId is null
                || schedule.Action == "rejoin" && !schedule.AllowRejoin
                || schedule.IntervalMinutes != 0 && schedule.IntervalMinutes is < 5 or > 1440
                || schedule.NextRunUtc.Kind != DateTimeKind.Utc)
                throw new InvalidDataException(L.T("Agendamento inválido. Use intervalo de pelo menos 5 minutos ou execução única."));
        if (data.Schedules.Select(s => s.Id).Distinct().Count() != data.Schedules.Length || data.Recipes.Select(r => r.Id).Distinct().Count() != data.Recipes.Length) throw new InvalidDataException();
        foreach (var recipe in data.Recipes) RecipeRunner.Validate(recipe);
        return data;
    }
    public static ScheduledManagerAction? NextDue(AutomationPreferences data, DateTime nowUtc) => data.Schedules
        .Where(s => s.Enabled && s.NextRunUtc <= nowUtc).OrderBy(s => s.NextRunUtc).FirstOrDefault();
    public static AutomationPreferences Claim(AutomationPreferences data, Guid id, DateTime nowUtc) => data with
    { Schedules = data.Schedules.Select(s => s.Id != id ? s : s with { Enabled = s.IntervalMinutes > 0, NextRunUtc = s.IntervalMinutes > 0 ? nowUtc.AddMinutes(s.IntervalMinutes) : s.NextRunUtc }).ToArray() };
}

/// <summary>Bounded declarative recipes. No shell, arbitrary JavaScript, file access or injected game code.</summary>
public static class RecipeRunner
{
    public static AutomationRecipe Parse(string json)
    {
        if (json.Length > 16384) throw new InvalidDataException();
        return Validate(JsonSerializer.Deserialize<AutomationRecipe>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidDataException());
    }
    public static AutomationRecipe Validate(AutomationRecipe recipe)
    {
        if (recipe.Version != 1 || recipe.Id == Guid.Empty || string.IsNullOrWhiteSpace(recipe.Name) || recipe.Name.Length > 80
            || recipe.Steps is null || recipe.Steps.Length is < 1 or > 64 || recipe.Steps.Any(s => s is null) || recipe.Steps.Sum(s => (long)s.Seconds) > 3600
            || recipe.Steps.Any(s => s.Action is not ("launch" or "rejoin" or "arrange" or "delay" or "pulse" or "trim")
                || s.Seconds is < 0 or > 600 || s.Action is "launch" or "rejoin" or "pulse" && s.PresetId is null
                || s.Action == "rejoin" && !recipe.AllowRejoin))
            throw new InvalidDataException(L.T("Receita inválida ou sem permissão para reabrir sessões."));
        return recipe;
    }
    public static async Task RunAsync(AutomationRecipe recipe, Func<AutomationStep, CancellationToken, Task> dispatch, CancellationToken token)
    {
        Validate(recipe);
        foreach (var step in recipe.Steps)
        {
            token.ThrowIfCancellationRequested();
            if (step.Action == "delay") await Task.Delay(TimeSpan.FromSeconds(step.Seconds), token);
            else await dispatch(step, token);
        }
    }
}
