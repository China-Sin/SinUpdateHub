using System.Runtime.InteropServices;
using System.Text;

namespace SinUpdateHub.Core.Services;

/// <summary>
/// 用 Windows DPAPI（CryptProtectData / CryptUnprotectData）加密敏感字符串（WebDAV 密码、GitHub Token）。
/// 直接 P/Invoke，避免额外 NuGet 依赖。密文格式：dpapi:&lt;base64&gt;。
/// 非 Windows 或加密失败时退化为 plain:&lt;base64&gt; 并记录日志。
/// </summary>
public static class SecretProtector
{
    private const string ProtectedPrefix = "dpapi:";
    private const string PlainPrefix = "plain:";
    private const int UiForbidden = 0x1;

    public static string Protect(string? plain)
    {
        if (string.IsNullOrEmpty(plain))
        {
            return string.Empty;
        }

        try
        {
            var bytes = Encoding.UTF8.GetBytes(plain);
            var outBlob = new DataBlob();
            var inBlob = new DataBlob { DataLength = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
            try
            {
                Marshal.Copy(bytes, 0, inBlob.Data, bytes.Length);
                if (CryptProtectData(ref inBlob, "SinUpdateHub", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, ref outBlob))
                {
                    try
                    {
                        var protectedBytes = new byte[outBlob.DataLength];
                        Marshal.Copy(outBlob.Data, protectedBytes, 0, outBlob.DataLength);
                        return ProtectedPrefix + Convert.ToBase64String(protectedBytes);
                    }
                    finally
                    {
                        if (outBlob.Data != IntPtr.Zero)
                        {
                            LocalFree(outBlob.Data);
                        }
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(inBlob.Data);
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("DPAPI 加密失败，退回弱保护", ex);
        }

        return PlainPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(plain));
    }

    public static string Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored))
        {
            return string.Empty;
        }

        try
        {
            if (stored.StartsWith(PlainPrefix, StringComparison.Ordinal))
            {
                return Encoding.UTF8.GetString(Convert.FromBase64String(stored[PlainPrefix.Length..]));
            }

            if (!stored.StartsWith(ProtectedPrefix, StringComparison.Ordinal))
            {
                // 兼容明文（用户手改 settings.json 的情况）
                return stored;
            }

            var protectedBytes = Convert.FromBase64String(stored[ProtectedPrefix.Length..]);
            var inBlob = new DataBlob { DataLength = protectedBytes.Length, Data = Marshal.AllocHGlobal(protectedBytes.Length) };
            var outBlob = new DataBlob();
            try
            {
                Marshal.Copy(protectedBytes, 0, inBlob.Data, protectedBytes.Length);
                if (CryptUnprotectData(ref inBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, ref outBlob))
                {
                    try
                    {
                        var plainBytes = new byte[outBlob.DataLength];
                        Marshal.Copy(outBlob.Data, plainBytes, 0, outBlob.DataLength);
                        return Encoding.UTF8.GetString(plainBytes);
                    }
                    finally
                    {
                        if (outBlob.Data != IntPtr.Zero)
                        {
                            LocalFree(outBlob.Data);
                        }
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(inBlob.Data);
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("DPAPI 解密失败", ex);
        }

        return string.Empty;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int DataLength;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob pDataIn,
        string? szDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        ref DataBlob pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob pDataIn,
        IntPtr ppszDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        ref DataBlob pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);
}
