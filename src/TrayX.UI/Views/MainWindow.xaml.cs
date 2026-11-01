using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using TrayX.UI.Localization;
using TrayX.UI.Services;
using TrayX.UI.ViewModels;

namespace TrayX.UI.Views;

public partial class MainWindow : Window
{
    private static readonly string[] PageOrder =
    {
        "dashboard", "audio", "displays", "apps", "tray", "profiles", "system", "settings"
    };

    private bool _forceClose;

    public MainWindow()
    {
        InitializeComponent();
        UpdateMaximizeIcon();
    }

    public void ForceClose()
    {
        _forceClose = true;
        Close();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ThemeManager.ApplyWindowChrome(this);
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_forceClose)
        {
            return;
        }

        e.Cancel = true;
        Hide();
        (DataContext as MainWindowViewModel)?.ShowToast(Loc.T("Window_HideHint"));
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        UpdateMaximizeIcon();

        if (WindowState == WindowState.Minimized)
        {
            Hide();
        }
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        if (IsVisible)
        {
            viewModel.ResumeCurrentPage();
        }
        else
        {
            viewModel.SuspendCurrentPage();
        }
    }

    private void OnActivatedState(object? sender, EventArgs e)
    {
        (DataContext as MainWindowViewModel)?.ResumeCurrentPage();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        if (e.Key == Key.Escape)
        {
            Hide();
            e.Handled = true;
            return;
        }

        var digit = (int)e.Key;
        if (Keyboard.Modifiers == ModifierKeys.Control &&
            digit >= (int)Key.D1 &&
            digit <= (int)Key.D8)
        {
            var index = (int)e.Key - (int)Key.D1;
            viewModel.Navigate(PageOrder[index]);
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.M)
        {
            viewModel.ToggleMuteCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.P)
        {
            viewModel.OpenQuickPanelCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void OnMaximizeClick(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void UpdateMaximizeIcon()
    {
        var key = WindowState == WindowState.Maximized ? "Icon.Restore" : "Icon.Maximize";
        MaximizeIcon.Data = TryFindResource(key) as Geometry;
    }
}
