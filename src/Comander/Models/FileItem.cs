using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace Comander.Models;

public class FileItem : INotifyPropertyChanged
{
    public string Name { get; set; } = "";
    public string FullPath { get; set; } = "";
    public bool IsDirectory { get; set; }
    public bool IsDrive { get; set; }
    public bool IsPinned { get; set; }
    public long Size { get; set; }
    public DateTime Modified { get; set; }

    bool _isCursor;
    public bool IsCursor
    {
        get => _isCursor;
        set { if (_isCursor != value) { _isCursor = value; Notify(); } }
    }

    bool _isMarked;
    public bool IsMarked
    {
        get => _isMarked;
        set { if (_isMarked != value) { _isMarked = value; Notify(); } }
    }

    // ---------- иконки: цветная плитка + глиф (запрос пользователя, ТЗ 0.9) ----------
    // Категория = цвет плитки; глиф — вектор Segoe MDL2 либо короткие буквы расширения.

    sealed record IconCat(string Glyph, bool UseMdl2, string Color);

    // глифы Segoe MDL2 Assets
    const string Mdl2Folder = "\uE8B7";
    const string Mdl2Drive = "\uEDA2";
    const string Mdl2Image = "\uEB9F";
    const string Mdl2Audio = "\uEC4F";
    const string Mdl2Video = "\uE714";
    const string Mdl2Archive = "\uF012";
    const string Mdl2Doc = "\uEA90";
    const string Mdl2Config = "\uE713";
    const string Mdl2File = "\uE8A5";

    static readonly IconCat CatFolder = new(Mdl2Folder, true, "#C9A227");
    static readonly IconCat CatDrive = new(Mdl2Drive, true, "#7A869A");
    static readonly IconCat CatFile = new(Mdl2File, true, "#6B7280");
    static readonly IconCat CatPin = new("\uE718", true, "#B66B3B"); // закреп (Pin)

    static readonly Dictionary<string, (IconCat Cat, string Type)> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        // текст и разметка
        [".txt"] = (new("TXT", false, "#8A8F98"), "Текст"),
        [".log"] = (new("LOG", false, "#8A8F98"), "Текст"),
        [".md"] = (new("MD", false, "#6B5CA5"), "Markdown"),
        [".markdown"] = (new("MD", false, "#6B5CA5"), "Markdown"),
        // архивы
        [".zip"] = (new(Mdl2Archive, true, "#D07B3C"), "Архив"),
        [".7z"] = (new(Mdl2Archive, true, "#D07B3C"), "Архив"),
        [".rar"] = (new(Mdl2Archive, true, "#D07B3C"), "Архив"),
        [".tar"] = (new(Mdl2Archive, true, "#D07B3C"), "Архив"),
        [".gz"] = (new(Mdl2Archive, true, "#D07B3C"), "Архив"),
        // приложения
        [".exe"] = (new("EXE", false, "#4CAF6E"), "Приложение"),
        [".msi"] = (new("MSI", false, "#4CAF6E"), "Приложение"),
        // аудио / изображения / видео
        [".mp3"] = (new(Mdl2Audio, true, "#D95D8D"), "Аудио"),
        [".flac"] = (new(Mdl2Audio, true, "#D95D8D"), "Аудио"),
        [".wav"] = (new(Mdl2Audio, true, "#D95D8D"), "Аудио"),
        [".ogg"] = (new(Mdl2Audio, true, "#D95D8D"), "Аудио"),
        [".m4a"] = (new(Mdl2Audio, true, "#D95D8D"), "Аудио"),
        [".png"] = (new(Mdl2Image, true, "#8E6FD8"), "Изображение"),
        [".jpg"] = (new(Mdl2Image, true, "#8E6FD8"), "Изображение"),
        [".jpeg"] = (new(Mdl2Image, true, "#8E6FD8"), "Изображение"),
        [".gif"] = (new(Mdl2Image, true, "#8E6FD8"), "Изображение"),
        [".bmp"] = (new(Mdl2Image, true, "#8E6FD8"), "Изображение"),
        [".webp"] = (new(Mdl2Image, true, "#8E6FD8"), "Изображение"),
        [".svg"] = (new(Mdl2Image, true, "#8E6FD8"), "Изображение"),
        [".ico"] = (new(Mdl2Image, true, "#8E6FD8"), "Изображение"),
        [".mp4"] = (new(Mdl2Video, true, "#D05050"), "Видео"),
        [".mkv"] = (new(Mdl2Video, true, "#D05050"), "Видео"),
        [".avi"] = (new(Mdl2Video, true, "#D05050"), "Видео"),
        [".webm"] = (new(Mdl2Video, true, "#D05050"), "Видео"),
        [".mov"] = (new(Mdl2Video, true, "#D05050"), "Видео"),
        // код
        [".cs"] = (new("CS", false, "#4F8FE8"), "Код"),
        [".py"] = (new("PY", false, "#4F8FE8"), "Код"),
        [".js"] = (new("JS", false, "#4F8FE8"), "Код"),
        [".ts"] = (new("TS", false, "#4F8FE8"), "Код"),
        [".html"] = (new("HTM", false, "#4F8FE8"), "Код"),
        [".css"] = (new("CSS", false, "#4F8FE8"), "Код"),
        [".cpp"] = (new("CPP", false, "#4F8FE8"), "Код"),
        [".java"] = (new("JAV", false, "#4F8FE8"), "Код"),
        [".go"] = (new("GO", false, "#4F8FE8"), "Код"),
        [".rs"] = (new("RS", false, "#4F8FE8"), "Код"),
        [".php"] = (new("PHP", false, "#4F8FE8"), "Код"),
        // конфиги
        [".json"] = (new(Mdl2Config, true, "#3FA7A8"), "Конфиг"),
        [".xml"] = (new(Mdl2Config, true, "#3FA7A8"), "Конфиг"),
        [".yml"] = (new(Mdl2Config, true, "#3FA7A8"), "Конфиг"),
        [".yaml"] = (new(Mdl2Config, true, "#3FA7A8"), "Конфиг"),
        [".ini"] = (new(Mdl2Config, true, "#3FA7A8"), "Конфиг"),
        [".toml"] = (new(Mdl2Config, true, "#3FA7A8"), "Конфиг"),
        // документы и таблицы
        [".pdf"] = (new(Mdl2Doc, true, "#3E6FD9"), "Документ"),
        [".doc"] = (new(Mdl2Doc, true, "#3E6FD9"), "Документ"),
        [".docx"] = (new(Mdl2Doc, true, "#3E6FD9"), "Документ"),
        [".rtf"] = (new(Mdl2Doc, true, "#3E6FD9"), "Документ"),
        [".odt"] = (new(Mdl2Doc, true, "#3E6FD9"), "Документ"),
        [".pptx"] = (new("PPT", false, "#C56A1A"), "Презентация"),
        [".ppt"] = (new("PPT", false, "#C56A1A"), "Презентация"),
        [".xlsx"] = (new("XLS", false, "#3E9E5C"), "Таблица"),
        [".xls"] = (new("XLS", false, "#3E9E5C"), "Таблица"),
        [".csv"] = (new("CSV", false, "#3E9E5C"), "Таблица"),
        // скрипты
        [".ps1"] = (new("PS1", false, "#A0B437"), "Скрипт"),
        [".bat"] = (new("BAT", false, "#A0B437"), "Скрипт"),
        [".cmd"] = (new("CMD", false, "#A0B437"), "Скрипт"),
        [".sh"] = (new("SH", false, "#A0B437"), "Скрипт"),
    };

    IconCat Category
    {
        get
        {
            if (IsPinned) return CatPin;
            if (IsDrive) return CatDrive;
            if (IsDirectory) return CatFolder;
            var ext = Path.GetExtension(Name);
            if (Map.TryGetValue(ext, out var v)) return v.Cat;
            if (ext.Length > 1)
            {
                var letters = ext.TrimStart('.').ToUpperInvariant();
                return new IconCat(letters.Length <= 3 ? letters : letters[..3], false, "#557A9E");
            }
            return CatFile;
        }
    }

    public string IconGlyph => Category.Glyph;
    public Brush IconBg => BrushFor(Category.Color);
    public FontFamily IconFont => Category.UseMdl2 ? Mdl2Font : UiFont;
    public double IconSize => Category.UseMdl2 ? 10.5 : 7.5;

    static readonly FontFamily Mdl2Font = new("Segoe MDL2 Assets");
    static readonly FontFamily UiFont = new("Segoe UI");
    static readonly Dictionary<string, SolidColorBrush> BrushCache = new();

    static Brush BrushFor(string hex)
    {
        if (!BrushCache.TryGetValue(hex, out var brush))
        {
            brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            BrushCache[hex] = brush;
        }
        return brush;
    }

    public string TypeName
    {
        get
        {
            if (IsDirectory) return IsDrive ? "Диск" : "Папка";
            if (Map.TryGetValue(Path.GetExtension(Name), out var v)) return v.Type;
            var ext = Path.GetExtension(Name);
            return string.IsNullOrEmpty(ext) ? "Файл" : ext.TrimStart('.').ToUpperInvariant() + " файл";
        }
    }

    public string SizeText => IsDirectory ? "—" : FormatSize(Size);

    public string ModifiedText => Modified.Year > 1601 ? Modified.ToString("dd.MM.yyyy HH:mm") : "";

    static string FormatSize(long bytes)
    {
        string[] units = { "Б", "КБ", "МБ", "ГБ", "ТБ" };
        double v = bytes; int i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{bytes} {units[0]}" : $"{v:0.#} {units[i]}";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    void Notify([CallerMemberName] string prop = "") => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
}
