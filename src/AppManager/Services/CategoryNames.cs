using AppManager.Core.Models;

namespace AppManager.Services;

/// <summary>Localized display labels for FolderCategory — single source of truth.</summary>
public static class CategoryNames
{
    public static string Of(FolderCategory category)
        => AppServices.Loc.TryGet($"Category_{category}") ?? category.ToString();

    public static IReadOnlyList<FolderCategory> All { get; } =
        Enum.GetValues<FolderCategory>();
}

/// <summary>Localized display labels for HarnessKind — single source of truth.</summary>
public static class HarnessKindNames
{
    public static string Of(HarnessKind kind)
        => AppServices.Loc.TryGet($"Kind_{kind}") ?? kind.ToString();
}
