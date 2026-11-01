using TrayX.UI.ViewModels;

namespace TrayX.UI.Services;

/// <summary>
/// Картинки значков тянутся из реестра по одному запросу, поэтому сначала используется кэш,
/// а новые ключи читаются ограниченным пакетом.
/// </summary>
public static class TrayIconImageLoader
{
    private const int BatchSize = 60;

    public static async Task LoadAsync(UiContext ui, IEnumerable<TrayLiveIconItemViewModel> items)
    {
        var pending = new List<TrayLiveIconItemViewModel>();

        foreach (var item in items)
        {
            if (item.RegistryKey is not { } key)
            {
                continue;
            }

            if (TrayImageProvider.TryGet(key, out var cached) && cached is not null)
            {
                item.Image = cached;
                continue;
            }

            pending.Add(item);
        }

        foreach (var item in pending.Take(BatchSize))
        {
            var key = item.RegistryKey!;
            var image = await ui.GetTrayIconImageAsync(key).ConfigureAwait(true);
            var decoded = TrayImageProvider.Decode(key, image?.ImageBase64);
            if (decoded is not null)
            {
                item.Image = decoded;
            }
        }
    }
}
