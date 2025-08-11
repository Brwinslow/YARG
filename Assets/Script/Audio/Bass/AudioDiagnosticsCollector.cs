using System;
using ManagedBass;
using YARG.Core.Logging;
using YARG.Audio.BASS.Output;
using YARG.Settings;

namespace YARG.Audio.BASS
{
    public class AudioDiagnosticsCollector
    {
        private readonly BassAudioManager _audioManager;
        private AudioDiagnosticsInfo _cachedInfo;
        private DateTime _lastUpdate = DateTime.MinValue;
        private readonly TimeSpan _updateInterval = TimeSpan.FromMilliseconds(500); // Update every 500ms

        public AudioDiagnosticsCollector(BassAudioManager audioManager)
        {
            _audioManager = audioManager;
            _cachedInfo = new AudioDiagnosticsInfo();
        }

        public AudioDiagnosticsInfo GetCurrentDiagnostics()
        {
            if (DateTime.Now - _lastUpdate > _updateInterval)
            {
                UpdateDiagnostics();
                _lastUpdate = DateTime.Now;
            }
            return _cachedInfo;
        }

        private void UpdateDiagnostics()
        {
            try
            {
                // Check if using decoding backend (WASAPI/ASIO)
                bool useDecodingBackend = _audioManager.MasterOutputMixer != 0;
                
                if (useDecodingBackend)
                {
                    // Get information from output engine
                    var engine = GetOutputEngine();
                    if (engine != null)
                    {
                        UpdateFromOutputEngine(engine);
                    }
                    else
                    {
                        UpdateAsUnknownBackend();
                    }
                }
                else
                {
                    // Using default BASS device
                    UpdateFromDefaultBassDevice();
                }

                _cachedInfo.IsConnected = Bass.CurrentDevice != -1;
                _cachedInfo.HasErrors = false; // Update if we can detect errors
                _cachedInfo.LastError = string.Empty;
                _cachedInfo.Update();
            }
            catch (Exception ex)
            {
                YargLogger.LogFormatError("Error updating audio diagnostics: {0}", ex);
                _cachedInfo.HasErrors = true;
                _cachedInfo.LastError = ex.Message;
                _cachedInfo.Update();
            }
        }

        private IAudioOutputEngine GetOutputEngine()
        {
            // Use reflection to access private _engine field
            var engineField = typeof(BassAudioManager).GetField("_engine", 
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            return engineField?.GetValue(_audioManager) as IAudioOutputEngine;
        }

        private void UpdateFromOutputEngine(IAudioOutputEngine engine)
        {
            _cachedInfo.BackendName = engine.GetCurrentDeviceName();
            _cachedInfo.BackendType = engine.Backend.ToString();
            _cachedInfo.DeviceName = engine.GetCurrentDeviceName();
            _cachedInfo.SampleRate = engine.SampleRate;
            _cachedInfo.Channels = engine.Channels;
            _cachedInfo.LatencyMs = engine.LatencyMs;
            
            // Get device ID from settings
            try
            {
                _cachedInfo.DeviceId = SettingsManager.Settings.OutputDeviceIndex?.Value ?? -1;
            }
            catch
            {
                _cachedInfo.DeviceId = -1;
            }

            // Try to get additional info from BASS
            try
            {
                var bassInfo = Bass.Info;
                _cachedInfo.BitDepth = 32; // BASS uses 32-bit float internally for decoding backends
                _cachedInfo.BufferSize = bassInfo.Latency;
                _cachedInfo.Period = Bass.GetConfig(Configuration.DevicePeriod);
            }
            catch (Exception ex)
            {
                YargLogger.LogFormatError("Error getting BASS info: {0}", ex);
            }
        }

        private void UpdateFromDefaultBassDevice()
        {
            _cachedInfo.BackendName = "BASS Default";
            _cachedInfo.BackendType = "DefaultDevice";
            
            try
            {
                var bassInfo = Bass.Info;
                _cachedInfo.DeviceName = GetBassDeviceName();
                _cachedInfo.SampleRate = 44100; // Default sample rate, actual rate not available from Bass.Info
                _cachedInfo.Channels = 2; // Default channels, actual channels not available from Bass.Info
                _cachedInfo.BitDepth = 16; // Default BASS device typically uses 16-bit
                _cachedInfo.LatencyMs = bassInfo.Latency;
                _cachedInfo.BufferSize = bassInfo.Latency;
                _cachedInfo.Period = Bass.GetConfig(Configuration.DevicePeriod);
                _cachedInfo.DeviceId = Bass.CurrentDevice;
            }
            catch (Exception ex)
            {
                YargLogger.LogFormatError("Error getting default BASS device info: {0}", ex);
                UpdateAsUnknownBackend();
            }
        }

        private void UpdateAsUnknownBackend()
        {
            _cachedInfo.BackendName = "Unknown";
            _cachedInfo.BackendType = "Unknown";
            _cachedInfo.DeviceName = "Unknown";
            _cachedInfo.DeviceId = -1;
            _cachedInfo.SampleRate = 0;
            _cachedInfo.Channels = 0;
            _cachedInfo.BitDepth = 0;
            _cachedInfo.LatencyMs = 0;
            _cachedInfo.BufferSize = 0;
            _cachedInfo.Period = 0;
        }

        private string GetBassDeviceName()
        {
            try
            {
                int currentDevice = Bass.CurrentDevice;
                var deviceInfo = Bass.GetDeviceInfo(currentDevice);
                return deviceInfo.Name ?? $"BASS Device {currentDevice}";
            }
            catch (Exception ex)
            {
                YargLogger.LogFormatError("Error getting BASS device name: {0}", ex);
                return "Unknown BASS Device";
            }
        }

        public void ForceUpdate()
        {
            _lastUpdate = DateTime.MinValue;
            GetCurrentDiagnostics();
        }
    }
}