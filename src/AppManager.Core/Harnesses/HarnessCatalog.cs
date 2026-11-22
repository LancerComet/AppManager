using System.Text.Json;
using System.Text.Json.Serialization;
using AppManager.Core.Models;

namespace AppManager.Core.Harnesses;

/// <summary>
/// Which path-token family a catalog may reference. CurrentUser catalogs are
/// restricted to per-user locations; Machine catalogs may also point at
/// machine-wide roots like Program Files, ProgramData and fixed drives.
/// </summary>
public enum HarnessCatalogScope
{
    CurrentUser,
    Machine,
}

/// <summary>How a root's contents become entries.</summary>
public enum HarnessRootMode
{
    /// <summary>Every child gets a classification; each classification becomes its own entry.</summary>
    Split,
    /// <summary>Rule-matched children split off as their own entries; everything else merges into one root entry.</summary>
    Merge,
}

public enum HarnessSourceType
{
    Env,
    Ini,
    Xml,
}

/// <summary>An additional candidate directory for a root, resolved at scan time.</summary>
public sealed record HarnessSourceDefinition
{
    public required HarnessSourceType Type { get; init; }

    /// <summary>Environment variable name (Type == Env).</summary>
    public string? Env { get; init; }

    /// <summary>Tokenized file path to read (Type == Ini or Xml).</summary>
    public string? FilePath { get; init; }

    /// <summary>INI key to look up (Type == Ini).</summary>
    public string? Key { get; init; }

    /// <summary>XML element local name to look up (Type == Xml).</summary>
    public string? Element { get; init; }

    /// <summary>Optional suffix appended to the resolved path.</summary>
    public string? Suffix { get; init; }
}

/// <summary>A rule matching a direct child of a root to a kind.</summary>
public sealed record HarnessChildRule
{
    public required string Match { get; init; }
    public required HarnessKind Kind { get; init; }
}

/// <summary>A catalog root: token path (last segment may contain * / ?), default
/// kind, mode, optional merged-unit label, child-name prefix, extra sources and
/// ordered child rules.</summary>
public sealed record HarnessRootDefinition
{
    public string? Path { get; init; }
    public required HarnessKind Kind { get; init; }
    public HarnessRootMode Mode { get; init; } = HarnessRootMode.Split;
    public string? Label { get; init; }
    public string? EntryPrefix { get; init; }
    public IReadOnlyList<HarnessSourceDefinition> Sources { get; init; } = [];
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
/// The authored harness catalogs (embedded HarnessCatalog.json / DevToolsCatalog.json).
/// Load() / LoadFromJson() validate: unique ids, known kinds, resolvable acyclic
/// presets, valid path tokens for the given scope, wildcards only in the last
/// segment, no duplicate raw root paths, valid source types, and no
/// versions/versionSet kinds under a merge root.
/// </summary>
public sealed class HarnessCatalog
{
    public IReadOnlyList<HarnessDefinition> Harnesses { get; private init; } = [];

    private const string ResourceName = "AppManager.Core.Harnesses.HarnessCatalog.json";
    private const string DevToolsResourceName = "AppManager.Core.Harnesses.DevToolsCatalog.json";

    public static HarnessCatalog Load()
        => LoadFromJson(ReadEmbedded(ResourceName));

    public static HarnessCatalog LoadDevTools()
        => LoadFromJson(ReadEmbedded(DevToolsResourceName), HarnessCatalogScope.Machine);

    private static string ReadEmbedded(string name)
    {
        var assembly = typeof(HarnessCatalog).Assembly;
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded resource '{name}' not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static HarnessCatalog LoadFromJson(
        string json, HarnessCatalogScope scope = HarnessCatalogScope.CurrentUser)
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
                var kind = ParseKind(h.Id, r.Kind);
                var hasPath = !string.IsNullOrWhiteSpace(r.Path);
                var hasSources = r.Sources is { Count: > 0 };
                if (!hasPath && !hasSources)
                    throw new InvalidDataException(
                        $"Harness '{h.Id}': a root needs 'path' or 'sources'.");
                if (hasPath)
                {
                    ValidateRootPath(h.Id, r.Path!, scope);
                    if (!rawPaths.Add(r.Path!))
                        throw new InvalidDataException($"Duplicate root path '{r.Path}'.");
                }

                var mode = HarnessRootMode.Split;
                if (!string.IsNullOrEmpty(r.Mode)
                    && (!Enum.TryParse(r.Mode, ignoreCase: true, out mode)
                        || mode is not (HarnessRootMode.Split or HarnessRootMode.Merge)))
                    throw new InvalidDataException(
                        $"Harness '{h.Id}': unknown root mode '{r.Mode}'.");

                var children = ExpandChildren(h.Id, r.Children, presets);
                var sources = (r.Sources ?? []).Select(s => ToSource(h.Id, s, scope)).ToList();

                if (mode == HarnessRootMode.Merge
                    && (kind is HarnessKind.Versions or HarnessKind.VersionSet
                        || children.Any(c => c.Kind is HarnessKind.Versions or HarnessKind.VersionSet)))
                    throw new InvalidDataException(
                        $"Harness '{h.Id}': merge roots cannot use versions/versionSet kinds.");

                roots.Add(new HarnessRootDefinition
                {
                    Path = hasPath ? r.Path : null,
                    Kind = kind,
                    Mode = mode,
                    Label = r.Label,
                    EntryPrefix = r.EntryPrefix,
                    Sources = sources,
                    Children = children,
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

    private static HarnessSourceDefinition ToSource(
        string harnessId, SourceDto s, HarnessCatalogScope scope)
    {
        var types = (s.Env is not null ? 1 : 0)
                  + (s.Ini is not null ? 1 : 0)
                  + (s.Xml is not null ? 1 : 0);
        if (types != 1)
            throw new InvalidDataException(
                $"Harness '{harnessId}': a source must be exactly one of 'env', 'ini', 'xml'.");
        if (s.Ini is not null && string.IsNullOrWhiteSpace(s.Key))
            throw new InvalidDataException(
                $"Harness '{harnessId}': an 'ini' source needs 'key'.");
        if (s.Xml is not null && string.IsNullOrWhiteSpace(s.Element))
            throw new InvalidDataException(
                $"Harness '{harnessId}': an 'xml' source needs 'element'.");
        var file = s.Ini ?? s.Xml;
        if (file is not null)
            ValidateRootPath(harnessId, file, scope);
        return new HarnessSourceDefinition
        {
            Type = s.Env is not null ? HarnessSourceType.Env
                : s.Ini is not null ? HarnessSourceType.Ini
                : HarnessSourceType.Xml,
            Env = s.Env,
            FilePath = file,
            Key = s.Key,
            Element = s.Element,
            Suffix = s.Suffix,
        };
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

    private static void ValidateRootPath(string harnessId, string path, HarnessCatalogScope scope)
    {
        var normalized = path.Replace('/', '\\');
        var segments = normalized.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || !AllowedToken(segments[0], scope))
            throw new InvalidDataException(
                $"Harness '{harnessId}': root path '{path}' must start with a path token " +
                $"allowed in {scope} scope.");
        for (int i = 1; i < segments.Length - 1; i++)
        {
            if (segments[i].Contains('*') || segments[i].Contains('?'))
                throw new InvalidDataException(
                    $"Harness '{harnessId}': wildcards are only allowed in the last " +
                    $"path segment ('{path}').");
        }
    }

    private static bool AllowedToken(string segment, HarnessCatalogScope scope)
    {
        if (segment.Equals("%USERPROFILE%", StringComparison.OrdinalIgnoreCase)
            || segment.Equals("%APPDATA%", StringComparison.OrdinalIgnoreCase)
            || segment.Equals("%LOCALAPPDATA%", StringComparison.OrdinalIgnoreCase))
            return true;
        return scope == HarnessCatalogScope.Machine
            && (segment.Equals("%PROGRAMFILES%", StringComparison.OrdinalIgnoreCase)
                || segment.Equals("%PROGRAMFILES(X86)%", StringComparison.OrdinalIgnoreCase)
                || segment.Equals("%PROGRAMDATA%", StringComparison.OrdinalIgnoreCase)
                || segment.Equals("%DRIVES%", StringComparison.OrdinalIgnoreCase));
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
        public string? Mode { get; set; }
        public string? Label { get; set; }
        public string? EntryPrefix { get; set; }
        public List<ChildRuleDto>? Children { get; set; }
        public List<SourceDto>? Sources { get; set; }
    }

    internal sealed class SourceDto
    {
        public string? Env { get; set; }
        public string? Ini { get; set; }
        public string? Key { get; set; }
        public string? Xml { get; set; }
        public string? Element { get; set; }
        public string? Suffix { get; set; }
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
