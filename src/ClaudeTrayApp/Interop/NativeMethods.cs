using System.Runtime.InteropServices;

namespace ClaudeTrayApp.Interop;

internal static partial class NativeMethods
{
    /// <summary>System DPI (96 = 100 %). The taskbar follows the primary monitor.</summary>
    [LibraryImport("user32.dll")]
    internal static partial uint GetDpiForSystem();

    /// <summary>Frees an HICON created by GDI+ (<c>Bitmap.GetHicon</c>); those are never freed automatically.</summary>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyIcon(nint handle);
}
