using System;
using YARG.Core.Logging;
using ManagedBass; // base BASS is safe to include
using YARG.Audio.BASS.Output;

namespace YARG.Audio.BASS.OutputEngines
{
    public class WasapiOutputEngine : IAudioOutputEngine
    {
        public AudioBackend Backend { get; private set; } = AudioBackend.WasapiShared;
        public int SampleRate { get; private set; }
        public int Channels { get; private set; }
        public int BufferSizeFrames { get; private set; }
        public int LatencyMs { get; private set; }

        private int _mixerHandle;
        private bool _exclusive;
        private int _deviceId = -1;

        public WasapiOutputEngine(bool exclusive)
        {
            _exclusive = exclusive;
            Backend = exclusive ? AudioBackend.WasapiExclusive : AudioBackend.WasapiShared;
        }

        public bool Init(int deviceId, int sampleRate, int channels, int bufferSizeFrames)
        {
            _deviceId = deviceId;
            SampleRate = sampleRate;
            Channels = channels;
            BufferSizeFrames = bufferSizeFrames;

            // Ensure BASS is in decode-only mode
            // This is safe to call even if already initialized in default backend path
            Bass.Free();
            if (!Bass.Init(0, sampleRate, 0))
            {
                YargLogger.LogFormatError("BASS decode-only init failed: {0}", Bass.LastError);
                return false;
            }

#if UNITY_STANDALONE_WIN && YARG_WASAPI
            try
            {
                var flags = ManagedBass.Wasapi.WasapiInitFlags.EventDriven;
                if (_exclusive) flags |= ManagedBass.Wasapi.WasapiInitFlags.Exclusive;

                // Convert buffer size from samples to milliseconds for WASAPI
                int bufferMs = YARG.Settings.SettingsManager.SettingContainer.BufferSizeToMilliseconds(bufferSizeFrames, sampleRate);
                // Set period to 1/4 of buffer size (typical ratio for stable performance)
                int periodMs = Math.Max(1, bufferMs / 4);

                // CRITICAL: BASS WASAPI expects buffer and period parameters in SECONDS, not milliseconds
                // Convert from milliseconds to seconds for the BASS WASAPI API
                float bufferSeconds = bufferMs / 1000.0f;
                float periodSeconds = periodMs / 1000.0f;

                if (!ManagedBass.Wasapi.BassWasapi.Init(deviceId, sampleRate, channels, flags, bufferSeconds, periodSeconds, WasapiProc, IntPtr.Zero))
                {
                    YargLogger.LogFormatError("BASSWASAPI init failed: {0}", ManagedBass.Wasapi.BassWasapi.LastError);
                    return false;
                }
                LatencyMs = ManagedBass.Wasapi.BassWasapi.Info.BufferLength;
                return true;
            }
            catch (Exception ex)
            {
                YargLogger.LogFormatError("WASAPI engine init exception: {0}", ex);
                return false;
            }
#else
            // WASAPI not compiled in; gracefully fail so caller can fallback
            return false;
#endif
        }

        public bool Start(int mixerHandle)
        {
            _mixerHandle = mixerHandle;
#if UNITY_STANDALONE_WIN && YARG_WASAPI
            if (!ManagedBass.Wasapi.BassWasapi.Start())
            {
                YargLogger.LogFormatError("BASSWASAPI start failed: {0}", ManagedBass.Wasapi.BassWasapi.LastError);
                return false;
            }
            return true;
#else
            return false;
#endif
        }

        public void Stop()
        {
#if UNITY_STANDALONE_WIN && YARG_WASAPI
            ManagedBass.Wasapi.BassWasapi.Stop();
#endif
        }

        public void SetMasterVolume(double volume)
        {
            // In WASAPI path, volume should be applied on the mixer or leave to system
            // No-op here; BassAudioManager will set mixer volume
        }

        public string GetCurrentDeviceName()
        {
#if UNITY_STANDALONE_WIN && YARG_WASAPI
            try
            {
                var deviceInfo = ManagedBass.Wasapi.BassWasapi.GetDeviceInfo(_deviceId);
                if (deviceInfo != null)
                {
                    return deviceInfo.Name ?? "Unknown WASAPI Device";
                }
            }
            catch (Exception ex)
            {
                YargLogger.LogFormatError("Error getting WASAPI device name: {0}", ex);
            }
#endif
            return _deviceId == -1 ? "Default WASAPI Device" : $"WASAPI Device {_deviceId}";
        }

        public void Dispose()
        {
            Stop();
#if UNITY_STANDALONE_WIN && YARG_WASAPI
            ManagedBass.Wasapi.BassWasapi.Free();
#endif
        }

#if UNITY_STANDALONE_WIN && YARG_WASAPI
        private int WasapiProc(IntPtr buffer, int length, IntPtr user)
        {
            // Pull PCM from the decoding mixer into WASAPI buffer
            int bytes = Bass.ChannelGetData(_mixerHandle, buffer, length);
            if (bytes < 0) return 0; // XRUN/underrun: provide silence
            return bytes;
        }
#endif
    }
}
