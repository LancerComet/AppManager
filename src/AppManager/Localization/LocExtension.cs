using AppManager.Services;
using Microsoft.UI.Xaml.Markup;

namespace AppManager.Localization;

/// <summary>
/// <c>{l:Loc Key=Overview_Title}</c> — resolves a resource string through
/// <see cref="AppServices.Loc"/> at XAML load time. Combined with the shell
/// rebuild on language change this replaces x:Uid, which never re-resolves.
/// </summary>
[MarkupExtensionReturnType(ReturnType = typeof(string))]
public sealed class LocExtension : MarkupExtension
{
    public string Key { get; set; } = string.Empty;

    protected override object ProvideValue() => AppServices.Loc.Get(Key);
}
