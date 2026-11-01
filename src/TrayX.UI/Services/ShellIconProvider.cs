using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.IO;

namespace TrayX.UI.Services;

public static class ShellIconProvider
{
    private const uint ShgfiIcon = 0x00000100;
    private const uint ShgfiLargeIcon = 0x00000000;
    private const uint ShgfiUseFileAttributes = 0x00000010;
    private const uint FileAttributeNormal = 0x00000080;

    private static readonly ConcurrentDictionary<string, ImageSource?> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? Load(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        return Cache.GetOrAdd(path, static key => CreateIcon(key));
    }

    private static ImageSource? CreateIcon(string path)
    {
        var exists = File.Exists(path) || Directory.Exists(path);
        if (!exists && !path.Contains('.'))
        {
            return null;
        }

        var info = new ShFileInfo();
        var flags = ShgfiIcon | ShgfiLargeIcon;
        if (!exists)
        {
            flags |= ShgfiUseFileAttributes;
        }

        try
        {
            var result = SHGetFileInfo(
                path,
                exists ? 0 : FileAttributeNormal,
                ref info,
                (uint)Marshal.SizeOf<ShFileInfo>(),
                flags);

            if (result == IntPtr.Zero || info.Icon == IntPtr.Zero)
            {
                return null;
            }

            var source = Imaging.CreateBitmapSourceFromHIcon(
                info.Icon,
                Int32Rect.Empty,
                BitmapSizeOptions.FromWidthAndHeight(32, 32));
            source.Freeze();
            return source;
        }
        catch (Exception exception) when (
            exception is COMException or ArgumentException or NotSupportedException)
        {
            return null;
        }
        finally
        {
            if (info.Icon != IntPtr.Zero)
            {
                _ = DestroyIcon(info.Icon);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileInfo
    {
        public IntPtr Icon;
        public int IconIndex;
        public uint Attributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string DisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string TypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(
        string path,
        uint fileAttributes,
        ref ShFileInfo fileInfo,
        uint fileInfoSize,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);
}
