using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Comander.Dialogs;
using Comander.Models;
using Comander.Services;

namespace Comander.Controls;

public partial class FilePanel : UserControl
{
    public ObservableCollection<FileItem> Items { get; } = new();
    List<FileItem> _all = new();
    readonly List<string?> _back = new();
    readonly List<string?> _fwd = new();
    readonly HashSet<string> _marked = new(StringComparer.OrdinalIgnoreCase);
    int _cursor = -1;
    int _anchor = -1;

    /// <summary>Текущий бэкенд: локальный или SFTP.</summary>
    public IPanelBackend Backend { get; private set; } = LocalBackend.Instance;

    /// <summary>Локальный: null = список дисков. SFTP: всегда POSIX-путь от "/".</summary>
    public string? CurrentPath { get; private set; }

    public bool IsRemote => Backend.IsRemote;
    public int MarkedCount => _marked.Count;

    public event Action<FilePanel>? Activated;
    public event Action? Changed;
    /// <summary>Введенный адрес, который панель сама обработать не может (sftp://, имя хоста).</summary>
    public event Action<FilePanel, string>? AddressSubmitted;
    public event Action<string>? StatusMessage;
    /// <summary>Нажата кнопка ▾ — меню режима панели строит главное окно (там профили).</summary>
    public event Action<FilePanel, Button>? ModeMenuRequested;

    /// <summary>Левая панель — строго локальная: без SFTP (распределение ролей панелей, выбор пользователя).</summary>
    public bool LockLocal { get; set; }

    /// <summary>F6 / кнопка ▾ — меню режима панели (локально / SFTP-профили).</summary>
    public void OpenModeMenu() => ModeMenuRequested?.Invoke(this, BtnMode);

    /// <summary>Кисти заголовков колонок зависают на старой теме при смене темы — переприменяем стиль.</summary>
    public void RefreshHeaderChrome() => GridViewResize.RefreshHeaderChrome(List);

    /// <summary>Стиль заголовков вешается в коде: DynamicResource на GridViewColumn
    /// (объект вне визуального дерева) в живом приложении не резолвится.</summary>
    void ApplyHeaderStyle()
    {
        if (FindResource("GridViewHeaderStyle") is not Style st) return;
        if (List.View is GridView gv)
            foreach (var c in gv.Columns) c.HeaderContainerStyle = st;
    }

    /// <summary>Размер служебных элементов панели (чип режима, кнопки) — главное окно задаёт относительно базового шрифта.</summary>
    public void SetChromeFontSize(double size)
    {
        TagText.FontSize = size;
        BtnDisconnect.FontSize = size;
        BtnMode.FontSize = size;
        BtnExtFilter.FontSize = size;
        BtnBack.FontSize = size;
        BtnFwd.FontSize = size;
    }

    public FilePanel()
    {
        InitializeComponent();
        ApplyHeaderStyle();
        List.ItemsSource = Items;
        PreviewMouseDown += (_, _) => Activated?.Invoke(this);
        List.PreviewMouseLeftButtonDown += OnListClick;
        List.PreviewMouseDoubleClick += OnListDoubleClick;
        PathBox.KeyDown += OnPathKeyDown;
        FilterBox.KeyDown += OnFilterKeyDown;
        FilterBox.TextChanged += (_, _) => Refilter();
        BtnMode.Click += (s, _) => ModeMenuRequested?.Invoke(this, (Button)s);
        BtnDisconnect.Click += (_, _) => SetBackend(LocalBackend.Instance, null);
        BtnExtFilter.Click += (_, _) =>
            // отложенно: открытие StaysOpen=False Popup прямо в Click — тот же MouseUp его закрывает
            _ = Dispatcher.BeginInvoke(new Action(ShowExtFilterMenu));
        BtnBack.Click += (_, _) => HistoryBack();
        BtnFwd.Click += (_, _) => HistoryForward();
        TagChip.MouseLeftButtonUp += (_, _) => OpenModeMenu();
        ExtSearch.TextChanged += (_, _) => RebuildExtOptions();
        ExtSearch.PreviewKeyDown += OnExtSearchKeyDown;
        ExtSearch.PreviewMouseWheel += OnExtWheel;
        ExtList.KeyDown += OnExtListKeyDown;
        ExtList.MouseDoubleClick += (_, _) => ApplySelectedExt();
    }

    // ---------- бэкенд ----------

    public void SetBackend(IPanelBackend backend, string? startPath)
    {
        if (!ReferenceEquals(Backend, backend) && Backend.IsRemote) Backend.Dispose();
        Backend = backend;
        _back.Clear();
        _fwd.Clear();
        UpdateTag();
        NavigateTo(startPath, pushHistory: false);
    }

    public void DisconnectBackend()
    {
        if (Backend.IsRemote) Backend.Dispose();
    }

    public void UpdateTag()
    {
        TagText.Text = IsRemote ? "SFTP" : "локально";
        var brush = (Brush)FindResource(IsRemote ? "Accent2Brush" : "AccentBrush");
        TagChip.BorderBrush = brush;
        TagText.Foreground = brush;
        BtnDisconnect.Visibility = IsRemote ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------- загрузка ----------

    public void NavigateTo(string? path, bool pushHistory = true)
    {
        List<FileItem> probe;
        try { probe = Backend.List(Normalize(path)); }
        catch (Exception ex)
        {
            // путь не найден / нет доступа — остаемся где были, показываем причину (больше никакой тишины)
            MessageBox.Show(Window.GetWindow(this), ex.Message, "Comander — не удалось открыть путь",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        path = Normalize(path);
        if (pushHistory && path != CurrentPath) { _back.Add(CurrentPath); _fwd.Clear(); }
        CurrentPath = path;
        _marked.Clear();
        _anchor = -1;
        _all = probe;
        SortAll();
        PathBox.Text = Backend.DisplayPath(CurrentPath);
        Refilter();
        UpdateNavButtons();
    }

    // кнопки ◀▶: ChromeButtonStyle не греет Disabled — имитируем через прозрачность
    void UpdateNavButtons()
    {
        BtnBack.IsEnabled = _back.Count > 0;
        BtnFwd.IsEnabled = _fwd.Count > 0;
        BtnBack.Opacity = BtnBack.IsEnabled ? 1.0 : 0.35;
        BtnFwd.Opacity = BtnFwd.IsEnabled ? 1.0 : 0.35;
    }

    string? Normalize(string? path) => IsRemote && string.IsNullOrEmpty(path) ? "/" : path;

    /// <summary>Перечитать текущий каталог, сохранив отметки.</summary>
    public void ReloadDir()
    {
        try { _all = Backend.List(CurrentPath); }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this), ex.Message, "Comander",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        SortAll();
        Refilter();
    }

    // корень: закрепы -> стандартные папки -> диски; внутри каталогов: папки -> файлы
    static int SortRank(FileItem i) => i.IsPinned ? 0 : i.IsDirectory && !i.IsDrive ? 1 : i.IsDrive ? 2 : 3;

    void SortAll() => _all.Sort((a, b) =>
    {
        var r = SortRank(a).CompareTo(SortRank(b));
        return r != 0 ? r : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
    });

    void Refilter()
    {
        var filter = FilterRow.Visibility == Visibility.Visible ? FilterBox.Text?.Trim() : null;
        Items.Clear();
        foreach (var it in _all)
        {
            if (!string.IsNullOrEmpty(filter) && !it.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                continue;
            // фильтр по расширению (F4): папки, диски и закрепы не трогаем — только файлы
            if (_extFilter != null && !it.IsDirectory && !it.IsDrive &&
                !string.Equals(Path.GetExtension(it.Name).TrimStart('.'), _extFilter,
                               StringComparison.OrdinalIgnoreCase))
                continue;
            it.IsMarked = _marked.Contains(it.FullPath);
            Items.Add(it);
        }
        SetCursor(Items.Count > 0 ? Math.Clamp(_cursor, 0, Items.Count - 1) : -1);
        if (_cursor < 0 && Items.Count > 0) SetCursor(0);
        Changed?.Invoke();
    }

    // ---------- фильтр по расширению (F4) ----------

    string? _extFilter; // lowercase без точки; null — все файлы

    /// <summary>Открыть меню фильтра по расширению (кнопка ext / F4).</summary>
    public void ShowExtFilterMenu()
    {
        ExtSearch.Text = _extFilter ?? "";
        RebuildExtOptions();
        ExtPopup.IsOpen = true;
        ExtSearch.SelectAll();
        Dispatcher.BeginInvoke(() => ExtSearch.Focus());
    }

    void RebuildExtOptions()
    {
        ExtList.Items.Clear();
        foreach (var label in BuildExtOptions(ExtSearch.Text ?? ""))
            ExtList.Items.Add(label);
        ExtList.SelectedIndex = ExtList.Items.Count > 0 ? 0 : -1;
    }

    static string NormalizeExt(string s)
    {
        s = s.Trim().ToLowerInvariant().TrimStart('.').Trim('*').Trim();
        return s == "*" ? "" : s;
    }

    List<string> BuildExtOptions(string query)
    {
        var q = NormalizeExt(query);
        var result = new List<string>();
        if (string.IsNullOrEmpty(q)) result.Add("Все файлы (*)");
        // расширения текущего каталога — по убыванию числа файлов
        var counts = _all.Where(i => !i.IsDirectory && !i.IsDrive)
            .Select(i => Path.GetExtension(i.Name).TrimStart('.').ToLowerInvariant())
            .Where(e => e.Length > 0)
            .GroupBy(e => e)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key)
            .ToList();
        foreach (var g in counts.Take(40))
        {
            if (!string.IsNullOrEmpty(q) && !g.Key.Contains(q, StringComparison.OrdinalIgnoreCase)) continue;
            result.Add($".{g.Key}   ({g.Count()})");
        }
        // введённое вручную — всегда доступно, даже если такого расширения тут нет
        if (!string.IsNullOrEmpty(q) && counts.All(g => g.Key != q))
            result.Add($".{q}   (вручную)");
        return result;
    }

    static string? ExtFromLabel(string label) =>
        label.StartsWith("Все файлы", StringComparison.OrdinalIgnoreCase)
            ? null
            : NormalizeExt(label.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0]);

    void ApplySelectedExt()
    {
        if (ExtList.SelectedItem is string label) ApplyExt(ExtFromLabel(label));
    }

    void ApplyExt(string? ext)
    {
        _extFilter = string.IsNullOrEmpty(ext) ? null : ext;
        ExtPopup.IsOpen = false;
        UpdateExtFilterButton();
        Refilter();
        FocusList();
    }

    void UpdateExtFilterButton()
    {
        BtnExtFilter.Content = _extFilter != null ? "." + _extFilter : "ext";
        BtnExtFilter.Opacity = _extFilter != null ? 1 : 0.55;
        BtnExtFilter.ToolTip = _extFilter != null
            ? $"Фильтр .{_extFilter} активен — F4, чтобы сменить"
            : "Фильтр по расширению файлов (F4)";
    }

    void OnExtSearchKeyDown(object sender, KeyEventArgs e)
    {
        // PreviewKeyDown: стрелки в TextBox класс-хэндлятся, обычный KeyDown не доходит
        switch (e.Key)
        {
            case Key.Down:
                // стрелки ходят по списку, не уводя фокус из строки поиска
                if (ExtList.Items.Count > 0)
                {
                    ExtList.SelectedIndex = Math.Min(ExtList.Items.Count - 1, ExtList.SelectedIndex + 1);
                    ExtList.ScrollIntoView(ExtList.SelectedItem);
                }
                e.Handled = true;
                break;
            case Key.Up:
                if (ExtList.Items.Count > 0)
                    ExtList.SelectedIndex = Math.Max(0, ExtList.SelectedIndex - 1);
                e.Handled = true;
                break;
            case Key.Enter:
                // Enter — применить выбранный в списке пункт (он всегда соответствует введённому)
                ApplySelectedExt();
                e.Handled = true;
                break;
            case Key.Escape:
                ExtPopup.IsOpen = false;
                e.Handled = true;
                break;
        }
    }

    void OnExtListKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter: ApplySelectedExt(); e.Handled = true; break;
            case Key.Escape: ExtPopup.IsOpen = false; e.Handled = true; break;
        }
    }

    /// <summary>Колесо над строкой поиска крутит список: вниз — вниз, без инверсий.</summary>
    void OnExtWheel(object sender, MouseWheelEventArgs e)
    {
        if (ExtList.Items.Count > 0)
        {
            var d = e.Delta > 0 ? -1 : 1;
            ExtList.SelectedIndex = Math.Clamp(ExtList.SelectedIndex + d, 0, ExtList.Items.Count - 1);
            ExtList.ScrollIntoView(ExtList.SelectedItem);
        }
        e.Handled = true;
    }

    // ---------- курсор и отметки ----------

    void SetCursor(int index)
    {
        if (_cursor >= 0 && _cursor < Items.Count) Items[_cursor].IsCursor = false;
        _cursor = index;
        if (_cursor >= 0 && _cursor < Items.Count)
        {
            Items[_cursor].IsCursor = true;
            List.ScrollIntoView(Items[_cursor]);
        }
    }

    FileItem? Current => _cursor >= 0 && _cursor < Items.Count ? Items[_cursor] : null;

    public void MoveCursor(int delta, bool extend)
    {
        if (Items.Count == 0) return;
        int old = _cursor < 0 ? 0 : _cursor;
        int next = Math.Clamp(old + delta, 0, Items.Count - 1);
        if (extend)
        {
            if (_anchor < 0) _anchor = old;
            int a = Math.Min(_anchor, next), b = Math.Max(_anchor, next);
            for (int i = a; i <= b; i++) SetMark(Items[i], true);
        }
        else _anchor = -1;
        SetCursor(next);
        Changed?.Invoke();
    }

    void SetMark(FileItem item, bool on)
    {
        if (on) _marked.Add(item.FullPath); else _marked.Remove(item.FullPath);
        item.IsMarked = on;
    }

    public void ToggleMarkAtCursor()
    {
        var it = Current;
        if (it == null) return;
        SetMark(it, !_marked.Contains(it.FullPath)); // курсор остается на месте (ТЗ 0.4)
        Changed?.Invoke();
    }

    public void ClearMarks()
    {
        _marked.Clear();
        foreach (var it in Items) it.IsMarked = false;
        Changed?.Invoke();
    }

    public List<FileItem> GetTargetItems()
    {
        if (_marked.Count > 0)
            return _all.Where(i => _marked.Contains(i.FullPath)).ToList();
        return Current != null ? new List<FileItem> { Current } : new List<FileItem>();
    }

    // ---------- навигация ----------

    public void OpenCurrent()
    {
        if (Current is { } it) OpenItem(it);
    }

    void OpenItem(FileItem it)
    {
        if (it.IsDirectory)
        {
            NavigateTo(it.FullPath);
            return;
        }
        if (IsRemote)
        {
            // удаленный файл: скачиваем во временную папку и открываем локально
            try
            {
                var tmp = Path.Combine(Path.GetTempPath(), "Comander");
                Directory.CreateDirectory(tmp);
                var local = Path.Combine(tmp, it.Name);
                StatusMessage?.Invoke("Скачиваю " + it.Name + "…");
                Backend.Download(it.FullPath, local);
                StatusMessage?.Invoke("");
                Process.Start(new ProcessStartInfo(local) { UseShellExecute = true });
            }
            catch (Exception ex) { ShowError(ex); }
            return;
        }
        try { Process.Start(new ProcessStartInfo(it.FullPath) { UseShellExecute = true }); }
        catch (Exception ex) { ShowError(ex); }
    }

    public void GoUp()
    {
        if (Backend.IsRoot(CurrentPath)) return;
        NavigateTo(Backend.ParentOf(CurrentPath!));
    }

    public void HistoryBack()
    {
        if (_back.Count == 0) return;
        _fwd.Add(CurrentPath);
        var p = _back[^1];
        _back.RemoveAt(_back.Count - 1);
        NavigateTo(p, pushHistory: false);
    }

    public void HistoryForward()
    {
        if (_fwd.Count == 0) return;
        _back.Add(CurrentPath);
        var p = _fwd[^1];
        _fwd.RemoveAt(_fwd.Count - 1);
        NavigateTo(p, pushHistory: false);
    }

    // ---------- фильтр и адресная строка ----------

    public void ShowFilter()
    {
        FilterRow.Visibility = Visibility.Visible;
        FilterBox.Focus();
        FilterBox.SelectAll();
    }

    void HideFilter()
    {
        FilterBox.Text = "";
        FilterRow.Visibility = Visibility.Collapsed;
        Refilter();
        FocusList();
    }

    public void FocusPath()
    {
        PathBox.Focus();
        PathBox.SelectAll();
    }

    public void FocusList() => List.Focus();

    /// <summary>Поставить курсор на объект по имени (переход из поиска).</summary>
    public void SelectName(string name)
    {
        for (int i = 0; i < Items.Count; i++)
            if (Items[i].Name.Equals(name, StringComparison.OrdinalIgnoreCase)) { SetCursor(i); return; }
    }

    void OnPathKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter || e.Key == Key.Return)
        {
            SubmitAddress(PathBox.Text.Trim());
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            PathBox.Text = Backend.DisplayPath(CurrentPath);
            FocusList();
            e.Handled = true;
        }
    }

    void SubmitAddress(string text)
    {
        FocusList();
        if (string.IsNullOrEmpty(text)) return;
        if (text.StartsWith("sftp://", StringComparison.OrdinalIgnoreCase) || LooksLikeHost(text))
        {
            AddressSubmitted?.Invoke(this, text); // подключение делает MainWindow
            return;
        }
        if (IsRemote)
        {
            NavigateTo(text.StartsWith('/') ? text : Backend.Combine(CurrentPath ?? "/", text));
            return;
        }
        if (Directory.Exists(text))
            NavigateTo(text);
        else
            MessageBox.Show(Window.GetWindow(this), $"Путь не найден:\n{text}", "Comander",
                MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    /// <summary>Голый IP или имя хоста без схемы — трактуем как SFTP-хост.</summary>
    static bool LooksLikeHost(string text)
    {
        if (text.Contains('\\') || (text.Length >= 2 && text[1] == ':')) return false; // Windows-путь
        return System.Text.RegularExpressions.Regex.IsMatch(text,
            @"^([\w.-]+@)?((\d{1,3}\.){3}\d{1,3}|[\w-]+(\.[\w-]+)+)(:\d+)?(/.*)?$");
    }

    void OnFilterKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { HideFilter(); e.Handled = true; }
        else if (e.Key == Key.Enter || e.Key == Key.Return) { FocusList(); e.Handled = true; }
    }

    // ---------- мышь ----------

    static FileItem? ItemFromEvent(MouseEventArgs e)
    {
        var dep = (DependencyObject)e.OriginalSource;
        while (dep != null && dep is not ListViewItem) dep = VisualTreeHelper.GetParent(dep);
        return (dep as ListViewItem)?.DataContext as FileItem;
    }

    void OnListClick(object sender, MouseButtonEventArgs e)
    {
        var it = ItemFromEvent(e);
        if (it != null)
        {
            int idx = Items.IndexOf(it);
            if (idx >= 0) SetCursor(idx);
        }
    }

    void OnListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemFromEvent(e) is { } it) OpenItem(it);
    }

    // ---------- файловые операции ----------

    Window? OwnerWindow => Window.GetWindow(this);

    public void NewFile()
    {
        if (CurrentPath == null) return;
        var dlg = new NewFileDialog() { Owner = OwnerWindow };
        if (dlg.ShowDialog() != true) return;
        try { Backend.CreateFile(CurrentPath, dlg.FileName); ReloadDir(); }
        catch (Exception ex) { ShowError(ex); }
    }

    public void NewFolder()
    {
        if (CurrentPath == null) return;
        var dlg = new InputDialog("Новая папка", "Имя папки:", "Новая папка") { Owner = OwnerWindow };
        if (dlg.ShowDialog() != true) return;
        try { Backend.CreateDirectory(CurrentPath, dlg.Value); ReloadDir(); }
        catch (Exception ex) { ShowError(ex); }
    }

    public void RenameAtCursor()
    {
        var it = Current;
        if (it == null || it.IsDrive) return;
        var dlg = new InputDialog("Переименовать", "Новое имя:", it.Name) { Owner = OwnerWindow };
        if (dlg.ShowDialog() != true || dlg.Value == it.Name) return;
        try { Backend.Rename(it.FullPath, dlg.Value, it.IsDirectory); ReloadDir(); }
        catch (Exception ex) { ShowError(ex); }
    }

    /// <summary>Ctrl+P — закрепить/открепить объект в корне быстрого доступа.</summary>
    public void TogglePinAtCursor()
    {
        var it = Current;
        if (it == null || IsRemote || it.IsDrive) return;
        PinStore.Toggle(it.FullPath);
        ReloadDir();
    }

    /// <summary>Локально — в корзину без диалога; на SFTP — с явным подтверждением (ТЗ 8/D09).
    /// Диски и закрепы — ярлыки/корни, их не удаляем.</summary>
    public void DeleteTargets()
    {
        var targets = GetTargetItems().Where(i => !i.IsDrive && !i.IsPinned).ToList();
        if (targets.Count == 0 || CurrentPath == null) return;
        if (IsRemote)
        {
            var answer = MessageBox.Show(OwnerWindow,
                $"Удалить безвозвратно на сервере: {targets.Count} объект(ов)?\nКорзины на SFTP нет.",
                "Comander — удаление на сервере", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) return;
        }
        var errors = new List<string>();
        foreach (var it in targets)
        {
            try
            {
                Backend.Delete(it.FullPath, it.IsDirectory);
                _marked.Remove(it.FullPath);
            }
            catch (Exception ex) { errors.Add($"{it.Name}: {ex.Message}"); }
        }
        ReloadDir();
        if (errors.Count > 0)
            MessageBox.Show(OwnerWindow, string.Join("\n", errors), "Comander — удаление",
                MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    /// <summary>
    /// Вставка из внутреннего буфера. Локально↔локально — прямое копирование;
    /// с SFTP — скачивание/загрузка; SFTP→SFTP — через временную локальную папку (ТЗ 7.3).
    /// Конфликт имен — пропуск.
    /// </summary>
    public void PasteFrom(FilePanel source, List<FileItem> items, bool cut, TransferProgress? progress = null)
    {
        if (items.Count == 0 || CurrentPath == null) return;
        var dstDir = CurrentPath;
        StatusMessage?.Invoke(cut ? "Перемещение…" : "Копирование…");
        Task.Run(() =>
        {
            int ok = 0, skipped = 0, i = 0;
            var cancelled = false;
            var errors = new List<string>();
            foreach (var it in items)
            {
                i++;
                var dst = Backend.Combine(dstDir, it.Name);
                try
                {
                    if (Backend.Exists(dst)) { skipped++; continue; }
                    progress?.BeginFile(i, it.Name, it.IsDirectory ? 0 : it.Size);

                    if (!source.IsRemote && !IsRemote)
                    {
                        // локально -> локально
                        if (it.IsDirectory)
                        {
                            if (cut) Directory.Move(it.FullPath, dst);
                            else LocalBackend.Instance.Upload(it.FullPath, dst, progress);
                        }
                        else
                        {
                            if (cut) File.Move(it.FullPath, dst);
                            else LocalBackend.Instance.Upload(it.FullPath, dst, progress);
                        }
                    }
                    else if (source.IsRemote && !IsRemote)
                    {
                        source.Backend.Download(it.FullPath, dst, progress);
                        if (cut) source.Backend.Delete(it.FullPath, it.IsDirectory);
                    }
                    else if (!source.IsRemote && IsRemote)
                    {
                        Backend.Upload(it.FullPath, dst, progress);
                        if (cut) LocalBackend.Instance.Delete(it.FullPath, it.IsDirectory);
                    }
                    else
                    {
                        // удаленно -> удаленно: через временную локальную папку
                        var tmp = Path.Combine(Path.GetTempPath(), "Comander", Guid.NewGuid().ToString("N"));
                        Directory.CreateDirectory(tmp);
                        try
                        {
                            var local = Path.Combine(tmp, it.Name);
                            source.Backend.Download(it.FullPath, local, progress);
                            Backend.Upload(local, dst, progress);
                        }
                        finally { try { Directory.Delete(tmp, true); } catch { } }
                        if (cut) source.Backend.Delete(it.FullPath, it.IsDirectory);
                    }
                    ok++;
                }
                catch (OperationCanceledException) { cancelled = true; TryDeletePartial(dst, it.IsDirectory); break; }
                catch (Exception ex) { errors.Add($"{it.Name}: {ex.Message}"); }
            }
            var summary = cancelled
                ? $"Перенос отменён: перенесено {ok} из {items.Count}"
                : $"Готово: {ok}" + (skipped > 0 ? $", пропущено (имя занято): {skipped}" : "");
            Dispatcher.Invoke(() =>
            {
                ReloadDir();
                StatusMessage?.Invoke("");
                progress?.Finish(summary);
                if (errors.Count > 0)
                    MessageBox.Show(OwnerWindow, string.Join("\n", errors), "Comander — вставка",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                else if (skipped > 0 && !cancelled)
                    MessageBox.Show(OwnerWindow, $"Готово: {ok}. Пропущено (имя занято): {skipped}.",
                        "Comander — вставка", MessageBoxButton.OK, MessageBoxImage.Information);
            });
        });
    }

    /// <summary>Убрать недокопированный объект при отмене (dst точно наш — конфликты пропускались).</summary>
    void TryDeletePartial(string dst, bool isDir)
    {
        try
        {
            if (IsRemote) Backend.Delete(dst, isDir);
            else if (isDir) Directory.Delete(dst, true);
            else File.Delete(dst);
        }
        catch { }
    }

    void ShowError(Exception ex) =>
        MessageBox.Show(OwnerWindow, ex.Message, "Comander", MessageBoxButton.OK, MessageBoxImage.Warning);

    // ---------- внешний вид ----------

    bool _bgOn;   // фоновая картинка включена
    bool _solo;   // панель одна на экране

    /// <summary>Активность панели = подсвеченная рамка (без внутреннего свечения —
    /// фон внутри виден так же, как на неактивной). Прозрачность панелей одинаковая,
    /// задаётся фоном кисти, не Opacity, чтобы текст и иконки были читаемы.</summary>
    public void SetActiveVisual(bool active)
    {
        Opacity = 1.0;
        RootBorder.BorderBrush = (Brush)FindResource(active ? "AccentBrush" : "BorderBrush");
        RootBorder.BorderThickness = active ? new Thickness(1.6) : new Thickness(1);
    }

    /// <summary>Прозрачность панели: обычная (фон включен — призрачная, обе одинаково),
    /// соло — ещё прозрачнее; фон выключен — плотная.</summary>
    public void SetSolo(bool solo, bool backgroundOn)
    {
        _solo = solo;
        _bgOn = backgroundOn;
        UpdatePaneBackground();
    }

    void UpdatePaneBackground() =>
        RootBorder.Background = (Brush)FindResource(_bgOn ? (_solo ? "PanelSoloBrush" : "PanelGhostBrush") : "PanelBrush");
}
