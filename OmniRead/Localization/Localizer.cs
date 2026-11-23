using System.Globalization;
using System.Resources;
using System.Windows.Data;
using System.Windows.Markup;

namespace OmniRead.Localization;

/// <summary>A language the interface can be shown in. An empty code means "whatever Windows uses".</summary>
public sealed record Language(string Code, string Name);

/// <summary>
/// Looks up interface text in Resources/Strings.resx and its translations (Strings.es.resx, ...).
/// To add a language, copy Strings.resx to Strings.&lt;culture&gt;.resx, translate it and list it in <see cref="Languages"/>.
/// </summary>
public static class Localizer {
    private static readonly ResourceManager Resources = new("OmniRead.Resources.Strings", typeof(Localizer).Assembly);

    public static IReadOnlyList<Language> Languages { get; } = [
        new("", ""),
        new("en", "English"),
        new("es", "Español")
    ];

    /// <summary>Selects the interface language for the whole process. Call before any window exists.</summary>
    public static void Apply(string code) {
        if (string.IsNullOrWhiteSpace(code)) {
            return;
        }
        try {
            CultureInfo culture = CultureInfo.GetCultureInfo(code);
            CultureInfo.DefaultThreadCurrentUICulture = culture;
            CultureInfo.CurrentUICulture = culture;
        }
        catch (CultureNotFoundException) {
            // An unknown code in the settings file falls back to the Windows language.
        }
    }

    public static string Get(string key) {
        return Resources.GetString(key, CultureInfo.CurrentUICulture) ?? key;
    }

    public static string Format(string key, params object?[] arguments) {
        return string.Format(CultureInfo.CurrentCulture, Get(key), arguments);
    }
}

/// <summary>XAML access to a string resource: <c>Text="{l:Loc Settings_Font}"</c>.</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class LocExtension(string key) : MarkupExtension {
    [ConstructorArgument("key")]
    public string Key { get; } = key;

    public override object ProvideValue(IServiceProvider serviceProvider) {
        return Localizer.Get(Key);
    }
}

/// <summary>Formats a bound number with a localised pattern; the converter parameter is the resource key.</summary>
public sealed class FormatConverter : IValueConverter {
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) {
        return (parameter is string key) ? Localizer.Format(key, value) : (value?.ToString() ?? "");
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) {
        throw new NotSupportedException();
    }
}