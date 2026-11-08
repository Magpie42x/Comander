using System.IO;
using Comander.Models;
using Renci.SshNet;
using VbFileIO = Microsoft.VisualBasic.FileIO;

namespace Comander.Services;

/// <summary>
/// Бэкенд файловой панели: локальный диск или SFTP (ТЗ 6/7).
/// Путь null допустим только у локального бэкенда — это список дисков.
/// У SFTP пути всегда POSIX, корень — "/".
/// </summary>
public interface IPanelBackend : IDisposable
{
    bool IsRemote { get; }
    string DisplayPath(string? path);
    string DisplayAddress { get; }          // префикс для адресной строки, напр. sftp://user@host
    bool IsRoot(string? path);
    string? ParentOf(string path);
    string Combine(string dir, string name);
    string NameOf(string path);
    bool Exists(string path);
    List<FileItem> List(string? path);
    void CreateFile(string dir, string name);
    void CreateDirectory(string dir, string name);
    void Rename(string path, string newName, bool isDir);
    void Delete(string path, bool isDir);   // локальный — в корзину, SFTP — безвозвратно (панель спрашивает)
    void Download(string srcPath, string localDst, TransferProgress? progress = null);  // рекурсивно
    void Upload(string localSrc, string dstPath, TransferProgress? progress = null);    // рекурсивно
}

public class LocalBackend : IPanelBackend
{
    public static readonly LocalBackend Instance = new();

    public bool IsRemote => false;
    public string DisplayAddress => "";
    public string DisplayPath(string? path) => path ?? "Этот компьютер (диски)";
    public bool IsRoot(string? path) => path == null;
    public string? ParentOf(string path) => Directory.GetParent(path)?.FullName;
    public string Combine(string dir, string name) => Path.Combine(dir, name);
    public string NameOf(string path) => Path.GetFileName(path.TrimEnd('\\', '/'));

    public bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    public List<FileItem> List(string? path)
    {
        var result = new List<FileItem>();
        if (path == null)
        {
            // корень = быстрый доступ: закрепы пользователя, стандартные папки, диски (ТЗ 0.9)
            foreach (var pin in PinStore.Pins)
            {
                try
                {
                    if (Directory.Exists(pin))
                    {
                        var di = new DirectoryInfo(pin);
                        result.Add(new FileItem { Name = di.Name, FullPath = pin, IsDirectory = true, Modified = di.LastWriteTime, IsPinned = true });
                    }
                    else if (File.Exists(pin))
                    {
                        var fi = new FileInfo(pin);
                        result.Add(new FileItem { Name = fi.Name, FullPath = pin, Size = fi.Length, Modified = fi.LastWriteTime, IsPinned = true });
                    }
                }
                catch { }
            }
            var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            foreach (var (label, dir) in new (string, string)[]
            {
                ("Рабочий стол", Environment.GetFolderPath(Environment.SpecialFolder.Desktop)),
                ("Документы", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)),
                ("Загрузки", Path.Combine(user, "Downloads")),
                ("Изображения", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)),
                ("Музыка", Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)),
                ("Видео", Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)),
            })
            {
                try
                {
                    if (!Directory.Exists(dir) || PinStore.IsPinned(dir)) continue;
                    var di = new DirectoryInfo(dir);
                    result.Add(new FileItem { Name = label, FullPath = dir, IsDirectory = true, Modified = di.LastWriteTime });
                }
                catch { }
            }
            foreach (var d in DriveInfo.GetDrives())
            {
                string label;
                try { if (!d.IsReady) continue; label = d.VolumeLabel; }
                catch { continue; }
                var name = string.IsNullOrWhiteSpace(label) ? d.Name : $"{d.Name} {label}";
                result.Add(new FileItem { Name = name, FullPath = d.RootDirectory.FullName, IsDirectory = true, IsDrive = true });
            }
            return result;
        }
        foreach (var dir in Directory.EnumerateDirectories(path))
        {
            var di = new DirectoryInfo(dir);
            result.Add(new FileItem { Name = di.Name, FullPath = dir, IsDirectory = true, Modified = di.LastWriteTime });
        }
        foreach (var f in Directory.EnumerateFiles(path))
        {
            var fi = new FileInfo(f);
            result.Add(new FileItem { Name = fi.Name, FullPath = f, Size = fi.Length, Modified = fi.LastWriteTime });
        }
        return result;
    }

    public void CreateFile(string dir, string name)
    {
        var path = Path.Combine(dir, name);
        if (Exists(path)) throw new IOException("Объект с таким именем уже существует.");
        using (File.Create(path)) { }
    }

    public void CreateDirectory(string dir, string name)
    {
        var path = Path.Combine(dir, name);
        if (Exists(path)) throw new IOException("Объект с таким именем уже существует.");
        Directory.CreateDirectory(path);
    }

    public void Rename(string path, string newName, bool isDir)
    {
        var target = Path.Combine(Path.GetDirectoryName(path)!, newName);
        if (isDir) Directory.Move(path, target); else File.Move(path, target);
    }

    /// <summary>Локальное удаление — в корзину (ТЗ 8/D09).</summary>
    public void Delete(string path, bool isDir)
    {
        if (isDir)
            VbFileIO.FileSystem.DeleteDirectory(path, VbFileIO.UIOption.OnlyErrorDialogs, VbFileIO.RecycleOption.SendToRecycleBin);
        else
            VbFileIO.FileSystem.DeleteFile(path, VbFileIO.UIOption.OnlyErrorDialogs, VbFileIO.RecycleOption.SendToRecycleBin);
    }

    public void Download(string srcPath, string localDst, TransferProgress? progress = null) => CopyAny(srcPath, localDst, progress);
    public void Upload(string localSrc, string dstPath, TransferProgress? progress = null) => CopyAny(localSrc, dstPath, progress);

    static void CopyAny(string src, string dst, TransferProgress? p)
    {
        p?.Token.ThrowIfCancellationRequested();
        if (Directory.Exists(src))
        {
            Directory.CreateDirectory(dst);
            foreach (var f in Directory.EnumerateFiles(src)) CopyAny(f, Path.Combine(dst, Path.GetFileName(f)), p);
            foreach (var d in Directory.EnumerateDirectories(src)) CopyAny(d, Path.Combine(dst, Path.GetFileName(d)), p);
        }
        else
        {
            const int bufSize = 1 << 20;
            using var i = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read, bufSize);
            using var o = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None, bufSize);
            var buf = new byte[bufSize];
            int n;
            while ((n = i.Read(buf, 0, buf.Length)) > 0)
            {
                o.Write(buf, 0, n);
                p?.AddBytes(n);
                p?.Token.ThrowIfCancellationRequested();
            }
            try { File.SetLastWriteTime(dst, File.GetLastWriteTime(src)); } catch { }
        }
    }

    public void Dispose() { }
}

public class SftpBackend : IPanelBackend
{
    readonly Renci.SshNet.SftpClient _client;
    readonly string _user;
    readonly string _host;
    readonly int _port;
    readonly string? _password;
    Renci.SshNet.SshClient? _shellClient;
    Renci.SshNet.ShellStream? _shellStream;

    SftpBackend(Renci.SshNet.SftpClient client, string user, string host, int port, string? password)
    {
        _client = client; _user = user; _host = host; _port = port; _password = password;
    }

    public bool IsRemote => true;
    public string DisplayAddress => $"sftp://{_user}@{_host}";
    public string DisplayPath(string? path) => DisplayAddress + (path ?? "/");
    public bool IsRoot(string? path) => string.IsNullOrEmpty(path) || path == "/";
    public string Describe() => $"{_user}@{_host}" + (_port != 22 ? $":{_port}" : "");

    public string? ParentOf(string path)
    {
        var p = path.TrimEnd('/');
        var idx = p.LastIndexOf('/');
        return idx <= 0 ? "/" : p[..idx];
    }

    public string Combine(string dir, string name) => dir.TrimEnd('/') + "/" + name;
    public string NameOf(string path) => path.TrimEnd('/').Split('/').Last();
    public bool Exists(string path) => _client.Exists(path);

    /// <summary>
    /// Подключение: пароль, либо (если пароль пуст) ключи из ~/.ssh без парольной фразы.
    /// Проверка хост-ключа — принять любой (пилот; хранение known_hosts — этап 3).
    /// </summary>
    public static SftpBackend Connect(string host, int port, string user, string? password)
    {
        var info = new ConnectionInfo(host, port, user, BuildAuth(user, password).ToArray()) { Timeout = TimeSpan.FromSeconds(15) };
        var client = new Renci.SshNet.SftpClient(info);
        client.Connect();
        return new SftpBackend(client, user, host, port, password);
    }

    static List<AuthenticationMethod> BuildAuth(string user, string? password)
    {
        var methods = new List<AuthenticationMethod>();
        if (!string.IsNullOrEmpty(password))
        {
            methods.Add(new PasswordAuthenticationMethod(user, password));
        }
        else
        {
            var sshDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
            foreach (var keyName in new[] { "id_ed25519", "id_rsa", "id_ecdsa" })
            {
                var keyPath = Path.Combine(sshDir, keyName);
                if (File.Exists(keyPath))
                {
                    try { methods.Add(new PrivateKeyAuthenticationMethod(user, new PrivateKeyFile(keyPath))); }
                    catch { /* ключ с парольной фразой или битый — пропускаем */ }
                }
            }
            if (methods.Count == 0)
                throw new InvalidOperationException("Не задан пароль и не найдены ключи в ~/.ssh");
        }
        return methods;
    }

    /// <summary>Интерактивный shell той же машины (терминал по SFTP). TERM=dumb — без ANSI-красок.</summary>
    public Renci.SshNet.ShellStream OpenShell()
    {
        if (_shellStream is { CanRead: true }) return _shellStream;
        var info = new ConnectionInfo(_host, _port, _user, BuildAuth(_user, _password).ToArray()) { Timeout = TimeSpan.FromSeconds(15) };
        var client = new Renci.SshNet.SshClient(info);
        client.HostKeyReceived += (_, _) => { }; // accept-any — пилот, как и в SFTP
        client.Connect();
        var stream = client.CreateShellStream("dumb", 120, 32, 1920, 960, 8192);
        _shellClient = client;
        _shellStream = stream;
        return stream;
    }

    /// <summary>Одиночная команда по тому же SSH-подключению, что и shell (отдельный канал).
    /// null — команда не выполнилась (нет такой команды / Windows / таймаут) — это и есть признак ОС.</summary>
    public string? Probe(string command)
    {
        try
        {
            if (_shellClient is not { IsConnected: true }) return null;
            var c = _shellClient.RunCommand(command);
            c.Execute();
            return c.ExitStatus == 0 ? c.Result.Trim() : null;
        }
        catch { return null; }
    }

    public void CloseShell()
    {
        try { _shellStream?.Dispose(); } catch { }
        try { if (_shellClient != null) { _shellClient.Dispose(); } } catch { }
        _shellStream = null;
        _shellClient = null;
    }

    public List<FileItem> List(string? path)
    {
        var result = new List<FileItem>();
        foreach (var e in _client.ListDirectory(path ?? "/"))
        {
            if (e.Name is "." or "..") continue;
            result.Add(new FileItem
            {
                Name = e.Name,
                FullPath = Combine(path ?? "/", e.Name),
                IsDirectory = e.IsDirectory,
                Size = e.IsDirectory ? 0 : e.Length,
                Modified = e.LastWriteTime,
            });
        }
        return result;
    }

    public void CreateFile(string dir, string name)
    {
        var path = Combine(dir, name);
        if (Exists(path)) throw new IOException("Объект с таким именем уже существует.");
        using (_client.Create(path)) { }
    }

    public void CreateDirectory(string dir, string name)
    {
        var path = Combine(dir, name);
        if (Exists(path)) throw new IOException("Объект с таким именем уже существует.");
        _client.CreateDirectory(path);
    }

    public void Rename(string path, string newName, bool isDir)
    {
        var parent = ParentOf(path) ?? "/";
        _client.RenameFile(path, Combine(parent, newName));
    }

    /// <summary>Удаление на SFTP — безвозвратное; подтверждение показывает панель (ТЗ 8).</summary>
    public void Delete(string path, bool isDir)
    {
        if (!isDir) { _client.DeleteFile(path); return; }
        foreach (var e in _client.ListDirectory(path))
        {
            if (e.Name is "." or "..") continue;
            Delete(Combine(path, e.Name), e.IsDirectory);
        }
        _client.DeleteDirectory(path);
    }

    public void Download(string srcPath, string localDst, TransferProgress? p = null)
    {
        var attrs = _client.GetAttributes(srcPath);
        if (attrs.IsDirectory)
        {
            Directory.CreateDirectory(localDst);
            foreach (var e in _client.ListDirectory(srcPath))
            {
                if (e.Name is "." or "..") continue;
                p?.Token.ThrowIfCancellationRequested();
                Download(Combine(srcPath, e.Name), Path.Combine(localDst, e.Name), p);
            }
        }
        else
        {
            p?.Token.ThrowIfCancellationRequested();
            using var fs = File.Create(localDst);
            long last = 0;
            _client.DownloadFile(srcPath, fs, cb =>
            {
                p?.Token.ThrowIfCancellationRequested();
                var done = (long)cb;
                p?.AddBytes(done - last);
                last = done;
            });
            p?.Token.ThrowIfCancellationRequested(); // если отмена пришла внутри callback и была обёрнута
        }
    }

    public void Upload(string localSrc, string dstPath, TransferProgress? p = null)
    {
        p?.Token.ThrowIfCancellationRequested();
        if (Directory.Exists(localSrc))
        {
            if (!Exists(dstPath)) _client.CreateDirectory(dstPath);
            foreach (var f in Directory.EnumerateFiles(localSrc))
                Upload(f, Combine(dstPath, Path.GetFileName(f)), p);
            foreach (var d in Directory.EnumerateDirectories(localSrc))
                Upload(d, Combine(dstPath, Path.GetFileName(d)), p);
        }
        else
        {
            using var fs = File.OpenRead(localSrc);
            long last = 0;
            _client.UploadFile(fs, dstPath, cb =>
            {
                p?.Token.ThrowIfCancellationRequested();
                var done = (long)cb;
                p?.AddBytes(done - last);
                last = done;
            });
            p?.Token.ThrowIfCancellationRequested();
        }
    }

    public void Dispose()
    {
        CloseShell();
        try { if (_client.IsConnected) _client.Disconnect(); } catch { }
        _client.Dispose();
    }
}
