using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Comander.Models;

namespace Comander;

/// <summary>
/// Поиск по всей системе (ТЗ 6/D07): по именам, прямой обход каталогов,
/// без индекса Windows. Фоновый поток + троттлинг обновления списка.
/// </summary>
public partial class SearchWindow : Window
{
    /// <summary>каталог, имя файла — главное окно переходит к результату</summary>
    public event Action<string, string>? NavigateToResult;

    readonly ObservableCollection<FileItem> _results = new();
    readonly ConcurrentQueue<FileItem> _queue = new();
    readonly DispatcherTimer _drainTimer;
    CancellationTokenSource? _cts;
    int _scanned;
    int _found;
    const int MaxResults = 2000;

    public SearchWindow(string startDir)
    {
        InitializeComponent();
        // стиль заголовков вешается в коде: DynamicResource на GridViewColumn
        // (объект вне визуального дерева) в живом приложении не резолвится
        if (FindResource("GridViewHeaderStyle") is Style st && Results.View is GridView gv)
            foreach (var c in gv.Columns) c.HeaderContainerStyle = st;
        Results.ItemsSource = _results;
        DirBox.Text = startDir;
        _drainTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _drainTimer.Tick += (_, _) => Drain();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        // Enter в полях запроса = кнопка «Найти» (пользователь не должен тянуться к мыши)
        QueryBox.KeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Return) { OnGo(this, new RoutedEventArgs()); e.Handled = true; } };
        DirBox.KeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Return) { OnGo(this, new RoutedEventArgs()); e.Handled = true; } };
        Results.PreviewMouseDoubleClick += (_, _) => ActivateSelected();
        Results.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Return) { ActivateSelected(); e.Handled = true; }
        };
        Loaded += (_, _) =>
        {
            QueryBox.Focus();
            // заголовки колонок иначе могут открыться с кистями прежней темы
            Controls.GridViewResize.RefreshHeaderChrome(Results);
        };
    }

    public void SetDir(string dir) => DirBox.Text = dir;

    void OnGo(object sender, RoutedEventArgs e)
    {
        if (_cts != null) StopSearch();
        else StartSearch();
    }

    void StartSearch()
    {
        var query = QueryBox.Text.Trim();
        if (query.Length == 0) return;
        StopSearch();
        _cts = new CancellationTokenSource();
        _results.Clear();
        _scanned = 0;
        _found = 0;
        GoBtn.Content = "Стоп";
        SearchStatus.Text = "Ищу…";

        var roots = new List<string>();
        var dirText = DirBox.Text.Trim();
        if (dirText.Length > 0 && Directory.Exists(dirText)) roots.Add(dirText);
        if (roots.Count == 0)
            foreach (var d in DriveInfo.GetDrives())
            {
                try { if (d.IsReady) roots.Add(d.RootDirectory.FullName); } catch { }
            }

        var token = _cts.Token;
        _drainTimer.Start();
        Task.Run(() =>
        {
            var stack = new Stack<string>();
            foreach (var r in roots) stack.Push(r);
            var options = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = false,
                AttributesToSkip = FileAttributes.ReparsePoint, // не ходим по симлинкам — защита от циклов
            };
            while (stack.Count > 0 && !token.IsCancellationRequested && _found < MaxResults)
            {
                var dir = stack.Pop();
                Interlocked.Increment(ref _scanned);
                try
                {
                    foreach (var f in Directory.EnumerateFiles(dir, "*", options))
                    {
                        if (token.IsCancellationRequested || _found >= MaxResults) break;
                        if (!Path.GetFileName(f).Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
                        try
                        {
                            var fi = new FileInfo(f);
                            _queue.Enqueue(new FileItem { Name = fi.Name, FullPath = f, Size = fi.Length, Modified = fi.LastWriteTime });
                            Interlocked.Increment(ref _found);
                        }
                        catch { }
                    }
                    foreach (var d in Directory.EnumerateDirectories(dir, "*", options))
                        stack.Push(d);
                }
                catch { /* нет доступа — пропускаем ветку */ }
            }
            Dispatcher.Invoke(FinishSearch);
        }, token);
    }

    void StopSearch()
    {
        _cts?.Cancel();
        _cts = null;
        _drainTimer.Stop();
        Drain();
        GoBtn.Content = "Найти";
    }

    void FinishSearch()
    {
        _cts = null;
        _drainTimer.Stop();
        Drain();
        GoBtn.Content = "Найти";
        SearchStatus.Text = $"Найдено: {_found}   ·   проверено каталогов: {_scanned}" +
                            (_found >= MaxResults ? "   ·   достигнут лимит результатов" : "");
    }

    void Drain()
    {
        while (_queue.TryDequeue(out var item)) _results.Add(item);
        if (_cts != null)
            SearchStatus.Text = $"Ищу… найдено: {_found}   ·   каталогов проверено: {_scanned}";
    }

    void ActivateSelected()
    {
        if (Results.SelectedItem is not FileItem it) return;
        var dir = Path.GetDirectoryName(it.FullPath);
        if (dir != null) NavigateToResult?.Invoke(dir, it.Name);
    }
}
