using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Comander.Dialogs;

/// <summary>
/// Создание файла: имя.расширение + список популярных расширений.
/// Точка ставится принудительно: слева от неё пользователь вводит имя,
/// справа — расширение. Список фильтруется ТОЛЬКО по части после точки.
/// Стрелки ↑/↓ и колесо мыши ходят по списку (вниз — вниз, без инверсий).
/// </summary>
public partial class NewFileDialog : Window
{
    sealed record Preset(string Label, string Ext)
    {
        public override string ToString() => Label;
    }

    static readonly Preset[] Presets =
    {
        new("Текст (.txt)", ".txt"),
        new("Markdown (.md)", ".md"),
        new("JSON (.json)", ".json"),
        new("YAML (.yml)", ".yml"),
        new("XML (.xml)", ".xml"),
        new("CSV (.csv)", ".csv"),
        new("C# (.cs)", ".cs"),
        new("Python (.py)", ".py"),
        new("JavaScript (.js)", ".js"),
        new("TypeScript (.ts)", ".ts"),
        new("HTML (.html)", ".html"),
        new("CSS (.css)", ".css"),
        new("SQL (.sql)", ".sql"),
        new("PowerShell (.ps1)", ".ps1"),
        new("CMD (.bat)", ".bat"),
        new("Bash (.sh)", ".sh"),
        new("Лог (.log)", ".log"),
        new("Конфиг (.ini)", ".ini"),
    };

    public string FileName { get; private set; } = "";

    bool _guard; // защита от рекурсии TextChanged при принудительной точке

    public NewFileDialog()
    {
        InitializeComponent();
        ValueBox.TextChanged += (_, _) => OnBoxChanged();
        ValueBox.PreviewKeyDown += OnBoxPreviewKeyDown;
        ValueBox.PreviewMouseWheel += OnBoxWheel;
        PresetList.KeyDown += OnListKeyDown;
        PresetList.MouseDoubleClick += (_, _) => Accept();
        Loaded += (_, _) => { ValueBox.Focus(); Refilter(); };
    }

    /// <summary>Точка ставится принудительно в конец; каретка остаётся до точки — дальше ввод имени.</summary>
    void OnBoxChanged()
    {
        if (_guard) return;
        var t = ValueBox.Text;
        var caret = ValueBox.CaretIndex;
        if (!t.Contains('.'))
        {
            _guard = true;
            ValueBox.Text = t + ".";
            ValueBox.CaretIndex = Math.Min(caret, t.Length);
            _guard = false;
        }
        Refilter();
    }

    void OnBoxPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter: // Key.Return — то же значение
                Accept(); e.Handled = true; break;
            case Key.Down:
                MoveSelection(1); e.Handled = true; break;
            case Key.Up:
                MoveSelection(-1); e.Handled = true; break;
            case Key.OemPeriod:
            case Key.OemComma: // точка/запятая — перескакиваем к расширению
                JumpToExt(); e.Handled = true; break;
            case Key.Right:
                // каретка у точки — перепрыгиваем через неё к расширению
                var dot = ValueBox.Text.IndexOf('.');
                if (dot >= 0 && ValueBox.CaretIndex == dot)
                {
                    ValueBox.CaretIndex = dot + 1;
                    e.Handled = true;
                }
                break;
        }
    }

    void JumpToExt()
    {
        if (!ValueBox.Text.Contains('.')) OnBoxChanged();
        var dot = ValueBox.Text.IndexOf('.');
        if (dot >= 0) ValueBox.CaretIndex = dot + 1;
    }

    void MoveSelection(int delta)
    {
        if (PresetList.Items.Count == 0) return;
        PresetList.SelectedIndex = Math.Clamp(PresetList.SelectedIndex + delta, 0, PresetList.Items.Count - 1);
        PresetList.ScrollIntoView(PresetList.SelectedItem);
    }

    /// <summary>Колесо над полем имени крутит список: вниз — вниз, без инверсий.</summary>
    void OnBoxWheel(object sender, MouseWheelEventArgs e)
    {
        MoveSelection(e.Delta > 0 ? -1 : 1);
        e.Handled = true;
    }

    void Refilter()
    {
        var text = ValueBox.Text;
        var dotIdx = text.IndexOf('.');
        // фильтрация только после точки: пока вводится имя (слева), список не сужаем
        string? extPart = dotIdx >= 0 && dotIdx < text.Length - 1 ? text[(dotIdx + 1)..] : null;

        PresetList.Items.Clear();
        foreach (var p in Presets)
        {
            bool match = string.IsNullOrEmpty(extPart)
                || p.Ext.TrimStart('.').StartsWith(extPart, StringComparison.OrdinalIgnoreCase)
                || p.Label.Contains(extPart, StringComparison.OrdinalIgnoreCase);
            if (match) PresetList.Items.Add(p);
        }
        if (PresetList.Items.Count > 0) PresetList.SelectedIndex = 0;
    }

    void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Return) { Accept(); e.Handled = true; }
    }

    void Accept()
    {
        var name = ValueBox.Text.Trim();
        if (string.IsNullOrEmpty(name)) return;
        if (name.EndsWith('.')) name = name[..^1]; // точка-разделитель без расширения — убираем
        // расширение не указано — подставляем выбранный тип
        if (!Path.GetFileName(name).Contains('.'))
        {
            var ext = (PresetList.SelectedItem as Preset)?.Ext
                      ?? (PresetList.Items.Count > 0 ? ((Preset)PresetList.Items[0]).Ext : ".txt");
            name += ext;
        }
        FileName = name;
        DialogResult = true;
    }

    void OnOk(object sender, RoutedEventArgs e) => Accept();
}
