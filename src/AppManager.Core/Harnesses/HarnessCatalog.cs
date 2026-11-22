using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using AppManager.Core.Models;

namespace AppManager.Core.Harnesses;

/// <summary>One child rule after preset expansion: glob match → kind.</summary>
public sealed record HarnessChildRule
{
    public required string Match { get; init; }
    public required HarnessKind Kind { get; init; }
}

/// <summary>A catalog root: token path (last segment may contain * / ?), default
/// kind and ordered child rules.</summary>
public sealed record HarnessRootDefinition
{
    public required string Path { get; init; }
    public required HarnessKind Kind { get; init; }
    public IReadOnlyList<HarnessChildRule> Children { get; init; } = [];
}

public sealed record HarnessDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Publisher { get; init; }
    public required HarnessType Type { get; init; }
    public string? IconApp { get; init; }
    public IReadOnlyList<HarnessRootDefinition> Roots { get; init; } = [];
}

/// <summary>
/// The authored LLM-harness catalog (embedded HarnessCatalog.json). Load() /
/// LoadFromJson() validate: unique ids, known kinds, resolvable acyclic
/// presets, valid path tokens, wildcards only in the last segment, and no
/// duplicate raw root paths.
/// </summary>
public sealed class HarnessCatalog
{
    public IReadOnlyList<HarnessDefinition> Harnesses { get; private init; } = [];

    private const string ResourceName = "AppManager.Core.Harnesses.HarnessCatalog.json";

    public static HarnessCatalog Load()
    {
        var assembly = typeof(HarnessCatalog).Assembly;
        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{ResourceName}' not found.");
        using var reader = new StreamReader(stream);
        return LoadFromJson(reader.ReadToEnd());
    }

    public static HarnessCatalog LoadFromJson(string json)
    {
        var dto = JsonSerializer.Deserialize<CatalogDto>(json, HarnessCatalogJsonContext.Default.CatalogDto)
            ?? throw new InvalidDataException("Empty harness catalog.");
        var presets = dto.Presets ?? new Dictionary<string, List<ChildRuleDto>>();

        var harnesses = new List<HarnessDefinition>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rawPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var h in dto.Harnesses ?? new List<HarnessDto>())
        {
            if (string.IsNullOrWhiteSpace(h.Id))
                throw new InvalidDataException("Harness missing 'id'.");
            if (!ids.Add(h.Id))
                throw new InvalidDataException($"Duplicate harness id '{h.Id}'.");
            if (string.IsNullOrWhiteSpace(h.Name))
                throw new InvalidDataException($"Harness '{h.Id}' missing 'name'.");

            var roots = new List<HarnessRootDefinition>();
            foreach (var r in h.Roots ?? new List<RootDto>())
            {
                if (string.IsNullOrWhiteSpace(r.Path))
                    throw new InvalidDataException($"Harness '{h.Id}': root missing 'path'.");
                ValidateRootPath(h.Id, r.Path);
                if (!rawPaths.Add(r.Path))
                    throw new InvalidDataException($"Duplicate root path '{r.Path}'.");
                var kind = ParseKind(h.Id, r.Kind);
                roots.Add(new HarnessRootDefinition
                {
                    Path = r.Path,
                    Kind = kind,
                    Children = ExpandChildren(h.Id, r.Children, presets),
                });
            }

            harnesses.Add(new HarnessDefinition
            {
                Id = h.Id,
                Name = h.Name,
                Publisher = h.Publisher,
                Type = ParseType(h.Id, h.Type),
                IconApp = h.IconApp,
                Roots = roots,
            });
        }

        return new HarnessCatalog { Harnesses = harnesses };
    }

    private static IReadOnlyList<HarnessChildRule> ExpandChildren(
        string harnessId,
        List<ChildRuleDto>? rules,
        Dictionary<string, List<ChildRuleDto>> presets)
    {
        var expanded = new List<HarnessChildRule>();
        void Visit(List<ChildRuleDto>? rs, HashSet<string> stack)
        {
            foreach (var r in rs ?? new List<ChildRuleDto>())
            {
                if (r.Preset is { } presetName)
                {
                    if (!presets.TryGetValue(presetName, out var presetRules))
                        throw new InvalidDataException(
                            $"Harness '{harnessId}': unknown preset '{presetName}'.");
                    if (!stack.Add(presetName))
                        throw new InvalidDataException(
                            $"Harness '{harnessId}': preset cycle involving '{presetName}'.");
                    Visit(presetRules, stack);
                    stack.Remove(presetName);
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(r.Match))
                        throw new InvalidDataException(
                            $"Harness '{harnessId}': child rule missing 'match'.");
                    expanded.Add(new HarnessChildRule
                    {
                        Match = r.Match,
                        Kind = ParseKind(harnessId, r.Kind),
                    });
                }
            }
        }
        Visit(rules, new HashSet<string>());
        return expanded;
    }

    private static void ValidateRootPath(string harnessId, string path)
    {
        var normalized = path.Replace('/', '\\');
        var segments = normalized.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 ||
            !(segments[0].Equals("%USERPROFILE%", StringComparison.OrdinalIgnoreCase) ||
              segments[0].Equals("%APPDATA%", StringComparison.OrdinalIgnoreCase) ||
              segments[0].Equals("%LOCALAPPDATA%", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException(
                $"Harness '{harnessId}': root path '{path}' must start with " +
                "%USERPROFILE%, %APPDATA% or %LOCALAPPDATA%.");
        for (int i = 1; i < segments.Length - 1; i++)
        {
            if (segments[i].Contains('*') || segments[i].Contains('?'))
                throw new InvalidDataException(
                    $"Harness '{harnessId}': wildcards are only allowed in the last " +
                    $"path segment ('{path}').");
        }
    }

    private static HarnessKind ParseKind(string harnessId, string? kind)
        => Enum.TryParse<HarnessKind>(kind, ignoreCase: true, out var k)
            ? k
            : throw new InvalidDataException($"Harness '{harnessId}': unknown kind '{kind}'.");

    private static HarnessType ParseType(string harnessId, string? type)
        => Enum.TryParse<HarnessType>(type, ignoreCase: true, out var t)
            ? t
            : throw new InvalidDataException($"Harness '{harnessId}': unknown type '{type}'.");

    internal sealed class CatalogDto
    {
        public Dictionary<string, List<ChildRuleDto>>? Presets { get; set; }
        public List<HarnessDto>? Harnesses { get; set; }
    }

    internal sealed class HarnessDto
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Publisher { get; set; }
        public string? Type { get; set; }
        public string? IconApp { get; set; }
        public List<RootDto>? Roots { get; set; }
    }

    internal sealed class RootDto
    {
        public string? Path { get; set; }
        public string? Kind { get; set; }
        public List<ChildRuleDto>? Children { get; set; }
    }

    internal sealed class ChildRuleDto
    {
        public string? Match { get; set; }
        public string? Kind { get; set; }
        public string? Preset { get; set; }
    }
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(HarnessCatalog.CatalogDto))]
internal partial class HarnessCatalogJsonContext : JsonSerializerContext
{
}
