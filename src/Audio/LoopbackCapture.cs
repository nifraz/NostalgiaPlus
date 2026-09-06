using System;
using System.Runtime.InteropServices;
using System.Threading;
using NostalgiaPlus.Dsp;

namespace NostalgiaPlus.Audio
{
    /// <summary>
    /// WASAPI shared-mode loopback capture of the default render endpoint.
    ///
    /// MusicBee's own plugin API only exposes a fixed, pre-computed FFT
    /// (NowPlaying_GetSpectrumData), which caps resolution before we start. Tapping
    /// the render endpoint gives full-rate PCM, so transform size, window, overlap
    /// and metering all become ours to choose. The trade-off is that this captures
    /// the system mix, so other applications' audio is included.
    /// </summary>
    public sealed class LoopbackCapture : IDisposable
    {
        private static readonly Guid ClsidMMDeviceEnumerator =
            new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");

        private Thread _thread;
        private volatile bool _running;
        private volatile string _status = "stopped";
        private volatile int _sampleRate;
        private volatile int _channels;

        public SampleRing Ring { get; private set; }
        public bool IsRunning { get { return _running; } }
        public string Status { get { return _status; } }
        public int SampleRate { get { return _sampleRate <= 0 ? 48000 : _sampleRate; } }
        public int Channels { get { return _channels <= 0 ? 2 : _channels; } }

        public LoopbackCapture()
        {
            Ring = new SampleRing(1 << 18); // ~5.5s at 48k, comfortably above the largest FFT
        }

        public void Start()
        {
            if (_running) return;
            _running = true;
            _thread = new Thread(CaptureLoop);
            _thread.IsBackground = true;
            _thread.Name = "NostalgiaPlus.Loopback";
            _thread.SetApartmentState(ApartmentState.MTA);
            _thread.Start();
        }

        public void Stop()
        {
            _running = false;
            Thread t = _thread;
            if (t != null && t.IsAlive)
            {
                if (!t.Join(1500)) { try { t.Abort(); } catch { } }
            }
            _thread = null;
            _status = "stopped";
        }

        public void Dispose() { Stop(); }

        private void CaptureLoop()
        {
            IntPtr formatPtr = IntPtr.Zero;
            IAudioClient client = null;
            IAudioCaptureClient capture = null;

            try
            {
                Type t = Type.GetTypeFromCLSID(ClsidMMDeviceEnumerator);
                if (t == null) { _status = "no Core Audio"; return; }
                var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(t);

                IMMDevice device;
                int hr = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console, out device);
                if (hr != 0 || device == null) { _status = "no output device"; return; }

                Guid iidAudioClient = typeof(IAudioClient).GUID;
                object clientObj;
                hr = device.Activate(ref iidAudioClient, WasapiConst.ClsCtxAll, IntPtr.Zero, out clientObj);
                if (hr != 0 || clientObj == null) { _status = "activate failed"; return; }
                client = (IAudioClient)clientObj;

                hr = client.GetMixFormat(out formatPtr);
                if (hr != 0 || formatPtr == IntPtr.Zero) { _status = "no mix format"; return; }

                WaveFormatEx wf = (WaveFormatEx)Marshal.PtrToStructure(formatPtr, typeof(WaveFormatEx));
                bool isFloat;
                if (wf.wFormatTag == WasapiConst.WaveFormatIeeeFloat)
                    isFloat = true;
                else if (wf.wFormatTag == WasapiConst.WaveFormatExtensible)
                {
                    byte[] guidBytes = new byte[16];
                    Marshal.Copy(new IntPtr(formatPtr.ToInt64() + 24), guidBytes, 0, 16);
                    isFloat = new Guid(guidBytes) == WasapiConst.SubtypeIeeeFloat;
                }
                else if (wf.wFormatTag == WasapiConst.WaveFormatPcm)
                    isFloat = false;
                else { _status = "unsupported format"; return; }

                if (!isFloat && wf.wBitsPerSample != 16 && wf.wBitsPerSample != 32)
                { _status = "unsupported bit depth"; return; }

                _sampleRate = wf.nSamplesPerSec;
                _channels = wf.nChannels;
                Ring.Clear();

                const long bufferDuration = 2000000; // 200 ms in 100-ns units
                hr = client.Initialize(WasapiConst.ShareModeShared,
                                       WasapiConst.StreamFlagsLoopback,
                                       bufferDuration, 0, formatPtr, IntPtr.Zero);
                if (hr != 0)
                {
                    // AUDCLNT_E_DEVICE_IN_USE (0x8889000A) means the endpoint is held
                    // in exclusive mode by some other application.
                    _status = (hr == unchecked((int)0x8889000A))
                        ? "output device in exclusive mode"
                        : "init failed 0x" + hr.ToString("X8");
                    return;
                }

                Guid iidCapture = typeof(IAudioCaptureClient).GUID;
                object captureObj;
                hr = client.GetService(ref iidCapture, out captureObj);
                if (hr != 0 || captureObj == null) { _status = "capture service failed"; return; }
                capture = (IAudioCaptureClient)captureObj;

                client.Start();
                _status = "wasapi " + _sampleRate + " Hz";

                int blockAlign = wf.nBlockAlign;
                float[] scratch = new float[16384];
                float[] silence = new float[_channels * 512];
                int idleMs = 0;

                while (_running)
                {
                    uint packetFrames;
                    if (capture.GetNextPacketSize(out packetFrames) != 0) break;

                    if (packetFrames == 0)
                    {
                        Thread.Sleep(4);
                        idleMs += 4;
                        // Keep the timeline moving during silence instead of freezing
                        // the display on the last frame that had audio.
                        if (idleMs >= 40)
                        {
                            Ring.Write(silence, 0, 512, _channels);
                            idleMs = 0;
                        }
                        continue;
                    }
                    idleMs = 0;

                    while (packetFrames != 0)
                    {
                        IntPtr data;
                        uint frames, flags;
                        long devPos, qpcPos;
                        if (capture.GetBuffer(out data, out frames, out flags, out devPos, out qpcPos) != 0)
                            break;

                        int needed = (int)frames * _channels;
                        if (scratch.Length < needed) scratch = new float[needed];

                        if ((flags & WasapiConst.BufferFlagsSilent) != 0 || data == IntPtr.Zero)
                        {
                            Array.Clear(scratch, 0, needed);
                        }
                        else if (isFloat && wf.wBitsPerSample == 32)
                        {
                            Marshal.Copy(data, scratch, 0, needed);
                        }
                        else if (wf.wBitsPerSample == 16)
                        {
                            short[] tmp = new short[needed];
                            Marshal.Copy(data, tmp, 0, needed);
                            for (int i = 0; i < needed; i++) scratch[i] = tmp[i] / 32768f;
                        }
                        else // 32-bit integer PCM
                        {
                            int[] tmp = new int[needed];
                            Marshal.Copy(data, tmp, 0, needed);
                            for (int i = 0; i < needed; i++) scratch[i] = (float)(tmp[i] / 2147483648.0);
                        }

                        Ring.Write(scratch, 0, (int)frames, _channels);
                        capture.ReleaseBuffer(frames);

                        if (capture.GetNextPacketSize(out packetFrames) != 0) break;
                    }
                }

                client.Stop();
            }
            catch (ThreadAbortException) { }
            catch (Exception ex)
            {
                _status = "error: " + ex.GetType().Name;
            }
            finally
            {
                if (formatPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(formatPtr);
                if (capture != null) Marshal.ReleaseComObject(capture);
                if (client != null) Marshal.ReleaseComObject(client);
                _running = false;
            }
        }
    }
}
