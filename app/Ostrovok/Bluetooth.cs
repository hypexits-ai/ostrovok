using System.Runtime.InteropServices;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace Ostrovok;

/// <summary>
/// Paired Bluetooth devices for the Bluetooth cell.
/// Classic devices (headphones, speakers, older keyboards) come from the Win32 Bluetooth API, which can also
/// connect / disconnect audio devices by switching their audio services, the same trick ToothTray uses.
/// Bluetooth LE devices (most modern mice and keyboards) come from WinRT and can only be shown.
/// </summary>
sealed class BtHub
{
    [StructLayout(LayoutKind.Sequential)]
    struct SearchParams
    {
        public int dwSize, fReturnAuthenticated, fReturnRemembered, fReturnUnknown, fReturnConnected, fIssueInquiry;
        public byte cTimeoutMultiplier;
        public IntPtr hRadio;
    }

    [StructLayout(LayoutKind.Sequential)] struct SYSTEMTIME { public ushort a, b, c, d, e, f, g, h; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DeviceInfo
    {
        public int dwSize;
        public ulong Address;
        public uint ulClassofDevice;
        public int fConnected, fRemembered, fAuthenticated;
        public SYSTEMTIME stLastSeen, stLastUsed;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)] public string szName;
    }

    [DllImport("bthprops.cpl", SetLastError = true)] static extern IntPtr BluetoothFindFirstDevice(ref SearchParams p, ref DeviceInfo info);
    [DllImport("bthprops.cpl", SetLastError = true)] static extern bool BluetoothFindNextDevice(IntPtr h, ref DeviceInfo info);
    [DllImport("bthprops.cpl")] static extern bool BluetoothFindDeviceClose(IntPtr h);
    [DllImport("bthprops.cpl")] static extern uint BluetoothSetServiceState(IntPtr hRadio, ref DeviceInfo info, ref Guid service, uint flags);

    static readonly Guid AudioSink = new("0000110B-0000-1000-8000-00805F9B34FB");
    static readonly Guid Handsfree = new("0000111E-0000-1000-8000-00805F9B34FB");

    public sealed record Device(string id, string name, string kind, bool connected, bool canToggle);

    readonly Dictionary<string, BluetoothLEDevice> le = new();

    List<DeviceInfo> Classic()
    {
        var list = new List<DeviceInfo>();
        var p = new SearchParams
        {
            dwSize = Marshal.SizeOf<SearchParams>(), fReturnAuthenticated = 1, fReturnRemembered = 1, fReturnConnected = 1
        };
        var info = new DeviceInfo { dwSize = Marshal.SizeOf<DeviceInfo>() };
        var h = BluetoothFindFirstDevice(ref p, ref info);
        if (h == IntPtr.Zero) return list;
        try
        {
            do
            {
                list.Add(info);
                info = new DeviceInfo { dwSize = Marshal.SizeOf<DeviceInfo>() };
            } while (BluetoothFindNextDevice(h, ref info));
        }
        finally { BluetoothFindDeviceClose(h); }
        return list;
    }

    static string Kind(uint cod)
    {
        uint major = (cod >> 8) & 0x1F, minor = (cod >> 2) & 0x3F;
        return major switch
        {
            1 => "pc",
            2 => "phone",
            4 => "head",
            5 => (minor & 0x10) != 0 ? "kbd" : (minor & 0x20) != 0 ? "mouse" : (minor & 0x0F) is 1 or 2 ? "pad" : "dev",
            _ => "dev"
        };
    }

    public async Task<List<Device>> ListAsync()
    {
        var result = new List<Device>();
        try
        {
            foreach (var d in Classic())
            {
                if (string.IsNullOrWhiteSpace(d.szName)) continue;
                string kind = Kind(d.ulClassofDevice);
                result.Add(new Device("bt:" + d.Address.ToString("X12"), d.szName, kind, d.fConnected != 0, kind == "head"));
            }
        }
        catch (Exception ex) { Log.Write("bt classic: " + ex.Message); }

        try
        {
            var infos = await DeviceInformation.FindAllAsync(BluetoothLEDevice.GetDeviceSelectorFromPairingState(true));
            foreach (var info in infos)
            {
                if (string.IsNullOrWhiteSpace(info.Name) || result.Any(r => r.name == info.Name)) continue;
                if (!le.TryGetValue(info.Id, out var dev))
                {
                    try { dev = await BluetoothLEDevice.FromIdAsync(info.Id); } catch { dev = null; }
                    if (dev != null) le[info.Id] = dev;
                }
                string kind = "dev";
                bool connected = false;
                if (dev != null)
                {
                    connected = dev.ConnectionStatus == BluetoothConnectionStatus.Connected;
                    var a = dev.Appearance;
                    if (a.Category == BluetoothLEAppearanceCategories.HumanInterfaceDevice)
                        kind = a.SubCategory == BluetoothLEAppearanceSubcategories.Keyboard ? "kbd"
                             : a.SubCategory == BluetoothLEAppearanceSubcategories.Mouse ? "mouse"
                             : a.SubCategory == BluetoothLEAppearanceSubcategories.Gamepad ? "pad" : "dev";
                    else if (a.Category == BluetoothLEAppearanceCategories.Phone) kind = "phone";
                    else if (a.Category == BluetoothLEAppearanceCategories.Computer) kind = "pc";
                }
                result.Add(new Device("le:" + info.Id, info.Name, kind, connected, false));
            }
        }
        catch (Exception ex) { Log.Write("bt le: " + ex.Message); }

        return result.OrderByDescending(d => d.connected).ThenBy(d => d.name).ToList();
    }

    /// <summary>Connects or disconnects a classic audio device by switching its audio services. Returns false if it could not.</summary>
    public bool Toggle(string id, bool connect)
    {
        if (!id.StartsWith("bt:")) return false;
        ulong address = Convert.ToUInt64(id[3..], 16);
        foreach (var d in Classic())
        {
            if (d.Address != address) continue;
            var info = d;
            uint flags = connect ? 1u : 0u; // BLUETOOTH_SERVICE_ENABLE / DISABLE
            var a = AudioSink; var h = Handsfree;
            uint r1 = BluetoothSetServiceState(IntPtr.Zero, ref info, ref a, flags);
            uint r2 = BluetoothSetServiceState(IntPtr.Zero, ref info, ref h, flags);
            Log.Write($"bt {(connect ? "connect" : "disconnect")} {info.szName}: {r1}/{r2}");
            return r1 == 0 || r2 == 0;
        }
        return false;
    }
}
