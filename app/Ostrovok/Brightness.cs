using System.Management;
using System.Runtime.InteropServices;

namespace Ostrovok;

/// <summary>
/// Brightness of the screen the island lives on (the primary one): an external monitor over DDC/CI (dxva2),
/// otherwise a laptop's built-in screen through WMI. Monitor calls take tens of milliseconds, so they run on
/// a worker thread, and a burst of slider moves collapses into the latest value.
/// </summary>
sealed class BrightnessHub
{
    /// <summary>Raised on the worker thread: whether this screen can be dimmed, and its brightness 0–100.</summary>
    public event Action<bool, int> Changed;
    public bool Supported { get; private set; }
    public int Value { get; private set; } = -1;

    readonly SemaphoreSlim signal = new(0);
    int pending = -1;
    volatile bool probeNeeded = true;
    DateTime lastSet = DateTime.MinValue;
    IntPtr phys; uint ddcMin, ddcMax;
    bool useDdc, useWmi, announced;

    public BrightnessHub() { _ = Task.Run(Worker); signal.Release(); }   // find the monitor right away

    public void Set(int percent)
    {
        Interlocked.Exchange(ref pending, Math.Clamp(percent, 0, 100));
        lastSet = DateTime.Now;
        signal.Release();
    }

    /// <summary>Read it again (the monitor's own buttons or Windows may have changed it).</summary>
    public void Refresh() => signal.Release();

    /// <summary>The displays changed: find the monitor again.</summary>
    public void Reset() { probeNeeded = true; signal.Release(); }

    async Task Worker()
    {
        while (true)
        {
            await signal.WaitAsync();
            try
            {
                if (probeNeeded) Probe();
                if (!Supported) continue;
                int v = Interlocked.Exchange(ref pending, -1);
                if (v >= 0) { Write(v); Value = v; continue; }            // the page already shows this value
                if ((DateTime.Now - lastSet).TotalSeconds < 3) continue;  // right after a move: do not fight the slider
                int cur = Read();
                if (cur < 0) continue;
                if (cur != Value || !announced) { Value = cur; announced = true; Changed?.Invoke(true, cur); }
            }
            catch (Exception ex) { Log.Write("brightness: " + ex.Message); }
        }
    }

    void Probe()
    {
        probeNeeded = false;
        if (phys != IntPtr.Zero) { DestroyPhysicalMonitor(phys); phys = IntPtr.Zero; }
        useDdc = useWmi = false;
        var mon = MonitorFromPoint(new POINT(), 1 /* MONITOR_DEFAULTTOPRIMARY */);
        if (GetNumberOfPhysicalMonitorsFromHMONITOR(mon, out uint n) && n > 0)
        {
            var arr = new PHYSICAL_MONITOR[n];
            if (GetPhysicalMonitorsFromHMONITOR(mon, n, arr))
                foreach (var p in arr)
                {
                    if (!useDdc && GetMonitorBrightness(p.Handle, out var mn, out _, out var mx) && mx > mn)
                    { phys = p.Handle; ddcMin = mn; ddcMax = mx; useDdc = true; }
                    else DestroyPhysicalMonitor(p.Handle);
                }
        }
        if (!useDdc) useWmi = WmiRead() >= 0;   // built-in laptop screens do not speak DDC/CI
        bool was = Supported;
        Supported = useDdc || useWmi;
        Log.Write("brightness: " + (useDdc ? "DDC/CI" : useWmi ? "WMI" : "not available"));
        announced = false;
        if (!Supported && (was || Value == -1)) { Value = 0; Changed?.Invoke(false, 0); }
    }

    int Read()
    {
        if (useDdc)
            return GetMonitorBrightness(phys, out var mn, out var cur, out var mx) && mx > mn
                ? (int)Math.Round((cur - mn) * 100.0 / (mx - mn)) : -1;
        return WmiRead();
    }

    void Write(int v)
    {
        if (useDdc) { SetMonitorBrightness(phys, (uint)Math.Round(ddcMin + (ddcMax - ddcMin) * v / 100.0)); return; }
        try
        {
            using var s = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM WmiMonitorBrightnessMethods");
            foreach (ManagementObject o in s.Get())
                using (o) o.InvokeMethod("WmiSetBrightness", new object[] { (uint)1, (byte)v });
        }
        catch (Exception ex) { Log.Write("brightness wmi: " + ex.Message); }
    }

    static int WmiRead()
    {
        try
        {
            using var s = new ManagementObjectSearcher(@"root\wmi", "SELECT CurrentBrightness FROM WmiMonitorBrightness WHERE Active=TRUE");
            foreach (ManagementObject o in s.Get())
                using (o) return Convert.ToInt32(o["CurrentBrightness"]);
        }
        catch { }
        return -1;
    }

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct PHYSICAL_MONITOR { public IntPtr Handle; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description; }
    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [DllImport("dxva2.dll")] static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr mon, out uint count);
    [DllImport("dxva2.dll")] static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr mon, uint count, [Out] PHYSICAL_MONITOR[] arr);
    [DllImport("dxva2.dll")] static extern bool GetMonitorBrightness(IntPtr h, out uint min, out uint cur, out uint max);
    [DllImport("dxva2.dll")] static extern bool SetMonitorBrightness(IntPtr h, uint value);
    [DllImport("dxva2.dll")] static extern bool DestroyPhysicalMonitor(IntPtr h);
}
