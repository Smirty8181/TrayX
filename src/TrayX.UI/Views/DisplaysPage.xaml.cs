using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using TrayX.UI.ViewModels;

namespace TrayX.UI.Views;

public partial class DisplaysPage : UserControl
{
    public DisplaysPage()
    {
        InitializeComponent();
    }

    private void OnTrayPanelToggle(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { DataContext: DisplayItemViewModel display } toggle)
        {
            display.SetTrayPanelEnabled(toggle.IsChecked == true);
        }
    }
}
