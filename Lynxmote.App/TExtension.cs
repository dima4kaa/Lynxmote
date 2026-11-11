using Avalonia.Markup.Xaml;
using Lynxmote.Core;

namespace Lynxmote.App;

/// <summary>Translated text in XAML: Text="{app:T 'Свойства'}".</summary>
public sealed class TExtension(string text) : MarkupExtension
{
    public override object ProvideValue(IServiceProvider serviceProvider) => L.T(text);
}
