using Microsoft.Win32;

namespace Ostrovok;

/// <summary>
/// Tells whether a game is in front. Windows (Xbox Game Bar) keeps the games it has recognised on this PC in
/// HKCU\System\GameConfigStore\Children (value MatchedExeFullPath); the foreground window's program is
/// looked up there.
/// </summary>
static class Games
{
    const string Store = @"System\GameConfigStore\Children";
    static HashSet<string> known = new(StringComparer.OrdinalIgnoreCase);
    static DateTime loadedAt = DateTime.MinValue;

    /// <summary>The foreground window belongs to a program Windows knows as a game.</summary>
    public static bool InFront()
    {
        var fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;
        Native.GetWindowThreadProcessId(fg, out uint pid);
        var path = AudioHub.ProcessPath(pid);
        if (path == null) return false;
        Refresh();
        return known.Contains(path);
    }

    // the list changes rarely (a new game gets recognised): read it again once a minute
    static void Refresh()
    {
        if ((DateTime.Now - loadedAt).TotalSeconds < 60) return;
        loadedAt = DateTime.Now;
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(Store);
            if (root != null)
                foreach (var name in root.GetSubKeyNames())
                {
                    using var k = root.OpenSubKey(name);
                    if (k?.GetValue("MatchedExeFullPath") is string exe && exe.Length > 0) set.Add(exe);
                }
        }
        catch (Exception ex) { Log.Write("games: " + ex.Message); }
        known = set;
    }
}
