// ScheduleIArcade.Doom - embedded host for Managed Doom (GPL-2.0, see ../LICENSE and ../NOTICE.md).
// Copyright (C) 2026 ScheduleIArcade contributors. Sample decoding and distance/pan maths adapted
// from ManagedDoom.Silk.SilkSound by Nobuaki Tanaka.
//
// This program is free software; you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation; either version 2 of the
// License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY.

using System;
using System.Collections.Generic;
using ManagedDoom;
using ManagedDoom.Audio;

namespace ScheduleIArcade.Doom
{
    /// <summary>
    /// Device-less ISound. Sound effects are decoded from the WAD's DMX lumps once, and every
    /// StartSound becomes a play event (sfx, gain 0..1, stereo pan -1..1) that the Unity host
    /// plays through the arcade's own volume path. Doom never touches an audio device itself.
    /// </summary>
    internal sealed class EmbeddedSound : ISound
    {
        private const float ClipDist = 1200f;
        private const float CloseDist = 160f;
        private const int MaxQueued = 32;

        private readonly Config config;
        private readonly float[][] samples;
        private readonly int[] sampleRates;
        private readonly Queue<SoundEvent> queue = new Queue<SoundEvent>();
        private Mobj listener;
        private float masterVolume;

        public EmbeddedSound(Config config, GameContent content)
        {
            this.config = config;
            config.audio_soundvolume = Math.Clamp(config.audio_soundvolume, 0, MaxVolume);
            masterVolume = (float)config.audio_soundvolume / MaxVolume;

            samples = new float[DoomInfo.SfxNames.Length][];
            sampleRates = new int[DoomInfo.SfxNames.Length];
            for (var i = 0; i < DoomInfo.SfxNames.Length; i++)
            {
                var name = "DS" + DoomInfo.SfxNames[i].ToString().ToUpperInvariant();
                if (content.Wad.GetLumpNumber(name) == -1)
                {
                    continue;
                }
                samples[i] = Decode(content.Wad.ReadLump(name), out sampleRates[i]);
            }
        }

        public struct SoundEvent
        {
            public int Sfx;
            public float Gain;
            public float Pan;
        }

        public int SfxCount => samples.Length;

        public float[] GetSamples(int sfx, out int sampleRate)
        {
            sampleRate = sfx >= 0 && sfx < sampleRates.Length ? sampleRates[sfx] : 0;
            return sfx >= 0 && sfx < samples.Length ? samples[sfx] : null;
        }

        public bool TryDequeue(out SoundEvent e)
        {
            if (queue.Count > 0)
            {
                e = queue.Dequeue();
                return true;
            }
            e = default;
            return false;
        }

        /// <summary>DMX lump: u16 format, u16 rate, u32 length, unsigned 8-bit mono PCM (with
        /// optional 16-byte padding at both ends).</summary>
        internal static float[] Decode(byte[] data, out int sampleRate)
        {
            sampleRate = 0;
            if (data == null || data.Length < 8)
            {
                return null;
            }

            sampleRate = BitConverter.ToUInt16(data, 2);
            var count = BitConverter.ToInt32(data, 4);
            var offset = 8;
            if (count >= 32 && HasPadding(data, count))
            {
                offset += 16;
                count -= 32;
            }

            count = Math.Min(count, data.Length - offset);
            if (count <= 0 || sampleRate <= 0)
            {
                return null;
            }

            var result = new float[count];
            for (var i = 0; i < count; i++)
            {
                result[i] = (data[offset + i] - 128) / 128f;
            }
            return result;
        }

        private static bool HasPadding(byte[] data, int count)
        {
            if (data.Length < 8 + count)
            {
                return false;
            }
            var first = data[8];
            for (var i = 1; i < 16; i++)
            {
                if (data[8 + i] != first) return false;
            }
            var last = data[8 + count - 1];
            for (var i = 1; i < 16; i++)
            {
                if (data[8 + count - i - 1] != last) return false;
            }
            return true;
        }

        private void Enqueue(Sfx sfx, float gain, float pan)
        {
            var index = (int)sfx;
            if (index < 0 || index >= samples.Length || samples[index] == null || gain <= 0.001f)
            {
                return;
            }
            if (queue.Count >= MaxQueued)
            {
                queue.Dequeue();
            }
            queue.Enqueue(new SoundEvent { Sfx = index, Gain = Math.Clamp(gain, 0f, 1f), Pan = Math.Clamp(pan, -1f, 1f) });
        }

        public void SetListener(Mobj listener) => this.listener = listener;

        public void Update() { }

        public void StartSound(Sfx sfx) => Enqueue(sfx, masterVolume, 0f);

        public void StartSound(Mobj mobj, Sfx sfx, SfxType type) => StartSound(mobj, sfx, type, 100);

        public void StartSound(Mobj mobj, Sfx sfx, SfxType type, int volume)
        {
            var gain = 0.01f * masterVolume * volume;
            var pan = 0f;
            if (type != SfxType.Diffuse && mobj != null && listener != null && mobj != listener)
            {
                var x = (mobj.X - listener.X).ToFloat();
                var y = (mobj.Y - listener.Y).ToFloat();
                if (Math.Abs(x) >= 16 || Math.Abs(y) >= 16)
                {
                    var dist = MathF.Sqrt(x * x + y * y);
                    var decay = dist < CloseDist ? 1f : Math.Max((ClipDist - dist) / (ClipDist - CloseDist), 0f);
                    var angle = MathF.Atan2(y, x) - (float)listener.Angle.ToRadian();
                    gain *= decay;
                    pan = -MathF.Sin(angle);
                }
            }
            Enqueue(sfx, gain, pan);
        }

        public void StopSound(Mobj mobj) { }

        public void Reset()
        {
            queue.Clear();
            listener = null;
        }

        public void Pause() { }
        public void Resume() { }

        public int MaxVolume => 15;

        public int Volume
        {
            get => config.audio_soundvolume;
            set
            {
                config.audio_soundvolume = value;
                masterVolume = (float)config.audio_soundvolume / MaxVolume;
            }
        }
    }
}
