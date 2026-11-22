using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Dial.Modules.Radio;

/// <summary>
/// Samples live stereo Left/Right output levels for the active audio session of the current process.
/// </summary>
public interface IAudioMeterService : IDisposable
{
    (float Left, float Right) SampleCurrentProcessLevels();
}

/// <summary>
/// Windows Core Audio (WASAPI) per-process stereo peak meter backed by <c>IAudioMeterInformation</c>.
/// </summary>
public sealed class AudioMeterService : IAudioMeterService
{
    private const int DataFlowRender = 0;
    private const int RoleMultimedia = 1;
    private const int ClsCtxAll = 23;
    private const int AudioSessionStateActive = 1;

    private readonly Lock _gate = new();
    private readonly float[] _peakBuffer = new float[2];
    private IAudioMeterInformation? _cachedMeter;
    private DateTimeOffset _lastResolveAttemptUtc = DateTimeOffset.MinValue;

    public (float Left, float Right) SampleCurrentProcessLevels()
    {
        if (!OperatingSystem.IsWindows())
        {
            return (0f, 0f);
        }

        lock (_gate)
        {
            try
            {
                if (_cachedMeter is null)
                {
                    if ((DateTimeOffset.UtcNow - _lastResolveAttemptUtc).TotalMilliseconds < 400)
                    {
                        return (0f, 0f);
                    }

                    _lastResolveAttemptUtc = DateTimeOffset.UtcNow;
                    _cachedMeter = TryResolveProcessMeter();
                }

                if (_cachedMeter is null)
                {
                    return (0f, 0f);
                }

                if (_cachedMeter.GetMeteringChannelCount(out var channelCount) != 0 || channelCount == 0)
                {
                    ReleaseCachedMeter();
                    return (0f, 0f);
                }

                if (channelCount >= 2)
                {
                    if (_cachedMeter.GetChannelsPeakValues(2, _peakBuffer) == 0)
                    {
                        return (ToVuLevel(_peakBuffer[0]), ToVuLevel(_peakBuffer[1]));
                    }
                }
                else if (_cachedMeter.GetPeakValue(out var monoPeak) == 0)
                {
                    var mono = ToVuLevel(monoPeak);
                    return (mono, mono);
                }

                ReleaseCachedMeter();
                return (0f, 0f);
            }
            catch
            {
                ReleaseCachedMeter();
                return (0f, 0f);
            }
        }
    }

    /// <summary>
    /// Maps a linear audio peak amplitude (0..1) onto a logarithmic VU response curve (-48 dB..0 dB).
    /// </summary>
    public static float ToVuLevel(float linearPeak)
    {
        if (float.IsNaN(linearPeak) || linearPeak <= 0.0005f)
        {
            return 0f;
        }

        var clamped = Math.Clamp(linearPeak, 0f, 1f);
        var db = 20f * MathF.Log10(clamped);
        const float minDb = -48f;
        return Math.Clamp((db - minDb) / -minDb, 0f, 1f);
    }

    [SupportedOSPlatform("windows")]
    private void ReleaseCachedMeter()
    {
        if (_cachedMeter is not null && Marshal.IsComObject(_cachedMeter))
        {
            try
            {
                Marshal.ReleaseComObject(_cachedMeter);
            }
            catch
            {
                // Ignore COM teardown failures.
            }
        }

        _cachedMeter = null;
    }

    public void Dispose()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        lock (_gate)
        {
            ReleaseCachedMeter();
        }
    }

    [SupportedOSPlatform("windows")]
    private static IAudioMeterInformation? TryResolveProcessMeter()
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        object? managerObj = null;
        IAudioSessionEnumerator? sessionEnum = null;

        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            if (enumerator.GetDefaultAudioEndpoint(DataFlowRender, RoleMultimedia, out device) != 0 || device is null)
            {
                return null;
            }

            var iidMgr = new Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
            if (device.Activate(ref iidMgr, ClsCtxAll, IntPtr.Zero, out managerObj) != 0 ||
                managerObj is not IAudioSessionManager2 manager)
            {
                return null;
            }

            if (manager.GetSessionEnumerator(out sessionEnum) != 0 || sessionEnum is null)
            {
                return null;
            }

            if (sessionEnum.GetCount(out var count) != 0 || count <= 0)
            {
                return null;
            }

            var currentPid = (uint)Environment.ProcessId;
            IAudioMeterInformation? fallbackSamePid = null;

            for (var i = 0; i < count; i++)
            {
                if (sessionEnum.GetSession(i, out var control) != 0 || control is null)
                {
                    continue;
                }

                var keepControl = false;
                try
                {
                    if (control is IAudioSessionControl2 control2 &&
                        control2.GetProcessId(out var pid) == 0 &&
                        pid == currentPid &&
                        control is IAudioMeterInformation meter)
                    {
                        if (control2.GetState(out var state) == 0 && state == AudioSessionStateActive)
                        {
                            if (fallbackSamePid is not null && !ReferenceEquals(fallbackSamePid, meter) && Marshal.IsComObject(fallbackSamePid))
                            {
                                Marshal.ReleaseComObject(fallbackSamePid);
                            }

                            keepControl = true;
                            return meter;
                        }

                        if (fallbackSamePid is null)
                        {
                            fallbackSamePid = meter;
                            keepControl = true;
                        }
                    }
                }
                finally
                {
                    if (!keepControl && Marshal.IsComObject(control))
                    {
                        Marshal.ReleaseComObject(control);
                    }
                }
            }

            return fallbackSamePid;
        }
        catch
        {
            return null;
        }
        finally
        {
            ReleaseIfCom(sessionEnum);
            ReleaseIfCom(managerObj);
            ReleaseIfCom(device);
            ReleaseIfCom(enumerator);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void ReleaseIfCom(object? obj)
    {
        if (obj is not null && Marshal.IsComObject(obj))
        {
            try
            {
                Marshal.ReleaseComObject(obj);
            }
            catch
            {
                // Ignore COM release issues.
            }
        }
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorCom { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, int dwStateMask, out IntPtr ppDevices);
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppEndpoint);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        int Activate([In] ref Guid iid, int dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        int GetAudioSessionControl(IntPtr audioSessionGuid, int streamFlags, out IntPtr sessionControl);
        int GetSimpleAudioVolume(IntPtr audioSessionGuid, int streamFlags, out IntPtr audioVolume);
        int GetSessionEnumerator(out IAudioSessionEnumerator sessionEnum);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        int GetCount(out int sessionCount);
        int GetSession(int sessionCount, out IAudioSessionControl session);
    }

    [ComImport, Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl { }

    [ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        int GetState(out int retVal);
        int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string retVal);
        int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);
        int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string retVal);
        int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);
        int GetGroupingParam(out Guid retVal);
        int SetGroupingParam(ref Guid overRide, ref Guid eventContext);
        int RegisterAudioSessionNotification(IntPtr newNotifications);
        int UnregisterAudioSessionNotification(IntPtr newNotifications);
        int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string retVal);
        int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string retVal);
        int GetProcessId(out uint retVal);
    }

    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioMeterInformation
    {
        int GetPeakValue(out float pfPeak);
        int GetMeteringChannelCount(out uint pnChannelCount);
        int GetChannelsPeakValues(uint u32ChannelCount, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] float[] afPeakValues);
    }
}
