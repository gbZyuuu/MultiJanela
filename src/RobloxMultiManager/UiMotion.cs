using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace RobloxMultiManager;

/// <summary>Short, replaceable render animations; no layout animation or idle timer.</summary>
public static class UiMotion
{
    private sealed class FeedbackState { public long Generation; }
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<FrameworkElement, FeedbackState> FeedbackStates = new();
    private static bool _initialized;
    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;
        // WPF can skip Loaded broadcasts for controls without instance handlers.
        // Attach lazily on real input instead; this also avoids work for offscreen rows.
        EventManager.RegisterClassHandler(typeof(System.Windows.Controls.Primitives.ButtonBase), UIElement.MouseEnterEvent,
            new System.Windows.Input.MouseEventHandler((sender, e) =>
            { if (!GetFeedback((DependencyObject)sender)) { SetFeedback((DependencyObject)sender, true); Enter(sender,e); } }), true);
        EventManager.RegisterClassHandler(typeof(System.Windows.Controls.Primitives.ButtonBase), UIElement.PreviewMouseLeftButtonDownEvent,
            new MouseButtonEventHandler((sender, e) =>
            { if (!GetFeedback((DependencyObject)sender)) { SetFeedback((DependencyObject)sender, true); Press(sender,e); } }), true);
        EventManager.RegisterClassHandler(typeof(System.Windows.Controls.Primitives.ButtonBase), Keyboard.GotKeyboardFocusEvent,
            new KeyboardFocusChangedEventHandler((sender, _) =>
            { var element=(FrameworkElement)sender; SetFeedback(element,true); if(Enabled && element.IsEnabled)Pulse(element,.86,110); }), true);
        EventManager.RegisterClassHandler(typeof(System.Windows.Controls.TabControl), System.Windows.Controls.Primitives.Selector.SelectionChangedEvent,
            new System.Windows.Controls.SelectionChangedEventHandler((sender, e) =>
            {
                if (ReferenceEquals(sender, e.OriginalSource) && sender is System.Windows.Controls.TabControl tabs)
                    tabs.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
                    {
                        if (tabs.Template?.FindName("PART_SelectedContentHost", tabs) is FrameworkElement content) Reveal(content);
                    }));
            }));
        EventManager.RegisterClassHandler(typeof(System.Windows.Controls.Expander), System.Windows.Controls.Expander.ExpandedEvent,
            new RoutedEventHandler((sender, e) => { if (ReferenceEquals(sender, e.OriginalSource) && sender is System.Windows.Controls.Expander { Content: FrameworkElement body }) Reveal(body); }));
        EventManager.RegisterClassHandler(typeof(System.Windows.Controls.ContextMenu), System.Windows.Controls.ContextMenu.OpenedEvent,
            new RoutedEventHandler((sender, _) => Reveal((FrameworkElement)sender)));
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) => { if (sender is Window { Content: FrameworkElement body }) Reveal(body); }));
    }
    public static bool Enabled => RobloxMultiManager.Core.UiPreferences.Current.Animations && SystemParameters.ClientAreaAnimation && (RenderCapability.Tier >> 16) > 0;

    public static readonly DependencyProperty FeedbackProperty = DependencyProperty.RegisterAttached(
        "Feedback", typeof(bool), typeof(UiMotion), new PropertyMetadata(false, FeedbackChanged));
    public static bool GetFeedback(DependencyObject target) => (bool)target.GetValue(FeedbackProperty);
    public static void SetFeedback(DependencyObject target, bool value) => target.SetValue(FeedbackProperty, value);

    private static void FeedbackChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is not FrameworkElement element) return;
        if ((bool)e.NewValue) { element.MouseEnter += Enter; element.MouseLeave += Leave; element.Unloaded += Unload; element.PreviewMouseLeftButtonDown += Press; element.PreviewMouseLeftButtonUp += Release; element.LostMouseCapture += Release; }
        else { element.MouseEnter -= Enter; element.MouseLeave -= Leave; element.Unloaded -= Unload; element.PreviewMouseLeftButtonDown -= Press; element.PreviewMouseLeftButtonUp -= Release; element.LostMouseCapture -= Release; }
    }
    private static void Press(object sender, MouseButtonEventArgs e)
    { var element = (FrameworkElement)sender; if (Enabled && element.IsEnabled) Pulse(element, .78, 140); }
    private static void Release(object sender, System.Windows.Input.MouseEventArgs e)
    { var element = (FrameworkElement)sender; if (Enabled && element.IsVisible && element.IsEnabled) Pulse(element, .9, 100); else Reset(element); }
    private static void Pulse(FrameworkElement element, double from, int duration)
    {
        // An effect clock leaves the base opacity (including disabled styles) untouched.
        var state = FeedbackStates.GetOrCreateValue(element); var generation = ++state.Generation;
        var pulse = new DoubleAnimation { From = from, Duration = TimeSpan.FromMilliseconds(duration), FillBehavior = FillBehavior.Stop, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        pulse.Completed += (_, _) => { if (state.Generation == generation) element.BeginAnimation(UIElement.OpacityProperty, null); };
        element.BeginAnimation(UIElement.OpacityProperty, pulse, HandoffBehavior.SnapshotAndReplace);
    }
    private static void Enter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        var element = (FrameworkElement)sender;
        if (element.IsEnabled && Enabled && element.IsVisible)
            Pulse(element, .86, 110);
    }
    private static void Leave(object sender, System.Windows.Input.MouseEventArgs e) => Reset((FrameworkElement)sender);
    private static void Unload(object sender, RoutedEventArgs e) => Reset((FrameworkElement)sender);
    private static void Reset(FrameworkElement element)
    { if (FeedbackStates.TryGetValue(element, out var state)) state.Generation++; element.BeginAnimation(UIElement.OpacityProperty, null); }

    public static void Reveal(FrameworkElement element)
    {
        var shift = element.RenderTransform as TranslateTransform ?? new TranslateTransform();
        element.RenderTransform = shift;
        var enabled = Enabled && element.IsVisible;
        Animate(shift, TranslateTransform.YProperty, 7, 0, 170, enabled);
        Animate(element, UIElement.OpacityProperty, 0.7, 1, 140, enabled);
    }

    public static void Animate(DependencyObject target, DependencyProperty property, double from, double to,
        int milliseconds, bool enabled, Action? completed = null)
    {
        // Set the resting value first. Removing a completed/replaced clock cannot
        // bring back a stale opacity or transform, even during rapid navigation.
        void Apply(AnimationTimeline? animation)
        {
            if (target is UIElement element) element.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
            else if (target is Animatable animatable) animatable.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
        }
        Apply(null);
        target.SetCurrentValue(property, to);
        if (!enabled) { completed?.Invoke(); return; }
        var motion = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(milliseconds))
        {
            FillBehavior = FillBehavior.Stop,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        motion.Completed += (_, _) =>
        {
            Apply(null);
            target.SetCurrentValue(property, to);
            completed?.Invoke();
        };
        Apply(motion);
    }
}
