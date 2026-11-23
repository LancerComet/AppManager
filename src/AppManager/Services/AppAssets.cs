namespace AppManager.Services;

/// <summary>
/// Resolves bundled Assets on disk. Works both packaged (install dir) and
/// unpackaged (exe dir) — unlike ms-appx:/// URIs, which need package identity.
/// MRT scale/targetsize-qualified assets have no unqualified file on disk, so a
/// plain name like "Square44x44Logo.png" resolves to its best qualified variant.
/// </summary>
public static class AppAssets
{
    public static string PathOf(string relativePath)
    {
        var path = Path.Combine(AppContext.BaseDirectory, relativePath);
        if (File.Exists(path))
            return path;
        return ResolveQualified(path);
    }

    public static Uri UriOf(string relativePath)
        => new(PathOf(relativePath));

    private static string ResolveQualified(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (dir is null || !Directory.Exists(dir))
            return path;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);

        // Prefer the highest .scale-N variant (downscales sharpest);
        // fall back to any qualified variant (.targetsize-*, etc.).
        string? best = null;
        var bestScale = -1;
        foreach (var f in Directory.EnumerateFiles(dir, stem + ".*" + ext))
        {
            var name = Path.GetFileNameWithoutExtension(f)
                .Substring(stem.Length + 1);
            if (name.StartsWith("scale-", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(name.AsSpan(6), out var scale))
            {
                if (scale > bestScale)
                {
                    bestScale = scale;
                    best = f;
                }
            }
            else
            {
                best ??= f;
            }
        }
        return best ?? path;
    }
}
