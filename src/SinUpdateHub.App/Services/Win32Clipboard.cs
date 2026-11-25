using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace SinUpdateHub.App.Services;

/// <summary>
/// 基于 Win32 API 的非阻塞剪贴板辅助类：
/// - 用 IsClipboardFormatAvailable 判断格式（不调用 OpenClipboard，零卡顿，彻底避免 Ctrl+E/聚焦时触发 WPF 内部重试）；
/// - 读写纯文本时直接调用 OpenClipboard/GetClipboardData/SetClipboardData，最多短暂重试 3 次（每次 8ms）。
/// </summary>
public static class Win32Clipboard
{
    private const uint CfBitmap = 2;
    private const uint CfDib = 8;
    private const uint CfUnicodeText = 13;
    private const uint CfHDrop = 15;
    private const uint CfDibV5 = 17;
    private const uint GmemMoveable = 0x0002;

    private static readonly uint CfPng = RegisterClipboardFormat("PNG");

    /// <summary>瞬时判断剪贴板是否包含 Unicode 文本格式（不打开剪贴板、不阻塞）。</summary>
    public static bool HasTextFormat() => IsClipboardFormatAvailable(CfUnicodeText);

    /// <summary>瞬时判断剪贴板是否可能包含图片或文件拖放格式（不打开剪贴板、不阻塞）。</summary>
    public static bool HasAnyImageFormat()
        => IsClipboardFormatAvailable(CfDibV5)
           || IsClipboardFormatAvailable(CfDib)
           || IsClipboardFormatAvailable(CfBitmap)
           || (CfPng != 0 && IsClipboardFormatAvailable(CfPng))
           || IsClipboardFormatAvailable(CfHDrop);

    /// <summary>快速读取剪贴板中的 Unicode 纯文本（不走 WPF 重试机制）。</summary>
    public static bool TryGetText(out string text)
    {
        text = string.Empty;
        if (!HasTextFormat())
        {
            return false;
        }

        if (!TryOpenClipboardWithShortRetry())
        {
            return false;
        }

        try
        {
            var handle = GetClipboardData(CfUnicodeText);
            if (handle == IntPtr.Zero)
            {
                return false;
            }

            var ptr = GlobalLock(handle);
            if (ptr == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                text = Marshal.PtrToStringUni(ptr) ?? string.Empty;
                return true;
            }
            finally
            {
                GlobalUnlock(handle);
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    /// <summary>快速写入 Unicode 纯文本到剪贴板（不走 WPF 内部 1 秒级阻塞重试）。</summary>
    public static bool TrySetText(string text)
    {
        text ??= string.Empty;
        var bytes = Encoding.Unicode.GetBytes(text + "\0");
        var hGlobal = GlobalAlloc(GmemMoveable, (UIntPtr)bytes.Length);
        if (hGlobal == IntPtr.Zero)
        {
            return false;
        }

        var ptr = GlobalLock(hGlobal);
        if (ptr == IntPtr.Zero)
        {
            GlobalFree(hGlobal);
            return false;
        }

        try
        {
            Marshal.Copy(bytes, 0, ptr, bytes.Length);
        }
        finally
        {
            GlobalUnlock(hGlobal);
        }

        if (!TryOpenClipboardWithShortRetry())
        {
            GlobalFree(hGlobal);
            return false;
        }

        try
        {
            EmptyClipboard();
            if (SetClipboardData(CfUnicodeText, hGlobal) == IntPtr.Zero)
            {
                GlobalFree(hGlobal);
                return false;
            }

            // SetClipboardData 成功后系统接管 hGlobal 所有权，切勿释放
            return true;
        }
        finally
        {
            CloseClipboard();
        }
    }

    private static bool TryOpenClipboardWithShortRetry()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (OpenClipboard(IntPtr.Zero))
            {
                return true;
            }

            if (attempt < 2)
            {
                Thread.Sleep(8);
            }
        }

        return false;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint RegisterClipboardFormat(string lpszFormat);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint uFormat);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr hMem);
}
