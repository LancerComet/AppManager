using System.Runtime.InteropServices;
using System.Text;
using AppManager.Core.Matching;

namespace AppManager.Core.Inventory;

/// <summary>
/// Maps Start Menu shortcut (.lnk) names to their target exe's directory, resolved
/// via IShellLinkW COM interop. Looked up by app name via NameNormalizer equality.
/// </summary>
public sealed class StartMenuShortcutLocator
{
    // normalized shortcut-name stem -> target exe directory
    private readonly IReadOnlyDictionary<string, string> _map;

    public StartMenuShortcutLocator(IReadOnlyDictionary<string, string> normalizedNameToDir)
        => _map = normalizedNameToDir;

    public string? FindInstallDir(string appName)
    {
        var key = NameNormalizer.Normalize(appName);
        return key.Length >= 3 && _map.TryGetValue(key, out var dir) ? dir : null;
    }

    public static StartMenuShortcutLocator Load()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
        };

        foreach (var root in roots)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
                continue;
            foreach (var lnk in EnumerateLinks(root))
            {
                try
                {
                    var stem = Path.GetFileNameWithoutExtension(lnk);
                    var normalized = NameNormalizer.Normalize(stem);
                    if (normalized.Length < 3 || map.ContainsKey(normalized))
                        continue;

                    var target = ResolveShortcut(lnk);
                    if (target is null ||
                        !target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var dir = Path.GetDirectoryName(target);
                    if (dir is null || RegistryValueParser.IsRejectedLocation(dir))
                        continue;

                    map[normalized] = RegistryValueParser.NormalizeForCompare(dir);
                }
                catch
                {
                }
            }
        }

        return new StartMenuShortcutLocator(map);
    }

    private static IEnumerable<string> EnumerateLinks(string root)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        return new System.IO.Enumeration.FileSystemEnumerable<string>(root, (ref System.IO.Enumeration.FileSystemEntry e) => e.ToSpecifiedFullPath(),
            options)
        {
            ShouldIncludePredicate = (ref System.IO.Enumeration.FileSystemEntry e) =>
                !e.IsDirectory && e.FileName.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase),
        };
    }

    private static string? ResolveShortcut(string lnkPath)
    {
        object? linkObj = null;
        try
        {
            linkObj = new ShellLink();
            ((IPersistFile)linkObj).Load(lnkPath, 0);
            var link = (IShellLinkW)linkObj;
            var sb = new StringBuilder(4096);
            link.GetPath(sb, sb.Capacity, IntPtr.Zero, SLGP_RAWPATH | SLGP_UNCPRIORITY);
            var target = sb.ToString();
            if (string.IsNullOrWhiteSpace(target))
                return null;
            return RegistryValueParser.ParseIconPath(target); // rooted + full-path normalized
        }
        catch
        {
            return null;
        }
        finally
        {
            if (linkObj is not null)
                Marshal.ReleaseComObject(linkObj);
        }
    }

    private const uint SLGP_UNCPRIORITY = 0x2;
    private const uint SLGP_RAWPATH = 0x4;

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private sealed class ShellLink
    {
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("0000010B-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        void IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }
}
