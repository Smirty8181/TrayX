using CommunityToolkit.Mvvm.ComponentModel;
using TrayX.UI.Localization;

namespace TrayX.UI.ViewModels;

public abstract class PageViewModelBase : ObservableObject
{
    protected PageViewModelBase()
    {
        Loc.Instance.LanguageChanged += OnLanguageChangedInternal;
    }

    public abstract string Key { get; }

    protected abstract string TitleKey { get; }

    protected abstract string SubtitleKey { get; }

    public string Title => Loc.T(TitleKey);

    public string Subtitle => Loc.T(SubtitleKey);

    public bool IsActive { get; private set; }

    public async Task ActivateAsync()
    {
        IsActive = true;
        try
        {
            await OnActivatedAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Page {Key} activation failed: {exception}");
        }
    }

    public void Deactivate()
    {
        if (!IsActive)
        {
            return;
        }

        IsActive = false;
        OnDeactivated();
    }

    protected virtual Task OnActivatedAsync() => Task.CompletedTask;

    protected virtual void OnDeactivated()
    {
    }

    protected virtual void OnLanguageChanged()
    {
    }

    private void OnLanguageChangedInternal(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Subtitle));
        OnLanguageChanged();
    }
}
