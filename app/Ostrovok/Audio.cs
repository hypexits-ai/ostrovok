using System.Runtime.InteropServices;

namespace Ostrovok;

/// <summary>System volume, mute and the live output level (for the equalizer), via Windows Core Audio.</summary>
sealed class AudioController
{
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumeratorCo { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        [PreserveSig] int OpenPropertyStore(int access, out IntPtr store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IAudioEndpointVolumeCallback cb);
        [PreserveSig] int UnregisterControlChangeNotify(IAudioEndpointVolumeCallback cb);
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

    [ComImport, Guid("657804FA-D6AD-4496-8A60-352752AF4F89"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioEndpointVolumeCallback
    {
        [PreserveSig] int OnNotify(IntPtr data);
    }

    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioMeterInformation
    {
        [PreserveSig] int GetPeakValue(out float peak);
    }

    [ComVisible(true)]
    sealed class Callback : IAudioEndpointVolumeCallback
    {
        readonly AudioController owner;
        public Callback(AudioController o) => owner = o;
        public int OnNotify(IntPtr data)
        {
            try
            {
                var ctx = Marshal.PtrToStructure<Guid>(data);
                bool muted = Marshal.ReadInt32(data, 16) != 0;
                float level = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(data, 20));
                owner.Changed?.Invoke(level, muted, ctx != Context);
            }
            catch { }
            return 0;
        }
    }

    static readonly Guid Context = Guid.NewGuid();

    /// <summary>(level 0..1, muted, changed by someone other than us). Raised on a COM thread.</summary>
    public event Action<float, bool, bool> Changed;

    IMMDeviceEnumerator enumerator;
    IAudioEndpointVolume volume;
    IAudioMeterInformation meter;
    Callback callback;
    string deviceId;

    public void Init()
    {
        enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCo();
        Bind();
    }

    /// <summary>Re-attach if the default output changed (e.g. headphones plugged in). Returns true when it did.</summary>
    public bool CheckDevice()
    {
        try
        {
            if (enumerator.GetDefaultAudioEndpoint(0, 1, out var dev) != 0 || dev == null) return false;
            dev.GetId(out var id);
            if (id == deviceId) return false;
            Bind();
            return true;
        }
        catch { return false; }
    }

    void Bind()
    {
        try
        {
            if (volume != null && callback != null) volume.UnregisterControlChangeNotify(callback);
        }
        catch { }
        volume = null; meter = null;
        if (enumerator.GetDefaultAudioEndpoint(0 /* eRender */, 1 /* eMultimedia */, out var dev) != 0 || dev == null) return;
        dev.GetId(out deviceId);
        var iid = typeof(IAudioEndpointVolume).GUID;
        if (dev.Activate(ref iid, 23, IntPtr.Zero, out var v) == 0) volume = (IAudioEndpointVolume)v;
        iid = typeof(IAudioMeterInformation).GUID;
        if (dev.Activate(ref iid, 23, IntPtr.Zero, out var m) == 0) meter = (IAudioMeterInformation)m;
        callback ??= new Callback(this);
        volume?.RegisterControlChangeNotify(callback);
    }

    public float Level
    {
        get { try { if (volume != null && volume.GetMasterVolumeLevelScalar(out var l) == 0) return l; } catch { } return 0; }
    }

    public bool Muted
    {
        get { try { if (volume != null && volume.GetMute(out var m) == 0) return m; } catch { } return false; }
    }

    public float Peak
    {
        get { try { if (meter != null && meter.GetPeakValue(out var p) == 0) return p; } catch { } return 0; }
    }

    public void SetLevel(float level)
    {
        if (volume == null) return;
        var g = Context;
        volume.SetMasterVolumeLevelScalar(Math.Clamp(level, 0f, 1f), ref g);
        if (level > 0 && Muted) volume.SetMute(false, ref g);
    }

    public void SetMuted(bool muted)
    {
        if (volume == null) return;
        var g = Context;
        volume.SetMute(muted, ref g);
    }
}
