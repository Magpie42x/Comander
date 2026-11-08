using System.Windows;

namespace Comander.Services;

/// <summary>
/// Переключение тем. Тема — ResourceDictionary с токенами цветов (ТЗ 0.8):
/// пользовательская тема позже сможет быть таким же файлом, загруженным из папки themes рядом с exe.
/// </summary>
public static class ThemeService
{
    public static readonly string[] Names = { "Light", "Dark", "DarkPurple" };

    public static void Apply(string name)
    {
        if (!Names.Contains(name)) name = "DarkPurple";
        var dict = new ResourceDictionary { Source = new Uri($"Themes/Theme.{name}.xaml", UriKind.Relative) };
        var merged = Application.Current.Resources.MergedDictionaries;
        var old = merged.FirstOrDefault(d => d.Source?.OriginalString.Contains("Themes/Theme.") == true);
        if (old != null) merged.Remove(old);
        merged.Insert(0, dict);
    }
}
