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

// SharpOS (step250): ManagedDoom's SilkSound with OpenAL (DrippyAL) replaced
// by a software mixer onto SharpOS.AppSdk.Audio. The game side — channels,
// priorities and their decay, distance attenuation, random pitch, the UI
// channel — is SilkSound's, line for line where it could be. What OpenAL did
// is here: each channel steps through the lump's 8-bit samples at
// rate x pitch / card rate (linear interpolation), a source's direction
// becomes left/right gains, and every Update tops the kernel's queue up to
// about 70 ms (two tics at 35 Hz). SharpOS cut: DateTime-based throttling of
// Update — the mixer has to run every tic.

using System;
using ManagedDoom;
using ManagedDoom.Audio;

namespace DoomApp
{
    public sealed class SharpSound : ISound
    {
        private const int channelCount = 8;

        private static readonly float fastDecay = (float)Math.Pow(0.5, 1.0 / (35 / 5));
        private static readonly float slowDecay = (float)Math.Pow(0.5, 1.0 / 35);

        private const float clipDist = 1200;
        private const float closeDist = 160;
        private const float attenuator = clipDist - closeDist;

        // How much sound to keep queued ahead of the card.
        private const int TargetMs = 70;

        private readonly Config config;

        private readonly Clip[] buffers;
        private readonly float[] amplitudes;

        private readonly DoomRandom random;

        private readonly Voice[] channels;
        private readonly ChannelInfo[] infos;

        private readonly Voice uiChannel;
        private Sfx uiReserved;

        private Mobj listener;

        private float masterVolumeDecay;

        private readonly int cardRate;
        private short[] mixBuffer = new short[2 * 4096];

        public SharpSound(Config config, GameContent content)
        {
            this.config = config;
            cardRate = SharpOS.AppSdk.Audio.SampleRate;

            config.audio_soundvolume = Math.Clamp(config.audio_soundvolume, 0, MaxVolume);

            buffers = new Clip[DoomInfo.SfxNames.Length];
            amplitudes = new float[DoomInfo.SfxNames.Length];

            if (config.audio_randompitch)
            {
                random = new DoomRandom();
            }

            for (var i = 0; i < DoomInfo.SfxNames.Length; i++)
            {
                var name = "DS" + DoomInfo.SfxNames[i].ToString().ToUpper();
                var lump = content.Wad.GetLumpNumber(name);
                if (lump == -1)
                {
                    continue;
                }

                var data = content.Wad.ReadLump(name);
                if (GetSamples(data, out int sampleRate, out int offset, out int sampleCount))
                {
                    buffers[i] = new Clip { Data = data, Offset = offset, Count = sampleCount, Rate = sampleRate };
                    amplitudes[i] = GetAmplitude(data, offset, sampleRate, sampleCount);
                }
            }

            channels = new Voice[channelCount];
            infos = new ChannelInfo[channelCount];
            for (var i = 0; i < channels.Length; i++)
            {
                channels[i] = new Voice();
                infos[i] = new ChannelInfo();
            }

            uiChannel = new Voice();
            uiReserved = Sfx.NONE;

            masterVolumeDecay = (float)config.audio_soundvolume / MaxVolume;
        }

        // SilkSound.GetSamples, returning where the samples are rather than a span.
        private static bool GetSamples(byte[] data, out int sampleRate, out int offset, out int sampleCount)
        {
            sampleRate = -1;
            offset = 8;
            sampleCount = -1;
            if (data.Length < 8)
            {
                return false;
            }

            sampleRate = BitConverter.ToUInt16(data, 2);
            sampleCount = BitConverter.ToInt32(data, 4);

            if (ContainsDmxPadding(data))
            {
                offset += 16;
                sampleCount -= 32;
            }

            sampleCount = Math.Min(sampleCount, data.Length - offset);
            return sampleCount > 0 && sampleRate > 0;
        }

        // Check if the data contains pad bytes.
        // If the first and last 16 samples are the same,
        // the data should contain pad bytes.
        // https://doomwiki.org/wiki/Sound
        private static bool ContainsDmxPadding(byte[] data)
        {
            var sampleCount = BitConverter.ToInt32(data, 4);
            if (sampleCount < 32 || 8 + sampleCount > data.Length)
            {
                return false;
            }
            else
            {
                var first = data[8];
                for (var i = 1; i < 16; i++)
                {
                    if (data[8 + i] != first)
                    {
                        return false;
                    }
                }

                var last = data[8 + sampleCount - 1];
                for (var i = 1; i < 16; i++)
                {
                    if (data[8 + sampleCount - i - 1] != last)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static float GetAmplitude(byte[] data, int offset, int sampleRate, int sampleCount)
        {
            var max = 0;
            if (sampleCount > 0)
            {
                var count = Math.Min(sampleRate / 5, sampleCount);
                for (var t = 0; t < count; t++)
                {
                    var a = data[offset + t] - 128;
                    if (a < 0)
                    {
                        a = -a;
                    }
                    if (a > max)
                    {
                        max = a;
                    }
                }
            }
            return (float)max / 128;
        }

        public void SetListener(Mobj listener)
        {
            this.listener = listener;
        }

        public void Update()
        {
            for (var i = 0; i < infos.Length; i++)
            {
                var info = infos[i];
                var channel = channels[i];

                if (info.Playing != Sfx.NONE)
                {
                    if (channel.Playing)
                    {
                        if (info.Type == SfxType.Diffuse)
                        {
                            info.Priority *= slowDecay;
                        }
                        else
                        {
                            info.Priority *= fastDecay;
                        }
                        SetParam(channel, info);
                    }
                    else
                    {
                        info.Playing = Sfx.NONE;
                        if (info.Reserved == Sfx.NONE)
                        {
                            info.Source = null;
                        }
                    }
                }

                if (info.Reserved != Sfx.NONE)
                {
                    channel.Start(buffers[(int)info.Reserved], GetPitch(info.Type, info.Reserved), cardRate);
                    SetParam(channel, info);
                    info.Playing = info.Reserved;
                    info.Reserved = Sfx.NONE;
                }
            }

            if (uiReserved != Sfx.NONE)
            {
                uiChannel.Start(buffers[(int)uiReserved], 1.0F, cardRate);
                uiChannel.SetGains(masterVolumeDecay, 0);
                uiReserved = Sfx.NONE;
            }

            Mix();
        }

        // Tops the kernel's queue up to TargetMs of sound.
        private void Mix()
        {
            int wanted = cardRate * TargetMs / 1000 - SharpOS.AppSdk.Audio.QueuedFrames;
            if (wanted <= 0)
            {
                return;
            }
            if (mixBuffer.Length < 2 * wanted)
            {
                mixBuffer = new short[2 * wanted];
            }

            for (int f = 0; f < wanted; f++)
            {
                float left = 0, right = 0;
                for (int c = 0; c < channels.Length; c++)
                {
                    channels[c].Next(ref left, ref right);
                }
                uiChannel.Next(ref left, ref right);
                mixBuffer[2 * f] = Clamp(left);
                mixBuffer[2 * f + 1] = Clamp(right);
            }
            SharpOS.AppSdk.Audio.Write(new ReadOnlySpan<short>(mixBuffer, 0, 2 * wanted));
        }

        private static short Clamp(float v)
        {
            int s = (int)(v * 32767F);
            return (short)(s > 32767 ? 32767 : s < -32768 ? -32768 : s);
        }

        public void StartSound(Sfx sfx)
        {
            if (buffers[(int)sfx] == null)
            {
                return;
            }

            uiReserved = sfx;
        }

        public void StartSound(Mobj mobj, Sfx sfx, SfxType type)
        {
            StartSound(mobj, sfx, type, 100);
        }

        public void StartSound(Mobj mobj, Sfx sfx, SfxType type, int volume)
        {
            if (buffers[(int)sfx] == null || listener == null)
            {
                return;
            }

            var x = (mobj.X - listener.X).ToFloat();
            var y = (mobj.Y - listener.Y).ToFloat();
            var dist = MathF.Sqrt(x * x + y * y);

            float priority;
            if (type == SfxType.Diffuse)
            {
                priority = volume;
            }
            else
            {
                priority = amplitudes[(int)sfx] * GetDistanceDecay(dist) * volume;
            }

            for (var i = 0; i < infos.Length; i++)
            {
                var info = infos[i];
                if (info.Source == mobj && info.Type == type)
                {
                    info.Reserved = sfx;
                    info.Priority = priority;
                    info.Volume = volume;
                    return;
                }
            }

            for (var i = 0; i < infos.Length; i++)
            {
                var info = infos[i];
                if (info.Reserved == Sfx.NONE && info.Playing == Sfx.NONE)
                {
                    info.Reserved = sfx;
                    info.Priority = priority;
                    info.Source = mobj;
                    info.Type = type;
                    info.Volume = volume;
                    return;
                }
            }

            var minPriority = float.MaxValue;
            var minChannel = -1;
            for (var i = 0; i < infos.Length; i++)
            {
                var info = infos[i];
                if (info.Priority < minPriority)
                {
                    minPriority = info.Priority;
                    minChannel = i;
                }
            }
            if (priority >= minPriority)
            {
                var info = infos[minChannel];
                info.Reserved = sfx;
                info.Priority = priority;
                info.Source = mobj;
                info.Type = type;
                info.Volume = volume;
            }
        }

        public void StopSound(Mobj mobj)
        {
            for (var i = 0; i < infos.Length; i++)
            {
                var info = infos[i];
                if (info.Source == mobj)
                {
                    info.LastX = info.Source.X;
                    info.LastY = info.Source.Y;
                    info.Source = null;
                    info.Volume /= 5;
                }
            }
        }

        public void Reset()
        {
            if (random != null)
            {
                random.Clear();
            }

            for (var i = 0; i < infos.Length; i++)
            {
                channels[i].Stop();
                infos[i].Clear();
            }

            listener = null;
        }

        // SilkSound pauses the channels with more than 200 ms left; here every
        // playing channel holds its place.
        public void Pause()
        {
            for (var i = 0; i < channels.Length; i++)
            {
                channels[i].Paused = true;
            }
        }

        public void Resume()
        {
            for (var i = 0; i < channels.Length; i++)
            {
                channels[i].Paused = false;
            }
        }

        // SilkSound.SetParam: OpenAL's listener-relative position becomes the
        // pan — x = -sin(angle) is how far right of the listener the source is.
        private void SetParam(Voice sound, ChannelInfo info)
        {
            if (info.Type == SfxType.Diffuse)
            {
                sound.SetGains(0.01F * masterVolumeDecay * info.Volume, 0);
            }
            else
            {
                Fixed sourceX;
                Fixed sourceY;
                if (info.Source == null)
                {
                    sourceX = info.LastX;
                    sourceY = info.LastY;
                }
                else
                {
                    sourceX = info.Source.X;
                    sourceY = info.Source.Y;
                }

                if (listener == null)
                {
                    return;
                }

                var x = (sourceX - listener.X).ToFloat();
                var y = (sourceY - listener.Y).ToFloat();

                if (Math.Abs(x) < 16 && Math.Abs(y) < 16)
                {
                    sound.SetGains(0.01F * masterVolumeDecay * info.Volume, 0);
                }
                else
                {
                    var dist = MathF.Sqrt(x * x + y * y);
                    var angle = MathF.Atan2(y, x) - (float)listener.Angle.ToRadian();
                    sound.SetGains(0.01F * masterVolumeDecay * GetDistanceDecay(dist) * info.Volume, -MathF.Sin(angle));
                }
            }
        }

        private float GetDistanceDecay(float dist)
        {
            if (dist < closeDist)
            {
                return 1F;
            }
            else
            {
                return Math.Max((clipDist - dist) / attenuator, 0F);
            }
        }

        private float GetPitch(SfxType type, Sfx sfx)
        {
            if (random != null)
            {
                if (sfx == Sfx.ITEMUP || sfx == Sfx.TINK || sfx == Sfx.RADIO)
                {
                    return 1.0F;
                }
                else if (type == SfxType.Voice)
                {
                    return 1.0F + 0.075F * (random.Next() - 128) / 128;
                }
                else
                {
                    return 1.0F + 0.025F * (random.Next() - 128) / 128;
                }
            }
            else
            {
                return 1.0F;
            }
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
                return config.audio_soundvolume;
            }

            set
            {
                config.audio_soundvolume = value;
                masterVolumeDecay = (float)config.audio_soundvolume / MaxVolume;
            }
        }

        // A lump's samples: unsigned 8-bit mono at Rate.
        private sealed class Clip
        {
            public byte[] Data;
            public int Offset;
            public int Count;
            public int Rate;
        }

        // What an OpenAL source was: a clip, a read position, a step, gains.
        private sealed class Voice
        {
            private Clip clip;
            private double position;
            private double step;
            private float left, right;
            public bool Paused;

            public bool Playing => clip != null;

            public void Start(Clip clip, float pitch, int cardRate)
            {
                this.clip = clip;
                position = 0;
                step = clip == null ? 0 : clip.Rate * (double)pitch / cardRate;
                Paused = false;
            }

            public void Stop() => clip = null;

            // Linear pan: centred is full volume on both sides, a source to
            // the right fades the left channel out.
            public void SetGains(float volume, float pan)
            {
                left = volume * Math.Min(1F, 1F - pan);
                right = volume * Math.Min(1F, 1F + pan);
            }

            public void Next(ref float mixLeft, ref float mixRight)
            {
                if (clip == null || Paused)
                {
                    return;
                }
                int i = (int)position;
                if (i >= clip.Count - 1)
                {
                    clip = null;
                    return;
                }
                double frac = position - i;
                byte[] d = clip.Data;
                float a = d[clip.Offset + i] - 128, b = d[clip.Offset + i + 1] - 128;
                float s = (float)((a + (b - a) * frac) / 128.0);
                mixLeft += s * left;
                mixRight += s * right;
                position += step;
            }
        }

        private class ChannelInfo
        {
            public Sfx Reserved;
            public Sfx Playing;
            public float Priority;

            public Mobj Source;
            public SfxType Type;
            public int Volume;
            public Fixed LastX;
            public Fixed LastY;

            public void Clear()
            {
                Reserved = Sfx.NONE;
                Playing = Sfx.NONE;
                Priority = 0;
                Source = null;
                Type = 0;
                Volume = 0;
                LastX = Fixed.Zero;
                LastY = Fixed.Zero;
            }
        }
    }
}
