using AppManager.Core.Models;
using Windows.ApplicationModel;
using Windows.Management.Deployment;

namespace AppManager.Core.Inventory;

/// <summary>
/// Enumerates MSIX/Store packages for the current user via PackageManager.
/// Full-trust apps can call FindPackagesForUser("") without extra capabilities.
/// </summary>
public sealed class MsixAppSource : IInstalledAppSource
{
    public IReadOnlyList<InstalledApp> GetInstalledApps()
    {
        try
        {
            var manager = new PackageManager();
            var packages = manager.FindPackagesForUser(string.Empty);

            var apps = new List<InstalledApp>();
            foreach (var package in packages)
            {
                var app = ReadPackage(package);
                if (app is not null)
                    apps.Add(app);
            }
            return apps;
        }
        catch
        {
            return Array.Empty<InstalledApp>();
        }
    }

    private static InstalledApp? ReadPackage(Package package)
    {
        try
        {
            if (package.IsFramework || package.IsResourcePackage || package.IsBundle)
                return null;

            var id = package.Id;
            var name = SafeGet(() => package.DisplayName);
            if (string.IsNullOrWhiteSpace(name) || name.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
                name = id.Name;

            string? iconPath = null;
            try
            {
                var logo = package.Logo;
                if (logo is not null && logo.IsFile && File.Exists(logo.LocalPath))
                    iconPath = logo.LocalPath;
            }
            catch
            {
            }

            var isSystem = false;
            try
            {
                isSystem = package.SignatureKind == PackageSignatureKind.System;
            }
            catch
            {
            }

            string? installedPath = null;
            try
            {
                if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
                    installedPath = package.InstalledPath;
            }
            catch
            {
            }

            return new InstalledApp
            {
                Id = $"msix:{id.FamilyName}",
                Name = name!,
                Publisher = SafeGet(() => package.PublisherDisplayName),
                Version = $"{id.Version.Major}.{id.Version.Minor}.{id.Version.Build}.{id.Version.Revision}",
                InstallLocation = installedPath,
                IconPath = iconPath,
                Source = AppSource.Msix,
                PackageFamilyName = id.FamilyName,
                IsSystem = isSystem,
            };
        }
        catch
        {
            return null;
        }
    }

    private static string? SafeGet(Func<string?> getter)
    {
        try
        {
            var value = getter();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch
        {
            return null;
        }
    }
}
