using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Windows.Devices.Radios;

namespace Ostrovok;

/// <summary>CPU and GPU load and, where Windows exposes it, temperature. Sampled on a background thread.</summary>
sealed class SysMonitor
{
    public volatile float Load;
    public double? Temp;
    public double? Gpu;
    long prevIdle, prevTotal;
    readonly GpuLoad gpu = new();

    public void Start()
    {
        Task.Run(async () =>
        {
            var thermal = TryThermal();
            while (true)
            {
                try
                {
                    SampleLoad();
                    if (thermal != null) Temp = ReadThermal(thermal);
                    Gpu = gpu.Sample();
                }
                catch (Exception ex) { Log.Write("sys: " + ex.Message); }
                await Task.Delay(1500);
            }
        });
    }

    void SampleLoad()
    {
        if (!Native.GetSystemTimes(out long idle, out long kernel, out long user)) return;
        long total = kernel + user;
        long dIdle = idle - prevIdle, dTotal = total - prevTotal;
        prevIdle = idle; prevTotal = total;
        if (dTotal > 0) Load = Math.Clamp(100f * (dTotal - dIdle) / dTotal, 0, 100);
    }

    // Windows publishes ACPI thermal zones as performance counters (no admin rights needed).
    // On many PCs this is the CPU/board sensor; on some it is missing or static.
    static List<(PerformanceCounter counter, bool precise)> TryThermal()
    {
        try
        {
            var cat = new PerformanceCounterCategory("Thermal Zone Information");
            var list = new List<(PerformanceCounter, bool)>();
            foreach (var inst in cat.GetInstanceNames())
            {
                bool precise = cat.CounterExists("High Precision Temperature");
                list.Add((new PerformanceCounter(cat.CategoryName, precise ? "High Precision Temperature" : "Temperature", inst, true), precise));
            }
            Log.Write($"thermal zones: {list.Count}");
            return list.Count > 0 ? list : null;
        }
        catch (Exception ex) { Log.Write("thermal: " + ex.Message); return null; }
    }

    static double? ReadThermal(List<(PerformanceCounter counter, bool precise)> zones)
    {
        double? best = null;
        foreach (var (c, precise) in zones)
        {
            try
            {
                double kelvin = precise ? c.NextValue() / 10.0 : c.NextValue();
                double celsius = kelvin - 273.15;
                if (celsius > 5 && celsius < 125) best = best is double b ? Math.Max(b, celsius) : celsius;
            }
            catch { }
        }
        return best;
    }
}

/// <summary>
/// GPU load the way Task Manager computes it: the "GPU Engine" counters summed per engine type
/// (3D, video decode, copy…), and the busiest engine type wins. Works for NVIDIA, AMD and Intel.
/// </summary>
sealed class GpuLoad
{
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhOpenQuery(string source, IntPtr user, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhAddEnglishCounter(IntPtr query, string path, IntPtr user, out IntPtr counter);
    [DllImport("pdh.dll")] static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint size, out uint count, IntPtr buffer);
    const uint PDH_FMT_DOUBLE = 0x200, PDH_FMT_NOCAP100 = 0x8000;

    IntPtr query, counter;
    bool failed, primed;

    public double? Sample()
    {
        if (failed) return null;
        try
        {
            if (query == IntPtr.Zero)
            {
                if (PdhOpenQuery(null, IntPtr.Zero, out query) != 0 ||
                    PdhAddEnglishCounter(query, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out counter) != 0)
                { failed = true; Log.Write("gpu counters unavailable"); return null; }
            }
            PdhCollectQueryData(query);
            if (!primed) { primed = true; return null; }

            uint size = 0;
            PdhGetFormattedCounterArray(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out _, IntPtr.Zero);
            if (size == 0) return 0;
            var buf = Marshal.AllocHGlobal((int)size);
            try
            {
                if (PdhGetFormattedCounterArray(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out uint n, buf) != 0) return null;
                var byType = new Dictionary<string, double>();
                for (int i = 0; i < n; i++)
                {
                    var item = buf + i * 24;                       // { name*, { status, pad, double } }
                    if (Marshal.ReadInt32(item + 8) != 0) continue;
                    string name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item)) ?? "";
                    int at = name.IndexOf("_engtype_", StringComparison.Ordinal);
                    string type = at >= 0 ? name[(at + 9)..] : "other";
                    double v = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item + 16));
                    byType[type] = byType.GetValueOrDefault(type) + v;
                }
                return byType.Count == 0 ? 0 : Math.Clamp(byType.Values.Max(), 0, 100);
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch (Exception ex) { Log.Write("gpu: " + ex.Message); return null; }
    }
}

/// <summary>Wi-Fi and Bluetooth on/off, through the same API as the Windows quick settings.</summary>
sealed class RadioController
{
    Radio wifi, bt;
    public event Action Changed;

    public async Task InitAsync()
    {
        try
        {
            var access = await Radio.RequestAccessAsync();
            Log.Write("radio access: " + access);
            foreach (var r in await Radio.GetRadiosAsync())
            {
                if (r.Kind == RadioKind.WiFi && wifi == null) wifi = r;
                else if (r.Kind == RadioKind.Bluetooth && bt == null) bt = r;
            }
            if (wifi != null) wifi.StateChanged += (_, _) => Changed?.Invoke();
            if (bt != null) bt.StateChanged += (_, _) => Changed?.Invoke();
        }
        catch (Exception ex) { Log.Write("radios: " + ex.Message); }
        Changed?.Invoke();
    }

    public bool? WifiOn => wifi == null ? null : wifi.State == RadioState.On;
    public bool? BtOn => bt == null ? null : bt.State == RadioState.On;

    public async Task ToggleAsync(string kind)
    {
        var r = kind == "wifi" ? wifi : bt;
        if (r == null) return;
        try
        {
            var res = await r.SetStateAsync(r.State == RadioState.On ? RadioState.Off : RadioState.On);
            if (res != RadioAccessStatus.Allowed)
            {
                Log.Write($"radio {kind}: {res}");
                Shell.Open(kind == "wifi" ? "ms-settings:network-wifi" : "ms-settings:bluetooth");
            }
        }
        catch (Exception ex) { Log.Write("radio toggle: " + ex.Message); }
    }
}

static class Shell
{
    public static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Write("open: " + ex.Message); }
    }
}

static class Autostart
{
    const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "Ostrovok";

    public static bool Enabled
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(Key);
            return k?.GetValue(Name) is string;
        }
        set
        {
            using var k = Registry.CurrentUser.CreateSubKey(Key);
            if (value) k.SetValue(Name, "\"" + Application.ExecutablePath + "\"");
            else k.DeleteValue(Name, false);
        }
    }
}
