using System;
using ManagedBass;
using YARG.Core.Logging;

namespace YARG.Audio.BASS.Output
{
    /// <summary>
    /// Default BASS output engine for non-Windows platforms (Core Audio on macOS, ALSA on Linux)
    /// Uses standard BASS device initialization with configurable buffer size
    /// </summary>
    public class DefaultBassOutputEngine : IAudioOutputEngine
    {
        public AudioBackend Backend => AudioBackend.DefaultDevice;
        public int SampleRate { get; private set; }
        public int Channels { get; private set; }
        public int BufferSizeFrames { get; private set; }
        public int LatencyMs { get; private set; }

        private bool _isInitialized = false;

        public bool Init(int deviceId, int sampleRate, int channels, int bufferSizeFrames)
        {
            SampleRate = sampleRate;
            Channels = channels;
            BufferSizeFrames = bufferSizeFrames;

            // Calculate buffer length in milliseconds for BASS configuration
            int bufferMs = YARG.Settings.SettingsManager.SettingContainer.BufferSizeToMilliseconds(bufferSizeFrames, sampleRate);
            
            // Configure BASS device buffer length
            // This affects latency on supported platforms (Linux, Android, Windows CE)
            Bass.DeviceBufferLength = bufferMs;
            
            // Initialize BASS with the specified parameters
            // deviceId: -1 uses default device, specific ID for device selection
            var flags = DeviceInitFlags.Default | DeviceInitFlags.Latency;
            
            if (!Bass.Init(deviceId, sampleRate, flags, IntPtr.Zero))
            {
                var error = Bass.LastError;
                if (error == Errors.Already)
                {
                    YargLogger.LogWarning("BASS already initialized, using existing instance");
                    _isInitialized = true;
                }
                else
                {
                    YargLogger.LogFormatError("Failed to initialize BASS default device: {0}", error);
                    return false;
                }
            }
            else
            {
                _isInitialized = true;
            }

            // Calculate and store actual latency
            var info = Bass.Info;
            int devPeriod = Bass.GetConfig(Configuration.DevicePeriod);
            LatencyMs = info.Latency + Bass.DeviceBufferLength + devPeriod;

            YargLogger.LogFormatInfo("Default BASS device initialized: {0}Hz, {1}ch, {2} samples buffer ({3}ms)",
                sampleRate, channels, bufferSizeFrames, bufferMs);

            return true;
        }

        public bool Start(int mixerHandle)
        {
            if (!_isInitialized)
            {
                YargLogger.LogError("Cannot start Default BASS engine - not initialized");
                return false;
            }

            // For default BASS backend, the mixer handle is played directly
            if (!Bass.ChannelPlay(mixerHandle))
            {
                YargLogger.LogFormatError("Failed to start Default BASS playback: {0}", Bass.LastError);
                return false;
            }

            YargLogger.LogInfo("Default BASS device started successfully");
            return true;
        }

        public void Stop()
        {
            if (_isInitialized)
            {
                Bass.Stop();
                YargLogger.LogInfo("Default BASS device stopped");
            }
        }

        public void SetMasterVolume(double volume)
        {
            if (_isInitialized)
            {
                // Convert from 0.0-1.0 to 0-10000 for BASS
                Bass.Volume = (float)volume;
            }
        }

        public string GetCurrentDeviceName()
        {
            if (!_isInitialized)
                return "Not Initialized";

            try
            {
                var deviceInfo = Bass.GetDeviceInfo(Bass.CurrentDevice);
                return deviceInfo.Name ?? "Unknown Device";
            }
            catch
            {
                return "Default Device";
            }
        }

        public void Dispose()
        {
            if (_isInitialized)
            {
                Stop();
                // Note: We don't call Bass.Free() here as it might be used by other parts of the application
                _isInitialized = false;
            }
        }
    }
}