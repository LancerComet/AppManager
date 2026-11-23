using System.Runtime.InteropServices;
using AppManager.Core.Matching;

namespace AppManager.Core.Inventory;

/// <summary>
/// Maps Start Menu shortcut (.lnk) names to their target exe's directory, resolved
/// via IShellLinkW COM interop. Looked up by app name via NameNormalizer equality.
/// </summary>
public sealed partial class StartMenuShortcutLocator
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

    // NativeAOT-safe COM: CoCreateInstance + raw vtable calls — built-in COM
    // interop ([ComImport]/Marshal.ReleaseComObject) is unavailable under AOT.
    // IShellLinkW vtable: QI/AddRef/Release, GetPath is index 3.
    // IPersistFile vtable: QI/AddRef/Release, GetClassID, IsDirty, Load is index 5.
    private static unsafe string? ResolveShortcut(string lnkPath)
    {
        IntPtr linkCom = IntPtr.Zero;
        IntPtr persistCom = IntPtr.Zero;
        try
        {
            var iid = IidIShellLinkW;
            var clsid = ClsidShellLink;
            var hr = CoCreateInstance(clsid, IntPtr.Zero, ClsctxInprocServer, iid, out linkCom);
            if (hr != 0 || linkCom == IntPtr.Zero)
                return null;

            var linkVt = *(void***)linkCom;
            var queryInterface =
                (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)linkVt[0];
            var iidPf = IidIPersistFile;
            hr = queryInterface(linkCom, &iidPf, &persistCom);
            if (hr != 0 || persistCom == IntPtr.Zero)
                return null;

            var load =
                (delegate* unmanaged[Stdcall]<IntPtr, char*, uint, int>)(*(void***)persistCom)[5];
            fixed (char* path = lnkPath)
                hr = load(persistCom, path, 0);
            if (hr != 0)
                return null;

            var getPath =
                (delegate* unmanaged[Stdcall]<IntPtr, char*, int, IntPtr, uint, int>)linkVt[3];
            var buffer = stackalloc char[4096];
            hr = getPath(linkCom, buffer, 4096, IntPtr.Zero, SLGP_RAWPATH | SLGP_UNCPRIORITY);
            if (hr != 0)
                return null;

            var target = new string(buffer);
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
            ReleaseCom(persistCom);
            ReleaseCom(linkCom);
        }
    }

    private static unsafe void ReleaseCom(IntPtr com)
    {
        if (com != IntPtr.Zero)
            ((delegate* unmanaged[Stdcall]<IntPtr, uint>)(*(void***)com)[2])(com);
    }

    private const uint SLGP_UNCPRIORITY = 0x2;
    private const uint SLGP_RAWPATH = 0x4;
    private const uint ClsctxInprocServer = 0x1;

    private static readonly Guid ClsidShellLink = new("00021401-0000-0000-C000-000000000046");
    private static readonly Guid IidIShellLinkW = new("000214F9-0000-0000-C000-000000000046");
    private static readonly Guid IidIPersistFile = new("0000010B-0000-0000-C000-000000000046");

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(
        in Guid rclsid,
        IntPtr pUnkOuter,
        uint dwClsContext,
        in Guid riid,
        out IntPtr ppv);
}
