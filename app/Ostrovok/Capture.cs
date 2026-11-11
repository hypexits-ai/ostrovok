using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Ostrovok;

/// <summary>
/// Notices screen recording and screenshots.
/// Windows notes every use of its screen-capture API under CapabilityAccessManager\ConsentStore\graphicsCapture*:
/// an entry with a start time but no stop time is a capture running now (Snipping Tool video, screen sharing in
/// Discord or Teams, OBS with Windows capture); a new start that already has its stop is a still screenshot.
/// The Snipping Tool overlay (Win+Shift+S) is found by its window class while the user picks an area.
/// </summary>
sealed class CaptureWatch
{
    const string Root = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\";
    static readonly string[] Caps = { "graphicsCaptureWithoutBorder", "graphicsCaptureProgrammatic" };
    // packaged apps are keyed by family name; these are the processes behind the ones we know
    static readonly Dictionary<string, string> PackagedExe = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Microsoft.ScreenSketch"] = "SnippingTool",
    };
    readonly Dictionary<string, long> lastStart = new();
    bool primed;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out int pid);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int value, int size);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }

    HashSet<int> snipPids = new();

    /// <summary>Remembers which processes are the Snipping Tool (refreshed once a second).</summary>
    public void RefreshSnipPids()
    {
        var set = new HashSet<int>();
        foreach (var p in Process.GetProcessesByName("SnippingTool")) { set.Add(p.Id); p.Dispose(); }
        snipPids = set;
    }

    /// <summary>
    /// Bottom edge (screen pixels) of a Snipping Tool bar at the top centre of the screen, such as the
    /// recording controls, or 0. The island steps down below it so its buttons stay reachable.
    /// </summary>
    public int TopBarBottom(int screenWidth)
    {
        if (snipPids.Count == 0) return 0;
        int bottom = 0; IntPtr h = IntPtr.Zero;
        while ((h = FindWindowEx(IntPtr.Zero, h, "XamlWindow", null)) != IntPtr.Zero)
        {
            GetWindowThreadProcessId(h, out int pid);
            if (!snipPids.Contains(pid) || !IsWindowVisible(h)) continue;
            if (DwmGetWindowAttribute(h, 14 /* DWMWA_CLOAKED */, out int cloaked, 4) == 0 && cloaked != 0) continue;
            if (!GetWindowRect(h, out var r)) continue;
            int hgt = r.B - r.T, wid = r.R - r.L;
            if (r.T <= 6 && hgt > 0 && hgt < 200 && wid < screenWidth / 2) bottom = Math.Max(bottom, r.B);
        }
        return bottom;
    }

    /// <summary>The Snipping Tool's "pick an area" overlay is on screen.</summary>
    public static bool SnipOverlay()
    {
        var h = FindWindow("SnipOverlayRootWindow", null);
        return h != IntPtr.Zero && IsWindowVisible(h);
    }

    /// <returns>recording: a capture runs right now; shot: a still capture happened since the previous call.</returns>
    public (bool recording, bool shot) Poll()
    {
        bool recording = false, shot = false;
        long now = DateTime.UtcNow.ToFileTimeUtc();
        foreach (var cap in Caps)
        {
            using var key = Registry.CurrentUser.OpenSubKey(Root + cap);
            if (key == null) continue;
            foreach (var (app, k) in Entries(key))
            {
                using (k)
                {
                    if (k.GetValue("LastUsedTimeStart") is not long start || start == 0) continue;
                    long stop = k.GetValue("LastUsedTimeStop") is long s ? s : 0;
                    string id = cap + "|" + app;
                    bool fresh = lastStart.TryGetValue(id, out var prev) && prev != start;
                    lastStart[id] = start;
                    if (stop == 0 || stop < start) { if (Running(app, start, now)) recording = true; }
                    else if (fresh && primed) shot = true;
                }
            }
        }
        primed = true;
        return (recording, shot);
    }

    static IEnumerable<(string app, RegistryKey key)> Entries(RegistryKey cap)
    {
        foreach (var name in cap.GetSubKeyNames())
        {
            if (name == "NonPackaged")
            {
                using var np = cap.OpenSubKey(name);
                if (np == null) continue;
                foreach (var n in np.GetSubKeyNames())
                {
                    var k = np.OpenSubKey(n);
                    if (k != null) yield return (n, k);
                }
            }
            else
            {
                var k = cap.OpenSubKey(name);
                if (k != null) yield return (name, k);
            }
        }
    }

    /// <summary>
    /// A capture "in use" whose app has since quit (crashed) never gets its stop time,
    /// so it only counts while that app is still running.
    /// </summary>
    static bool Running(string app, long start, long now)
    {
        string exe = null;
        if (app.Contains('#')) exe = Path.GetFileNameWithoutExtension(app.Replace('#', '\\'));   // C:#path#to#app.exe
        else
        {
            var family = app.Split('_')[0];
            if (!PackagedExe.TryGetValue(family, out exe))
                return now - start < TimeSpan.FromHours(6).Ticks;   // unknown packaged app: trust recent ones
        }
        try
        {
            var ps = Process.GetProcessesByName(exe);
            bool any = ps.Length > 0;
            foreach (var p in ps) p.Dispose();
            return any;
        }
        catch { return true; }
    }
}
