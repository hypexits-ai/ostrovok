using System.Runtime.InteropServices;

namespace Ostrovok;

sealed class FlyoutMask : IDisposable
{
    [DllImport("user32.dll")] static extern int GetWindowRgn(IntPtr window, IntPtr region);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr region);
    IntPtr host, previous;

    static bool IsHost(IntPtr h) =>
        Native.ClassName(h) == "XamlExplorerHostIslandWindow" && Native.Title(h).Length == 0 &&
        string.Equals(Path.GetFileName(Native.ProcessPath(h)), "explorer.exe", StringComparison.OrdinalIgnoreCase);

    public void Update(bool enabled)
    {
        if (host != IntPtr.Zero)
        {
            if (enabled && IsHost(host)) return;
            Dispose();
        }
        if (!enabled) return;
        IntPtr h = IntPtr.Zero;
        while ((h = Native.FindWindowEx(IntPtr.Zero, h, "XamlExplorerHostIslandWindow", null)) != IntPtr.Zero)
        {
            if (!IsHost(h) || !Native.GetWindowRect(h, out var r)) continue;
            var screen = Screen.FromHandle(h).Bounds;
            int height = r.Bottom - r.Top, width = r.Right - r.Left;
            if (height <= 0 || width < 2 * height || width > screen.Width / 2 ||
                r.Top < screen.Top + screen.Height * .65 ||
                Math.Abs(r.Left + width / 2 - screen.Left - screen.Width / 2) > screen.Width / 10) continue;
            var saved = Native.CreateRectRgn(0, 0, 0, 0);
            if (saved == IntPtr.Zero) return;
            if (GetWindowRgn(h, saved) == 0) { DeleteObject(saved); saved = IntPtr.Zero; }
            var empty = Native.CreateRectRgn(0, 0, 0, 0);
            if (empty != IntPtr.Zero && Native.SetWindowRgn(h, empty, true) != 0)
            { host = h; previous = saved; return; }
            if (empty != IntPtr.Zero) DeleteObject(empty);
            if (saved != IntPtr.Zero) DeleteObject(saved);
        }
    }

    public void Dispose()
    {
        if (host != IntPtr.Zero && IsHost(host) && Native.SetWindowRgn(host, previous, true) != 0)
            previous = IntPtr.Zero;
        if (previous != IntPtr.Zero) DeleteObject(previous);
        host = previous = IntPtr.Zero;
    }
}
