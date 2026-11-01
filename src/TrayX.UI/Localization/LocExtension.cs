using System.Windows.Data;
using System.Windows.Markup;

namespace TrayX.UI.Localization;

[MarkupExtensionReturnType(typeof(string))]
public sealed class LocExtension : MarkupExtension
{
    public LocExtension()
    {
    }

    public LocExtension(string key)
    {
        Key = key;
    }

    [ConstructorArgument("key")]
    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding
        {
            Source = Loc.Instance,
            Path = new System.Windows.PropertyPath($"[{Key}]"),
            Mode = BindingMode.OneWay
        };

        return binding.ProvideValue(serviceProvider);
    }
}
