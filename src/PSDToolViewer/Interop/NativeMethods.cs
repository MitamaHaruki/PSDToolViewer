using System.Runtime.InteropServices;

namespace PSDToolViewer.Interop;

internal static partial class NativeMethods
{
    public const int WM_NCHITTEST = 0x0084;
    public const int WM_NCMOUSEMOVE = 0x00A0;
    public const int WM_NCLBUTTONDOWN = 0x00A1;
    public const int WM_NCLBUTTONUP = 0x00A2;
    public const int WM_NCLBUTTONDBLCLK = 0x00A3;
    public const int WM_NCMOUSELEAVE = 0x02A2;
    public const int WM_MOUSEMOVE = 0x0200;
    public const int HTMAXBUTTON = 9;

    public const int SM_CXFRAME = 32;
    public const int SM_CYFRAME = 33;
    public const int SM_CXPADDEDBORDER = 92;

    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    public const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    public const int DWMSBT_NONE = 1;
    public const int DWMSBT_MAINWINDOW = 2;

    public const uint SHCNE_ASSOCCHANGED = 0x08000000;

    public const int GWL_STYLE = -16;
    public const long WS_SYSMENU = 0x00080000;

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static partial nint GetWindowLongPtr(nint hwnd, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    public static partial nint SetWindowLongPtr(nint hwnd, int index, nint value);

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    [LibraryImport("user32.dll")]
    public static partial int GetSystemMetricsForDpi(int index, uint dpi);

    [LibraryImport("shell32.dll")]
    public static partial void SHChangeNotify(uint eventId, uint flags, nint item1, nint item2);

    public static void SetWindowAttribute(nint hwnd, int attribute, int value)
        => DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int));

    public static int GetX(nint lParam) => unchecked((short)(long)lParam);
    public static int GetY(nint lParam) => unchecked((short)((long)lParam >> 16));
}
