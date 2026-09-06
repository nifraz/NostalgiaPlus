using System;
using System.Runtime.InteropServices;

namespace NostalgiaPlus.Audio
{
    // Minimal Core Audio interop. Method order in each interface is the COM vtable
    // order and must not be rearranged.

    internal enum DataFlow { Render = 0, Capture = 1, All = 2 }
    internal enum Role { Console = 0, Multimedia = 1, Communications = 2 }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(DataFlow dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(DataFlow dataFlow, Role role, out IMMDevice device);
        [PreserveSig] int GetDevice(string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
                                   [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        [PreserveSig] int OpenPropertyStore(int access, out IntPtr properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, int streamFlags, long bufferDuration,
                                     long periodicity, IntPtr format, IntPtr audioSessionGuid);
        [PreserveSig] int GetBufferSize(out uint bufferFrameCount);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint paddingFrames);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);
        [PreserveSig] int GetMixFormat(out IntPtr deviceFormat);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr eventHandle);
        [PreserveSig] int GetService(ref Guid interfaceId, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    }

    [ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint framesToRead, out uint flags,
                                    out long devicePosition, out long qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint framesRead);
        [PreserveSig] int GetNextPacketSize(out uint packetFrames);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct WaveFormatEx
    {
        public short wFormatTag;
        public short nChannels;
        public int nSamplesPerSec;
        public int nAvgBytesPerSec;
        public short nBlockAlign;
        public short wBitsPerSample;
        public short cbSize;
    }

    internal static class WasapiConst
    {
        public const int ShareModeShared = 0;
        public const int StreamFlagsLoopback = 0x00020000;
        public const int ClsCtxAll = 23;
        public const uint BufferFlagsSilent = 0x2;

        public const short WaveFormatPcm = 1;
        public const short WaveFormatIeeeFloat = 3;
        public const short WaveFormatExtensible = unchecked((short)0xFFFE);

        public static readonly Guid SubtypeIeeeFloat =
            new Guid("00000003-0000-0010-8000-00AA00389B71");
        public static readonly Guid SubtypePcm =
            new Guid("00000001-0000-0010-8000-00AA00389B71");
    }
}
