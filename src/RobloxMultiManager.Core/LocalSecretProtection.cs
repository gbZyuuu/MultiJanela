using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace RobloxMultiManager.Core;

public static class LocalSecretProtection
{
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Length; public nint Data; }
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CryptProtectData(ref Blob input, string? description, nint entropy, nint reserved, nint prompt, uint flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(ref Blob input, nint description, nint entropy, nint reserved, nint prompt, uint flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint memory);
    public static string Protect(string value) => string.IsNullOrEmpty(value) ? "" : Convert.ToBase64String(Transform(Encoding.UTF8.GetBytes(value), false));
    public static string Unprotect(string value) => string.IsNullOrEmpty(value) ? "" : Decode(Transform(Convert.FromBase64String(value), true));
    private static string Decode(byte[] value)
    { try { return Encoding.UTF8.GetString(value); } finally { CryptographicOperations.ZeroMemory(value); } }
    private static byte[] Transform(byte[] bytes, bool decrypt)
    {
        if (bytes.Length > 32768) throw new ArgumentException();
        var input = new Blob { Length = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) }; Blob output = default;
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            var success = decrypt ? CryptUnprotectData(ref input, 0, 0, 0, 0, 1, out output)
                : CryptProtectData(ref input, "MultiJanela", 0, 0, 0, 1, out output);
            if (!success) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (output.Length is < 1 or > 65536) throw new CryptographicException();
            var result = new byte[output.Length]; Marshal.Copy(output.Data, result, 0, result.Length); return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            for (var i = 0; i < input.Length; i++) Marshal.WriteByte(input.Data, i, 0);
            Marshal.FreeHGlobal(input.Data);
            if (output.Data != 0)
            { for (var i = 0; i < output.Length; i++) Marshal.WriteByte(output.Data, i, 0); LocalFree(output.Data); }
        }
    }
}

public sealed record StoredNotificationPreferences(bool Desktop = false, bool Webhook = false, string ProtectedUrl = "")
{
    public NotificationPreferences ToRuntime() => NotificationPreferences.Validate(new(Desktop, Webhook, LocalSecretProtection.Unprotect(ProtectedUrl)));
    public static StoredNotificationPreferences FromRuntime(NotificationPreferences value) => new(value.Desktop, value.Webhook, LocalSecretProtection.Protect(value.WebhookUrl));
}
