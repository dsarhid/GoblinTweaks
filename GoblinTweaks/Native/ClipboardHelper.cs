using System.Runtime.InteropServices;

namespace GoblinTweaks.Native;

/// <summary>Puts text on the Windows clipboard.</summary>
internal static class ClipboardHelper
{
    private const uint CfUnicodeText = 13;
    private const uint GmemMoveable  = 0x0002;

    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(nint newOwner);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetClipboardData(uint format, nint memory);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint GlobalAlloc(uint flags, nuint bytes);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint GlobalLock(nint memory);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalUnlock(nint memory);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint GlobalFree(nint memory);

    [DllImport("user32.dll", SetLastError = true)] private static extern nint GetClipboardData(uint format);

    /// <summary>The text on the clipboard, or an empty string when there is none.</summary>
    public static string GetText()
    {
        if (!OpenClipboard(0)) return string.Empty;

        try
        {
            var memory = GetClipboardData(CfUnicodeText);
            if (memory == 0) return string.Empty;

            var source = GlobalLock(memory);
            if (source == 0) return string.Empty;

            try { return Marshal.PtrToStringUni(source) ?? string.Empty; }
            finally { GlobalUnlock(memory); }
        }
        finally { CloseClipboard(); }
    }

    public static bool SetText(string text)
    {
        var bytes  = (text.Length + 1) * 2;
        var memory = GlobalAlloc(GmemMoveable, (nuint)bytes);
        if (memory == 0) return false;

        var target = GlobalLock(memory);
        if (target == 0)
        {
            GlobalFree(memory);
            return false;
        }

        Marshal.Copy(text.ToCharArray(), 0, target, text.Length);
        Marshal.WriteInt16(target, text.Length * 2, 0);
        GlobalUnlock(memory);

        if (!OpenClipboard(0))
        {
            GlobalFree(memory);
            return false;
        }

        try
        {
            EmptyClipboard();
            if (SetClipboardData(CfUnicodeText, memory) != 0) return true; // the clipboard owns the memory now
            GlobalFree(memory);
            return false;
        }
        finally { CloseClipboard(); }
    }
}
