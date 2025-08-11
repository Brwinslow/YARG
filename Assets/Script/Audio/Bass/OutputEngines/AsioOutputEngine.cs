using System;
using YARG.Core.Logging;
using ManagedBass; // base BASS
using YARG.Audio.BASS.Output;

namespace YARG.Audio.BASS.OutputEngines
{
    public class AsioOutputEngine : IAudioOutputEngine
    {
        public AudioBackend Backend => AudioBackend.Asio;
        public int SampleRate { get; private set; }
        public int Channels { get; private set; }
        public int BufferSizeFrames { get; private set; }
        public int LatencyMs { get; private set; }

        private int _mixerHandle;
        private int _deviceId;

        public bool Init(int deviceId, int sampleRate, int channels, int bufferSizeFrames)
        {
            _deviceId = deviceId;
            SampleRate = sampleRate;
            Channels = channels;
            BufferSizeFrames = bufferSizeFrames;

            Bass.Free();
            if (!Bass.Init(0, sampleRate, 0))
            {
                YargLogger.LogFormatError("BASS decode-only init failed: {0}", Bass.LastError);
                return false;
            }

#if UNITY_STANDALONE_WIN && YARG_ASIO
            try
            {
                if (!ManagedBass.Asio.BassAsio.Init(deviceId))
                {
                    YargLogger.LogFormatError("BASSASIO init failed: {0}", ManagedBass.Asio.BassAsio.LastError);
                    return false;
                }
                // Set sample rate and buffer size
                ManagedBass.Asio.BassAsio.Rate = sampleRate;
                
                // Note: ASIO buffer size is typically controlled by the ASIO driver/control panel
                // The bufferSizeFrames parameter will be used when starting the device
                YargLogger.LogFormatInfo("ASIO initialized: desired buffer size {0} frames", bufferSizeFrames);
                
                // Calculate latency from actual ASIO buffer size (may differ from requested)
                LatencyMs = (int)(ManagedBass.Asio.BassAsio.Info.BufferSize * 1000.0 / sampleRate);
                return true;
            }
            catch (Exception ex)
            {
                YargLogger.LogFormatError("ASIO engine init exception: {0}", ex);
                return false;
            }
#else
            return false;
#endif
        }

        public bool Start(int mixerHandle)
        {
            _mixerHandle = mixerHandle;
#if UNITY_STANDALONE_WIN && YARG_ASIO
            try
            {
                // Enable a stereo pair fed by the BASS mixer
                // Note: this is the simplest mapping; multi-channel mapping can be added later
                bool ok0 = ManagedBass.Asio.BassAsio.ChannelEnableBASS(false, 0, _mixerHandle, true);
                bool ok1 = ManagedBass.Asio.BassAsio.ChannelEnableBASS(false, 1, _mixerHandle, true);
                if (!ok0 || !ok1)
                {
                    YargLogger.LogFormatError("BASSASIO ChannelEnableBASS failed: {0}", ManagedBass.Asio.BassAsio.LastError);
                    return false;
                }
                ManagedBass.Asio.BassAsio.ChannelJoin(false, 1, 0);

                for (int ch = 2; ch < Channels; ch++)
                {
                    ManagedBass.Asio.BassAsio.ChannelEnable(false, ch, null);
                    ManagedBass.Asio.BassAsio.ChannelJoin(false, ch, 0);
                }

                // Start ASIO with the requested buffer size (0 = use current/default buffer size)
                // Note: Actual buffer size may be adjusted by the ASIO driver
                int requestedBufferSize = BufferSizeFrames > 0 ? BufferSizeFrames : 0;
                if (!ManagedBass.Asio.BassAsio.Start(requestedBufferSize))
                {
                    YargLogger.LogFormatError("BASSASIO start failed: {0}", ManagedBass.Asio.BassAsio.LastError);
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                YargLogger.LogFormatError("ASIO start exception: {0}", ex);
                return false;
            }
#else
            return false;
#endif
        }

        public void Stop()
        {
#if UNITY_STANDALONE_WIN && YARG_ASIO
            ManagedBass.Asio.BassAsio.Stop();
            ManagedBass.Asio.BassAsio.Free();
#endif
        }

        public void SetMasterVolume(double volume)
        {
            // Prefer mixer volume; leave hardware untouched
        }

        public string GetCurrentDeviceName()
        {
#if UNITY_STANDALONE_WIN && YARG_ASIO
            try
            {
                var deviceInfo = ManagedBass.Asio.BassAsio.GetDeviceInfo(_deviceId);
                if (deviceInfo != null)
                {
                    return deviceInfo.Name ?? "Unknown ASIO Device";
                }
            }
            catch (Exception ex)
            {
                YargLogger.LogFormatError("Error getting ASIO device name: {0}", ex);
            }
#endif
            return _deviceId == -1 ? "Default ASIO Device" : $"ASIO Device {_deviceId}";
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
