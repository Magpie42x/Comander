using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Comander.Controls;
using Comander.Dialogs;
using Comander.Models;
using Comander.Services;
using Microsoft.Win32;

namespace Comander;

public partial class MainWindow : Window
{
    FilePanel _active;
    readonly SessionData _session;
    readonly DispatcherTimer _clock;
    string _transientStatus = "";

    // внутренний буфер обмена (буфер Проводника — открытая деталь, ТЗ 13)
    FilePanel? _clipSource;
    List<FileItem> _clipItems = new();
    bool _clipCut;

    // фоны
    string? _bgDir;
    List<string> _bgFiles = new();
    int _bgIndex = -1;

    // глобальный хоткей Ctrl+Alt+C
    const int HotkeyId = 0xC0DE;
    const int WM_HOTKEY = 0x0312;
    const uint MOD_ALT = 0x0001, MOD_CONTROL = 0x0002, MOD_NOREPEAT = 0x4000;
    HwndSource? _hwndSource;

    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public MainWindow()
    {
        InitializeComponent();
        _session = SessionService.Load();
        PinStore.Load(_session.PinnedPaths);
        Terminal.SetShellMode(_session.ShellMode);
        Terminal.SetRemoteShellMode(_session.RemoteShellMode);
        ThemeService.Apply(_session.Theme);
        UpdateThemeTabs();
        ApplyFont(_session.FontFamily, _session.FontSize);

        WirePanel(PanelLeft);
        WirePanel(PanelRight);

        // активная панель назначается ДО первой загрузки каталогов:
        // NavigateTo вызывает Changed -> UpdateStatus, которому нужен _active
        _active = _session.ActiveLeft || !_session.RightVisible ? PanelLeft : PanelRight;

        PanelLeft.SetBackend(LocalBackend.Instance, LocalPathOrNull(_session.LeftPath));
        PanelRight.SetBackend(LocalBackend.Instance, LocalPathOrNull(_session.RightPath));
        PanelLeft.LockLocal = true; // левая панель — всегда этот ПК; правая — локально или SFTP (выбор пользователя)

        SetRightVisible(_session.RightVisible);

        InitBackgrounds();
        ApplyBackgroundEnabled(_session.BackgroundEnabled);

        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) => ClockText.Text = DateTime.Now.ToString("dd.MM.yyyy · HH:mm");
        _clock.Start();
        ClockText.Text = DateTime.Now.ToString("dd.MM.yyyy · HH:mm");

        Loaded += (_, _) =>
        {
            ApplyActive();
            // филлерные заголовки (справа и под скроллбаром) стиль колонки игнорируют —
            // переприменяем к живым контейнерам (заодно лечит кисти после смены темы)
            PanelLeft.RefreshHeaderChrome();
            PanelRight.RefreshHeaderChrome();
        };
        Closing += OnClosing;
        Terminal.CloseRequested += () => { if (_consoleVisible) ToggleConsole(); };
    }



    /// <summary>SFTP-пути при старте не восстанавливаем (нужен пароль) — только локальные.</summary>
    static string? LocalPathOrNull(string? path) =>
        path != null && !path.StartsWith("sftp://", StringComparison.OrdinalIgnoreCase) && Directory.Exists(path) ? path : null;

    void WirePanel(FilePanel p)
    {
        p.Activated += SetActive;
        p.Changed += UpdateStatus;
        p.StatusMessage += OnPanelStatus;
        p.AddressSubmitted += OnAddressSubmitted;
        p.ModeMenuRequested += OnPanelModeMenu;
    }

    // ---------- активная панель ----------

    void SetActive(FilePanel panel)
    {
        _active = panel;
        ApplyActive();
    }

    void ApplyActive()
    {
        PanelLeft.SetActiveVisual(_active == PanelLeft);
        PanelRight.SetActiveVisual(_active == PanelRight);
        _active.FocusList();
        UpdateStatus();
    }

    void OnPanelStatus(string msg)
    {
        _transientStatus = msg;
        UpdateStatus();
    }

    void UpdateStatus()
    {
        // контекстные подсказки в нижней строке: всегда то, что доступно прямо сейчас (ТЗ 0.9)
        if (_active.MarkedCount > 0)
            HintsText.Text = "Ctrl+C — копировать   ·   Ctrl+X — вырезать   ·   Delete — удалить   ·   Esc — снять отметки";
        else if (_clipItems.Count > 0)
            HintsText.Text = "Ctrl+V — вставить сюда   ·   Ctrl+C — скопировать ещё";
        else
            HintsText.Text = "F3 — по имени   ·   F4 — по типу   ·   F6 — локально/SFTP   ·   Ctrl+←→ — перенос   ·   Ctrl+F — поиск   ·   Ctrl+` — терминал";

        if (!string.IsNullOrEmpty(_transientStatus))
        {
            StatusText.Text = _transientStatus;
            return;
        }
        StatusText.Text = $"Отмечено: {_active.MarkedCount}   ·   объектов: {_active.Items.Count}" +
                          (_clipItems.Count > 0 ? $"   ·   в буфере: {_clipItems.Count}{(_clipCut ? " (вырезать)" : "")}" : "");
    }

    // ---------- встроенный терминал (Ctrl+`) ----------

    bool _consoleVisible;
    double _terminalHeight = 380; // запоминаем высоту между открытиями

    void OnToggleTerminal(object sender, RoutedEventArgs e) => ToggleConsole();

    void ToggleConsole()
    {
        _consoleVisible = !_consoleVisible;
        if (_consoleVisible)
        {
            TerminalRow.Height = new GridLength(_terminalHeight);
            Terminal.Visibility = Visibility.Visible;
            TerminalSplitter.Visibility = Visibility.Visible;
            if (_active.IsRemote && _active.Backend is SftpBackend sftp)
            {
                Terminal.SetRemote(sftp);   // shell на удалённой машине
            }
            else
            {
                if (Terminal.IsRemoteSession) Terminal.StopRemote();
                Terminal.SetCwd(_active.CurrentPath);   // локальный шелл в каталоге активной панели
            }
            Terminal.FocusInput();
        }
        else
        {
            if (TerminalRow.Height.Value > 40) _terminalHeight = TerminalRow.Height.Value;
            TerminalRow.Height = new GridLength(0);
            Terminal.Visibility = Visibility.Collapsed;
            TerminalSplitter.Visibility = Visibility.Collapsed;
            _active.FocusList();
        }
    }

    // ---------- поиск по системе (Ctrl+F) ----------

    SearchWindow? _searchWindow;

    void OpenSearch()
    {
        var startDir = _active.IsRemote ? "" : _active.CurrentPath ?? "";
        if (_searchWindow == null || !_searchWindow.IsLoaded)
        {
            _searchWindow = new SearchWindow(startDir) { Owner = this };
            _searchWindow.NavigateToResult += OnSearchNavigate;
            _searchWindow.Closed += (_, _) => _searchWindow = null;
            _searchWindow.Show();
        }
        else
        {
            if (startDir.Length > 0) _searchWindow.SetDir(startDir);
            _searchWindow.Activate();
        }
    }

    void OnSearchNavigate(string dir, string name)
    {
        // если активная панель — SFTP, результат открываем во второй (локальной) панели, чтобы не рвать соединение
        var p = _active;
        if (p.IsRemote)
        {
            var other = p == PanelLeft ? PanelRight : PanelLeft;
            if (!other.IsVisible) { SetRightVisible(true); }
            p = other;
        }
        if (p.IsRemote) p.SetBackend(LocalBackend.Instance, dir);
        else p.NavigateTo(dir);
        p.SelectName(name);
        SetActive(p);
        Activate();
    }

    void SwitchPanel()
    {
        if (!PanelRight.IsVisible) return;
        SetActive(_active == PanelLeft ? PanelRight : PanelLeft);
    }

    void ToggleSecondPanel()
    {
        bool show = !PanelRight.IsVisible;
        SetRightVisible(show);
        if (show) SetActive(PanelRight);            // при показе фокус на новой панели (ТЗ 0.7)
        else if (_active == PanelRight) SetActive(PanelLeft);
    }

    void SetRightVisible(bool visible)
    {
        PanelRight.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        RightCol.Width = visible ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        GapCol.Width = visible ? new GridLength(44) : new GridLength(0);
        ApplySoloStates();
    }

    /// <summary>В одиночном режиме при включенном фоне панель полупрозрачная — фон видно.</summary>
    void ApplySoloStates()
    {
        bool solo = !PanelRight.IsVisible;
        PanelLeft.SetSolo(solo, _session.BackgroundEnabled);
        PanelRight.SetSolo(false, _session.BackgroundEnabled);
    }

    // ---------- SFTP ----------

    void OnAddressSubmitted(FilePanel panel, string text)
    {
        if (panel.LockLocal)
        {
            FlashStatus("Левая панель — только локальный режим; SFTP — в правой (F6)");
            panel.ReloadDir();
            return;
        }
        try
        {
            var t = text.StartsWith("sftp://", StringComparison.OrdinalIgnoreCase) ? text[7..] : text;
            var path = "/";
            var slashIdx = t.IndexOf('/');
            if (slashIdx >= 0) { path = t[slashIdx..]; t = t[..slashIdx]; }
            string? user = null;
            var atIdx = t.IndexOf('@');
            if (atIdx >= 0) { user = t[..atIdx]; t = t[(atIdx + 1)..]; }
            var port = 22;
            var colonIdx = t.IndexOf(':');
            if (colonIdx >= 0) { port = int.Parse(t[(colonIdx + 1)..]); t = t[..colonIdx]; }
            var host = t;
            if (string.IsNullOrWhiteSpace(host)) return;

            var userDlg = new InputDialog("SFTP — " + host, "Пользователь:", user ?? Environment.UserName) { Owner = this };
            if (userDlg.ShowDialog() != true) { panel.ReloadDir(); return; }
            var passDlg = new InputDialog("SFTP — " + host,
                $"Пароль для {userDlg.Value}@{host}\n(оставь пустым — попробую ключи из ~/.ssh):",
                isPassword: true) { Owner = this };
            if (passDlg.ShowDialog() != true) { panel.ReloadDir(); return; }

            ConnectSftpFlow(panel, host, port, userDlg.Value, path, passDlg.Value, askSave: true);
        }
        catch (Exception ex)
        {
            OnPanelStatus("");
            MessageBox.Show(this, "Не понял адрес: " + ex.Message, "Comander — SFTP",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// Ядро SFTP-подключения: коннект в фоне, по успеху — смена бэкенда панели.
    /// askSave: после успеха предложить сохранить точку в профили (пароль — в DPAPI).
    /// </summary>
    void ConnectSftpFlow(FilePanel panel, string host, int port, string user, string path,
                         string? password, bool askSave)
    {
        OnPanelStatus($"Подключение к {user}@{host}…");
        Task.Run(() => SftpBackend.Connect(host, port, user, string.IsNullOrEmpty(password) ? null : password))
            .ContinueWith(task => Dispatcher.Invoke(() =>
            {
                OnPanelStatus("");
                if (task.IsFaulted)
                {
                    var ex = task.Exception?.InnerException ?? task.Exception;
                    MessageBox.Show(this, $"Не удалось подключиться к {host}:\n{ex?.Message}",
                        "Comander — SFTP", MessageBoxButton.OK, MessageBoxImage.Error);
                    panel.ReloadDir();
                    return;
                }
                panel.SetBackend(task.Result, path);
                if (!askSave) return;
                var name = $"{user}@{host}" + (port != 22 ? $":{port}" : "");
                if (_session.Profiles.Any(pr => pr.Name == name)) return; // уже в профилях
                // сохранение: даём своё имя (видно в меню вместо адреса); отмена — не сохранять
                var nameDlg = new InputDialog("Сохранить SFTP-подключение",
                    $"Адрес: {name}\n\nИмя подключения — будет видно в меню вместо адреса.\nОставь пустым, чтобы видеть адрес.\nПароль сохранится в DPAPI (только этот ПК и пользователь).",
                    "") { Owner = this };
                if (nameDlg.ShowDialog() != true) return;
                _session.Profiles.Add(new SftpProfile
                {
                    Name = name, Alias = nameDlg.Value.Trim(), Host = host, Port = port, User = user,
                    ProtectedPassword = string.IsNullOrEmpty(password) ? "" : SecretStore.Protect(password),
                });
                SessionService.Save(_session);
            }));
    }

    /// <summary>Меню режима панели (кнопка ▾ / F6): локальный режим, новое SFTP-подключение, сохранённые профили.</summary>
    void OnPanelModeMenu(FilePanel panel, Button btn)
    {
        if (panel.LockLocal)
        {
            FlashStatus("Левая панель закреплена за локальным режимом; SFTP — правая (F6)");
            return;
        }
        var menu = new ContextMenu();

        var local = new MenuItem { Header = "🏠 Локальный режим", IsEnabled = panel.IsRemote };
        local.Click += (_, _) => panel.SetBackend(LocalBackend.Instance, null);
        menu.Items.Add(local);

        var fresh = new MenuItem { Header = "🌐 SFTP — новое подключение…" };
        fresh.Click += (_, _) =>
        {
            var dlg = new InputDialog("SFTP — новое подключение",
                "Адрес (host, user@host:port/path — любой формат):", "") { Owner = this };
            if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.Value))
                OnAddressSubmitted(panel, dlg.Value.Trim());
        };
        menu.Items.Add(fresh);

        if (_session.Profiles.Count > 0)
        {
            menu.Items.Add(new Separator());
            foreach (var prof in _session.Profiles)
            {
                var p1 = prof;
                // своё имя вместо адреса; адрес (IP) — в подсказке при наведении
                var item = new MenuItem { Header = "🌐 " + ProfileDisplay(p1), ToolTip = p1.Name };
                item.Click += (_, _) => ConnectProfile(panel, p1);
                menu.Items.Add(item);
            }
        }

        menu.PlacementTarget = btn;
        menu.Placement = PlacementMode.Bottom;
        // отложенно: если открыть ContextMenu прямо в обработчике Click,
        // тот же MouseUp доезжает до меню и мгновенно его закрывает (меню «мигает»)
        _ = Dispatcher.BeginInvoke(new Action(() => menu.IsOpen = true));
    }

    static string ProfileDisplay(SftpProfile p) => string.IsNullOrEmpty(p.Alias) ? p.Name : p.Alias;

    void ConnectProfile(FilePanel panel, SftpProfile prof)
    {
        string? pwd = null;
        if (!string.IsNullOrEmpty(prof.ProtectedPassword))
        {
            pwd = SecretStore.Unprotect(prof.ProtectedPassword);
            if (pwd == "")
            {
                // DPAPI не расшифровал (другой ПК/пользователь) — спрашиваем пароль заново
                var dlg = new InputDialog("SFTP — " + prof.Name,
                    $"Пароль для {prof.User}@{prof.Host}\n(сохранённый не расшифровался на этом ПК):",
                    isPassword: true) { Owner = this };
                if (dlg.ShowDialog() != true) return;
                pwd = dlg.Value;
            }
        }
        ConnectSftpFlow(panel, prof.Host, prof.Port, prof.User, "/", pwd, askSave: false);
    }

    // ---------- темы, шрифты, фон ----------

    void OnThemeClick(object sender, RoutedEventArgs e)
    {
        var name = sender == BtnThemeLight ? "Light" : sender == BtnThemeDark ? "Dark" : "DarkPurple";
        _session.Theme = name;
        ThemeService.Apply(name);
        UpdateThemeTabs();
        PanelLeft.UpdateTag();
        PanelRight.UpdateTag();
        ApplySoloStates();
        ApplyActive(); // ореол активной панели пересобирается с цветом новой темы
        // кисти заголовков колонок не перечитываются при замене словаря темы — дёргаем вручную
        PanelLeft.RefreshHeaderChrome();
        PanelRight.RefreshHeaderChrome();
    }

    void UpdateThemeTabs()
    {
        BtnThemeLight.Tag = _session.Theme == "Light" ? "active" : null;
        BtnThemeDark.Tag = _session.Theme == "Dark" ? "active" : null;
        BtnThemePurple.Tag = _session.Theme == "DarkPurple" ? "active" : null;
    }

    void ApplyFont(string family, double size)
    {
        _session.FontFamily = family;
        _session.FontSize = size;
        Application.Current.Resources["AppFontFamily"] = FontService.Resolve(family);
        Application.Current.Resources["AppFontSize"] = size;

        // служебные надписи (статусбар, шапка терминала, чипы/кнопки панелей) — относительно базового,
        // но не мельче 11, чтобы при мелком базовом шрифте хром не превращался в пыль
        var chrome = Math.Max(11, size - 2);
        StatusText.FontSize = chrome;
        HintsText.FontSize = chrome;
        Terminal.SetChromeFontSize(chrome);
        PanelLeft.SetChromeFontSize(chrome);
        PanelRight.SetChromeFontSize(chrome);
    }

    void OnSettings(object sender, RoutedEventArgs e)
    {
        var dlg = new SettingsWindow(_session) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        ApplyFont(dlg.SelectedFont, dlg.SelectedSize);
        _session.ShellMode = dlg.SelectedShell;
        Terminal.SetShellMode(dlg.SelectedShell);
        _session.RemoteShellMode = dlg.SelectedRemoteShell;
        Terminal.SetRemoteShellMode(dlg.SelectedRemoteShell);
        _session.AutoStart = dlg.AutoStart;
        SetAutoStart(dlg.AutoStart);
    }

    static void SetAutoStart(bool on)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (key == null) return;
            if (on) key.SetValue("Comander", $"\"{Environment.ProcessPath}\"");
            else key.DeleteValue("Comander", throwOnMissingValue: false);
        }
        catch { /* нет прав — не критично */ }
    }

    void InitBackgrounds()
    {
        _bgDir = FindBackgroundsDir();
        if (_bgDir == null) return;
        _bgFiles = Directory.EnumerateFiles(_bgDir)
            .Where(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
            .Where(f => !f.EndsWith("_preview.jpg", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f)
            .ToList();
        if (!string.IsNullOrEmpty(_session.BackgroundFile))
            _bgIndex = _bgFiles.FindIndex(f => string.Equals(Path.GetFileName(f), _session.BackgroundFile, StringComparison.OrdinalIgnoreCase));
        if (_bgIndex < 0) _bgIndex = _bgFiles.Count > 0 ? 0 : -1;
        ShowCurrentBackground();
    }

    static string? FindBackgroundsDir()
    {
        var beside = Path.Combine(AppContext.BaseDirectory, "backgrounds");
        if (Directory.Exists(beside)) return beside;
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "assets", "backgrounds");
            if (Directory.Exists(candidate)) return candidate;
        }
        return null;
    }

    void ShowCurrentBackground()
    {
        if (_bgIndex < 0 || _bgIndex >= _bgFiles.Count) { BgArt.Source = null; return; }
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(_bgFiles[_bgIndex]);
            bmp.DecodePixelWidth = 2048;
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            BgArt.Source = bmp;
            _session.BackgroundFile = Path.GetFileName(_bgFiles[_bgIndex]);
        }
        catch { BgArt.Source = null; }
    }

    /// <summary>Выбор фона списком из имеющихся.</summary>
    void OnBgMenu(object sender, RoutedEventArgs e)
    {
        if (_bgFiles.Count == 0) return;
        var menu = new ContextMenu();
        for (int i = 0; i < _bgFiles.Count; i++)
        {
            var idx = i;
            var item = new MenuItem
            {
                Header = (idx == _bgIndex ? "●  " : "    ") + Path.GetFileNameWithoutExtension(_bgFiles[i]),
            };
            item.Click += (_, _) =>
            {
                _bgIndex = idx;
                ShowCurrentBackground();
                if (!_session.BackgroundEnabled) ApplyBackgroundEnabled(true);
            };
            menu.Items.Add(item);
        }
        menu.PlacementTarget = (Button)sender;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    void OnToggleBackground(object sender, RoutedEventArgs e) =>
        ApplyBackgroundEnabled(!_session.BackgroundEnabled);

    void ApplyBackgroundEnabled(bool enabled)
    {
        _session.BackgroundEnabled = enabled;
        BgArt.Visibility = enabled && BgArt.Source != null ? Visibility.Visible : Visibility.Collapsed;
        BtnBgToggle.Opacity = enabled ? 1 : 0.45;
        Terminal.SetBackgroundOn(enabled);
        ApplySoloStates();
    }

    // ---------- справка ----------

    void OnHelp(object sender, RoutedEventArgs e) => ShowHelp();

    void ShowHelp() => new HelpWindow { Owner = this }.ShowDialog();

    // ---------- перенос между панелями (стрелки ← →) ----------

    void OnTransferToRight(object sender, RoutedEventArgs e) => TransferBetween(PanelLeft, PanelRight);
    void OnTransferToLeft(object sender, RoutedEventArgs e) => TransferBetween(PanelRight, PanelLeft);

    /// <summary>Копирование выделенного (отметки или объект под курсором) в соседнюю панель;
    /// работает и локально↔SFTP — то есть между машинами.</summary>
    void TransferBetween(FilePanel from, FilePanel to)
    {
        if (to.CurrentPath == null)
        {
            FlashStatus("Некуда переносить: целевая панель в корне (список дисков)");
            return;
        }
        var items = from.GetTargetItems();
        if (items.Count == 0)
        {
            FlashStatus("Нечего переносить: в панели-источнике пусто");
            return;
        }
        to.PasteFrom(from, items, cut: false, BeginTransfer(items.Count));
    }

    // ---------- индикатор переноса (не блокирует работу) ----------

    TransferProgress? _transfer;

    TransferProgress BeginTransfer(int fileCount)
    {
        _transfer?.Cancel(); // два переноса сразу — старый гасим, показываем новый
        var tp = new TransferProgress(fileCount);
        _transfer = tp;
        tp.Changed += () => _ = Dispatcher.BeginInvoke(() => UpdateTransferUI(tp));
        tp.Finished += summary => _ = Dispatcher.BeginInvoke(() =>
        {
            TransferBox.Visibility = Visibility.Collapsed;
            if (ReferenceEquals(_transfer, tp)) _transfer = null;
            FlashStatus(summary);
        });
        TransferBox.Visibility = Visibility.Visible;
        UpdateTransferUI(tp);
        return tp;
    }

    void UpdateTransferUI(TransferProgress tp)
    {
        if (!ReferenceEquals(_transfer, tp)) return;
        double pct = tp.BytesTotal > 0
            ? Math.Min(100, tp.BytesDone * 100.0 / tp.BytesTotal)
            : (tp.FileIndex - 1) * 100.0 / tp.FileCount;
        TransferBar.Value = pct;
        var size = tp.BytesTotal > 0 ? $" · {FormatMb(tp.BytesDone)}/{FormatMb(tp.BytesTotal)}" : "";
        TransferText.Text = $"{tp.FileIndex}/{tp.FileCount} · {tp.CurrentName}{size}";
    }

    static string FormatMb(long b)
    {
        string[] u = { "Б", "КБ", "МБ", "ГБ" };
        double v = b; int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{b} Б" : $"{v:0.#} {u[i]}";
    }

    void OnCancelTransfer(object sender, RoutedEventArgs e)
    {
        _transfer?.Cancel();
        FlashStatus("Отмена переноса…");
    }

    /// <summary>Показать сообщение в статусбаре на 4 секунды.</summary>
    void FlashStatus(string msg)
    {
        OnPanelStatus(msg);
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            if (_transientStatus == msg) { _transientStatus = ""; UpdateStatus(); }
        };
        t.Start();
    }

    // ---------- буфер ----------

    void CopyTargets(bool cut)
    {
        var items = _active.GetTargetItems();
        if (items.Count == 0) return;
        _clipSource = _active;
        _clipItems = items;
        _clipCut = cut;
        UpdateStatus();
    }

    void PasteTargets()
    {
        if (_clipSource == null || _clipItems.Count == 0) return;
        _active.PasteFrom(_clipSource, _clipItems, _clipCut, BeginTransfer(_clipItems.Count));
        if (_clipCut) { _clipItems = new(); _clipSource = null; _clipCut = false; }
        UpdateStatus();
    }

    // ---------- клавиатура ----------

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Handled) return;
        // не перехватываем ввод в текстовых полях (путь, фильтр, диалоги)
        if (Keyboard.FocusedElement is TextBox) return;

        var mod = Keyboard.Modifiers;
        bool ctrl = mod.HasFlag(ModifierKeys.Control);
        bool shift = mod.HasFlag(ModifierKeys.Shift);
        var p = _active;

        switch (e.Key)
        {
            // W/S и стрелки — по физическим позициям, работают в RU-раскладке (ТЗ 0.4)
            case Key.W: case Key.Up: p.MoveCursor(-1, shift); break;
            case Key.S: case Key.Down: p.MoveCursor(1, shift); break;
            case Key.Space when ctrl: p.ToggleMarkAtCursor(); break;
            case Key.Space: p.OpenCurrent(); break;
            case Key.Return: p.OpenCurrent(); break;
            case Key.Back: p.GoUp(); break;
            case Key.Q: p.HistoryBack(); break;
            case Key.E: p.HistoryForward(); break;
            case Key.Escape: p.ClearMarks(); break;
            case Key.F1: ShowHelp(); break;
            case Key.F2: p.RenameAtCursor(); break;
            case Key.R when ctrl: p.RenameAtCursor(); break;
            case Key.F3: p.ShowFilter(); break;
            case Key.F4: p.ShowExtFilterMenu(); break;
            case Key.F6: p.OpenModeMenu(); break;   // переключение локально/SFTP
            case Key.F5: p.ReloadDir(); break;
            case Key.F when ctrl: OpenSearch(); break;
            case Key.OemTilde when ctrl: ToggleConsole(); break;
            case Key.N when ctrl && shift: p.NewFolder(); break;
            case Key.N when ctrl: p.NewFile(); break;
            case Key.Delete: p.DeleteTargets(); break;
            case Key.C when ctrl: CopyTargets(false); break;
            case Key.X when ctrl: CopyTargets(true); break;
            case Key.V when ctrl: PasteTargets(); break;
            case Key.L when ctrl: p.FocusPath(); break;
            case Key.P when ctrl: p.TogglePinAtCursor(); break;
            case Key.M when ctrl: WindowState = WindowState.Minimized; break;
            case Key.Tab when ctrl: SwitchPanel(); break;
            case Key.D2 when ctrl: ToggleSecondPanel(); break;
            // перенос выделенного между панелями (и между машинами по SFTP) — как стрелки в интерфейсе
            case Key.Right when ctrl: OnTransferToRight(this, new RoutedEventArgs()); break;
            case Key.Left when ctrl: OnTransferToLeft(this, new RoutedEventArgs()); break;
            default: return;
        }
        e.Handled = true;
    }

    // ---------- глобальный хоткей Ctrl+Alt+C ----------

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwndSource = PresentationSource.FromVisual(this) as HwndSource;
        _hwndSource?.AddHook(WndProc);
        var handle = new WindowInteropHelper(this).Handle;
        // если сочетание занято другим приложением — просто не сработает, живем дальше
        RegisterHotKey(handle, HotkeyId, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, 0x43); // 'C'
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            ToggleWindowVisibility();
            handled = true;
        }
        return IntPtr.Zero;
    }

    void ToggleWindowVisibility()
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
            Show();
            Activate();
            Topmost = true;  // вытащить наверх и сразу отпустить
            Topmost = false;
        }
        else if (IsActive)
        {
            WindowState = WindowState.Minimized;
        }
        else
        {
            Show();
            Activate();
        }
    }

    // ---------- сессия ----------

    void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        UnregisterHotKey(new WindowInteropHelper(this).Handle, HotkeyId);
        _session.LeftPath = PanelLeft.Backend.DisplayPath(PanelLeft.CurrentPath);
        _session.RightPath = PanelRight.Backend.DisplayPath(PanelRight.CurrentPath);
        _session.RightVisible = PanelRight.IsVisible;
        _session.ActiveLeft = _active == PanelLeft;
        _session.PinnedPaths = PinStore.Pins.ToList();
        SessionService.Save(_session);
        PanelLeft.DisconnectBackend();
        PanelRight.DisconnectBackend();
    }
}
