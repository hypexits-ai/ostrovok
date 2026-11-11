using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Ostrovok;

/// <summary>Programs that load the computer the most, for the processor cell.</summary>
sealed class ProcMonitor
{
    [StructLayout(LayoutKind.Sequential)]
    struct MEMORYSTATUSEX { public uint dwLength, dwMemoryLoad; public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual; }
    [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);

    readonly Dictionary<int, TimeSpan> prev = new();
    DateTime prevAt = DateTime.MinValue;
    readonly Dictionary<string, (string name, string exe)> names = new();

    public sealed record Row(string key, string exe, string name, double cpu, double memMb);

    /// <summary>Runs off the UI thread. The first call only primes the counters.</summary>
    public (List<Row> rows, double usedGb, double totalGb) Sample()
    {
        var now = DateTime.UtcNow;
        double elapsedMs = (now - prevAt).TotalMilliseconds;
        var byName = new Dictionary<string, (double cpuMs, double mem, int pid)>();
        var seen = new HashSet<int>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.Id == 0 || p.ProcessName == "Idle") continue;
                seen.Add(p.Id);
                double cpuMs = 0;
                try
                {
                    var t = p.TotalProcessorTime;
                    if (prev.TryGetValue(p.Id, out var old)) cpuMs = (t - old).TotalMilliseconds;
                    prev[p.Id] = t;
                }
                catch { }
                var key = p.ProcessName;
                byName.TryGetValue(key, out var acc);
                byName[key] = (acc.cpuMs + cpuMs, acc.mem + p.WorkingSet64, acc.pid == 0 ? p.Id : acc.pid);
            }
            catch { }
            finally { p.Dispose(); }
        }
        foreach (var gone in prev.Keys.Where(k => !seen.Contains(k)).ToList()) prev.Remove(gone);
        bool primed = prevAt != DateTime.MinValue;
        prevAt = now;

        var rows = new List<Row>();
        if (primed && elapsedMs > 0)
        {
            foreach (var (key, v) in byName.OrderByDescending(x => x.Value.cpuMs).Take(5))
            {
                if (!names.TryGetValue(key, out var info))
                {
                    string exe = AudioHub.ProcessPath((uint)v.pid), name = null;
                    if (exe != null) try { name = FileVersionInfo.GetVersionInfo(exe).FileDescription; } catch { }
                    info = names[key] = (string.IsNullOrWhiteSpace(name) ? key : name.Trim(), exe);
                }
                rows.Add(new Row(key, info.exe, info.name, Math.Round(100 * v.cpuMs / elapsedMs / Environment.ProcessorCount, 1), Math.Round(v.mem / 1048576)));
            }
        }
        var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        GlobalMemoryStatusEx(ref m);
        return (rows, (m.ullTotalPhys - m.ullAvailPhys) / 1073741824.0, m.ullTotalPhys / 1073741824.0);
    }
}

/// <summary>Weather from Open-Meteo (free, no key). The city is the only thing sent.</summary>
sealed class WeatherHub
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };
    object cached; DateTime cachedAt; string cachedCity;

    public async Task<object> GetAsync(string city)
    {
        if (string.IsNullOrWhiteSpace(city)) return new { t = "weather", need = true };
        if (cached != null && cachedCity == city && (DateTime.Now - cachedAt).TotalMinutes < 15) return cached;
        try
        {
            var geo = JsonDocument.Parse(await Http.GetStringAsync(
                $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(city)}&count=1&language=ru&format=json"));
            if (!geo.RootElement.TryGetProperty("results", out var res) || res.GetArrayLength() == 0)
                return new { t = "weather", error = "Город не найден", city };
            var place = res[0];
            double lat = place.GetProperty("latitude").GetDouble(), lon = place.GetProperty("longitude").GetDouble();
            string name = place.GetProperty("name").GetString();
            var inv = CultureInfo.InvariantCulture;
            var fc = JsonDocument.Parse(await Http.GetStringAsync(
                $"https://api.open-meteo.com/v1/forecast?latitude={lat.ToString(inv)}&longitude={lon.ToString(inv)}" +
                "&current=temperature_2m,apparent_temperature,weather_code,is_day&hourly=temperature_2m,weather_code&forecast_hours=7&timezone=auto"));
            var cur = fc.RootElement.GetProperty("current");
            var hourly = fc.RootElement.GetProperty("hourly");
            var times = hourly.GetProperty("time").EnumerateArray().Select(x => x.GetString()).ToList();
            var temps = hourly.GetProperty("temperature_2m").EnumerateArray().Select(x => x.GetDouble()).ToList();
            var codes = hourly.GetProperty("weather_code").EnumerateArray().Select(x => x.GetInt32()).ToList();
            var hours = new List<object>();
            for (int i = 1; i < Math.Min(7, times.Count); i++)
                hours.Add(new { h = times[i][11..13], code = codes[i], temp = (int)Math.Round(temps[i]) });
            cached = new
            {
                t = "weather", city = name,
                temp = (int)Math.Round(cur.GetProperty("temperature_2m").GetDouble()),
                feels = (int)Math.Round(cur.GetProperty("apparent_temperature").GetDouble()),
                code = cur.GetProperty("weather_code").GetInt32(),
                day = cur.GetProperty("is_day").GetInt32() == 1,
                hours
            };
            cachedAt = DateTime.Now; cachedCity = city;
            return cached;
        }
        catch (Exception ex)
        {
            Log.Write("weather: " + ex.Message);
            return new { t = "weather", error = "Нет связи с сервисом погоды", city };
        }
    }
}

/// <summary>Small persistent settings (%LOCALAPPDATA%\Ostrovok\settings.json).</summary>
sealed class AppSettings
{
    public string City { get; set; }
    /// <summary>After this many minutes without the pointer on it, the island shrinks to its smallest form.</summary>
    public double IdleMinutes { get; set; } = 5;
    /// <summary>"capsule" (floats under the screen edge) or "notch" (hangs from it).</summary>
    public string Mode { get; set; } = "capsule";
    /// <summary>Stay visible over borderless full-screen games and videos (exclusive full screen still hides it).</summary>
    public bool OverFullscreen { get; set; } = true;

    static string FilePath => Path.Combine(Log.Dir, "settings.json");

    public static AppSettings Load()
    {
        try { if (File.Exists(FilePath)) return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new(); }
        catch { }
        return new AppSettings();
    }

    public void Save()
    {
        try { Directory.CreateDirectory(Log.Dir); File.WriteAllText(FilePath, JsonSerializer.Serialize(this)); }
        catch (Exception ex) { Log.Write(ex); }
    }
}
