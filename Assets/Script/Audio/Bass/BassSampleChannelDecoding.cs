using System.IO;
using ManagedBass;
using ManagedBass.Mix;
using YARG.Core.Audio;
using YARG.Core.Logging;

namespace YARG.Audio.BASS
{
    // Decoding SFX channel: on Play(), create a decoding stream for the sample and add it to the SFX mixer with AutoFree
    public sealed class BassSampleChannelDecoding : SampleChannel
    {
        private readonly int _sfxMixer;
        private readonly double _baseVolume;

        public BassSampleChannelDecoding(SfxSample sample, string path, int sfxMixer, double baseVolume)
            : base(sample, path, playbackCount: 0)
        {
            _sfxMixer = sfxMixer;
            _baseVolume = baseVolume;
        }

        protected override void Play_Internal()
        {
            int stream = Bass.CreateStream(_path, 0, 0, BassFlags.Decode);
            if (stream == 0)
            {
                YargLogger.LogFormatError("Failed to create decoding stream for SFX {0}: {1}", Sample, Bass.LastError);
                return;
            }

            // Apply base volume on the stream
            Bass.ChannelSetAttribute(stream, ChannelAttribute.Volume, _baseVolume);

            if (!ManagedBass.Mix.BassMix.MixerAddChannel(_sfxMixer, stream, BassFlags.Decode | BassFlags.AutoFree))
            {
                YargLogger.LogFormatError("Failed to add SFX stream to mixer: {0}", Bass.LastError);
                Bass.StreamFree(stream);
            }
        }

        protected override void SetVolume_Internal(double volume)
        {
            // Volume applied per-instance on Play; no persistent channel to set here
        }

        protected override void DisposeUnmanagedResources()
        {
            // No persistent resources to free
        }
    }
}
