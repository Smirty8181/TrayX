using System.Runtime.InteropServices;

namespace TrayX.Windows.Shell;

/// <summary>
/// Реестр трея хранит пути вида {6D809377-...}\Steam\steam.exe: это известная папка плюс относительный путь.
/// </summary>
internal static class KnownFolderPaths
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath(ref Guid folderId, uint flags, IntPtr token, out IntPtr path);

    [DllImport("ole32.dll")]
    private static extern void CoTaskMemFree(IntPtr block);

    private static readonly Dictionary<Guid, string> Folders = new();

    public static string Resolve(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var candidate = value.Trim().TrimStart('"');
        if (candidate.Length < 34 || candidate[0] != '{')
        {
            return candidate;
        }

        var separator = candidate.IndexOf('\\', StringComparison.Ordinal);
        if (separator < 0)
        {
            return candidate;
        }

        var rawGuid = candidate[..separator];
        if (!Guid.TryParse(rawGuid, out var folderId))
        {
            return candidate;
        }

        var folder = FolderPath(folderId);
        if (folder is null)
        {
            return candidate;
        }

        var relative = candidate[(separator + 1)..];
        return string.IsNullOrEmpty(relative) ? folder : Path.Combine(folder, relative);
    }

    private static string? FolderPath(Guid folderId)
    {
        if (Folders.TryGetValue(folderId, out var cached))
        {
            return cached;
        }

        string? resolved = null;
        try
        {
            if (SHGetKnownFolderPath(ref folderId, 0, IntPtr.Zero, out var pointer) == 0 && pointer != IntPtr.Zero)
            {
                resolved = Marshal.PtrToStringUni(pointer);
                CoTaskMemFree(pointer);
            }
        }
        catch (Exception exception) when (
            exception is EntryPointNotFoundException or
                DllNotFoundException or
                OutOfMemoryException)
        {
        }

        if (string.IsNullOrEmpty(resolved))
        {
            return null;
        }

        Folders[folderId] = resolved;
        return resolved;
    }
}
