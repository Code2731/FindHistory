using System.Globalization;
using System.Windows.Data;
using System.Windows.Markup;
using WpfBinding = System.Windows.Data.Binding;

namespace FindHistory.Localization;

[MarkupExtensionReturnType(typeof(object))]
public sealed class TranslateExtension : MarkupExtension
{
    public TranslateExtension(string koreanText) => KoreanText = koreanText;

    public string KoreanText { get; }

    public override object ProvideValue(IServiceProvider serviceProvider) => new WpfBinding(nameof(LocalizationManager.Language))
    {
        Source = LocalizationManager.Instance,
        Mode = BindingMode.OneWay,
        Converter = new TranslateConverter(KoreanText)
    }.ProvideValue(serviceProvider);

    private sealed class TranslateConverter(string koreanText) : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            LocalizationManager.Instance.Translate(koreanText);

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            WpfBinding.DoNothing;
    }
}
