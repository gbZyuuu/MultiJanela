using System.Collections;
using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RobloxMultiManager.Core;

namespace RobloxMultiManager;

// One native selection model for the canvas and all existing process actions.
// Only realized cards bind to ClockUtc; no process query runs on this timer.
public sealed class InstanceMap : System.Windows.Controls.ListBox
{
    public static readonly DependencyProperty ClockUtcProperty = DependencyProperty.Register(
        nameof(ClockUtc), typeof(DateTime), typeof(InstanceMap), new PropertyMetadata(DateTime.UtcNow));
    public DateTime ClockUtc { get => (DateTime)GetValue(ClockUtcProperty); private set => SetValue(ClockUtcProperty, value); }
    public static readonly DependencyProperty ProfilesSourceProperty = DependencyProperty.Register(
        nameof(ProfilesSource), typeof(IEnumerable), typeof(InstanceMap), new PropertyMetadata(null, ProfilesChanged));
    public IEnumerable? ProfilesSource { get => (IEnumerable?)GetValue(ProfilesSourceProperty); set => SetValue(ProfilesSourceProperty, value); }
    public static readonly DependencyProperty ProfileRevisionProperty = DependencyProperty.Register(
        nameof(ProfileRevision), typeof(int), typeof(InstanceMap), new PropertyMetadata(0));
    public int ProfileRevision { get => (int)GetValue(ProfileRevisionProperty); private set => SetValue(ProfileRevisionProperty, value); }
    private readonly DispatcherTimer _clock;
    public bool IsClockRunning => _clock.IsEnabled;
    public InstanceMap()
    {
        SelectionMode = System.Windows.Controls.SelectionMode.Extended;
        _clock = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) => ClockUtc = DateTime.UtcNow;
        Loaded += (_, _) => UpdateClock();
        Unloaded += (_, _) => _clock.Stop();
        IsVisibleChanged += (_, _) => UpdateClock();
        System.Windows.Automation.AutomationProperties.SetName(this, RobloxMultiManager.Core.L.T("Canvas de instâncias do Roblox"));
    }
    protected override void OnItemsChanged(NotifyCollectionChangedEventArgs e) { base.OnItemsChanged(e); UpdateClock(); }
    private void UpdateClock()
    {
        if (_clock is null) return;
        ClockUtc = DateTime.UtcNow;
        _clock.IsEnabled = IsLoaded && IsVisible && Items.Count > 0;
    }
    private static void ProfilesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var canvas = (InstanceMap)d;
        if (e.OldValue is INotifyCollectionChanged old) CollectionChangedEventManager.RemoveHandler(old, canvas.ProfilesUpdated);
        if (e.NewValue is INotifyCollectionChanged current) CollectionChangedEventManager.AddHandler(current, canvas.ProfilesUpdated);
        canvas.ProfileRevision++;
    }
    private void ProfilesUpdated(object? sender, NotifyCollectionChangedEventArgs e) => ProfileRevision++;
}

public sealed class SessionTimeConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.FirstOrDefault() is not DateTime start || start == default) return "—";
        if (parameter as string == "start") return start.ToLocalTime().ToString("dd/MM HH:mm:ss", culture);
        var now = values.Length > 1 && values[1] is DateTime clock ? clock : DateTime.UtcNow;
        return FormatElapsed(start, now);
    }
    public static string FormatElapsed(DateTime start, DateTime now)
    {
        var elapsed = now - start;
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        return $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
    }
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class SessionAvatarConverter : IMultiValueConverter
{
    private readonly Dictionary<string, BitmapImage> _cache = new(StringComparer.Ordinal);
    public object? Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.FirstOrDefault() is not RobloxInstanceInfo session || values.Length < 2 || values[1] is not IEnumerable source) return null;
        var profiles = source.OfType<AccountProfile>();
        var profile = session.AccountUserId is { } id ? profiles.FirstOrDefault(p => p.RobloxUserId == id) : null;
        if (profile is null && session.ProfileName is { } name)
        {
            var matches = profiles.Where(p => p.Name == name).Take(2).ToArray();
            if (matches.Length == 1) profile = matches[0];
        }
        if (profile?.AvatarUrl is not { } url || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeFile) return null;
        if (_cache.TryGetValue(url, out var cached)) return cached;
        try
        {
            var image = new BitmapImage();
            image.BeginInit(); image.DecodePixelWidth = 96; image.UriSource = uri; image.EndInit();
            if (_cache.Count >= 128) _cache.Remove(_cache.Keys.First());
            _cache[url] = image;
            return image;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.IO.IOException or NotSupportedException or FormatException) { return null; }
    }
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
