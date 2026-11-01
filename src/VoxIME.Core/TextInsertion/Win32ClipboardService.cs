using System.Text;
using VoxIME.Core.Abstractions;

namespace VoxIME.Core.TextInsertion;

/// <summary>
/// 通过 Win32 剪贴板 API（CF_UNICODETEXT）读写系统剪贴板，不依赖 WPF。
/// </summary>
/// <remarks>
/// 剪贴板同一时刻只能被一个进程打开，被 Office/远程桌面等占用时会失败；
/// 这里用有限次重试（共约 200ms）提高成功率，失败时抛 <see cref="InvalidOperationException"/>。
/// </remarks>
public sealed class Win32ClipboardService : IClipboardService
{
    private const int OpenRetryCount = 10;
    private const int OpenRetryDelayMs = 20;

    public void SetText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (!TryOpenClipboard())
        {
            throw new InvalidOperationException("剪贴板正被其他程序占用，写入失败。");
        }

        try
        {
            if (!TextInsertionNativeMethods.EmptyClipboard())
            {
                throw new InvalidOperationException($"清空剪贴板失败（Windows 错误码 {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}）。");
            }

            var bytes = Encoding.Unicode.GetBytes(text + "\0");
            var handle = TextInsertionNativeMethods.GlobalAlloc(
                TextInsertionNativeMethods.GMEM_MOVEABLE | TextInsertionNativeMethods.GMEM_ZEROINIT,
                (UIntPtr)(uint)bytes.Length);
            if (handle == IntPtr.Zero)
            {
                throw new InvalidOperationException($"分配剪贴板内存失败（Windows 错误码 {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}）。");
            }

            var locked = false;
            try
            {
                var pointer = TextInsertionNativeMethods.GlobalLock(handle);
                if (pointer == IntPtr.Zero)
                {
                    throw new InvalidOperationException("锁定剪贴板内存失败。");
                }

                try
                {
                    System.Runtime.InteropServices.Marshal.Copy(bytes, 0, pointer, bytes.Length);
                }
                finally
                {
                    TextInsertionNativeMethods.GlobalUnlock(handle);
                }

                if (TextInsertionNativeMethods.SetClipboardData(TextInsertionNativeMethods.CF_UNICODETEXT, handle) == IntPtr.Zero)
                {
                    throw new InvalidOperationException($"写入剪贴板失败（Windows 错误码 {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}）。");
                }

                locked = true; // 所有权移交系统，不能再 GlobalFree。
            }
            finally
            {
                if (!locked)
                {
                    TextInsertionNativeMethods.GlobalFree(handle);
                }
            }
        }
        finally
        {
            TextInsertionNativeMethods.CloseClipboard();
        }
    }

    public string? TryGetText()
    {
        if (!TryOpenClipboard())
        {
            return null;
        }

        try
        {
            var handle = TextInsertionNativeMethods.GetClipboardData(TextInsertionNativeMethods.CF_UNICODETEXT);
            if (handle == IntPtr.Zero)
            {
                return null;
            }

            var pointer = TextInsertionNativeMethods.GlobalLock(handle);
            if (pointer == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                return System.Runtime.InteropServices.Marshal.PtrToStringUni(pointer);
            }
            finally
            {
                TextInsertionNativeMethods.GlobalUnlock(handle);
            }
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            TextInsertionNativeMethods.CloseClipboard();
        }
    }

    private static bool TryOpenClipboard()
    {
        for (var attempt = 0; attempt < OpenRetryCount; attempt++)
        {
            if (TextInsertionNativeMethods.OpenClipboard(IntPtr.Zero))
            {
                return true;
            }

            Thread.Sleep(OpenRetryDelayMs);
        }

        return false;
    }
}
