using System.Text;
using AppManager.Core.Models;

namespace AppManager.Core.Scanning;

/// <summary>
/// Builds a CSV report from (already filtered) app-usage groups. Pure, no IO.
/// All user-visible strings come from <see cref="CsvLabels"/> — the UI supplies
/// the localized values; Core stays language-neutral.
/// </summary>
public static class CsvReportBuilder
{
    /// <summary>Localized strings injected by the caller.</summary>
    public sealed record CsvLabels
    {
        /// <summary>Full header line (comma-separated column names).</summary>
        public required string Header { get; init; }
        public required string MsixSource { get; init; }
        public required string RegistrySource { get; init; }
        public required string ToolSource { get; init; }
        public required string Yes { get; init; }
        public required string No { get; init; }
        public required Func<MatchConfidence, string> ConfidenceName { get; init; }
        public required Func<AttributedFolder, string> ReasonText { get; init; }
    }

    /// <summary>
    /// One app row plus the folders/totals the caller chose to show
    /// (already filtered to match the UI: hidden-low-confidence...).
    /// </summary>
    public sealed record Row(
        AppUsageGroup Group,
        IReadOnlyList<AttributedFolder> Folders,
        long TotalBytes,
        long InstallBytes,
        long DataBytes);

    public static string Build(IReadOnlyList<Row> rows, CsvLabels labels,
        Func<FolderCategory, string>? categoryName = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine(labels.Header);

        foreach (var row in rows)
        {
            var g = row.Group;
            var appName = Escape(g.DisplayName);
            var publisher = Escape(g.Publisher);
            var source = g.App?.Source == AppSource.Msix
                ? labels.MsixSource
                : g.App?.Source == AppSource.Registry ? labels.RegistrySource : labels.ToolSource;
            var estimated = g.InstallSizeIsEstimated ? labels.Yes : labels.No;

            if (row.Folders.Count == 0)
            {
                sb.Append(appName).Append(',')
                  .Append(publisher).Append(',')
                  .Append(source).Append(',')
                  .Append(row.TotalBytes).Append(',')
                  .Append(row.InstallBytes).Append(',')
                  .Append(row.DataBytes).Append(',')
                  .Append(estimated).Append(",,,,,\n");
                continue;
            }

            foreach (var f in row.Folders)
            {
                sb.Append(appName).Append(',')
                  .Append(publisher).Append(',')
                  .Append(source).Append(',')
                  .Append(row.TotalBytes).Append(',')
                  .Append(row.InstallBytes).Append(',')
                  .Append(row.DataBytes).Append(',')
                  .Append(estimated).Append(',')
                  .Append(Escape(f.Folder.FullPath)).Append(',')
                  .Append(Escape(categoryName is null ? f.Folder.Category.ToString() : categoryName(f.Folder.Category))).Append(',')
                  .Append(f.Folder.SizeBytes).Append(',')
                  .Append(Escape(labels.ReasonText(f))).Append(',')
                  .Append(Escape(labels.ConfidenceName(f.Confidence))).Append(',')
                  .Append(Escape(DriveRoot(f.Folder.FullPath))).Append('\n');
            }
        }

        return sb.ToString();
    }

    private static string DriveRoot(string path)
    {
        try
        {
            return Path.GetPathRoot(path) ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    internal static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        if (value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0)
            return value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
