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

    // Honor a user's custom desktop shortcut icon rather than replacing it on upgrades.
    public static Icon LoadTrayIcon()
    {
        object? shell = null;
        try
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return LoadEmbedded();
            shell = Activator.CreateInstance(shellType);
            foreach (var path in Directory.EnumerateFiles(desktop,"*.lnk").OrderBy(p=>Path.GetFileName(p)=="额度管理.lnk"?0:1))
            {
                object? shortcut = null;
                try
                {
                    shortcut = ((dynamic)shell!).CreateShortcut(path);
                    var target = (string)((dynamic)shortcut).TargetPath;
                    if (!string.Equals(Path.GetFullPath(target),Environment.ProcessPath,StringComparison.OrdinalIgnoreCase)) continue;
                    var location = (string)((dynamic)shortcut).IconLocation;
                    if (string.IsNullOrWhiteSpace(location)) return LoadEmbedded();
                    var comma = location.LastIndexOf(',');
                    var index = comma>=0 && int.TryParse(location[(comma+1)..],out var number)?number:0;
                    var file = Environment.ExpandEnvironmentVariables((comma>=0?location[..comma]:location).Trim('"'));
                    return LoadFromFile(file,index) ?? LoadEmbedded();
                }
                catch { }
                finally { if (shortcut is not null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut); }
            }
        }
        catch { }
        finally { if (shell is not null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell); }
        return LoadEmbedded();
    }

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
