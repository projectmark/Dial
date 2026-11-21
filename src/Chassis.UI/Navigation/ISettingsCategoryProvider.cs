using Microsoft.UI.Xaml;

namespace Chassis.UI.Navigation;

/// <summary>
/// Describes an extensible Settings filter category and lazily generated section content.
/// </summary>
public sealed record SettingsCategoryDescriptor(
    string Id,
    string TitleLocalizationKey,
    Func<UIElement> SectionFactory,
    int Order = 100);

/// <summary>
/// Implemented by modules or host adapters that contribute custom Settings categories.
/// </summary>
public interface ISettingsCategoryProvider
{
    IEnumerable<SettingsCategoryDescriptor> GetCategories();
}
