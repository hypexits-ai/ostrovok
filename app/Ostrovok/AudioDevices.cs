using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Ostrovok;

/// <summary>
/// Everything for the sound cell: output and input devices (and switching the default one),
/// per-app volume, and the microphone level / mute.
/// </summary>
sealed class AudioHub
{
    #region COM
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumeratorCo { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint i, out PROPERTYKEY key);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
    }
    [StructLayout(LayoutKind.Sequential)] struct PROPERTYKEY { public Guid fmtid; public uint pid; }
    [StructLayout(LayoutKind.Explicit, Size = 24)] struct PROPVARIANT { [FieldOffset(0)] public ushort vt; [FieldOffset(8)] public IntPtr p; }
    [DllImport("ole32.dll")] static extern int PropVariantClear(ref PROPVARIANT pv);

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr cb);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr cb);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float db, ref Guid ctx);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid ctx);
        [PreserveSig] int GetMasterVolumeLevel(out float db);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint ch, float db, ref Guid ctx);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint ch, float level, ref Guid ctx);
        [PreserveSig] int GetChannelVolumeLevel(uint ch, out float db);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint ch, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(IntPtr sessionGuid, int flags, out IntPtr control);
        [PreserveSig] int GetSimpleAudioVolume(IntPtr sessionGuid, int flags, out IntPtr volume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator e);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, [MarshalAs(UnmanagedType.IUnknown)] out object session);
    }

    [ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionControl2
    {
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid ctx);
        [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid ctx);
        [PreserveSig] int GetGroupingParam(out Guid g);
        [PreserveSig] int SetGroupingParam(ref Guid g, ref Guid ctx);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr n);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr n);
        [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetProcessId(out uint pid);
        [PreserveSig] int IsSystemSoundsSession();
    }

    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ISimpleAudioVolume
    {
        [PreserveSig] int SetMasterVolume(float level, ref Guid ctx);
        [PreserveSig] int GetMasterVolume(out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    // Undocumented but long-stable interface that the Windows sound panel itself uses to change the default device.
    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")] class PolicyConfigClient { }
    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr format);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, int def, IntPtr format);
        [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr a, IntPtr b);
        [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, int def, IntPtr a, IntPtr b);
        [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr a);
        [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
        [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
        [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, int store, IntPtr key, IntPtr value);
        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, int store, IntPtr key, IntPtr value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
    }
    #endregion

    static readonly Guid Ctx = Guid.NewGuid();
    static readonly PROPERTYKEY KeyDesc = new() { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 2 };
    static readonly PROPERTYKEY KeyFriendly = new() { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 14 };
    static readonly PROPERTYKEY KeyInterface = new() { fmtid = new Guid("026e516e-b814-414b-83cd-856d6fef4822"), pid = 2 };
    const int Render = 0, Capture = 1;

    // created by CLSID: AudioController declares its own coclass with the same CLSID, and the runtime
    // would otherwise hand back an object of that other wrapper type
    readonly IMMDeviceEnumerator en = (IMMDeviceEnumerator)Activator.CreateInstance(
        Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")));
    readonly Dictionary<string, string> names = new();

    public sealed record Device(string id, string name, string sub, bool def);
    public sealed record AppVolume(string id, string name, string icon, float v, bool mute);

    public object Snapshot()
    {
        var mic = MicVolume();
        float micLevel = 0; bool micMute = false;
        if (mic != null) { mic.GetMasterVolumeLevelScalar(out micLevel); mic.GetMute(out micMute); }
        return new
        {
            t = "audio",
            outputs = Devices(Render),
            inputs = Devices(Capture),
            apps = Apps(),
            mic = new { has = mic != null, v = micLevel, mute = micMute }
        };
    }

    List<Device> Devices(int flow)
    {
        var list = new List<Device>();
        string defId = null;
        try { if (en.GetDefaultAudioEndpoint(flow, 1, out var d) == 0 && d != null) d.GetId(out defId); } catch { }
        if (en.EnumAudioEndpoints(flow, 1 /* DEVICE_STATE_ACTIVE */, out var col) != 0 || col == null) return list;
        col.GetCount(out uint n);
        for (uint i = 0; i < n; i++)
        {
            try
            {
                if (col.Item(i, out var dev) != 0 || dev == null) continue;
                dev.GetId(out var id);
                string desc = null, iface = null, friendly = null;
                if (dev.OpenPropertyStore(0, out var ps) == 0 && ps != null)
                {
                    desc = Prop(ps, KeyDesc); iface = Prop(ps, KeyInterface); friendly = Prop(ps, KeyFriendly);
                }
                list.Add(new Device(id, desc ?? friendly ?? "Устройство", iface ?? "", id == defId));
            }
            catch { }
        }
        return list;
    }

    static string Prop(IPropertyStore ps, PROPERTYKEY key)
    {
        try
        {
            if (ps.GetValue(ref key, out var v) != 0) return null;
            string s = v.vt == 31 ? Marshal.PtrToStringUni(v.p) : null;
            PropVariantClear(ref v);
            return string.IsNullOrWhiteSpace(s) ? null : s;
        }
        catch { return null; }
    }

    public void SetDefault(string id)
    {
        var pc = (IPolicyConfig)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")));
        for (int role = 0; role < 3; role++) pc.SetDefaultEndpoint(id, role);
    }

    // ── per-app volume ──

    List<(ISimpleAudioVolume vol, string key, string name, string exe)> Sessions()
    {
        var result = new List<(ISimpleAudioVolume, string, string, string)>();
        if (en.GetDefaultAudioEndpoint(Render, 1, out var dev) != 0 || dev == null) return result;
        var iid = typeof(IAudioSessionManager2).GUID;
        if (dev.Activate(ref iid, 23, IntPtr.Zero, out var o) != 0) return result;
        var mgr = (IAudioSessionManager2)o;
        if (mgr.GetSessionEnumerator(out var e) != 0 || e == null) return result;
        e.GetCount(out int n);
        for (int i = 0; i < n; i++)
        {
            try
            {
                if (e.GetSession(i, out var s) != 0 || s == null) continue;
                var c2 = (IAudioSessionControl2)s;
                c2.GetState(out int state);
                if (state == 2) continue; // expired
                bool system = c2.IsSystemSoundsSession() == 0;
                c2.GetProcessId(out uint pid);
                string exe = system ? null : ProcessPath(pid);
                if (!system && exe == null) continue;
                string key = system ? "sys" : exe.ToLowerInvariant();
                string name = system ? "Системные звуки" : Describe(exe);
                result.Add(((ISimpleAudioVolume)s, key, name, exe));
            }
            catch { }
        }
        return result;
    }

    List<AppVolume> Apps()
    {
        var seen = new Dictionary<string, AppVolume>();
        foreach (var (vol, key, name, exe) in Sessions())
        {
            if (seen.ContainsKey(key)) continue;
            vol.GetMasterVolume(out float v);
            vol.GetMute(out bool m);
            seen[key] = new AppVolume(key, name, exe != null ? ShellIcons.Icon(exe) : null, v, m);
        }
        // system sounds last
        return seen.Values.OrderBy(a => a.id == "sys").ThenBy(a => a.name).ToList();
    }

    public void SetAppVolume(string key, float v)
    {
        var g = Ctx;
        foreach (var s in Sessions().Where(s => s.key == key))
        {
            s.vol.SetMasterVolume(Math.Clamp(v, 0, 1), ref g);
            if (v > 0) s.vol.SetMute(false, ref g);
        }
    }

    public void ToggleAppMute(string key)
    {
        var g = Ctx;
        var list = Sessions().Where(s => s.key == key).ToList();
        if (list.Count == 0) return;
        list[0].vol.GetMute(out bool m);
        foreach (var s in list) s.vol.SetMute(!m, ref g);
    }

    // ── microphone ──

    IAudioEndpointVolume MicVolume()
    {
        try
        {
            if (en.GetDefaultAudioEndpoint(Capture, 1, out var d) != 0 || d == null) return null;
            var iid = typeof(IAudioEndpointVolume).GUID;
            return d.Activate(ref iid, 23, IntPtr.Zero, out var o) == 0 ? (IAudioEndpointVolume)o : null;
        }
        catch { return null; }
    }

    public void SetMicLevel(float v)
    {
        var mic = MicVolume(); if (mic == null) return;
        var g = Ctx;
        mic.SetMasterVolumeLevelScalar(Math.Clamp(v, 0, 1), ref g);
    }

    public void ToggleMicMute()
    {
        var mic = MicVolume(); if (mic == null) return;
        var g = Ctx;
        mic.GetMute(out bool m);
        mic.SetMute(!m, ref g);
    }

    // ── helpers ──

    string Describe(string exe)
    {
        if (names.TryGetValue(exe, out var n)) return n;
        try { n = FileVersionInfo.GetVersionInfo(exe).FileDescription; } catch { }
        if (string.IsNullOrWhiteSpace(n)) n = Path.GetFileNameWithoutExtension(exe);
        return names[exe] = n.Trim();
    }

    public static string ProcessPath(uint pid)
    {
        if (pid == 0) return null;
        var h = Native.OpenProcess(0x1000, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024); int size = sb.Capacity;
            return Native.QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { Native.CloseHandle(h); }
    }
}
