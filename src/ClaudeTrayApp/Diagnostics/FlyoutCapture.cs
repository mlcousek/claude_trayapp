using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using ClaudeTrayApp.Interop;

namespace ClaudeTrayApp.Interop
{
    internal static partial class NativeMethods
    {
        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetWindowRect(nint hwnd, out RECT rect);
    }
}

namespace ClaudeTrayApp.Diagnostics
{
    /// <summary>Development aid behind <c>--capture-flyout</c>: screenshots the flyout as the screen shows it, backdrop included.</summary>
    internal static class FlyoutCapture
    {
        public static string Capture(Window window, string path)
        {
            ArgumentNullException.ThrowIfNull(window);
            ArgumentException.ThrowIfNullOrWhiteSpace(path);

            var hwnd = new WindowInteropHelper(window).Handle;
            if (!NativeMethods.GetWindowRect(hwnd, out var rect))
            {
                throw new InvalidOperationException("The flyout window rectangle is not available.");
            }

            var width = Math.Max(1, rect.Right - rect.Left);
            var height = Math.Max(1, rect.Bottom - rect.Top);
            using var bitmap = new Bitmap(width, height);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(rect.Left, rect.Top, 0, 0, new System.Drawing.Size(width, height));
            }

            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            bitmap.Save(path, ImageFormat.Png);
            return Path.GetFullPath(path);
        }
    }
}
