using Microsoft.UI.Xaml;
using Windows.System;

namespace Chassis.UI.Navigation;

/// <summary>
/// Describes a lazy-loaded navigation destination in the Chassis top navigation bar.
/// </summary>
public sealed record NavigationItemDescriptor(
    string Id,
    string TitleLocalizationKey,
    string Glyph,
    Func<UIElement> ViewFactory,
    VirtualKey AcceleratorKey = VirtualKey.None,
    int Order = 0,
    bool ShowInNav = true);

/// <summary>
/// Registry of top-level navigation tabs sorted by <see cref="NavigationItemDescriptor.Order"/>.
/// </summary>
public interface INavigationRegistry
{
    event Action? ItemsChanged;
    void Register(NavigationItemDescriptor item);
    IReadOnlyList<NavigationItemDescriptor> GetItems();
    NavigationItemDescriptor? Find(string id);
}

/// <summary>
/// Thread-safe default implementation of <see cref="INavigationRegistry"/>.
/// </summary>
public sealed class NavigationRegistry : INavigationRegistry
{
    private readonly List<NavigationItemDescriptor> _items = [];
    private readonly Lock _gate = new();

    public event Action? ItemsChanged;

    public void Register(NavigationItemDescriptor item)
    {
        ArgumentNullException.ThrowIfNull(item);
        lock (_gate)
        {
            _items.RemoveAll(existing => string.Equals(existing.Id, item.Id, StringComparison.OrdinalIgnoreCase));
            _items.Add(item);
        }

        ItemsChanged?.Invoke();
    }

    public IReadOnlyList<NavigationItemDescriptor> GetItems()
    {
        lock (_gate)
        {
            return _items.OrderBy(i => i.Order).ThenBy(i => i.Id, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    public NavigationItemDescriptor? Find(string id)
    {
        lock (_gate)
        {
            return _items.FirstOrDefault(i => string.Equals(i.Id, id, StringComparison.OrdinalIgnoreCase));
        }
    }
}
