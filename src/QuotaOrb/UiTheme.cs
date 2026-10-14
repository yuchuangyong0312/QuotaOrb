using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace QuotaOrb;

internal static class UiTheme
{
    internal static Brush Muted => (Brush)Application.Current.FindResource("Subtle");

    internal static void Apply(Window window)
    {
        window.SetResourceReference(FrameworkElement.StyleProperty, "AppWindowStyle");
        window.SourceInitialized += (_, _) =>
        {
            var dark = 1;
            var handle = new WindowInteropHelper(window).Handle;
            // Unsupported Windows builds keep their normal native title bar.
            if (DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int)) != 0)
                DwmSetWindowAttribute(handle, 19, ref dark, sizeof(int));
        };
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
