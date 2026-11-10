using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Ostrovok;

/// <summary>Crisp app icons and localized names straight from the Windows shell.</summary>
static class ShellIcons
{
    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm);
    }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetParent(out IntPtr ppsi);
        [PreserveSig] int GetDisplayName(uint sigdn, out IntPtr name);
        [PreserveSig] int GetAttributes(uint mask, out uint attribs);
        [PreserveSig] int Compare(IntPtr psi, uint hint, out int order);
    }

    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential)] struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public ushort bmPlanes, bmBitsPixel; public IntPtr bmBits; }
    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight; public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid, out IntPtr ppv);
    [DllImport("gdi32.dll")] static extern int GetObject(IntPtr h, int size, out BITMAP bm);
    [DllImport("gdi32.dll")] static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint lines, IntPtr bits, ref BITMAPINFOHEADER bi, uint usage);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("ole32.dll")] static extern void CoTaskMemFree(IntPtr p);

    static readonly Dictionary<string, string> IconCache = new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, string> NameCache = new(StringComparer.OrdinalIgnoreCase);

    static T Create<T>(string parsingName) where T : class
    {
        var iid = typeof(T).GUID;
        if (SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out var p) != 0 || p == IntPtr.Zero) return null;
        try { return (T)Marshal.GetObjectForIUnknown(p); }
        finally { Marshal.Release(p); }
    }

    /// <summary>PNG data URL of the item's icon, or null.</summary>
    public static string Icon(string parsingName, int size = 64)
    {
        if (string.IsNullOrEmpty(parsingName)) return null;
        if (IconCache.TryGetValue(parsingName, out var cached)) return cached;
        string result = null;
        try
        {
            var factory = Create<IShellItemImageFactory>(parsingName);
            if (factory != null)
            {
                // SIIGBF_ICONONLY
                if (factory.GetImage(new SIZE { cx = size, cy = size }, 0x4, out var hbm) == 0 && hbm != IntPtr.Zero)
                {
                    try
                    {
                        using var bmp = FromHBitmap(hbm);
                        using var ms = new MemoryStream();
                        bmp.Save(ms, ImageFormat.Png);
                        result = "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
                    }
                    finally { DeleteObject(hbm); }
                }
                Marshal.ReleaseComObject(factory);
            }
        }
        catch (Exception ex) { Log.Write($"icon {parsingName}: {ex.Message}"); }
        IconCache[parsingName] = result;
        return result;
    }

    /// <summary>Name as Explorer shows it (localized), or null.</summary>
    public static string DisplayName(string parsingName)
    {
        if (string.IsNullOrEmpty(parsingName)) return null;
        if (NameCache.TryGetValue(parsingName, out var cached)) return cached;
        string result = null;
        try
        {
            var item = Create<IShellItem>(parsingName);
            if (item != null)
            {
                if (item.GetDisplayName(0 /* SIGDN_NORMALDISPLAY */, out var p) == 0 && p != IntPtr.Zero)
                {
                    result = Marshal.PtrToStringUni(p);
                    CoTaskMemFree(p);
                }
                Marshal.ReleaseComObject(item);
            }
        }
        catch { }
        NameCache[parsingName] = result;
        return result;
    }

    static unsafe Bitmap FromHBitmap(IntPtr hbm)
    {
        GetObject(hbm, Marshal.SizeOf<BITMAP>(), out var bm);
        int w = bm.bmWidth, h = Math.Abs(bm.bmHeight);
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
        var bi = new BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        var dc = GetDC(IntPtr.Zero);
        GetDIBits(dc, hbm, 0, (uint)h, data.Scan0, ref bi, 0);
        ReleaseDC(IntPtr.Zero, dc);
        byte* px = (byte*)data.Scan0;
        int total = data.Stride * h;
        bool hasAlpha = false;
        for (int i = 3; i < total; i += 4) if (px[i] != 0) { hasAlpha = true; break; }
        if (!hasAlpha) for (int i = 3; i < total; i += 4) px[i] = 255;
        bmp.UnlockBits(data);
        return bmp;
    }
}
