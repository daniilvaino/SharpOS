//
// Copyright (C) 1993-1996 Id Software, Inc.
// Copyright (C) 2019-2020 Nobuaki Tanaka
//
// This program is free software; you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation; either version 2 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//

// SharpOS (step250): ManagedDoom's SilkMusic with OpenAL (DrippyAL) replaced
// by a stream of the kernel's mixer (SharpOS.AppSdk.AudioStream). The MUS and
// MIDI decoders are SilkMusic's, unchanged but for the rate: MeltySynth
// renders at the card's rate (48 kHz) rather than 44.1 kHz, so nothing needs
// resampling; a MUS tick is rate / 140 samples (342 at 48 kHz, 0.25% fast).
// What OpenAL's streaming source did — ask for blocks on a thread — is Pump,
// called once a tic from the game loop: it renders blocks until the stream
// holds about 100 ms. SharpOS cut: the decoder swap across threads (volatile
// fields) — Pump and StartMusic run on the same thread.

using System;
using System.IO;
using ManagedDoom;
using ManagedDoom.Audio;
using MeltySynth;

namespace DoomApp
{
    public sealed class SharpMusic : IMusic, IDisposable
    {
        private const int TargetMs = 100;
        private const int blockLength = 2048;

        private readonly Config config;
        private readonly Wad wad;

        private readonly Synthesizer synthesizer;
        private readonly SharpOS.AppSdk.AudioStream audioStream;
        private readonly int sampleRate;

        private readonly float[] left = new float[blockLength];
        private readonly float[] right = new float[blockLength];
        private readonly short[] samples = new short[2 * blockLength];

        private IDecoder current;
        private Bgm currentBgm;

        private SharpMusic(Config config, GameContent content, string sfPath, SharpOS.AppSdk.AudioStream stream, int sampleRate)
        {
            this.config = config;
            this.wad = content.Wad;
            this.sampleRate = sampleRate;
            audioStream = stream;

            config.audio_musicvolume = Math.Clamp(config.audio_musicvolume, 0, MaxVolume);

            var settings = new SynthesizerSettings(sampleRate);
            settings.BlockSize = sampleRate / 140;
            settings.EnableReverbAndChorus = config.audio_musiceffect;
            synthesizer = new Synthesizer(sfPath, settings);

            currentBgm = Bgm.NONE;
        }

        /// <summary>Music on its own stream, or null without a card, a free stream or the SoundFont.</summary>
        public static SharpMusic TryCreate(Config config, GameContent content, string sfPath)
        {
            int rate = SharpOS.AppSdk.Audio.SampleRate;
            if (rate == 0 || !File.Exists(sfPath)) return null;
            var stream = SharpOS.AppSdk.AudioStream.Open();
            if (stream == null) return null;
            return new SharpMusic(config, content, sfPath, stream, rate);
        }

        public void StartMusic(Bgm bgm, bool loop)
        {
            if (bgm == currentBgm)
            {
                return;
            }

            var lump = "D_" + DoomInfo.BgmNames[(int)bgm].ToString().ToUpper();
            var data = wad.ReadLump(lump);
            current = ReadData(data, loop, sampleRate);
            synthesizer.Reset();
            audioStream.Clear();

            currentBgm = bgm;
        }

        /// <summary>Renders until the stream holds about TargetMs. Once a tic, from the game loop.</summary>
        public void Pump()
        {
            if (current == null)
            {
                return;
            }

            int target = sampleRate * TargetMs / 1000;
            var a = 32768 * (2.0F * config.audio_musicvolume / MaxVolume);
            while (audioStream.QueuedFrames < target)
            {
                current.RenderWaveform(synthesizer, left, right);

                var pos = 0;
                for (var t = 0; t < blockLength; t++)
                {
                    samples[pos++] = Clamp(a * left[t]);
                    samples[pos++] = Clamp(a * right[t]);
                }

                int sent = 0;
                while (sent < samples.Length)
                {
                    int taken = audioStream.Write(new ReadOnlySpan<short>(samples, sent, samples.Length - sent));
                    if (taken == 0) return;            // full: the next tic carries on
                    sent += taken;
                }
            }
        }

        private static short Clamp(float v)
        {
            int s = (int)v;
            return (short)(s < short.MinValue ? short.MinValue : s > short.MaxValue ? short.MaxValue : s);
        }

        private static IDecoder ReadData(byte[] data, bool loop, int sampleRate)
        {
            var isMus = true;
            for (var i = 0; i < MusDecoder.MusHeader.Length; i++)
            {
                if (data[i] != MusDecoder.MusHeader[i])
                {
                    isMus = false;
                }
            }

            if (isMus)
            {
                return new MusDecoder(data, loop, sampleRate);
            }

            var isMidi = true;
            for (var i = 0; i < MidiDecoder.MidiHeader.Length; i++)
            {
                if (data[i] != MidiDecoder.MidiHeader[i])
                {
                    isMidi = false;
                }
            }

            if (isMidi)
            {
                return new MidiDecoder(data, loop);
            }

            throw new Exception("Unknown format!");
        }

        public void Dispose()
        {
            audioStream?.Dispose();
        }

        public int MaxVolume
        {
            get
            {
                return 15;
            }
        }

        public int Volume
        {
            get
            {
                return config.audio_musicvolume;
            }

            set
            {
                config.audio_musicvolume = value;
            }
        }



        private interface IDecoder
        {
            void RenderWaveform(Synthesizer synthesizer, Span<float> left, Span<float> right);
        }



        private class MusDecoder : IDecoder
        {
            public static readonly byte[] MusHeader = new byte[]
            {
                (byte)'M',
                (byte)'U',
                (byte)'S',
                0x1A
            };

            private byte[] data;
            private bool loop;

            private int scoreLength;
            private int scoreStart;
            private int channelCount;
            private int channelCount2;
            private int instrumentCount;
            private int[] instruments;

            private MusEvent[] events;
            private int eventCount;

            private int[] lastVolume;
            private int p;
            private int delay;

            private int blockWrote;

            public MusDecoder(byte[] data, bool loop, int sampleRate)
            {
                CheckHeader(data);

                this.data = data;
                this.loop = loop;

                scoreLength = BitConverter.ToUInt16(data, 4);
                scoreStart = BitConverter.ToUInt16(data, 6);
                channelCount = BitConverter.ToUInt16(data, 8);
                channelCount2 = BitConverter.ToUInt16(data, 10);
                instrumentCount = BitConverter.ToUInt16(data, 12);
                instruments = new int[instrumentCount];
                for (var i = 0; i < instruments.Length; i++)
                {
                    instruments[i] = BitConverter.ToUInt16(data, 16 + 2 * i);
                }

                events = new MusEvent[128];
                for (var i = 0; i < events.Length; i++)
                {
                    events[i] = new MusEvent();
                }
                eventCount = 0;

                lastVolume = new int[16];

                Reset();

                blockWrote = sampleRate / 140;
            }

            private static void CheckHeader(byte[] data)
            {
                for (var p = 0; p < MusHeader.Length; p++)
                {
                    if (data[p] != MusHeader[p])
                    {
                        throw new Exception("Invalid format!");
                    }
                }
            }

            public void RenderWaveform(Synthesizer synthesizer, Span<float> left, Span<float> right)
            {
                var wrote = 0;
                while (wrote < left.Length)
                {
                    if (blockWrote == synthesizer.BlockSize)
                    {
                        ProcessMidiEvents(synthesizer);
                        blockWrote = 0;
                    }

                    var srcRem = synthesizer.BlockSize - blockWrote;
                    var dstRem = left.Length - wrote;
                    var rem = Math.Min(srcRem, dstRem);

                    synthesizer.Render(left.Slice(wrote, rem), right.Slice(wrote, rem));

                    blockWrote += rem;
                    wrote += rem;
                }
            }

            private void ProcessMidiEvents(Synthesizer synthesizer)
            {
                if (delay > 0)
                {
                    delay--;
                }

                if (delay == 0)
                {
                    delay = ReadSingleEventGroup();
                    SendEvents(synthesizer);

                    if (delay == -1)
                    {
                        synthesizer.NoteOffAll(false);

                        if (loop)
                        {
                            Reset();
                        }
                    }
                }
            }

            private void Reset()
            {
                for (var i = 0; i < lastVolume.Length; i++)
                {
                    lastVolume[i] = 0;
                }

                p = scoreStart;

                delay = 0;
            }

            private int ReadSingleEventGroup()
            {
                eventCount = 0;
                while (true)
                {
                    var result = ReadSingleEvent();
                    if (result == ReadResult.EndOfGroup)
                    {
                        break;
                    }
                    else if (result == ReadResult.EndOfFile)
                    {
                        return -1;
                    }
                }

                var time = 0;
                while (true)
                {
                    var value = data[p++];
                    time = time * 128 + (value & 127);
                    if ((value & 128) == 0)
                    {
                        break;
                    }
                }

                return time;
            }

            private ReadResult ReadSingleEvent()
            {
                var channelNumber = data[p] & 0xF;

                if (channelNumber == 15)
                {
                    channelNumber = 9;
                }
                else if (channelNumber >= 9)
                {
                    channelNumber++;
                }

                var eventType = (data[p] & 0x70) >> 4;
                var last = (data[p] >> 7) != 0;

                p++;

                var me = events[eventCount];
                eventCount++;

                switch (eventType)
                {
                    case 0: // RELEASE NOTE
                        me.Type = 0;
                        me.Channel = channelNumber;

                        var releaseNote = data[p++];

                        me.Data1 = releaseNote;
                        me.Data2 = 0;

                        break;

                    case 1: // PLAY NOTE
                        me.Type = 1;
                        me.Channel = channelNumber;

                        var playNote = data[p++];
                        var noteNumber = playNote & 127;
                        var noteVolume = (playNote & 128) != 0 ? data[p++] : -1;

                        me.Data1 = noteNumber;
                        if (noteVolume == -1)
                        {
                            me.Data2 = lastVolume[channelNumber];
                        }
                        else
                        {
                            me.Data2 = noteVolume;
                            lastVolume[channelNumber] = noteVolume;
                        }

                        break;

                    case 2: // PITCH WHEEL
                        me.Type = 2;
                        me.Channel = channelNumber;

                        var pitchWheel = data[p++];

                        var pw2 = (pitchWheel << 7) / 2;
                        var pw1 = pw2 & 127;
                        pw2 >>= 7;
                        me.Data1 = pw1;
                        me.Data2 = pw2;

                        break;

                    case 3: // SYSTEM EVENT
                        me.Type = 3;
                        me.Channel = channelNumber;

                        var systemEvent = data[p++];
                        me.Data1 = systemEvent;
                        me.Data2 = 0;

                        break;

                    case 4: // CONTROL CHANGE
                        me.Type = 4;
                        me.Channel = channelNumber;

                        var controllerNumber = data[p++];
                        var controllerValue = data[p++];

                        me.Data1 = controllerNumber;
                        me.Data2 = controllerValue;

                        break;

                    case 6: // END OF FILE
                        return ReadResult.EndOfFile;

                    default:
                        throw new Exception("Unknown event type!");
                }

                if (last)
                {
                    return ReadResult.EndOfGroup;
                }
                else
                {
                    return ReadResult.Ongoing;
                }
            }

            private void SendEvents(Synthesizer synthesizer)
            {
                for (var i = 0; i < eventCount; i++)
                {
                    var me = events[i];
                    switch (me.Type)
                    {
                        case 0: // RELEASE NOTE
                            synthesizer.NoteOff(me.Channel, me.Data1);
                            break;

                        case 1: // PLAY NOTE
                            synthesizer.NoteOn(me.Channel, me.Data1, me.Data2);
                            break;

                        case 2: // PITCH WHEEL
                            synthesizer.ProcessMidiMessage(me.Channel, 0xE0, me.Data1, me.Data2);
                            break;

                        case 3: // SYSTEM EVENT
                            switch (me.Data1)
                            {
                                case 11: // ALL NOTES OFF
                                    synthesizer.NoteOffAll(me.Channel, false);
                                    break;

                                case 14: // RESET ALL CONTROLS
                                    synthesizer.ResetAllControllers(me.Channel);
                                    break;
                            }
                            break;

                        case 4: // CONTROL CHANGE
                            switch (me.Data1)
                            {
                                case 0: // PROGRAM CHANGE
                                    synthesizer.ProcessMidiMessage(me.Channel, 0xC0, me.Data2, 0);
                                    break;

                                case 1: // BANK SELECTION
                                    synthesizer.ProcessMidiMessage(me.Channel, 0xB0, 0x00, me.Data2);
                                    break;

                                case 2: // MODULATION
                                    synthesizer.ProcessMidiMessage(me.Channel, 0xB0, 0x01, me.Data2);
                                    break;

                                case 3: // VOLUME
                                    synthesizer.ProcessMidiMessage(me.Channel, 0xB0, 0x07, me.Data2);
                                    break;

                                case 4: // PAN
                                    synthesizer.ProcessMidiMessage(me.Channel, 0xB0, 0x0A, me.Data2);
                                    break;

                                case 5: // EXPRESSION
                                    synthesizer.ProcessMidiMessage(me.Channel, 0xB0, 0x0B, me.Data2);
                                    break;

                                case 6: // REVERB
                                    synthesizer.ProcessMidiMessage(me.Channel, 0xB0, 0x5B, me.Data2);
                                    break;

                                case 7: // CHORUS
                                    synthesizer.ProcessMidiMessage(me.Channel, 0xB0, 0x5D, me.Data2);
                                    break;

                                case 8: // PEDAL
                                    synthesizer.ProcessMidiMessage(me.Channel, 0xB0, 0x40, me.Data2);
                                    break;
                            }
                            break;
                    }
                }
            }

            private class MusEvent
            {
                public int Type;
                public int Channel;
                public int Data1;
                public int Data2;
            }

            private enum ReadResult
            {
                Ongoing,
                EndOfGroup,
                EndOfFile
            }
        }



        private class MidiDecoder : IDecoder
        {
            public static readonly byte[] MidiHeader = new byte[]
            {
                (byte)'M',
                (byte)'T',
                (byte)'h',
                (byte)'d'
            };

            private MidiFile midi;
            private MidiFileSequencer sequencer;

            private bool loop;

            public MidiDecoder(byte[] data, bool loop)
            {
                midi = new MidiFile(new MemoryStream(data));

                this.loop = loop;
            }

            public void RenderWaveform(Synthesizer synthesizer, Span<float> left, Span<float> right)
            {
                if (sequencer == null)
                {
                    sequencer = new MidiFileSequencer(synthesizer);
                    sequencer.Play(midi, loop);
                }

                sequencer.Render(left, right);
            }
        }
    }
}
