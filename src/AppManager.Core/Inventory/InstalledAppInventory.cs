using AppManager.Core.Models;

namespace AppManager.Core.Inventory;

/// <summary>
/// Combines all installed-app sources. A failing source contributes nothing.
/// </summary>
public sealed class InstalledAppInventory
{
    private readonly IReadOnlyList<IInstalledAppSource> _sources;

    public InstalledAppInventory(IEnumerable<IInstalledAppSource>? sources = null)
    {
        _sources = sources?.ToList() ?? new List<IInstalledAppSource>
        {
            new RegistryAppSource(),
            new MsixAppSource(),
        };
    }

    public IReadOnlyList<InstalledApp> GetInstalledApps()
    {
        var apps = new List<InstalledApp>();
        foreach (var source in _sources)
        {
            try
            {
                apps.AddRange(source.GetInstalledApps());
            }
            catch
            {
                // Tolerate a failing source.
            }
        }
        return apps;
    }
}
