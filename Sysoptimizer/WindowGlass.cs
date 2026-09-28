using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Sysoptimizer;

/// <summary>Acrylic backdrop and theme-colored title bar, shared by every window (see vault Branding/Aplicatii, WPF section).</summary>
public static class WindowGlass
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins { public int Left, Right, Top, Bottom; }

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWA_TEXT_COLOR = 36;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMSBT_ACRYLIC = 3;

    public static void EnableAcrylic(Window window)
    {
        var hwndSource = (HwndSource)PresentationSource.FromVisual(window)!;
        IntPtr hwnd = hwndSource.Handle;

        // The compositor's own background is opaque black by default: without this, every partial-alpha
        // pixel composes over black before it ever reaches DWM.
        hwndSource.CompositionTarget.BackgroundColor = Colors.Transparent;

        var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        DwmExtendFrameIntoClientArea(hwnd, ref margins);

        int dark = 1;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));

        int backdrop = DWMSBT_ACRYLIC;
        DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));

        ApplyChrome(window);
    }

    /// <summary>Title bar colors follow the active theme (Windows 11); Glass keeps the system look.</summary>
    public static void ApplyChrome(Window window)
    {
        if (PresentationSource.FromVisual(window) is not HwndSource source) return;
        var caption = ThemeManager.Current.Caption;
        int background = caption?.Background ?? unchecked((int)0xFFFFFFFF);
        int text = caption?.Text ?? unchecked((int)0xFFFFFFFF);
        int border = caption?.Border ?? unchecked((int)0xFFFFFFFF);
        DwmSetWindowAttribute(source.Handle, DWMWA_CAPTION_COLOR, ref background, sizeof(int));
        DwmSetWindowAttribute(source.Handle, DWMWA_TEXT_COLOR, ref text, sizeof(int));
        DwmSetWindowAttribute(source.Handle, DWMWA_BORDER_COLOR, ref border, sizeof(int));
    }
}
