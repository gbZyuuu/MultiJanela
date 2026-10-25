using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Binding = System.Windows.Data.Binding;
using System.Windows.Markup;
using RobloxMultiManager.Core;

namespace RobloxMultiManager;

/// <summary>One shared change signal; WPF weak bindings update existing controls and templates.</summary>
internal sealed class TranslationSource : INotifyPropertyChanged
{
    public static TranslationSource Instance { get; } = new();
    public string Language => L.Language;
    public event PropertyChangedEventHandler? PropertyChanged;
    private TranslationSource() => L.LanguageChanged += (_, _) =>
    {
        void Notify() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess()) dispatcher.BeginInvoke(Notify);
        else Notify();
    };
}

[MarkupExtensionReturnType(typeof(object))]
public sealed class TrExtension(string text) : MarkupExtension, IValueConverter
{
    public string Text { get; } = text;
    public override object ProvideValue(IServiceProvider serviceProvider) => new Binding(nameof(TranslationSource.Language))
    {
        Source = TranslationSource.Instance,
        Mode = BindingMode.OneWay,
        Converter = this
    }.ProvideValue(serviceProvider);
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => L.T(Text);
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
