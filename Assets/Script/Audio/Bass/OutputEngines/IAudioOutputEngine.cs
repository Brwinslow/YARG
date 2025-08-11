using System;

namespace YARG.Audio.BASS.Output
{
    public enum AudioBackend
    {
        Auto,
        WasapiShared,
        WasapiExclusive,
        Asio,
        DefaultDevice
    }

    public interface IAudioOutputEngine : IDisposable
    {
        AudioBackend Backend { get; }
        int SampleRate { get; }
        int Channels { get; }
        int BufferSizeFrames { get; }
        int LatencyMs { get; }

        bool Init(int deviceId, int sampleRate, int channels, int bufferSizeFrames);
        bool Start(int mixerHandle);
        void Stop();
        void SetMasterVolume(double volume);
        string GetCurrentDeviceName();
    }
}
