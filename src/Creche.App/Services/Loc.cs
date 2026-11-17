using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;
using Creche.Domain;

namespace Creche.App.Services;

public sealed record Option<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// JSON-based localisation. Language files are embedded resources (Resources/Languages/{code}.json);
/// English is the fallback for any missing key. Arabic switches the whole UI to right-to-left.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    public static Loc Instance { get; } = new();
    public static readonly IReadOnlyList<Option<string>> Languages =
    [
        new("en", "English"), new("fr", "Français"), new("ar", "العربية"),
    ];

    private readonly Dictionary<string, string> _fallback;
    private Dictionary<string, string> _strings;

    private Loc()
    {
        _fallback = Read("en");
        _strings = _fallback;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? LanguageChanged;

    public string Language { get; private set; } = "en";

    /// <summary>Bumped on every language change; XAML bindings listen to it to refresh their text.</summary>
    public int Version { get; private set; }

    public FlowDirection FlowDirection => Language == "ar" ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

    public string this[string key] => _strings.TryGetValue(key, out var value) ? value : _fallback.GetValueOrDefault(key, key);

    public bool Has(string key) => _strings.ContainsKey(key) || _fallback.ContainsKey(key);

    public void SetLanguage(string? code)
    {
        code = Languages.Any(l => l.Value == code) ? code! : "en";
        _strings = code == "en" ? _fallback : Read(code);
        Language = code;
        // Latin digits and Gregorian dates in every language, local separators and month names.
        var culture = CultureInfo.GetCultureInfo(code switch { "fr" => "fr-FR", "ar" => "ar-DZ", _ => "en-GB" });
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        if (System.Windows.Application.Current is { } app)
            foreach (Window window in app.Windows) Apply(window);
        Version++;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Version)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FlowDirection)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Makes date pickers and other culture-aware controls of a window follow the chosen language.</summary>
    public static void Apply(Window window)
        => window.Language = XmlLanguage.GetLanguage(CultureInfo.CurrentCulture.IetfLanguageTag);

    public static string T(string key) => Instance[key];

    public static string T(string key, params object?[] args)
    {
        try { return string.Format(CultureInfo.CurrentCulture, Instance[key], args); }
        catch (FormatException) { return Instance[key]; }
    }

    public static string Enum<T>(T value) where T : struct, System.Enum => Instance[$"Enum.{typeof(T).Name}.{value}"];

    public static string EnumObject(object value) => Instance[$"Enum.{value.GetType().Name}.{value}"];

    public static List<Option<T>> Options<T>() where T : struct, System.Enum
        => System.Enum.GetValues<T>().Select(v => new Option<T>(v, Enum(v))).ToList();

    /// <summary>Options preceded by an "All" entry, for filters.</summary>
    public static List<Option<TEnum?>> OptionsWithAll<TEnum>() where TEnum : struct, System.Enum
    {
        var list = new List<Option<TEnum?>> { new(null, T("Common.All")) };
        list.AddRange(System.Enum.GetValues<TEnum>().Select(v => new Option<TEnum?>(v, Enum(v))));
        return list;
    }

    /// <summary>Translated text for a broken business rule (falls back to the English message).</summary>
    public static string Error(BusinessRuleException ex)
    {
        string key = $"Error.{ex.Code}";
        return Instance.Has(key) ? T(key, ex.Args) : ex.Message;
    }

    private static Dictionary<string, string> Read(string code)
    {
        var assembly = Assembly.GetExecutingAssembly();
        string? name = assembly.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith($".Languages.{code}.json", StringComparison.OrdinalIgnoreCase));
        if (name is null) return new Dictionary<string, string>();
        using var stream = assembly.GetManifestResourceStream(name)!;
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? new Dictionary<string, string>();
    }
}

/// <summary>XAML: <c>Text="{loc:T Children.Title}"</c> — updates live when the language changes.</summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class TExtension(string key) : MarkupExtension
{
    private sealed class Lookup : IValueConverter
    {
        public static readonly Lookup Instance = new();
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => Loc.Instance[(string)parameter];
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }

    [ConstructorArgument("key")]
    public string Key { get; set; } = key;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding(nameof(Loc.Version))
        {
            Source = Loc.Instance,
            Mode = BindingMode.OneWay,
            Converter = Lookup.Instance,
            ConverterParameter = Key,
        };
        return binding.ProvideValue(serviceProvider);
    }
}
