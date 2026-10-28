using System.Runtime.InteropServices;

namespace Strata.Native;

/// <summary>Master volume and the playback peak level of the default playback device, via Core Audio.</summary>
internal sealed class AudioEndpoint
{
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorCom { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
        [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid context);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDb);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    /// <summary>Only the first method is used: the peak sample level of what the device is playing now.</summary>
    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioMeterInformation
    {
        [PreserveSig] int GetPeakValue(out float peak);
    }

    private const int ERender = 0;
    private const int EMultimedia = 1;
    private const int ClsctxAll = 0x17;

    private IAudioEndpointVolume? _volume;
    private DateTime _acquired;
    private Guid _context = Guid.Empty;

    private IAudioEndpointVolume? Volume
    {
        get
        {
            // Re-acquire periodically so a changed default device (headphones plugged in) is picked up.
            if (_volume is null || DateTime.UtcNow - _acquired > TimeSpan.FromSeconds(5))
            {
                if (_volume is not null) { Marshal.ReleaseComObject(_volume); _volume = null; }
                _volume = Acquire<IAudioEndpointVolume>();
                _acquired = DateTime.UtcNow;
            }
            return _volume;
        }
    }

    private IAudioMeterInformation? _meter;
    private DateTime _meterAcquired;

    /// <summary>
    /// The peak level (0..1) of what the default playback device plays right now, or null without one. A level
    /// only, no audio: the media waves use it to follow the music. Re-acquired like the volume.
    /// </summary>
    public double? Peak
    {
        get
        {
            if (_meter is null || DateTime.UtcNow - _meterAcquired > TimeSpan.FromSeconds(5))
            {
                if (_meter is not null) { Marshal.ReleaseComObject(_meter); _meter = null; }
                _meter = Acquire<IAudioMeterInformation>();
                _meterAcquired = DateTime.UtcNow;
            }
            try { return _meter is { } m && m.GetPeakValue(out float peak) == 0 ? Math.Clamp(peak, 0, 1) : null; }
            catch (COMException) { _meter = null; return null; }
        }
    }

    private static T? Acquire<T>() where T : class
    {
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            try
            {
                if (enumerator.GetDefaultAudioEndpoint(ERender, EMultimedia, out var device) != 0) return null;
                try
                {
                    var iid = typeof(T).GUID;
                    if (device.Activate(ref iid, ClsctxAll, IntPtr.Zero, out object o) != 0) return null;
                    return (T)o;
                }
                finally { Marshal.ReleaseComObject(device); }
            }
            finally { Marshal.ReleaseComObject(enumerator); }
        }
        catch { return null; }
    }

    public bool Available => Volume is not null;

    /// <summary>0..100, or -1 when no playback device exists.</summary>
    public int Level
    {
        get => Volume is { } v && v.GetMasterVolumeLevelScalar(out float l) == 0 ? (int)Math.Round(l * 100) : -1;
        set => Volume?.SetMasterVolumeLevelScalar(Math.Clamp(value, 0, 100) / 100f, ref _context);
    }

    public bool Muted
    {
        get => Volume is { } v && v.GetMute(out bool m) == 0 && m;
        set => Volume?.SetMute(value, ref _context);
    }
}
