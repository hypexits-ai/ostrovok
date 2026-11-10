using System.Runtime.InteropServices;
using System.Security;
using System.Text;

namespace Ostrovok;

/// <summary>Wi-Fi networks through the native WLAN API: list, connect (saved, open or with a password), disconnect.</summary>
sealed class WifiHub : IDisposable
{
    [DllImport("wlanapi.dll")] static extern uint WlanOpenHandle(uint ver, IntPtr reserved, out uint negotiated, out IntPtr handle);
    [DllImport("wlanapi.dll")] static extern uint WlanCloseHandle(IntPtr h, IntPtr reserved);
    [DllImport("wlanapi.dll")] static extern uint WlanEnumInterfaces(IntPtr h, IntPtr reserved, out IntPtr list);
    [DllImport("wlanapi.dll")] static extern uint WlanScan(IntPtr h, ref Guid iface, IntPtr ssid, IntPtr ie, IntPtr reserved);
    [DllImport("wlanapi.dll")] static extern uint WlanGetAvailableNetworkList(IntPtr h, ref Guid iface, uint flags, IntPtr reserved, out IntPtr list);
    [DllImport("wlanapi.dll")] static extern uint WlanConnect(IntPtr h, ref Guid iface, ref ConnectionParams p, IntPtr reserved);
    [DllImport("wlanapi.dll")] static extern uint WlanDisconnect(IntPtr h, ref Guid iface, IntPtr reserved);
    [DllImport("wlanapi.dll", CharSet = CharSet.Unicode)] static extern uint WlanSetProfile(IntPtr h, ref Guid iface, uint flags, string xml, string sd, bool overwrite, IntPtr reserved, out uint reason);
    [DllImport("wlanapi.dll")] static extern void WlanFreeMemory(IntPtr p);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ConnectionParams
    {
        public int mode;                 // 0 = by profile
        [MarshalAs(UnmanagedType.LPWStr)] public string profile;
        public IntPtr ssid, bssids;
        public int bssType;              // 1 = infrastructure
        public uint flags;
    }

    public sealed record Network(string ssid, int signal, bool secure, bool connected, bool saved, string auth);

    IntPtr handle;
    Guid iface;
    public bool Available { get; private set; }

    public WifiHub()
    {
        try
        {
            if (WlanOpenHandle(2, IntPtr.Zero, out _, out handle) != 0) return;
            if (WlanEnumInterfaces(handle, IntPtr.Zero, out var list) != 0) return;
            int count = Marshal.ReadInt32(list);
            if (count > 0)
            {
                iface = Marshal.PtrToStructure<Guid>(list + 8);
                Available = true;
            }
            WlanFreeMemory(list);
        }
        catch (Exception ex) { Log.Write("wifi: " + ex.Message); }
    }

    public void Scan()
    {
        if (Available) WlanScan(handle, ref iface, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>Visible networks, strongest first. error = "location" when Windows hides network names from apps.</summary>
    public (List<Network> list, string error) Networks()
    {
        var result = new List<Network>();
        if (!Available) return (result, "none");
        uint rc = WlanGetAvailableNetworkList(handle, ref iface, 0, IntPtr.Zero, out var p);
        if (rc == 5) return (result, "location");
        if (rc != 0) return (result, "error");
        try
        {
            int n = Marshal.ReadInt32(p);
            const int size = 628;
            int hidden = 0;
            for (int i = 0; i < n; i++)
            {
                var e = p + 8 + i * size;
                int len = Marshal.ReadInt32(e + 512);
                if (len <= 0 || len > 32) { hidden++; continue; }
                var bytes = new byte[len];
                Marshal.Copy(e + 516, bytes, 0, len);
                string ssid = Encoding.UTF8.GetString(bytes);
                int quality = Marshal.ReadInt32(e + 604);
                bool secure = Marshal.ReadInt32(e + 608) != 0;
                int auth = Marshal.ReadInt32(e + 612);
                int flags = Marshal.ReadInt32(e + 620);
                var net = new Network(ssid, quality, secure, (flags & 1) != 0, (flags & 2) != 0, auth >= 8 && auth <= 9 ? "sae" : "psk");
                var existing = result.FindIndex(x => x.ssid == ssid);
                if (existing < 0) result.Add(net);
                else
                {
                    var x = result[existing];
                    result[existing] = x with { connected = x.connected || net.connected, saved = x.saved || net.saved, signal = Math.Max(x.signal, net.signal) };
                }
            }
            if (result.Count == 0 && hidden > 0) return (result, "location");
        }
        finally { WlanFreeMemory(p); }
        return (result.OrderByDescending(x => x.connected).ThenByDescending(x => x.signal).ToList(), null);
    }

    public bool Connect(string ssid, string password)
    {
        if (!Available) return false;
        var (list, _) = Networks();
        var net = list.FirstOrDefault(x => x.ssid == ssid);
        if (net == null) return false;
        if (!net.saved)
        {
            if (net.secure && string.IsNullOrEmpty(password)) return false;
            string xml = Profile(ssid, net.secure ? password : null, net.auth);
            uint rc = WlanSetProfile(handle, ref iface, 0, xml, null, true, IntPtr.Zero, out uint reason);
            if (rc != 0) { Log.Write($"wifi profile {rc}/{reason}"); return false; }
        }
        var cp = new ConnectionParams { mode = 0, profile = ssid, bssType = 1 };
        return WlanConnect(handle, ref iface, ref cp, IntPtr.Zero) == 0;
    }

    public void Disconnect()
    {
        if (Available) WlanDisconnect(handle, ref iface, IntPtr.Zero);
    }

    static string Profile(string ssid, string password, string auth)
    {
        string name = SecurityElement.Escape(ssid);
        string hex = Convert.ToHexString(Encoding.UTF8.GetBytes(ssid));
        string security = password == null
            ? "<authEncryption><authentication>open</authentication><encryption>none</encryption><useOneX>false</useOneX></authEncryption>"
            : $"<authEncryption><authentication>{(auth == "sae" ? "WPA3SAE" : "WPA2PSK")}</authentication><encryption>AES</encryption><useOneX>false</useOneX></authEncryption>" +
              $"<sharedKey><keyType>passPhrase</keyType><protected>false</protected><keyMaterial>{SecurityElement.Escape(password)}</keyMaterial></sharedKey>";
        return "<?xml version=\"1.0\"?><WLANProfile xmlns=\"http://www.microsoft.com/networking/WLAN/profile/v1\">" +
               $"<name>{name}</name><SSIDConfig><SSID><hex>{hex}</hex><name>{name}</name></SSID></SSIDConfig>" +
               "<connectionType>ESS</connectionType><connectionMode>auto</connectionMode>" +
               $"<MSM><security>{security}</security></MSM></WLANProfile>";
    }

    public void Dispose()
    {
        if (handle != IntPtr.Zero) WlanCloseHandle(handle, IntPtr.Zero);
        handle = IntPtr.Zero;
    }
}
