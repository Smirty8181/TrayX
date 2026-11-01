using System.Collections.Concurrent;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TrayX.UI.Services;

/// <summary>
/// Windows держит PNG-снимок каждого значка в реестре NotifyIconSettings — здесь он становится картинкой.
/// </summary>
public static class TrayImageProvider
{
    private static readonly ConcurrentDictionary<string, ImageSource?> Cache = new(StringComparer.Ordinal);

    public static bool TryGet(string key, out ImageSource? image) => Cache.TryGetValue(key, out image);

    public static ImageSource? Decode(string key, string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64))
        {
            return null;
        }

        return Cache.GetOrAdd(key, _ => Create(base64));
    }

    private static ImageSource? Create(string base64)
    {
        try
        {
            var bytes = Convert.FromBase64String(base64);
            if (bytes.Length == 0)
            {
                return null;
            }

            using var stream = new MemoryStream(bytes);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception exception) when (
            exception is FormatException or IOException or NotSupportedException)
        {
            return null;
        }
    }
}
