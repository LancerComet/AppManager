using System.Text.Json;
using System.Text.Json.Serialization;
using AppManager.Core.Models;

namespace AppManager.Core.Scanning;

/// <summary>
/// Persists the last ScanResult to disk so the UI can show it before a new scan.
/// </summary>
public static class ScanResultStore
{
    public const int SchemaVersion = 3;

    internal sealed class Envelope
    {
        public int SchemaVersion { get; set; }
        public ScanResult? Result { get; set; }
    }

    public static async Task SaveAsync(ScanResult result, string path, CancellationToken cancellationToken = default)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var envelope = new Envelope { SchemaVersion = SchemaVersion, Result = result };
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(
            stream, envelope, ScanResultJsonContext.Default.Envelope, cancellationToken);
    }

    public static async Task<ScanResult?> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(path))
                return null;
            await using var stream = File.OpenRead(path);
            var envelope = await JsonSerializer.DeserializeAsync(
                stream, ScanResultJsonContext.Default.Envelope, cancellationToken);
            if (envelope is null || envelope.SchemaVersion != SchemaVersion)
                return null;
            return envelope.Result;
        }
        catch
        {
            return null;
        }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ScanResultStore.Envelope))]
internal partial class ScanResultJsonContext : JsonSerializerContext
{
}
