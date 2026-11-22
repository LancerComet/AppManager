using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace AppManager.Services;

/// <summary>
/// Loads app icons: images via BitmapImage, everything else via the shell
/// thumbnail cache (StorageFile.GetThumbnailAsync) with an
/// IShellItemImageFactory fallback. UI-thread usage only.
/// In DEBUG every load outcome is appended to icon-log.txt in LocalFolder.
/// </summary>
public sealed class IconService
{
    private readonly ConcurrentDictionary<string, Task<ImageSource?>> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _limit = new(4);

    public Task<ImageSource?> GetIconAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Task.FromResult<ImageSource?>(null);
        var key = path.Trim().Trim('"');
        return _cache.GetOrAdd(key, k => LoadForPathAsync(k));
    }

    private async Task<ImageSource?> LoadForPathAsync(string path)
    {
        await _limit.WaitAsync();
        try
        {
            var result = await LoadCoreAsync(path);
#if DEBUG
            Log(path, result);
#endif
            return result;
        }
        catch (Exception ex)
        {
#if DEBUG
            Log(path, null, ex);
#endif
            return null;
        }
        finally
        {
            _limit.Release();
        }
    }

    private static async Task<ImageSource?> LoadCoreAsync(string path)
    {
        var ext = Path.GetExtension(path);
        if (ext.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
        {
            // A missing image file yields a BitmapImage that decodes to nothing —
            // return null so callers can show their fallback instead of a blank.
            if (!File.Exists(path))
                return null;
            return new BitmapImage(new Uri(path));
        }

        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var thumb = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, 64);
            if (thumb is not null && thumb.Size > 0)
            {
                var bitmap = new BitmapImage();
                await bitmap.SetSourceAsync(thumb);
                return bitmap;
            }
        }
        catch
        {
            // Fall through to the shell image factory.
        }

        var pixels = await Task.Run(() => ShellImageLoader.GetPixels(path, 64));
        if (pixels is null)
            return null;
        var softwareBitmap = new SoftwareBitmap(
            BitmapPixelFormat.Bgra8, 64, 64, BitmapAlphaMode.Premultiplied);
        softwareBitmap.CopyFromBuffer(pixels.AsBuffer());
        var source = new SoftwareBitmapSource();
        await source.SetBitmapAsync(softwareBitmap);
        return source;
    }

#if DEBUG
    private static void Log(string path, ImageSource? result, Exception? error = null)
    {
        try
        {
            var outcome = error is not null
                ? $"exception {error.GetType().Name} 0x{error.HResult:X8}: {error.Message}"
                : result is not null ? "ok" : "null";
            File.AppendAllText(
                Path.Combine(AppServices.Settings.StorageFolder, "icon-log.txt"),
                $"{DateTime.Now:HH:mm:ss.fff}\t{outcome}\t{path}\n");
        }
        catch
        {
        }
    }
#endif
}

/// <summary>
/// IShellItemImageFactory-based icon fallback: GetImage (SIIGBF_ICONONLY, 64px)
/// → HBITMAP → BGRA pixels → SoftwareBitmapSource. Runs off the UI thread.
/// </summary>
internal static class ShellImageLoader
{
    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(SIZE size, int flags, out IntPtr phbm);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx;
        public int cy;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
        IntPtr pbc,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object ppv);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(
        IntPtr hdc, IntPtr hbm, uint start, uint cLines,
        [Out] byte[] lpvBits, ref BITMAPINFOHEADER lpbmi, uint usage);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    private static readonly Guid IidIShellItemImageFactory = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");
    private const int SIIGBF_ICONONLY = 0x4;
    private const int SIIGBF_BIGGERSIZEOK = 0x1;

    /// <summary>Returns premultiplied BGRA pixels (size×size×4) or null.</summary>
    public static byte[]? GetPixels(string path, int size)
    {
        IntPtr hbm = IntPtr.Zero;
        try
        {
            SHCreateItemFromParsingName(path, IntPtr.Zero, IidIShellItemImageFactory, out var obj);
            var factory = (IShellItemImageFactory)obj;
            var hr = factory.GetImage(new SIZE { cx = size, cy = size },
                SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK, out hbm);
            Marshal.ReleaseComObject(factory);
            if (hr != 0 || hbm == IntPtr.Zero)
                return null;

            var header = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = size,
                biHeight = -size, // top-down
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0, // BI_RGB
            };
            var pixels = new byte[size * size * 4];
            var dc = GetDC(IntPtr.Zero);
            try
            {
                var rows = GetDIBits(dc, hbm, 0, (uint)size, pixels, ref header, 0);
                return rows > 0 ? pixels : null;
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, dc);
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            if (hbm != IntPtr.Zero)
                DeleteObject(hbm);
        }
    }
}
