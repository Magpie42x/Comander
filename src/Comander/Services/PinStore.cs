namespace Comander.Services;

/// <summary>
/// Закрепленные пользователем пути быстрого доступа (файлы и папки, ТЗ 0.9).
/// Живут в сессии, показываются в корне панели (список дисков).
/// </summary>
public static class PinStore
{
    public static List<string> Pins { get; } = new();

    public static void Load(IEnumerable<string>? pins)
    {
        Pins.Clear();
        if (pins != null) Pins.AddRange(pins);
    }

    public static bool IsPinned(string path) =>
        Pins.Any(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));

    /// <summary>Возвращает true, если путь теперь закреплен.</summary>
    public static bool Toggle(string path)
    {
        var existing = Pins.FindIndex(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0) { Pins.RemoveAt(existing); return false; }
        Pins.Add(path);
        return true;
    }

    public static void Unpin(string path) =>
        Pins.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
}
