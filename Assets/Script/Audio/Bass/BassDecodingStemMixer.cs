using System;
using System.IO;
using ManagedBass;
using ManagedBass.Mix;
using YARG.Core.Audio;
using YARG.Core.Logging;
using YARG.Core.Song;

namespace YARG.Audio.BASS
{
    // A decoding variant of the stem mixer that mixes stems into a per-song decoding mixer,
    // which is then added to the global master decoding mixer owned by BassAudioManager.
    public sealed class BassDecodingStemMixer : StemMixer
    {
        private new readonly BassAudioManager _manager;
        private readonly int _songMixerHandle; // decoding 2ch song mixer
        private readonly int _sourceStream;

        private StreamHandle _mainHandle;
        private float _speed;

        public override event Action SongEnd
        {
            add { _songEnd += value; }
            remove { _songEnd -= value; }
        }

        internal BassDecodingStemMixer(string name, BassAudioManager manager, float speed, double volume, int sourceStream, bool clampStemVolume)
            : base(name, manager, clampStemVolume)
        {
            _manager = manager;
            _sourceStream = sourceStream;
            _speed = speed;

            // Create per-song decoding mixer
            _songMixerHandle = BassMix.CreateMixerStream(44100, 2, BassFlags.Float | BassFlags.Decode);
            if (_songMixerHandle == 0)
            {
                YargLogger.LogFormatError("Failed to create decoding song mixer: {0}!", Bass.LastError);
            }

            // Add the song mixer to the master output mixer so it is actually heard
            if (!BassMix.MixerAddChannel(_manager.MasterOutputMixer, _songMixerHandle, BassFlags.Default))
            {
                YargLogger.LogFormatError("Failed to add song mixer to master: {0}!", Bass.LastError);
            }

            SetVolume_Internal(volume);
        }

        protected override int Play_Internal(bool restartBuffer)
        {
            // Decoding path is pull-driven (WASAPI/ASIO): no play call needed; keep return OK
            return 0;
        }

        protected override void FadeIn_Internal(double maxVolume, double duration)
        {
            float scaled = (float) BassAudioManager.ExponentialVolume(maxVolume);
            Bass.ChannelSlideAttribute(_songMixerHandle, ChannelAttribute.Volume, scaled, (int) (duration * 1000));
        }

        protected override void FadeOut_Internal(double duration)
        {
            Bass.ChannelSlideAttribute(_songMixerHandle, ChannelAttribute.Volume, 0, (int) (duration * 1000));
        }

        protected override int Pause_Internal()
        {
            // Nothing to pause in decoding path
            return 0;
        }

        protected override double GetPosition_Internal()
        {
            long position = Bass.ChannelGetPosition(_mainHandle.Stream);
            if (position < 0)
            {
                YargLogger.LogFormatError("Failed to get position: {0}", Bass.LastError);
                return -1;
            }
            double seconds = Bass.ChannelBytes2Seconds(_mainHandle.Stream, position);
            return seconds;
        }

        protected override double GetVolume_Internal()
        {
            if (!Bass.ChannelGetAttribute(_songMixerHandle, ChannelAttribute.Volume, out float volume))
            {
                YargLogger.LogFormatError("Failed to get volume: {0}", Bass.LastError);
            }
            return BassAudioManager.LogarithmicVolume(volume);
        }

        protected override void SetPosition_Internal(double position)
        {
            if (_channels.Count == 0)
            {
                long bytes = Bass.ChannelSeconds2Bytes(_mainHandle.Stream, position);
                if (bytes >= 0)
                {
                    BassMix.ChannelSetPosition(_mainHandle.Stream, bytes, PositionFlags.Bytes | PositionFlags.MixerReset);
                }
            }
            else
            {
                if (_sourceStream != 0)
                {
                    BassMix.SplitStreamReset(_sourceStream);
                }
                foreach (var channel in _channels)
                {
                    channel.SetPosition(position);
                }
            }
        }

        protected override void SetVolume_Internal(double volume)
        {
            volume = BassAudioManager.ExponentialVolume(volume);
            Bass.ChannelSetAttribute(_songMixerHandle, ChannelAttribute.Volume, volume);
        }

        protected override int GetFFTData_Internal(float[] buffer, int fftSize, bool complex)
        {
            int flags = 0;
            switch (1 << fftSize)
            {
                case 256: flags |= (int) DataFlags.FFT256; break;
                case 512: flags |= (int) DataFlags.FFT512; break;
                case 1024: flags |= (int) DataFlags.FFT1024; break;
                case 2048: flags |= (int) DataFlags.FFT2048; break;
                case 4096: flags |= (int) DataFlags.FFT4096; break;
                default: return -1;
            }
            if (complex) flags |= (int) DataFlags.FFTComplex;

            int data = Bass.ChannelGetData(_songMixerHandle, buffer, flags);
            return data < 0 ? (int) Bass.LastError : data;
        }

        protected override int GetSampleData_Internal(float[] buffer)
        {
            int data = Bass.ChannelGetData(_songMixerHandle, buffer, (buffer.Length * 4) | (int) DataFlags.Float);
            return data < 0 ? (int) Bass.LastError : data;
        }

        protected override int GetLevel_Internal(float[] level)
        {
            bool status = Bass.ChannelGetLevel(_songMixerHandle, level, 0.2f, LevelRetrievalFlags.Mono | LevelRetrievalFlags.RMS);
            return status ? 0 : (int) ManagedBass.Errors.OK;
        }

        protected override void SetSpeed_Internal(float speed, bool shiftPitch)
        {
            speed = (float) Math.Clamp(speed, 0.05, 50);
            if (_speed == speed) return;
            _speed = speed;
            foreach (var channel in _channels)
            {
                channel.SetSpeed(speed, shiftPitch);
            }
        }

        protected override bool AddChannel_Internal(SongStem stem)
        {
            _mainHandle = StreamHandle.Create(_sourceStream, null);
            if (_mainHandle == null) return false;

            if (!BassMix.MixerAddChannel(_songMixerHandle, _mainHandle.Stream, BassFlags.Default))
            {
                YargLogger.LogFormatError("Failed to add channel {stem} to song mixer: {0}!", Bass.LastError);
                return false;
            }
            _length = BassAudioManager.GetLengthInSeconds(_sourceStream);
            return true;
        }

        protected override bool AddChannel_Internal(SongStem stem, Stream stream)
        {
            if (!BassAudioManager.CreateSourceStream(stream, out int sourceStream)) return false;

            if (!BassAudioManager.CreateSplitStreams(sourceStream, null, out var streamHandles, out var reverbHandles))
            {
                return false;
            }

            if (!BassMix.MixerAddChannel(_songMixerHandle, streamHandles.Stream, BassFlags.Default) ||
                !BassMix.MixerAddChannel(_songMixerHandle, reverbHandles.Stream, BassFlags.Default))
            {
                YargLogger.LogFormatError("Failed to add channel {stem} to song mixer: {0}!", Bass.LastError);
                return false;
            }

            CreateChannel(stem, sourceStream, streamHandles, reverbHandles);
            return true;
        }

        protected override bool AddChannel_Internal(SongStem stem, int[] indices, float[] panning)
        {
            if (!BassAudioManager.CreateSplitStreams(_sourceStream, indices, out var streamHandles, out var reverbHandles))
            {
                return false;
            }

            if (!BassMix.MixerAddChannel(_songMixerHandle, streamHandles.Stream, BassFlags.MixerChanMatrix) ||
                !BassMix.MixerAddChannel(_songMixerHandle, reverbHandles.Stream, BassFlags.MixerChanMatrix))
            {
                YargLogger.LogFormatError("Failed to add channel {stem} to song mixer: {0}!", Bass.LastError);
                return false;
            }

            int outCh = Bass.ChannelGetInfo(_manager.MasterOutputMixer).Channels;
            float[,] volumeMatrix = new float[outCh, indices.Length];

            int pair = SelectOutputPair(stem, outCh);
            int LEFT_PAN = pair * 2 + 0;
            int RIGHT_PAN = pair * 2 + 1;
            LEFT_PAN = Math.Min(LEFT_PAN, outCh - 1);
            RIGHT_PAN = Math.Min(RIGHT_PAN, outCh - 1);

            for (int i = 0; i < indices.Length; ++i) volumeMatrix[LEFT_PAN, i] = panning[2 * i];
            for (int i = 0; i < indices.Length; ++i) volumeMatrix[RIGHT_PAN, i] = panning[2 * i + 1];

            if (!BassMix.ChannelSetMatrix(streamHandles.Stream, volumeMatrix) ||
                !BassMix.ChannelSetMatrix(reverbHandles.Stream, volumeMatrix))
            {
                YargLogger.LogFormatError("Failed to set {stem} matrices: {0}!", Bass.LastError);
                return false;
            }

            CreateChannel(stem, 0, streamHandles, reverbHandles);
            return true;
        }

        protected override bool RemoveChannel_Internal(SongStem stemToRemove)
        {
            int index = _channels.FindIndex(channel => channel.Stem == stemToRemove);
            if (index == -1) return false;
            _channels[index].Dispose();
            _channels.RemoveAt(index);
            return true;
        }

        protected override void ToggleBuffer_Internal(bool enable) { }
        protected override void SetBufferLength_Internal(int length) { }

        protected override void DisposeUnmanagedResources()
        {
            if (_songMixerHandle != 0)
            {
                Bass.StreamFree(_songMixerHandle);
            }
            if (_sourceStream != 0)
            {
                Bass.StreamFree(_sourceStream);
            }
        }

        private static int SelectOutputPair(SongStem stem, int outputChannels)
        {
            // Simple multi-channel routing presets as described in the design document
            // Stereo (2ch): all stems on pair 0 (channels 0/1)
            // Quad (4ch): Crowd -> pair 1 (channels 2/3), others -> pair 0 (channels 0/1)
            // SixChannel (6ch): Drums -> pair 1 (channels 2/3), Crowd -> pair 2 (channels 4/5), others -> pair 0 (channels 0/1)
            
            if (outputChannels <= 2)
            {
                return 0; // Stereo: everything goes to pair 0
            }
            else if (outputChannels == 4)
            {
                // Quad routing
                return stem == SongStem.Crowd ? 1 : 0;
            }
            else if (outputChannels >= 6)
            {
                // 6+ channel routing
                switch (stem)
                {
                    case SongStem.Drums:
                    case SongStem.Drums1:
                    case SongStem.Drums2:
                    case SongStem.Drums3:
                    case SongStem.Drums4:
                        return 1; // Drums to pair 1 (channels 2/3)
                    case SongStem.Crowd:
                        return 2; // Crowd to pair 2 (channels 4/5)
                    default:
                        return 0; // Everything else to pair 0 (channels 0/1)
                }
            }
            
            return 0; // Default to pair 0
        }

        private void CreateChannel(SongStem stem, int sourceStream, StreamHandle streamHandles, StreamHandle reverbHandles)
        {
            var pitchparams = BassAudioManager.SetPitchParams(stem, _speed, streamHandles, reverbHandles);
            var stemchannel = new BassStemChannel(_manager, stem, _clampStemVolume, sourceStream, pitchparams, streamHandles, reverbHandles);

            double length = BassAudioManager.GetLengthInSeconds(streamHandles.Stream);
            if (_mainHandle == null || length > _length)
            {
                _mainHandle = streamHandles;
                _length = length;
            }

            _channels.Add(stemchannel);
        }
    }
}
