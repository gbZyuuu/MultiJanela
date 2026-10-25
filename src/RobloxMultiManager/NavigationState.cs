using System.Windows;
namespace RobloxMultiManager;
public static class NavigationState
{
    public static readonly DependencyProperty ActiveProperty = DependencyProperty.RegisterAttached("Active", typeof(bool), typeof(NavigationState), new PropertyMetadata(false));
    public static bool GetActive(DependencyObject value) => (bool)value.GetValue(ActiveProperty);
    public static void SetActive(DependencyObject value, bool active) => value.SetValue(ActiveProperty, active);
}
