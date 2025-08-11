using System;
using System.Collections.Generic;
using System.IO;
using ManagedBass;
using ManagedBass.Fx;
using ManagedBass.Mix;
using UnityEngine;
using YARG.Core.Audio;
using YARG.Core.Logging;
using YARG.Settings;
using YARG.Audio.BASS.Output;
using YARG.Audio.BASS.OutputEngines;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace YARG.Audio.BASS
{
    internal class StreamHandle : IDisposable
    {
#nullable enable
        public static StreamHandle? Create(int sourceStream, int[] indices)
        {
            const BassFlags splitFlags = BassFlags.Decode | BassFlags.SplitPosition;
            const BassFlags tempoFlags = BassFlags.SampleOverrideLowestVolume | BassFlags.Decode | BassFlags.FxFreeSource;

            int[]? channelMap = null;
#nullable disable
            if (indices != null)
            {
                channelMap = new int[indices.Length + 1];
                for (int i = 0; i < indices.Length; ++i)
                {
                    channelMap[i] = indices[i];
                }
                channelMap[indices.Length] = -1;
            }

            int streamSplit = BassMix.CreateSplitStream(sourceStream, splitFlags, channelMap);
            if (streamSplit == 0)
            {
                YargLogger.LogFormatError("Failed to create split stream: {0}!", Bass.LastError);
                return null;
            }
            return new StreamHandle(BassFx.TempoCreate(streamSplit, tempoFlags));
        }

        private bool _disposed;
        public readonly int Stream;

        public int CompressorFX;
        public int PitchFX;
        public int ReverbFX;

        public int LowEQ;
        public int MidEQ;
        public int HighEQ;

        private StreamHandle(int stream)
        {
            Stream = stream;
        }

        private void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                // FX handles are freed automatically, we only need to free the stream
                if (!Bass.StreamFree(Stream))
                {
                    YargLogger.LogFormatError("Failed to free channel stream (THIS WILL LEAK MEMORY): {0}!", Bass.LastError);
                }
                _disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        ~StreamHandle()
        {
            Dispose(false);
        }
    }

    public class BassAudioManager : AudioManager
    {
        private IAudioOutputEngine _engine; // null unless decoding backend is used
        internal int MasterOutputMixer { get; private set; } // decoding master mixer for WASAPI/ASIO
        private int _sfxMixer; // decoding mixer for SFX/DrumSFX
        private bool _useDecodingBackend;

        // Re-initialization fields
        private bool _reinitializationRequested;
        private string _reinitializationReason;
        private readonly object _reinitLock = new object();

        private static readonly string[] FORMATS =
        {
            ".ogg", ".mogg", ".wav", ".mp3", ".aiff", ".opus",
        };

        protected override ReadOnlySpan<string> SupportedFormats => FORMATS;

        private readonly int _opusHandle = 0;

        public BassAudioManager()
        {
            YargLogger.LogInfo("Initializing BASS...");
            string bassPath = GetBassDirectory();
            string opusLibDirectory = Path.Combine(bassPath, "bassopus");

            _opusHandle = Bass.PluginLoad(opusLibDirectory);
            if (_opusHandle == 0) YargLogger.LogFormatError("Failed to load .opus plugin: {0}!", Bass.LastError);

            Bass.Configure(Configuration.IncludeDefaultDevice, true);

            Bass.UpdatePeriod = 5;
            //Bass.PlaybackBufferLength = BassHelpers.PLAYBACK_BUFFER_LENGTH;
            Bass.DeviceNonStop = true;
            Bass.AsyncFileBufferLength = 65536;

            // This not the same as Bass.UpdatePeriod
            // If not explicitly set by the audio driver or OS, the default will be 10
            // https://www.un4seen.com/doc/#bass/BASS_CONFIG_DEV_PERIOD.html
            int devPeriod = Bass.GetConfig(Configuration.DevicePeriod);

            // Documentation recommends setting the device buffer to at least 2x the device period
            // https://www.un4seen.com/doc/#bass/BASS_CONFIG_DEV_BUFFER.html
            Bass.DeviceBufferLength = 2 * devPeriod;

            // Affects Windows only. Forces device names to be in UTF-8 on Windows rather than ANSI.
            Bass.UnicodeDeviceInformation = true;
            Bass.FloatingPointDSP = true;
            Bass.VistaTruePlayPosition = false;
            Bass.UpdateThreads = GlobalAudioHandler.MAX_THREADS;

            // Undocumented BASS_CONFIG_MP3_OLDGAPS config.
            Bass.Configure((Configuration) 68, 1);

            // Disable undocumented BASS_CONFIG_DEV_TIMEOUT config. Prevents pausing audio output if a device times out.
            Bass.Configure((Configuration) 70, false);

            // Initialize audio backends with current settings
            InitializeAudioBackends();
        }


#nullable enable
        protected override StemMixer? CreateMixer(string name, float speed, double mixerVolume, bool clampStemVolume)
        {
            if (GlobalAudioHandler.LogMixerStatus)
            {
                YargLogger.LogDebug("Loading song");
            }

            if (_useDecodingBackend)
            {
                return new BassDecodingStemMixer(name, this, speed, mixerVolume, 0, clampStemVolume);
            }
            else
            {
                if (!CreateMixerHandle(out int handle))
                {
                    return null;
                }
                return new BassStemMixer(name, this, speed, mixerVolume, handle, 0, clampStemVolume);
            }
        }

        protected override StemMixer? CreateMixer(string name, Stream stream, float speed, double mixerVolume, bool clampStemVolume)
        {
            if (GlobalAudioHandler.LogMixerStatus)
            {
                YargLogger.LogDebug("Loading song");
            }

            if (_useDecodingBackend)
            {
                if (!CreateSourceStream(stream, out int sourceStream))
                {
                    return null;
                }
                return new BassDecodingStemMixer(name, this, speed, mixerVolume, sourceStream, clampStemVolume);
            }
            else
            {
                if (!CreateMixerHandle(out int handle))
                {
                    return null;
                }
                if (!CreateSourceStream(stream, out int sourceStream))
                {
                    return null;
                }
                return new BassStemMixer(name, this, speed, mixerVolume, handle, sourceStream, clampStemVolume);
            }
        }

        protected override MicDevice? GetInputDevice(string name)
        {
            for (int deviceIndex = 0; Bass.RecordGetDeviceInfo(deviceIndex, out var info); deviceIndex++)
            {
                // Ignore disabled/claimed devices
                if (!info.IsEnabled || info.IsInitialized) continue;

                // Ignore loopback devices, they're potentially confusing and can cause feedback loops
                if (info.IsLoopback) continue;

                // Check if type is in whitelist
                // The "Default" device is also excluded here since we want the user to explicitly pick which microphone to use
                // if (!typeWhitelist.Contains(info.Type) || info.Name == "Default") continue;
                if (info.Name == "Default" || info.Name != name) continue;

                return CreateDevice(deviceIndex, name);
            }
            return null;
        }
#nullable disable

        protected override List<(int id, string name)> GetAllInputDevices()
        {
            var mics = new List<(int id, string name)>();

            // Ignored for now since it causes issues on Linux, BASS must not report device info correctly there
            // TODO: allow configuring this at runtime?
            // Also put into a static variable instead of instantiating every time
            // var typeWhitelist = new List<DeviceType>()
            // {
            //     DeviceType.Headset,
            //     DeviceType.Digital,
            //     DeviceType.Line,
            //     DeviceType.Headphones,
            //     DeviceType.Microphone,
            // };

            for (int deviceIndex = 0; Bass.RecordGetDeviceInfo(deviceIndex, out var info); deviceIndex++)
            {
                // Ignore disabled/claimed devices
                if (!info.IsEnabled || info.IsInitialized) continue;

                // Ignore loopback devices, they're potentially confusing and can cause feedback loops
                if (info.IsLoopback) continue;

                // Check if type is in whitelist
                // The "Default" device is also excluded here since we want the user to explicitly pick which microphone to use
                // if (!typeWhitelist.Contains(info.Type) || info.Name == "Default") continue;
                if (info.Name == "Default") continue;

                mics.Add((deviceIndex, info.Name));
            }

            return mics;
        }

#nullable enable
        protected override MicDevice? CreateDevice(int deviceId, string name)
#nullable disable
        {
            var device = BassMicDevice.Create(deviceId, name);
            device?.SetMonitoringLevel(SettingsManager.Settings.VocalMonitoring.Value);
            return device;
        }

        private void LoadSfx()
        {
            YargLogger.LogInfo("Loading SFX");

            string sfxFolder = Path.Combine(Application.streamingAssetsPath, "sfx");

            foreach (string sfxFile in AudioHelpers.SfxPaths)
            {
                string sfxBase = Path.Combine(sfxFolder, sfxFile);
                foreach (string format in SupportedFormats)
                {
                    string sfxPath = sfxBase + format;
                    if (File.Exists(sfxPath))
                    {
                        var sfxSample = AudioHelpers.GetSfxFromName(sfxFile);
                        var sfx = _useDecodingBackend ? (SampleChannel) new BassSampleChannelDecoding(sfxSample, sfxPath, GetSfxMixerHandle(), AudioHelpers.SfxVolume[(int) sfxSample]) : BassSampleChannel.Create(sfxSample, sfxPath, 8);
                        if (sfx != null)
                        {
                            SfxSamples[(int) sfxSample] = sfx;
                            YargLogger.LogFormatInfo("Loaded {0}", sfxFile);
                        }
                        break;
                    }
                }
            }

            YargLogger.LogInfo("Finished loading SFX");
        }

        private void LoadDrumSfx()
        {
            YargLogger.LogInfo("Loading Drum SFX");

            string sfxFolder = Path.Combine(Application.streamingAssetsPath, "drumSfx");

            foreach (string sfxFile in AudioHelpers.DrumSfxPaths)
            {
                string sfxBase = Path.Combine(sfxFolder, sfxFile);
                foreach (string format in SupportedFormats)
                {
                    string sfxPath = sfxBase + format;
                    if (File.Exists(sfxPath))
                    {
                        var sfxSample = AudioHelpers.GetDrumSfxFromName(sfxFile);
                        var sfx = _useDecodingBackend ? (DrumSampleChannel) new BassDrumSampleChannelDecoding(sfxSample, sfxPath, GetSfxMixerHandle()) : BassDrumSampleChannel.Create(sfxSample, sfxPath, 8);
                        if (sfx != null)
                        {
                            DrumSfxSamples[(int) sfxSample] = sfx;
                        }
                        break;
                    }
                }
            }

            YargLogger.LogInfo("Finished loading Drum SFX");
        }

        protected override void SetMasterVolume(double volume)
        {
#if UNITY_EDITOR
            if (EditorUtility.audioMasterMute)
                volume = 0;
#endif
            SetMasterVolumeInternal(volume);
        }

        /// <summary>
        /// Thread-safe version of SetMasterVolume that doesn't access Unity Editor APIs
        /// Used during audio re-initialization from background threads
        /// </summary>
        private void SetMasterVolumeInternal(double volume)
        {
            if (_useDecodingBackend)
            {
                Bass.ChannelSetAttribute(MasterOutputMixer, ChannelAttribute.Volume, volume);
            }
            else
            {
                Bass.GlobalStreamVolume = (int) (10_000 * volume);
                Bass.GlobalSampleVolume = (int) (10_000 * volume);
            }
        }

        protected override void ToggleBuffer_Internal(bool enable)
        {
            // Nothing
        }

        protected override void SetBufferLength_Internal(int length)
        {
            Bass.PlaybackBufferLength = length;
        }

        private int GetSfxMixerHandle()
        {
            return _useDecodingBackend ? _sfxMixer : 0;
        }

        public void RequestAudioReinitialize(string reason)
        {
            lock (_reinitLock)
            {
                if (_reinitializationRequested)
                {
                    YargLogger.LogInfo($"Audio re-initialization already requested. Previous reason: {_reinitializationReason}, New reason: {reason}");
                    return;
                }

                _reinitializationRequested = true;
                _reinitializationReason = reason;
                
                YargLogger.LogFormatInfo("Requesting audio re-initialization: {0}", reason);
                
                // Start the re-initialization process on a background thread to avoid blocking the main thread
                var reinitTask = new System.Threading.Tasks.Task(PerformReinitializationAsync);
                reinitTask.Start();
            }
        }

        private void PerformReinitializationAsync()
        {
            try
            {
                YargLogger.LogFormatInfo("Starting audio re-initialization: {0}", _reinitializationReason);

                // Step 1: Fade out current audio smoothly
                FadeOutCurrentAudio();

                // Step 2: Stop and dispose current engines/mixers
                CleanupCurrentAudioResources();

                // Step 3: Re-initialize with new settings
                ReinitializeAudioWithNewSettings();

                // Step 4: Fade back in
                FadeInNewAudio();

                YargLogger.LogInfo("Audio re-initialization completed successfully");
            }
            catch (System.Exception ex)
            {
                YargLogger.LogFormatError("Audio re-initialization failed: {0}", ex);
                
                // Try to fallback to a safe state
                try
                {
                    CleanupCurrentAudioResources();
                    // Re-run basic initialization as fallback
                    ReinitializeAudioWithNewSettings();
                }
                catch (System.Exception fallbackEx)
                {
                    YargLogger.LogFormatError("Audio re-initialization fallback also failed: {0}", fallbackEx);
                }
            }
            finally
            {
                lock (_reinitLock)
                {
                    _reinitializationRequested = false;
                    _reinitializationReason = null;
                }
            }
        }

        private void FadeOutCurrentAudio()
        {
            const int fadeDurationMs = 500; // 500ms fade out
            const int fadeSteps = 20;
            const int stepDelayMs = fadeDurationMs / fadeSteps;

            YargLogger.LogInfo("Fading out audio for re-initialization...");

            double originalVolume = SettingsManager.Settings.MasterMusicVolume?.Value ?? 0.75;
            
            for (int step = fadeSteps; step >= 0; step--)
            {
                double fadeVolume = originalVolume * (step / (double)fadeSteps);
                SetMasterVolumeInternal(fadeVolume);
                System.Threading.Thread.Sleep(stepDelayMs);
            }
            
            // Mute completely
            SetMasterVolumeInternal(0.0);
        }

        private void FadeInNewAudio()
        {
            const int fadeDurationMs = 300; // 300ms fade in (shorter than fade out)
            const int fadeSteps = 15;
            const int stepDelayMs = fadeDurationMs / fadeSteps;

            YargLogger.LogInfo("Fading in audio after re-initialization...");

            double targetVolume = SettingsManager.Settings.MasterMusicVolume?.Value ?? 0.75;
            
            for (int step = 0; step <= fadeSteps; step++)
            {
                double fadeVolume = targetVolume * (step / (double)fadeSteps);
                SetMasterVolumeInternal(fadeVolume);
                System.Threading.Thread.Sleep(stepDelayMs);
            }
        }

        private void CleanupCurrentAudioResources()
        {
            YargLogger.LogInfo("Cleaning up current audio resources...");
            
            // Stop and dispose current engine
            _engine?.Stop();
            _engine?.Dispose();
            _engine = null;

            // Clean up mixers
            if (MasterOutputMixer != 0)
            {
                Bass.StreamFree(MasterOutputMixer);
                MasterOutputMixer = 0;
            }
            
            if (_sfxMixer != 0)
            {
                Bass.StreamFree(_sfxMixer);
                _sfxMixer = 0;
            }

            _useDecodingBackend = false;

            // Free BASS but keep plugins loaded
            Bass.Free();
        }

        private void ReinitializeAudioWithNewSettings()
        {
            YargLogger.LogInfo("Re-initializing audio with new settings...");
            
            // Re-run the initialization logic from constructor
            Bass.Configure(Configuration.IncludeDefaultDevice, true);
            Bass.UpdatePeriod = 5;
            Bass.DeviceNonStop = true;
            Bass.AsyncFileBufferLength = 65536;

            int devPeriod = Bass.GetConfig(Configuration.DevicePeriod);
            Bass.DeviceBufferLength = 2 * devPeriod;

            // Reconfigure all the settings
            Bass.UnicodeDeviceInformation = true;
            Bass.FloatingPointDSP = true;
            Bass.VistaTruePlayPosition = false;
            Bass.UpdateThreads = GlobalAudioHandler.MAX_THREADS;

            Bass.Configure((Configuration) 68, 1);
            Bass.Configure((Configuration) 70, false);

            // Re-run backend selection with current settings
            InitializeAudioBackends();

            // Reload SFX if needed
            if (!_useDecodingBackend)
            {
                LoadSfx();
                LoadDrumSfx();
            }
        }

        private void InitializeAudioBackends()
        {
            // Get universal audio settings
            var desiredBackend = AudioBackend.Auto;
            int desiredDevice = -1;
            int desiredChannels = 2;
            int desiredSampleRate = 44100;
            int desiredBufferSize = 512;
            try
            {
                desiredBackend = SettingsManager.Settings.OutputBackend?.Value ?? AudioBackend.Auto;
                desiredDevice = SettingsManager.Settings.OutputDeviceIndex?.Value ?? -1;
                desiredChannels = SettingsManager.Settings.OutputChannels?.Value ?? 2;
                desiredSampleRate = SettingsManager.Settings.AudioSampleRate?.Value ?? 44100;
                desiredBufferSize = SettingsManager.Settings.AudioBufferSize?.Value ?? 512;
            }
            catch { }

#if UNITY_STANDALONE_WIN
            if (desiredBackend == AudioBackend.Auto || desiredBackend == AudioBackend.Asio)
            {
                var asio = new AsioOutputEngine();
                if (asio.Init(desiredDevice, desiredSampleRate, desiredChannels, desiredBufferSize))
                {
                    _engine = asio;
                }
            }
            if (_engine == null && (desiredBackend == AudioBackend.Auto || desiredBackend == AudioBackend.WasapiExclusive))
            {
                var wasapiEx = new WasapiOutputEngine(exclusive: true);
                if (wasapiEx.Init(desiredDevice, desiredSampleRate, desiredChannels, desiredBufferSize))
                {
                    _engine = wasapiEx;
                }
            }
            if (_engine == null && (desiredBackend == AudioBackend.Auto || desiredBackend == AudioBackend.WasapiShared))
            {
                var wasapi = new WasapiOutputEngine(exclusive: false);
                if (wasapi.Init(desiredDevice, desiredSampleRate, desiredChannels, desiredBufferSize))
                {
                    _engine = wasapi;
                }
            }
#endif

            // Use DefaultBassOutputEngine for explicit DefaultDevice selection only
            // On non-Windows platforms, Auto should fall through to regular BASS initialization  
            if (_engine == null && desiredBackend == AudioBackend.DefaultDevice)
            {
                var defaultEngine = new DefaultBassOutputEngine();
                if (defaultEngine.Init(desiredDevice, desiredSampleRate, desiredChannels, desiredBufferSize))
                {
                    _engine = defaultEngine;
                }
            }

            if (_engine != null)
            {
                _useDecodingBackend = true;
                MasterOutputMixer = BassMix.CreateMixerStream(desiredSampleRate, desiredChannels, BassFlags.Float | BassFlags.Decode);
                if (MasterOutputMixer == 0)
                {
                    YargLogger.LogFormatError("Failed to create master decoding mixer: {0}!", Bass.LastError);
                    _useDecodingBackend = false;
                    _engine?.Dispose();
                    _engine = null;
                }
                else
                {
                    // Create SFX mixer for decoding backends (always stereo)
                    _sfxMixer = BassMix.CreateMixerStream(desiredSampleRate, 2, BassFlags.Float | BassFlags.Decode);
                    if (_sfxMixer != 0)
                    {
                        var flags = desiredChannels > 2 ? BassFlags.MixerChanMatrix : BassFlags.Default;
                        if (!BassMix.MixerAddChannel(MasterOutputMixer, _sfxMixer, flags))
                        {
                            YargLogger.LogFormatError("Failed to add SFX mixer to master: {0}!", Bass.LastError);
                        }
                        else if (flags.HasFlag(BassFlags.MixerChanMatrix))
                        {
                            float[,] mat = new float[desiredChannels, 2];
                            mat[0,0] = 1f; mat[1,1] = 1f;
                            BassMix.ChannelSetMatrix(_sfxMixer, mat);
                        }
                    }

                    if (!_engine.Start(MasterOutputMixer))
                    {
                        YargLogger.LogError("Failed to start selected audio backend. Falling back to default device.");
                        _useDecodingBackend = false;
                        _engine?.Dispose();
                        _engine = null;
                    }
                }
            }

            if (!_useDecodingBackend)
            {
                // Fall back to default BASS initialization
                // Configure buffer size for platforms that support it (Linux, Android, Windows CE)
                int bufferMs = YARG.Settings.SettingsManager.SettingContainer.BufferSizeToMilliseconds(desiredBufferSize, desiredSampleRate);
                Bass.DeviceBufferLength = bufferMs;
                
                YargLogger.LogFormatInfo("Falling back to regular BASS playback: {0}Hz, {1}ch, {2} samples buffer ({3}ms)",
                    desiredSampleRate, desiredChannels, desiredBufferSize, bufferMs);
                
                int deviceCount = Bass.DeviceCount;
                YargLogger.LogFormatInfo("Devices found: {0}", deviceCount);

                if (!Bass.Init(-1, desiredSampleRate, DeviceInitFlags.Default | DeviceInitFlags.Latency, IntPtr.Zero))
                {
                    var error = Bass.LastError;
                    if (error == Errors.Already)
                    {
                        YargLogger.LogWarning("BASS is already initialized - attempting to free and retry");
                        Bass.Free();
                        // Retry initialization after freeing
                        if (!Bass.Init(-1, desiredSampleRate, DeviceInitFlags.Default | DeviceInitFlags.Latency, IntPtr.Zero))
                        {
                            YargLogger.LogFormatError("Failed to initialize BASS after retry: {0}!", Bass.LastError);
                            return;
                        }
                    }
                    else
                    {
                        YargLogger.LogFormatError("Failed to initialize BASS: {0}!", error);
                        return;
                    }
                }

                int devPeriod = Bass.GetConfig(Configuration.DevicePeriod);
                var info = Bass.Info;
                PlaybackLatency = info.Latency + Bass.DeviceBufferLength + devPeriod;
                MinimumBufferLength = info.MinBufferLength + Bass.UpdatePeriod;
                MaximumBufferLength = 5000;

                YargLogger.LogInfo("BASS Successfully Initialized");
                YargLogger.LogFormatInfo("BASS: {0} - BASS.FX: {1} - BASS.Mix: {2}", Bass.Version, BassFx.Version, BassMix.Version);
                YargLogger.LogFormatInfo("Update Period: {0}ms. Device Buffer Length: {1}ms. Playback Buffer Length: {2}ms. Device Playback Latency: {3}ms",
                    Bass.UpdatePeriod, Bass.DeviceBufferLength, Bass.PlaybackBufferLength, PlaybackLatency);
                if (!_useDecodingBackend)
                {
                    YargLogger.LogFormatInfo("Current Device: {0}", Bass.GetDeviceInfo(Bass.CurrentDevice).Name);
                }
            }
        }

        protected override void DisposeUnmanagedResources()
        {
            YargLogger.LogInfo("Unloading BASS plugins");
            _engine?.Dispose();
            if (MasterOutputMixer != 0) { Bass.StreamFree(MasterOutputMixer); MasterOutputMixer = 0; }
            if (_sfxMixer != 0) { Bass.StreamFree(_sfxMixer); _sfxMixer = 0; }
            Bass.PluginFree(0);
            Bass.Free();
        }

        private static string GetBassDirectory()
        {
            string pluginDirectory = Path.Combine(Application.dataPath, "Plugins");

            // Locate windows directory
            // Checks if running on 64 bit and sets the path accordingly
#if !UNITY_EDITOR && UNITY_STANDALONE_WIN
#if UNITY_64
			pluginDirectory = Path.Combine(pluginDirectory, "x86_64");
#else
			pluginDirectory = Path.Combine(pluginDirectory, "x86");
#endif
#endif

            // Unity Editor directory, Assets/Plugins/Bass/
#if UNITY_EDITOR
            pluginDirectory = Path.Combine(pluginDirectory, "BassNative");
#endif

            // Editor paths differ to standalone paths, as the project contains platform specific folders
#if UNITY_EDITOR_WIN
            pluginDirectory = Path.Combine(pluginDirectory, "Windows/x86_64");
#elif UNITY_EDITOR_OSX
			pluginDirectory = Path.Combine(pluginDirectory, "Mac");
#elif UNITY_EDITOR_LINUX
			pluginDirectory = Path.Combine(pluginDirectory, "Linux/x86_64");
#endif

            return pluginDirectory;
        }

        private static bool CreateMixerHandle(out int mixerHandle)
        {
            // The float flag allows >0dB signals.
            // Note that the compressor attempts to normalize signals >-2dB, but some mixes will pierce through.
            mixerHandle = BassMix.CreateMixerStream(44100, 2, BassFlags.Float);
            if (mixerHandle == 0)
            {
                YargLogger.LogFormatError("Failed to create mixer: {0}!", Bass.LastError);
                return false;
            }

            int compressorFX = BassHelpers.AddCompressorToChannel(mixerHandle);
            if (compressorFX == 0)
            {
                YargLogger.LogError("Failed to set up compressor for mixer stream!");
            }
            return true;
        }

        internal static bool CreateSourceStream(Stream stream, out int streamHandle)
        {
            // Last flag is new BASS_SAMPLE_NOREORDER flag, which is not in the BassFlags enum,
            // as it was made as part of an update to fix <= 8 channel oggs.
            // https://www.un4seen.com/forum/?topic=20148.msg140872#msg140872
            const BassFlags streamFlags = BassFlags.Prescan | BassFlags.Decode | BassFlags.AsyncFile | (BassFlags) 64;

            streamHandle = Bass.CreateStream(StreamSystem.NoBuffer, streamFlags, new BassStreamProcedures(stream));
            if (streamHandle == 0)
            {
                YargLogger.LogFormatError("Failed to create source stream: {0}!", Bass.LastError);
                return false;
            }
            return true;
        }

        internal static bool GetSpeed(int streamHandle, out float speed)
        {
            if (!Bass.ChannelGetAttribute(streamHandle, ChannelAttribute.Tempo, out float relativeSpeed))
            {
                speed = 0;
                YargLogger.LogFormatError("Failed to get channel speed: {0}", Bass.LastError);
                return false;
            }

            // Turn relative speed into percentage speed
            float percentageSpeed = relativeSpeed + 100;
            speed = percentageSpeed / 100;

            return true;
        }

        internal static void SetSpeed(float speed, int streamHandle, int reverbHandle, bool shiftPitch)
        {
            // Gets relative speed from 100% (so 1.05f = 5% increase)
            float percentageSpeed = speed * 100;
            float relativeSpeed = percentageSpeed - 100;

            if (!Bass.ChannelSetAttribute(streamHandle, ChannelAttribute.Tempo, relativeSpeed) ||
                !Bass.ChannelSetAttribute(reverbHandle, ChannelAttribute.Tempo, relativeSpeed))
            {
                YargLogger.LogFormatError("Failed to set channel speed: {0}!", Bass.LastError);
            }

            if (GlobalAudioHandler.IsChipmunkSpeedup && shiftPitch)
            {
                SetChipmunking(speed, streamHandle, reverbHandle);
            }
        }

#nullable enable
        internal static bool CreateSplitStreams(int sourceStream, int[] channelMap, out StreamHandle? streamHandles, out StreamHandle? reverbHandles)
#nullable disable
        {
            streamHandles = StreamHandle.Create(sourceStream, channelMap);
            if (streamHandles == null)
            {
                reverbHandles = null;
                return false;
            }

            reverbHandles = StreamHandle.Create(sourceStream, channelMap);
            if (reverbHandles == null)
            {
                streamHandles.Dispose();
                return false;
            }
            return true;
        }

        internal static PitchShiftParametersStruct SetPitchParams(SongStem stem, float speed, StreamHandle streamHandles, StreamHandle reverbHandles)
        {
            PitchShiftParametersStruct pitchParams = new(1, 0, GlobalAudioHandler.WHAMMY_FFT_DEFAULT, GlobalAudioHandler.WHAMMY_OVERSAMPLE_DEFAULT);
            // Set whammy pitch bending if enabled
            if (GlobalAudioHandler.UseWhammyFx && AudioHelpers.PitchBendAllowedStems.Contains(stem))
            {
                // Setting the FFT size causes a crash in BASS_FX :/
                // _pitchParams.FFTSize = _manager.Options.WhammyFFTSize;
                pitchParams.OversampleFactor = GlobalAudioHandler.WhammyOversampleFactor;
                if (SetupPitchBend(pitchParams, streamHandles))
                {
                    SetupPitchBend(pitchParams, reverbHandles);
                }
            }

            speed = (float) Math.Clamp(speed, 0.05, 50);
            if (!Mathf.Approximately(speed, 1))
            {
                SetSpeed(speed, streamHandles.Stream, reverbHandles.Stream, true);
            }
            return pitchParams;
        }

        internal static void SetChipmunking(float speed, int streamHandle, int reverbHandle)
        {
            double accurateSemitoneShift = 12 * Math.Log(speed, 2);
            float finalSemitoneShift = (float) Math.Clamp(accurateSemitoneShift, -60, 60);
            if (!Bass.ChannelSetAttribute(streamHandle, ChannelAttribute.Pitch, finalSemitoneShift) ||
                !Bass.ChannelSetAttribute(reverbHandle, ChannelAttribute.Pitch, finalSemitoneShift))
            {
                YargLogger.LogFormatError("Failed to set channel pitch: {0}!", Bass.LastError);
            }
        }

        internal static bool SetupPitchBend(in PitchShiftParametersStruct pitchParams, StreamHandle handles)
        {
            handles.CompressorFX = BassHelpers.FXAddParameters(handles.Stream, EffectType.PitchShift, pitchParams);
            if (handles.CompressorFX == 0)
            {
                YargLogger.LogError("Failed to set up pitch bend for main stream!");
                return false;
            }
            return true;
        }

        internal static double GetLengthInSeconds(int handle)
        {
            long length = Bass.ChannelGetLength(handle);
            if (length < 0)
            {
                YargLogger.LogFormatError("Failed to get channel length in bytes: {0}!", Bass.LastError);
                return -1;
            }

            double seconds = Bass.ChannelBytes2Seconds(handle, length);
            if (seconds < 0)
            {
                YargLogger.LogFormatError("Failed to get channel length in seconds: {0}!", Bass.LastError);
                return -1;
            }

            return seconds;
        }

        private const double BASE = 2;
        private const double FACTOR = BASE - 1;
        internal static double ExponentialVolume(double volume)
        {
            return (Math.Pow(BASE, volume) - 1) / FACTOR;
        }

        internal static double LogarithmicVolume(double volume)
        {
            return Math.Log(FACTOR * volume + 1, BASE);
        }
    }
}