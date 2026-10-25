using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;
namespace RobloxMultiManager;

// One writer per panel. Coalesce rapid edits, retaining independent account scopes.
internal sealed class SettingsSaveQueue
{
    private readonly Dictionary<string, Func<Task>> _pending = [];
    private static readonly HashSet<SettingsSaveQueue> Active = [];
    public static Task FlushAllAsync() => Task.WhenAll(Active.Select(q => q.Completion).ToArray());
    public bool IsBusy { get; private set; }
    public Task Completion { get; private set; } = Task.CompletedTask;
    public void Enqueue(string key, Func<Task> save)
    {
        _pending[key] = save;
        if (!IsBusy) Completion = DrainAsync();
    }
    private async Task DrainAsync()
    {
        IsBusy = true; Active.Add(this);
        try
        {
            await Task.Delay(180);
            while (_pending.Count > 0)
            {
                var key = _pending.Keys.First(); var save = _pending[key]; _pending.Remove(key);
                // Each panel reports persistence failures without discarding later changes.
                await save();
            }
        }
        finally { IsBusy = false; Active.Remove(this); }
    }
    public static void Watch(FrameworkElement root, Action changed, params object[] controls)
    {
        void Update() { if (root.IsLoaded) changed(); }
        foreach (var control in controls)
            switch (control)
            {
                case ComboBox combo: combo.SelectionChanged += (_, _) => Update(); break;
                case TextBox text: text.TextChanged += (_, _) => Update(); break;
                case PasswordBox password: password.PasswordChanged += (_, _) => Update(); break;
                case ToggleButton toggle: toggle.Checked += (_, _) => Update(); toggle.Unchecked += (_, _) => Update(); break;
                case RangeBase range: range.ValueChanged += (_, _) => Update(); break;
            }
    }
}
