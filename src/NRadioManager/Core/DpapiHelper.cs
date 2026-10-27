using System.Runtime.InteropServices;
using System.Text;

namespace NRadioManager.Core;

/// <summary>
/// Windows DPAPI 包装（CryptProtectData / CryptUnprotectData）。
/// 用于加密保存登录密码：密文只能由当前 Windows 用户解开，不落明文。
/// 通过 P/Invoke 直接调用系统 API，避免为此引入第三方 NuGet 包。
/// </summary>
internal static class DpapiHelper
{
    private const int CryptProtectUiForbidden = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DataBlob pInput, string? description, IntPtr entropy, IntPtr reserved,
        IntPtr prompt, int flags, out DataBlob pOutput);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DataBlob pInput, IntPtr description, IntPtr entropy, IntPtr reserved,
        IntPtr prompt, int flags, out DataBlob pOutput);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    public static string? Protect(string plain)
    {
        var bytes = Encoding.UTF8.GetBytes(plain);
        var input = new DataBlob { cbData = bytes.Length, pbData = Marshal.AllocHGlobal(bytes.Length) };
        try
        {
            Marshal.Copy(bytes, 0, input.pbData, bytes.Length);
            if (!CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out var output))
                return null;
            try
            {
                var result = new byte[output.cbData];
                Marshal.Copy(output.pbData, result, 0, output.cbData);
                return Convert.ToBase64String(result);
            }
            finally { LocalFree(output.pbData); }
        }
        finally { Marshal.FreeHGlobal(input.pbData); }
    }

    public static string? Unprotect(string base64)
    {
        byte[] bytes;
        try { bytes = Convert.FromBase64String(base64); }
        catch { return null; }

        var input = new DataBlob { cbData = bytes.Length, pbData = Marshal.AllocHGlobal(bytes.Length) };
        try
        {
            Marshal.Copy(bytes, 0, input.pbData, bytes.Length);
            if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out var output))
                return null;
            try
            {
                var result = new byte[output.cbData];
                Marshal.Copy(output.pbData, result, 0, output.cbData);
                return Encoding.UTF8.GetString(result);
            }
            finally { LocalFree(output.pbData); }
        }
        finally { Marshal.FreeHGlobal(input.pbData); }
    }
}
