using System.Windows;
using System.Windows.Input;

namespace TrayX.UI.Views;

public partial class DialogWindow : Window
{
    private readonly bool _requiresText;

    public DialogWindow(
        string title,
        string message,
        string? initialText,
        string okText,
        string cancelText,
        bool requiresText)
    {
        InitializeComponent();

        TitleText.Text = title;
        MessageText.Text = message;
        OkButton.Content = okText;
        CancelButton.Content = cancelText;
        _requiresText = requiresText;

        if (requiresText)
        {
            InputBox.Text = initialText ?? string.Empty;
            UpdateOkState();
            Loaded += (_, _) =>
            {
                InputBox.Focus();
                InputBox.SelectAll();
            };
        }
        else
        {
            InputHost.Visibility = Visibility.Collapsed;
            Loaded += (_, _) => OkButton.Focus();
        }

        KeyDown += OnKeyDown;
    }

    public string? ResultText { get; private set; }

    private void OnInputChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        UpdateOkState();
    }

    private void UpdateOkState()
    {
        if (OkButton is not null)
        {
            OkButton.IsEnabled = !_requiresText || !string.IsNullOrWhiteSpace(InputBox.Text);
        }
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        if (_requiresText && string.IsNullOrWhiteSpace(InputBox.Text))
        {
            return;
        }

        ResultText = _requiresText ? InputBox.Text.Trim() : null;
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
        }
        else if (e.Key == Key.Enter && OkButton.IsEnabled)
        {
            OnOkClick(sender, e);
        }
    }
}
