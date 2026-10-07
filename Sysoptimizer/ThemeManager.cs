using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Microsoft.Win32;

namespace Sysoptimizer;

/// <param name="Caption">Title-bar colors as COLORREF (0x00BBGGRR); null keeps the Windows default.</param>
public sealed record ThemeInfo(string Key, string Label, bool UpperCase, (int Background, int Text, int Border)? Caption = null);

/// <summary>Bound by the control templates so labels re-case the moment the theme changes.</summary>
public sealed class ThemeState : INotifyPropertyChanged
{
    public static ThemeState Instance { get; } = new();
    private bool _upperCase;

    public bool UpperCase
    {
        get => _upperCase;
        set { if (_upperCase == value) return; _upperCase = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UpperCase))); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class CaseConverter : IMultiValueConverter
{
    public static CaseConverter Instance { get; } = new();

    public object? Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        object? content = values[0] == DependencyProperty.UnsetValue ? null : values[0];
        bool upper = values.Length > 1 && values[1] is true;
        return upper && content is string text ? text.ToUpperInvariant() : content;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public static class ThemeManager
{
    private const string RegistryPath = @"Software\Sysoptimizer";

    public static readonly ThemeInfo[] Themes =
    {
        new("Glass", "Glass", false),
        new("StarTrek", "Star Trek", true, (0x000000, 0x0099FF, 0x0099FF)),
        new("StarCraft", "StarCraft", true, (0x120B07, 0xE6DDCF, 0x4D3A26)),
        new("Cyberpunk", "Cyberpunk", true, (0x0E0A0A, 0xDAE9EC, 0x0AEEFC)), // title in TextPrimary, like the caption buttons beside it
    };

    public static ThemeInfo Current { get; private set; } = Themes[0];
    public static event Action? ThemeChanged;

    public static void LoadSaved()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryPath);
        Apply(key?.GetValue("Theme") as string ?? Themes[0].Key, save: false);
    }

    public static void Apply(string themeKey, bool save = true)
    {
        var theme = Themes.FirstOrDefault(t => t.Key == themeKey) ?? Themes[0];

        var merged = System.Windows.Application.Current.Resources.MergedDictionaries;
        var dictionary = new ResourceDictionary { Source = new Uri($"pack://application:,,,/Themes/{theme.Key}.xaml", UriKind.Absolute) };
        int existing = merged.ToList().FindIndex(d => d.Source?.OriginalString.Contains("Themes/") == true);
        if (existing >= 0) merged[existing] = dictionary; else merged.Insert(0, dictionary);

        Current = theme;
        ThemeState.Instance.UpperCase = theme.UpperCase;
        if (save)
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegistryPath);
            key.SetValue("Theme", theme.Key, RegistryValueKind.String);
        }
        ThemeChanged?.Invoke();
    }
}
