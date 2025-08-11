using System.IO;
using ManagedBass;
using ManagedBass.Mix;
using YARG.Core.Audio;
using YARG.Core.Logging;

namespace YARG.Audio.BASS
{
    public sealed class BassDrumSampleChannelDecoding : DrumSampleChannel
    {
        private readonly int _sfxMixer;

        public BassDrumSampleChannelDecoding(DrumSfxSample sample, string path, int sfxMixer)
            : base(sample, path, playbackCount: 0)
        {
            _sfxMixer = sfxMixer;
        }

        protected override void Play_Internal()
        {
            int stream = Bass.CreateStream(_path, 0, 0, BassFlags.Decode);
            if (stream == 0)
            {
                YargLogger.LogFormatError("Failed to create decoding stream for Drum SFX {0}: {1}", Sample, Bass.LastError);
                return;
            }
            if (!ManagedBass.Mix.BassMix.MixerAddChannel(_sfxMixer, stream, BassFlags.Decode | BassFlags.AutoFree))
            {
                YargLogger.LogFormatError("Failed to add Drum SFX stream to mixer: {0}", Bass.LastError);
                Bass.StreamFree(stream);
            }
        }

        protected override void SetVolume_Internal(double volume)
        {
            // Applied per-instance during play if needed
        }

        protected override void DisposeUnmanagedResources()
        {
        }
    }
}
