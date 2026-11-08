using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Comander.Services;

namespace Comander.Controls;

/// <summary>
/// Встроенный терминал (ТЗ 0.9): шелл выбирается в настройках —
/// Git Bash (linux-команды в Windows), PowerShell или cmd; auto = bash, если найден.
/// Интерактивные программы (vim, mc) не поддерживаются — вывод потоковый.
/// cd отслеживается вручную, история — стрелками вверх/вниз.
/// </summary>
public partial class ConsolePanel : UserControl
{
    string _cwd = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    readonly List<string> _history = new();
    int _histIdx = -1;
    Process? _running;

    string _shellMode = "auto";
    string _shellPath = "cmd.exe";
    bool _isBash;

    // удалённый режим: интерактивный shell на машине SFTP-панели
    SftpBackend? _remoteBackend;
    Renci.SshNet.ShellStream? _remoteShell;
    CancellationTokenSource? _remoteCts;
    /// <summary>auto | bash | sh | powershell | cmd | raw — шелл на удалённой машине (настройки).</summary>
    string _remoteShellMode = "auto";

    public event Action? CloseRequested;

    public ConsolePanel()
    {
        InitializeComponent();
        var mono = FontService.Resolve("JetBrains Mono");
        Output.FontFamily = mono;
        Input.FontFamily = mono;
        PromptText.FontFamily = mono;
        Input.KeyDown += OnInputKeyDown;
        UpdatePrompt();
    }

    /// <summary>auto | bash | powershell | cmd — из настроек.</summary>
    public void SetShellMode(string mode)
    {
        _shellMode = mode;
        ResolveShell();
    }

    /// <summary>auto | bash | sh | powershell | cmd | raw — шелл на удалённой машине (из настроек).</summary>
    public void SetRemoteShellMode(string mode) => _remoteShellMode = mode;

    bool _bgOn = true;

    /// <summary>Фоновая картинка вкл/выкл: терминал, как панели, — призрачный при вкл, плотный при выкл.</summary>
    public void SetBackgroundOn(bool on)
    {
        _bgOn = on;
        OuterBd.Background = (Brush)FindResource(on ? "PanelGhostBrush" : "PanelBrush");
    }

    /// <summary>Размер служебной шапки терминала — задаёт главное окно относительно базового шрифта.</summary>
    public void SetChromeFontSize(double size) => HeaderText.FontSize = size;

    void ResolveShell()
    {
        var bash = FindBash();
        (_shellPath, _isBash) = _shellMode switch
        {
            "bash" => (bash ?? "cmd.exe", bash != null),
            "powershell" => ("powershell.exe", false),
            "cmd" => ("cmd.exe", false),
            _ => (bash ?? "cmd.exe", bash != null),
        };
        var kind = _isBash ? "bash — linux-команды (ls, grep, find…) работают"
            : _shellPath.Contains("powershell") ? "PowerShell"
            : "cmd" + (_shellMode is "auto" or "bash" ? " — Git Bash не найден, linux-команды недоступны" : "");
        HeaderText.Text = "Терминал · этот ПК (" + Environment.MachineName + ") · " + kind;
        UpdatePrompt();
    }

    // ---------- удалённый shell (машина активной SFTP-панели) ----------

    /// <summary>Режим удалённой машины: интерактивный shell по SSH (по тем же реквизитам, что SFTP).</summary>
    public void SetRemote(SftpBackend backend)
    {
        if (ReferenceEquals(_remoteBackend, backend) && _remoteShell is { CanRead: true }) return;
        StopRemote();
        _remoteBackend = backend;
        try
        {
            _remoteShell = backend.OpenShell();
            _remoteCts = new CancellationTokenSource();
            HeaderText.Text = $"Терминал · {backend.Describe()} (удалённая машина) — определяю ОС…";
            PromptText.Text = backend.Describe() + " $";
            var shell = _remoteShell;
            var ct = _remoteCts.Token;
            _ = Task.Run(() => DetectRemoteShell(backend, shell));
            _ = Task.Run(async () =>
            {
                var buf = new byte[4096];
                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        var n = await shell.ReadAsync(buf, ct);
                        if (n <= 0) break;
                        AppendText(Encoding.UTF8.GetString(buf, 0, n));
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { AppendText("\n[соединение оборвалось: " + ex.Message + "]\n"); }
                finally
                {
                    _ = Dispatcher.BeginInvoke(() =>
                    {
                        if (ReferenceEquals(_remoteBackend, backend)) StopRemote();
                    });
                }
            });
        }
        catch (Exception ex)
        {
            AppendText("[не удалось открыть shell на удалённой машине: " + ex.Message + "]\n");
            StopRemote();
        }
    }

    /// <summary>Определить ОС удалённой машины и, по настройке, переключить шелл.
    /// uname -s работает только на *nix; на Windows-SSH командник отвечает ошибкой — это и есть детект.</summary>
    void DetectRemoteShell(SftpBackend backend, Renci.SshNet.ShellStream shell)
    {
        string? os = null;
        string? shellName = null;
        try
        {
            os = backend.Probe("uname -s"); // "Linux" / "Darwin" / …; null/пусто — Windows
            var unix = !string.IsNullOrEmpty(os);
            string? start = _remoteShellMode switch
            {
                "bash" => unix ? "bash -l" : null,                    // на Windows bash обычно отсутствует — не мусорим
                "sh" => unix ? "sh" : null,
                "powershell" => unix ? null : "powershell -NoLogo",   // в cmd Windows есть powershell
                "cmd" => null,                                         // дефолт Windows-SSH и так cmd; на Linux — login shell
                "raw" => null,
                _ => unix
                    ? (backend.Probe("command -v bash") != null ? "bash -l" : null)
                    : "powershell -NoLogo",
            };
            shellName = _remoteShellMode switch
            {
                "bash" => unix ? "bash" : "cmd",
                "sh" => unix ? "sh" : "cmd",
                "powershell" => unix ? "login shell" : "PowerShell",
                "cmd" => unix ? "login shell" : "cmd",
                "raw" => unix ? "login shell" : "cmd",
                _ => unix ? (start != null ? "bash" : "login shell") : "PowerShell",
            };
            if (start != null && shell.CanWrite)
            {
                var bytes = Encoding.UTF8.GetBytes(start + "\n");
                shell.Write(bytes, 0, bytes.Length);
                shell.Flush();
            }
        }
        catch { /* зонд не удался — остаёмся на сыром шелле, ОС неизвестна */ }
        var osLabel = string.IsNullOrEmpty(os) ? "Windows (?)" : os!;
        _ = Dispatcher.BeginInvoke(() =>
        {
            // сессия могла уже смениться/закрыться, пока шёл зонд
            if (!ReferenceEquals(_remoteShell, shell)) return;
            HeaderText.Text = $"Терминал · {backend.Describe()} (удалённая машина) — {osLabel} · {shellName ?? "shell"}";
        });
    }

    /// <summary>Закрыть удалённую сессию и вернуть локальный шелл.</summary>
    public void StopRemote()
    {
        _remoteCts?.Cancel();
        _remoteCts = null;
        try { _remoteBackend?.CloseShell(); } catch { }
        _remoteShell = null;
        _remoteBackend = null;
        ResolveShell();
        UpdatePrompt();
    }

    /// <summary>Терминал сейчас в режиме удалённой машины.</summary>
    public bool IsRemoteSession => _remoteShell != null;

    public void SetCwd(string? path)
    {
        if (_remoteShell != null) return; // cd локальный — только для локального шелла
        if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
        {
            _cwd = path;
            UpdatePrompt();
        }
    }

    public void FocusInput() => Input.Focus();

    void UpdatePrompt()
    {
        if (_remoteBackend != null) return; // в удалённом режиме приглашение задаёт SetRemote
        PromptText.Text = Shorten(_cwd) + (_isBash ? " $" : " >");
    }

    static string Shorten(string path) =>
        path.Length <= 42 ? path : "…" + path[^41..];

    static string? FindBash()
    {
        // System32\bash.exe — это WSL, его пропускаем
        var systemDir = Environment.SystemDirectory;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "")
                 .Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var p = Path.Combine(dir.Trim(), "bash.exe");
                if (File.Exists(p) && !p.StartsWith(systemDir, StringComparison.OrdinalIgnoreCase))
                    return p;
            }
            catch { }
        }
        foreach (var p in new[]
        {
            @"C:\Program Files\Git\bin\bash.exe",
            @"C:\Program Files (x86)\Git\bin\bash.exe",
            // запасной вариант: bash из комплекта Kimi Work на этой машине
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                @"kimi-desktop\daimon-bundle\runtime\git\bin\bash.exe"),
        })
        {
            if (File.Exists(p)) return p;
        }
        return null;
    }

    void OnClose(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();

    void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter: // Key.Return — тот же код
                var cmd = Input.Text;
                Input.Text = "";
                if (!string.IsNullOrWhiteSpace(cmd))
                {
                    _history.Add(cmd);
                    _histIdx = _history.Count;
                    RunCommand(cmd.Trim());
                }
                e.Handled = true;
                break;
            case Key.C when Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && (_running != null || _remoteShell != null):
                // Ctrl+C при работающей команде — прервать (когда ничего не крутится, остаётся «копировать»)
                if (_remoteShell != null)
                {
                    try { _remoteShell.WriteByte(0x03); _remoteShell.Flush(); } catch { }
                }
                else if (_running != null)
                {
                    try { _running.Kill(entireProcessTree: true); } catch { }
                    AppendText("[прервано пользователем]\n");
                }
                e.Handled = true;
                break;
            case Key.Up:
                if (_history.Count > 0)
                {
                    _histIdx = Math.Max(0, _histIdx - 1);
                    Input.Text = _history[_histIdx];
                    Input.CaretIndex = Input.Text.Length;
                }
                e.Handled = true;
                break;
            case Key.Down:
                if (_histIdx < _history.Count - 1)
                {
                    _histIdx++;
                    Input.Text = _history[_histIdx];
                    Input.CaretIndex = Input.Text.Length;
                }
                else { _histIdx = _history.Count; Input.Text = ""; }
                e.Handled = true;
                break;
            case Key.Escape:
                CloseRequested?.Invoke();
                e.Handled = true;
                break;
        }
    }

    void RunCommand(string cmd)
    {
        // удалённый режим: строка уходит в shell на удалённой машине (он сам эхом отвечает)
        if (_remoteShell != null)
        {
            if (cmd is "clear" or "cls") { Output.Clear(); return; } // ANSI-коды не рендерим — чистим локально
            try
            {
                var bytes = Encoding.UTF8.GetBytes(cmd + "\n");
                _remoteShell.Write(bytes, 0, bytes.Length);
                _remoteShell.Flush();
            }
            catch (Exception ex) { AppendText("[ошибка отправки: " + ex.Message + "]\n"); }
            return;
        }

        AppendText(Shorten(_cwd) + (_isBash ? " $ " : " > ") + cmd + "\n");

        if (cmd is "clear" or "cls") { Output.Clear(); return; }
        if (cmd == "cd" || cmd.StartsWith("cd ", StringComparison.Ordinal)) { ChangeDir(cmd[2..].Trim()); return; }

        if (_running != null)
        {
            AppendText("[подожди: предыдущая команда ещё выполняется — Ctrl+C её прервать]\n");
            return;
        }

        var psi = _isBash
            ? new ProcessStartInfo(_shellPath, "-c \"" + cmd.Replace("\"", "\\\"") + "\"")
            : _shellPath.Contains("powershell")
                ? new ProcessStartInfo(_shellPath, "-NoProfile -Command \"" + cmd.Replace("\"", "\\\"") + "\"")
                : new ProcessStartInfo(_shellPath, "/c " + cmd);
        psi.WorkingDirectory = _cwd;
        psi.CreateNoWindow = true;
        psi.UseShellExecute = false;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.RedirectStandardInput = true; // сразу закроем: команда не сможет висеть, ожидая ввод
        if (_isBash) // MSYS2 отдаёт UTF-8 — иначе русские имена файлов превращаются в кракозябры
        {
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;
        }

        Process process;
        try
        {
            process = new Process { StartInfo = psi };
            _running = process;
            process.OutputDataReceived += (_, a) => { if (a.Data != null) AppendText(a.Data + "\n"); };
            process.ErrorDataReceived += (_, a) => { if (a.Data != null) AppendText(a.Data + "\n"); };
            process.Start();
            process.StandardInput.Close(); // stdin = EOF сразу
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        catch (Exception ex)
        {
            AppendText("[ошибка запуска: " + ex.Message + "]\n");
            _running = null;
            return;
        }

        // завершение и сторожевой таймер — вне UI-потока; в UI возвращаемся через BeginInvoke
        _ = Task.Run(async () =>
        {
            var exitTask = process.WaitForExitAsync();
            var winner = await Task.WhenAny(exitTask, Task.Delay(TimeSpan.FromSeconds(60)));
            var killed = winner != exitTask;
            if (killed)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                try { await exitTask.WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
            }
            int? code = null;
            try { code = process.ExitCode; } catch { }
            _ = Dispatcher.BeginInvoke(() =>
            {
                if (ReferenceEquals(_running, process)) _running = null;
                if (killed) AppendText("[прервано: команда работала дольше 60 сек]\n");
                else if (code is > 0) AppendText("[код выхода: " + code + "]\n");
                try { process.Dispose(); } catch { }
            });
        });
    }

    void ChangeDir(string arg)
    {
        string target;
        if (string.IsNullOrEmpty(arg) || arg == "~")
        {
            target = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }
        else
        {
            arg = arg.Trim('"');
            // bash-стиль /c/... -> C:\...
            if (_isBash && arg.Length >= 3 && arg[0] == '/' && arg[2] == '/' && char.IsLetter(arg[1]))
                arg = char.ToUpperInvariant(arg[1]) + ":\\" + arg[3..].Replace('/', '\\');
            target = Path.GetFullPath(Path.IsPathRooted(arg) ? arg : Path.Combine(_cwd, arg));
        }
        if (Directory.Exists(target))
        {
            _cwd = target;
            UpdatePrompt();
        }
        else AppendText("cd: нет такого каталога: " + target + "\n");
    }

    void AppendText(string text)
    {
        if (Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                // страховка от раздувания: держим не больше ~200k символов
                if (Output.Text.Length > 200_000)
                    Output.Text = "…\n" + Output.Text[^100_000..];
                Output.AppendText(text);
                Output.ScrollToEnd();
            }
            catch { /* панель уже умирает — вывод просто теряем */ }
        });
    }
}
