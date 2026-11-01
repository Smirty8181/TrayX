using System.Diagnostics;
using System.IO;
using System.Text;

namespace TrayX.UI.Services;

internal static class UiExceptionLog
{
    private const long DefaultMaxFileSizeBytes = 5 * 1024 * 1024;
    private const int DefaultRetainedFileCount = 14;
    private const int MaxEntryLength = 64 * 1024;
    private static readonly object Gate = new();
    private static string? _filePath;
    private static long _maxFileSizeBytes = DefaultMaxFileSizeBytes;
    private static int _retainedFileCount = DefaultRetainedFileCount;

    public static void Initialize()
    {
        try
        {
            var logDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "TrayX",
                "logs");
            Initialize(logDirectory, DefaultMaxFileSizeBytes, DefaultRetainedFileCount);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"TrayX.UI could not initialize its log file: {exception}");
        }
    }

    internal static void Initialize(string logDirectory, long maxFileSizeBytes, int retainedFileCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logDirectory);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxFileSizeBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(retainedFileCount);

        lock (Gate)
        {
            Directory.CreateDirectory(logDirectory);
            _filePath = Path.Combine(logDirectory, $"ui-{DateTime.Now:yyyyMMdd}.log");
            _maxFileSizeBytes = maxFileSizeBytes;
            _retainedFileCount = retainedFileCount;
            PruneOldLogs(logDirectory, retainedFileCount);
        }
    }

    internal static void ResetForTests()
    {
        lock (Gate)
        {
            _filePath = null;
            _maxFileSizeBytes = DefaultMaxFileSizeBytes;
            _retainedFileCount = DefaultRetainedFileCount;
        }
    }

    public static void Write(string source, string details)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(details);
        if (details.Length > MaxEntryLength)
        {
            details = details[..MaxEntryLength];
        }

        var entry = $"{DateTimeOffset.Now:O} [{source}] {details}{Environment.NewLine}";
        lock (Gate)
        {
            if (_filePath is null)
            {
                Debug.WriteLine(entry);
                return;
            }

            try
            {
                RotateIfNeeded(_filePath, Encoding.UTF8.GetByteCount(entry), _maxFileSizeBytes, _retainedFileCount);
                File.AppendAllText(_filePath, entry);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Debug.WriteLine($"TrayX.UI could not write its log file: {exception}{Environment.NewLine}{entry}");
            }
        }
    }

    private static void RotateIfNeeded(string filePath, int incomingBytes, long maxFileSizeBytes, int retainedFileCount)
    {
        if (!File.Exists(filePath) || new FileInfo(filePath).Length + incomingBytes <= maxFileSizeBytes)
        {
            return;
        }

        var oldest = $"{filePath}.{retainedFileCount - 1}";
        if (retainedFileCount > 1 && File.Exists(oldest))
        {
            File.Delete(oldest);
        }

        for (var index = retainedFileCount - 2; index >= 1; index--)
        {
            var source = $"{filePath}.{index}";
            if (File.Exists(source))
            {
                File.Move(source, $"{filePath}.{index + 1}");
            }
        }

        if (retainedFileCount > 1)
        {
            File.Move(filePath, $"{filePath}.1");
        }
        else
        {
            File.Delete(filePath);
        }
    }

    private static void PruneOldLogs(string logDirectory, int retainedFileCount)
    {
        var files = new DirectoryInfo(logDirectory)
            .EnumerateFiles("ui-*.log*")
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ToArray();
        foreach (var file in files.Skip(retainedFileCount))
        {
            file.Delete();
        }
    }
}
