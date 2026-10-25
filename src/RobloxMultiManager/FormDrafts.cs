using System.Windows;
using System.Windows.Controls;
using System.Globalization;
using TextBox = System.Windows.Controls.TextBox;

namespace RobloxMultiManager;

internal static class FormDrafts
{
    internal static Dictionary<string, string> Capture(FrameworkElement owner, params string[] names)
    {
        var result = new Dictionary<string, string>();
        foreach (var name in names)
        {
            var value = owner.FindName(name) switch
            {
                TextBox text => text.Text,
                System.Windows.Controls.Primitives.ToggleButton check => check.IsChecked?.ToString() ?? "",
                DatePicker date => date.SelectedDate?.ToString("O") ?? "",
                System.Windows.Controls.Primitives.RangeBase range => range.Value.ToString(CultureInfo.InvariantCulture),
                System.Windows.Controls.Primitives.Selector select => select.SelectedIndex.ToString(CultureInfo.InvariantCulture),
                _ => ""
            };
            result[name] = value;
        }
        return result;
    }
    internal static void Restore(FrameworkElement owner, Dictionary<string, string>? draft, params string[] names)
    {
        if (draft is null) return;
        foreach (var name in names)
        {
            if (!draft.TryGetValue(name, out var value) || value.Length > 16384) continue;
            switch (owner.FindName(name))
            {
                case TextBox text: text.Text = value; break;
                case System.Windows.Controls.Primitives.ToggleButton check when bool.TryParse(value, out var enabled): check.IsChecked = enabled; break;
                case System.Windows.Controls.Primitives.RangeBase range when double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number): range.Value = Math.Clamp(number, range.Minimum, range.Maximum); break;
                case DatePicker date when DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var day): date.SelectedDate = day; break;
                case System.Windows.Controls.Primitives.Selector select when int.TryParse(value, out var index) && index >= -1 && index < select.Items.Count: select.SelectedIndex = index; break;
            }
        }
    }
}
