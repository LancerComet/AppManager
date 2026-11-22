using System.Text;
using AppManager.Core.Models;

namespace AppManager.Core.Scanning;

/// <summary>
/// Builds a CSV report from (already filtered) app-usage groups. Pure, no IO.
/// </summary>
public static class CsvReportBuilder
{
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

    public static string Build(IReadOnlyList<AppUsageGroup> groups)
        => Build(groups.Select(g => new Row(g, g.Folders, g.TotalBytes, g.InstallBytes, g.DataBytes)).ToList());

    public static string Build(IReadOnlyList<Row> rows, Func<FolderCategory, string>? categoryName = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("应用,发布者,来源,应用总计(字节),安装(字节),数据(字节),安装大小为估算,文件夹路径,类别,文件夹大小(字节),匹配方式,置信度,所在磁盘");

        foreach (var row in rows)
        {
            var g = row.Group;
            var appName = Escape(g.DisplayName);
            var publisher = Escape(g.Publisher);
            var source = g.App?.Source == AppSource.Msix
                ? "应用商店应用"
                : g.App?.Source == AppSource.Registry ? "传统程序" : "已知工具";
            var estimated = g.InstallSizeIsEstimated ? "是" : "否";

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
                  .Append(Escape(f.Reason)).Append(',')
                  .Append(Escape(ConfidenceText(f.Confidence))).Append(',')
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

    private static string ConfidenceText(MatchConfidence confidence) => confidence switch
    {
        MatchConfidence.High => "高",
        MatchConfidence.Medium => "中",
        MatchConfidence.Low => "低",
        _ => "无",
    };

    internal static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        if (value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0)
            return value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
