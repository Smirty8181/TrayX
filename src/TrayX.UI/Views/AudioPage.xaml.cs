using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TrayX.UI.Views;

public partial class AudioPage : UserControl
{
    public AudioPage()
    {
        InitializeComponent();
    }

    private void OnPreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        var current = e.OriginalSource as DependencyObject;
        while (current is not null && current is not Slider)
        {
            current = VisualTreeHelper.GetParent(current);
        }

        if (current is not Slider slider || !slider.IsEnabled)
        {
            return;
        }

        var step = slider.SmallChange > 0 ? slider.SmallChange : 2;
        slider.Value = Math.Clamp(
            slider.Value + (e.Delta > 0 ? step : -step),
            slider.Minimum,
            slider.Maximum);
        e.Handled = true;
    }
}
