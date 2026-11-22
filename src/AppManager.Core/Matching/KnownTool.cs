using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using AppManager.Core.Models;

namespace AppManager.Core.Matching;

/// <summary>
/// A rule describing a well-known tool and the folders it typically creates
/// (e.g. ".gradle" in the user home). Loaded from the embedded KnownTools.json.
/// </summary>
public sealed record KnownTool
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public List<string> Folders { get; init; } = new();

    /// <summary>Optional: restrict matching to these categories. Empty = any.</summary>
    public List<FolderCategory> Scopes { get; init; } = new();

    /// <summary>App-name keywords; when one matches an installed app the folder is attributed to it.</summary>
    public List<string> Keywords { get; init; } = new();

    /// <summary>Optional: rule only matches when the folder's parent directory has this name.</summary>
    public string? Parent { get; init; }

    public string? Description { get; init; }
    public string? Category { get; init; }
}

public static class KnownTools
{
    private const string ResourceName = "AppManager.Core.Matching.KnownTools.json";

    public static IReadOnlyList<KnownTool> Load()
    {
        var assembly = typeof(KnownTools).Assembly;
        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{ResourceName}' not found.");
        var rules = JsonSerializer.Deserialize<List<KnownTool>>(stream, KnownToolsJsonContext.Default.ListKnownTool)
            ?? new List<KnownTool>();
        return rules;
    }
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(List<KnownTool>))]
[JsonSerializable(typeof(KnownTool))]
internal partial class KnownToolsJsonContext : JsonSerializerContext
{
}
