using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;

namespace QuotaOrb;

internal static class AppIcons
{
    public static Icon LoadEmbedded(int size = 16)
    {
        using var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/QuotaOrb.ico")).Stream;
        using var icon = new Icon(stream,size,size);
        return (Icon)icon.Clone();
    }

    // All surfaces use the bundled brand; old Windows shortcut icons must not leak into the tray.
    public static Icon LoadTrayIcon() => LoadEmbedded(System.Windows.Forms.SystemInformation.SmallIconSize.Width);

    public static Icon? LoadFromFile(string file,int index,int size = 16)
    {
        var handles = new IntPtr[1];
        try
        {
            if (PrivateExtractIcons(file,index,size,size,handles,new uint[1],1,0)!=1 || handles[0]==IntPtr.Zero) return null;
            return (Icon)Icon.FromHandle(handles[0]).Clone();
        }
        finally { if (handles[0]!=IntPtr.Zero) DestroyIcon(handles[0]); }
    }

    [DllImport("user32.dll",CharSet=CharSet.Unicode)]
    private static extern uint PrivateExtractIcons(string file,int index,int cx,int cy,IntPtr[] icons,uint[] ids,uint count,uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
}
