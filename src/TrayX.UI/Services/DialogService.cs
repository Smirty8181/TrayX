using System.Windows;
using TrayX.UI.Localization;
using TrayX.UI.Views;

namespace TrayX.UI.Services;

public sealed class DialogService
{
    public Task<bool> ConfirmAsync(string title, string message)
    {
        return ConfirmAsync(title, message, Loc.T("Common_Continue"), Loc.T("Common_Cancel"));
    }

    public Task<bool> ConfirmAsync(string title, string message, string okText, string cancelText)
    {
        var dialog = new DialogWindow(title, message, null, okText, cancelText, requiresText: false)
        {
            Owner = ResolveOwner()
        };

        return Task.FromResult(dialog.ShowDialog() == true);
    }

    public Task<string?> PromptAsync(string title, string message, string? initialText)
    {
        var dialog = new DialogWindow(
            title,
            message,
            initialText,
            Loc.T("Common_Continue"),
            Loc.T("Common_Cancel"),
            requiresText: true)
        {
            Owner = ResolveOwner()
        };

        var result = dialog.ShowDialog();
        return Task.FromResult(result == true ? dialog.ResultText : null);
    }

    private static Window? ResolveOwner()
    {
        var application = Application.Current;
        if (application is null)
        {
            return null;
        }

        return application.Windows
                   .OfType<Window>()
                   .FirstOrDefault(window => window.IsActive && window.IsVisible)
               ?? application.MainWindow;
    }
}
