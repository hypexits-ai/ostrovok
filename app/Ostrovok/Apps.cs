using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Ostrovok;

class WinInfo
{
    public IntPtr Handle;
    public string Name, Title, Icon, Exe, Aumid;
    public uint Pid;
    public long Seen;
}

/// <summary>Finds the same windows the taskbar shows, and switches between them.</summary>
class WindowTracker
{
    public IntPtr Self;
    readonly Dictionary<IntPtr, WinInfo> known = new();
    long seq;

    public List<WinInfo> Scan()
    {
        var alive = new List<IntPtr>();
        Native.EnumWindows((h, _) => { if (IsAppWindow(h)) alive.Add(h); return true; }, IntPtr.Zero);
        var set = new HashSet<IntPtr>(alive);
        foreach (var gone in known.Keys.Where(k => !set.Contains(k)).ToList()) known.Remove(gone);
        foreach (var h in alive)
        {
            if (!known.TryGetValue(h, out var w))
            {
                w = Describe(h);
                w.Seen = seq++;
                known[h] = w;
            }
            w.Title = Native.Title(h);
        }
        return known.Values.OrderBy(w => w.Seen).ToList();
    }

    bool IsAppWindow(IntPtr h)
    {
        if (h == Self || !Native.IsWindowVisible(h)) return false;
        long ex = (long)Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE);
        bool appWindow = (ex & Native.WS_EX_APPWINDOW) != 0;
        if (!appWindow && (ex & Native.WS_EX_TOOLWINDOW) != 0) return false;
        if (!appWindow && (ex & Native.WS_EX_NOACTIVATE) != 0) return false;
        if (!appWindow && Native.GetWindow(h, Native.GW_OWNER) != IntPtr.Zero) return false;
        if (Native.DwmGetWindowAttribute(h, Native.DWMWA_CLOAKED, out int cloaked, 4) == 0 && cloaked != 0) return false;
        if (Native.GetWindowTextLength(h) == 0) return false;
        var cls = Native.ClassName(h);
        return cls is not ("Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd");
    }

    static WinInfo Describe(IntPtr h)
    {
        string aumid = Aumid.ForWindow(h);
        string exe = Native.ProcessPath(h);
        string title = Native.Title(h);
        string icon = null, name = null;

        if (aumid != null)
        {
            var item = "shell:AppsFolder\\" + aumid;
            icon = ShellIcons.Icon(item);
            if (icon != null) name = ShellIcons.DisplayName(item);
        }
        if (icon == null && exe != null) icon = ShellIcons.Icon(exe);
        if (string.IsNullOrWhiteSpace(name) && exe != null)
        {
            if (exe.EndsWith("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase)) name = title;
            else
            {
                try { name = FileVersionInfo.GetVersionInfo(exe).FileDescription; } catch { }
                if (string.IsNullOrWhiteSpace(name)) name = Path.GetFileNameWithoutExtension(exe);
            }
        }
        Native.GetWindowThreadProcessId(h, out uint pid);
        return new WinInfo
        {
            Handle = h, Name = string.IsNullOrWhiteSpace(name) ? title : name.Trim(), Title = title, Icon = icon,
            Exe = exe, Aumid = aumid, Pid = pid
        };
    }

    /// <summary>Asks the window to close, exactly like its × button (the app may still ask to save).</summary>
    public static void Close(IntPtr h)
    {
        if (Native.IsWindow(h)) Native.PostMessage(h, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>Taskbar-style click: bring the window forward, or minimise it if it is already in front.</summary>
    public static void Activate(IntPtr h)
    {
        if (!Native.IsWindow(h)) return;
        var fg = Native.GetForegroundWindow();
        if (fg == h && !Native.IsIconic(h)) { Native.ShowWindow(h, Native.SW_MINIMIZE); return; }
        if (Native.IsIconic(h)) Native.ShowWindow(h, Native.SW_RESTORE);

        uint fgThread = Native.GetWindowThreadProcessId(fg, out _);
        uint me = Native.GetCurrentThreadId();
        bool attached = fgThread != 0 && fgThread != me && Native.AttachThreadInput(me, fgThread, true);
        Native.BringWindowToTop(h);
        Native.SetForegroundWindow(h);
        if (attached) Native.AttachThreadInput(me, fgThread, false);
        if (Native.GetForegroundWindow() != h) Native.SwitchToThisWindow(h, true);
    }
}

/// <summary>Reads a window's AppUserModelID, which identifies Store apps and many desktop apps.</summary>
static class Aumid
{
    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint i, out PROPERTYKEY key);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
    }
    [StructLayout(LayoutKind.Sequential)] struct PROPERTYKEY { public Guid fmtid; public uint pid; }
    [StructLayout(LayoutKind.Explicit, Size = 24)] struct PROPVARIANT { [FieldOffset(0)] public ushort vt; [FieldOffset(8)] public IntPtr p; }

    [DllImport("shell32.dll")] static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid iid, out IntPtr store);
    [DllImport("ole32.dll")] static extern int PropVariantClear(ref PROPVARIANT pv);

    public static string ForWindow(IntPtr h)
    {
        try
        {
            var iid = typeof(IPropertyStore).GUID;
            if (SHGetPropertyStoreForWindow(h, ref iid, out var p) != 0 || p == IntPtr.Zero) return null;
            var store = (IPropertyStore)Marshal.GetObjectForIUnknown(p);
            Marshal.Release(p);
            try
            {
                var key = new PROPERTYKEY { fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), pid = 5 };
                if (store.GetValue(ref key, out var v) != 0) return null;
                string s = v.vt == 31 /* VT_LPWSTR */ ? Marshal.PtrToStringUni(v.p) : null;
                PropVariantClear(ref v);
                return string.IsNullOrWhiteSpace(s) ? null : s;
            }
            finally { Marshal.ReleaseComObject(store); }
        }
        catch { return null; }
    }
}

class AppEntry
{
    public string Name, Target, Icon;
}

/// <summary>Favourites for the wheel: the apps pinned to the taskbar, or a custom list.</summary>
static class Favorites
{
    public static string CustomFile => Path.Combine(Log.Dir, "favorites.json");

    public static List<AppEntry> Load()
    {
        var targets = new List<string>();
        try
        {
            if (File.Exists(CustomFile))
                targets.AddRange(JsonSerializer.Deserialize<List<string>>(File.ReadAllText(CustomFile)) ?? new());
        }
        catch (Exception ex) { Log.Write("favorites.json: " + ex.Message); }

        if (targets.Count == 0)
        {
            var pinned = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");
            if (Directory.Exists(pinned))
                targets.AddRange(Directory.GetFiles(pinned, "*.lnk").OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase));

            if (targets.Count < 6)
            {
                targets.Add("shell:AppsFolder\\windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel");
                targets.Add("shell:AppsFolder\\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App");
                targets.Add("shell:AppsFolder\\Microsoft.WindowsNotepad_8wekyb3d8bbwe!App");
            }
        }

        var list = new List<AppEntry>();
        foreach (var t in targets.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var icon = ShellIcons.Icon(t);
            if (icon == null) continue;
            var name = ShellIcons.DisplayName(t);
            if (string.IsNullOrWhiteSpace(name)) name = Path.GetFileNameWithoutExtension(t);
            list.Add(new AppEntry { Name = name, Target = t, Icon = icon });
        }
        return list;
    }

    /// <summary>Saves the wheel's order (paths or shell:AppsFolder ids); from then on the custom list is used.</summary>
    public static void Save(IEnumerable<string> targets)
    {
        try
        {
            Directory.CreateDirectory(Log.Dir);
            File.WriteAllText(CustomFile, JsonSerializer.Serialize(targets.ToList(), new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { Log.Write(ex); }
    }

    public static void Launch(AppEntry app)
    {
        try
        {
            if (app.Target.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + app.Target + "\"") { UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo(app.Target) { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Write("launch: " + ex.Message); }
    }
}
