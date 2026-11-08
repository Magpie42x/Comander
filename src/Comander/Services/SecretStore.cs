using System.Security.Cryptography;
using System.Text;

namespace Comander.Services;

/// <summary>
/// Секреты (пароли SFTP-профилей) — DPAPI текущего пользователя (ТЗ 7/D10).
/// Строка в base64 безопасна только на этой машине и под этим пользователем Windows.
/// </summary>
public static class SecretStore
{
    public static string Protect(string plain) =>
        Convert.ToBase64String(ProtectedData.Protect(
            Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser));

    public static string Unprotect(string cipher)
    {
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(
                Convert.FromBase64String(cipher), null, DataProtectionScope.CurrentUser));
        }
        catch { return ""; } // чужая машина/пользователь — секрет не читается
    }
}
