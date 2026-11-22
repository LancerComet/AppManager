using System.Globalization;

namespace AppManager.Core.Scanning;

public static class SizeFormatter
{
    private const long KB = 1024L;
    private const long MB = KB * 1024;
    private const long GB = MB * 1024;
    private const long TB = GB * 1024;

    /// <summary>"0 B", "512 KB", "1.5 MB", "12.3 GB", "1.98 TB" (1024-based).</summary>
    public static string Format(long bytes)
    {
        if (bytes < 0)
            bytes = 0;
        if (bytes < KB)
            return $"{bytes} B";
        if (bytes < MB)
            return $"{bytes / KB} KB";
        if (bytes < GB)
            return ((double)bytes / MB).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
        if (bytes < TB)
            return ((double)bytes / GB).ToString("0.0", CultureInfo.InvariantCulture) + " GB";
        return ((double)bytes / TB).ToString("0.0", CultureInfo.InvariantCulture) + " TB";
    }
}
