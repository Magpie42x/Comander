using System.IO;
using System.Windows.Media;

namespace Comander.Services;

/// <summary>
/// Шрифты: системные + приложенные файлы (Inter, JetBrains Mono — assets/fonts).
/// Файлы ищутся рядом с exe (папка fonts) или в assets/fonts при запуске из исходников.
/// </summary>
public static class FontService
{
    public static readonly string[] FontNames = { "Segoe UI", "Inter", "JetBrains Mono", "Consolas", "Arial" };

    static string? _fontDir;

    public static string? FindFontDir()
    {
        if (_fontDir != null) return _fontDir;
        var beside = Path.Combine(AppContext.BaseDirectory, "fonts");
        if (Directory.Exists(beside)) return _fontDir = beside;
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "assets", "fonts");
            if (Directory.Exists(candidate)) return _fontDir = candidate;
        }
        return null;
    }

    public static FontFamily Resolve(string name)
    {
        try
        {
            var dir = FindFontDir();
            if (dir != null)
            {
                var file = name switch
                {
                    "Inter" => "Inter[opsz,wght].ttf",
                    "JetBrains Mono" => "JetBrainsMono[wght].ttf",
                    _ => null
                };
                if (file != null && File.Exists(Path.Combine(dir, file)))
                    return new FontFamily(new Uri(dir + Path.DirectorySeparatorChar), "./#" + name);
            }
            return new FontFamily(name);
        }
        catch { return new FontFamily("Segoe UI"); }
    }
}
