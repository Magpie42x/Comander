using System.IO;
using System.Text.Json;

namespace Comander.Services;

/// <summary>
/// Сессия хранится в JSON рядом с exe (поставка portable, ТЗ 0.6/D12).
/// </summary>
public class SessionData
{
    public string? LeftPath { get; set; }
    public string? RightPath { get; set; }
    public bool RightVisible { get; set; } = true;
    public bool ActiveLeft { get; set; } = true;
    public string Theme { get; set; } = "DarkPurple";
    public string? BackgroundFile { get; set; }
    public bool BackgroundEnabled { get; set; } = true;
    public string FontFamily { get; set; } = "Segoe UI";
    public double FontSize { get; set; } = 13;
    public bool AutoStart { get; set; }
    /// <summary>auto | bash | powershell | cmd — шелл встроенного терминала.</summary>
    public string ShellMode { get; set; } = "auto";
    /// <summary>auto | bash | sh | powershell | cmd | raw — шелл на удалённой машине (терминал по SSH).</summary>
    public string RemoteShellMode { get; set; } = "auto";
    public List<string> PinnedPaths { get; set; } = new();
    public List<SftpProfile> Profiles { get; set; } = new();
}

/// <summary>Сохраненная точка SFTP-подключения; пароль — в DPAPI (SecretStore).</summary>
public class SftpProfile
{
    public string Name { get; set; } = "";   // user@host[:port] — технический ключ и подсказка
    public string? Alias { get; set; }        // своё имя: показывается в меню вместо адреса
    public string Host { get; set; } = "";
    public int Port { get; set; } = 22;
    public string User { get; set; } = "";
    public string ProtectedPassword { get; set; } = ""; // пусто — вход по ключам ~/.ssh
}

public static class SessionService
{
    static string FilePath => Path.Combine(AppContext.BaseDirectory, "comander.session.json");

    public static SessionData Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var data = JsonSerializer.Deserialize<SessionData>(File.ReadAllText(FilePath));
                if (data != null) return data;
            }
        }
        catch { /* поврежденная сессия не должна мешать запуску */ }
        return new SessionData();
    }

    public static void Save(SessionData data)
    {
        try
        {
            File.WriteAllText(FilePath, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* нет прав на запись рядом с exe — молча пропускаем */ }
    }
}
