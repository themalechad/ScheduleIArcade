// ScheduleIArcade.Doom - embedded host for Managed Doom (GPL-2.0, see ../LICENSE and ../NOTICE.md).
// Copyright (C) 2026 ScheduleIArcade contributors. Main-loop structure adapted from
// ManagedDoom.Silk.SilkDoom by Nobuaki Tanaka.
//
// This program is free software; you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation; either version 2 of the
// License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY.

using System;
using System.IO;
using ManagedDoom;
using ManagedDoom.UserInput;

namespace ScheduleIArcade.Doom
{
    /// <summary>
    /// A complete Doom instance with no window, audio device or input device. The Schedule I
    /// arcade mod drives it through this deliberately small, primitive-typed API (it talks to this
    /// separate GPL-2.0 assembly by reflection): advance time, report keys, copy RGBA frames out,
    /// drain sound events.
    ///
    /// The constructor does no Unity calls and may run on a worker thread; every other member must
    /// be called from one thread (the Unity main thread).
    /// </summary>
    public sealed class EmbeddedDoomRuntime : IDisposable
    {
        public const int ApiVersion = 1;
        public const int TicRate = 35;
        private const int MaxTicsPerAdvance = 6;

        private readonly CommandLineArgs args;
        private readonly Config config;
        private readonly GameContent content;
        private readonly EmbeddedVideo video;
        private readonly EmbeddedSound sound;
        private readonly EmbeddedUserInput input;
        private readonly ManagedDoom.Doom doom;
        private double accumulator;
        private bool disposed;

        public EmbeddedDoomRuntime(string iwadPath, string dataDirectory)
        {
            if (string.IsNullOrEmpty(iwadPath) || !CanOpen(iwadPath))
            {
                throw new FileNotFoundException("IWAD not found.", iwadPath);
            }

            if (!string.IsNullOrEmpty(dataDirectory))
            {
                Directory.CreateDirectory(dataDirectory);
                ConfigUtilities.DataDirectoryOverride = dataDirectory;
            }

            IwadPath = iwadPath;
            args = new CommandLineArgs(new[] { "-iwad", iwadPath });
            config = new Config
            {
                video_highresolution = true,
                video_displaymessage = true,
                game_alwaysrun = true,
                audio_soundvolume = 15, // full engine gain; the arcade applies its own volume path
                audio_randompitch = false
            };

            content = new GameContent(args);
            video = new EmbeddedVideo(config, content);
            sound = new EmbeddedSound(config, content);
            input = new EmbeddedUserInput(config);
            doom = new ManagedDoom.Doom(args, config, content, video, sound, null, input);
            IsRunning = true;
        }

        /// <summary>Symlink-safe existence check (opens the file; never trusts FileInfo.Length).</summary>
        private static bool CanOpen(string path)
        {
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    return stream.Length >= 12;
                }
            }
            catch
            {
                return false;
            }
        }

        public string IwadPath { get; }
        public int Width => video.Width;
        public int Height => video.Height;
        public bool IsRunning { get; private set; }
        public string LastError { get; private set; } = string.Empty;
        public long Tic { get; private set; }
        public long FramesRendered => video.FramesRendered;
        public string State => doom.State.ToString();
        public int SfxCount => sound.SfxCount;
        public string QuitMessage => doom.QuitMessage ?? string.Empty;

        /// <summary>Reports a key by its ManagedDoom.UserInput.DoomKey name (e.g. "Up", "LControl",
        /// "Enter", "Escape", "Y"). Returns false for unknown names.</summary>
        public bool SetKey(string doomKeyName, bool down)
        {
            if (!IsRunning || !Enum.TryParse(doomKeyName, true, out DoomKey key) || key == DoomKey.Unknown)
            {
                return false;
            }

            input.SetKey(key, down);
            doom.PostEvent(new DoomEvent(down ? EventType.KeyDown : EventType.KeyUp, key));
            return true;
        }

        public void ReleaseAllKeys()
        {
            input.ReleaseAll();
        }

        /// <summary>Runs whole 35 Hz game tics for the elapsed real time. Returns the tic count run.
        /// Long hitches are capped instead of fast-forwarding the game.</summary>
        public int Advance(double seconds)
        {
            if (!IsRunning || disposed || !(seconds > 0))
            {
                return 0;
            }

            accumulator = Math.Min(accumulator + seconds, (double)MaxTicsPerAdvance / TicRate);
            var ran = 0;
            try
            {
                while (accumulator >= 1.0 / TicRate && IsRunning)
                {
                    accumulator -= 1.0 / TicRate;
                    if (doom.Update() == UpdateResult.Completed)
                    {
                        IsRunning = false; // player chose Quit Game in the Doom menu
                    }
                    Tic++;
                    ran++;
                }
            }
            catch (Exception e)
            {
                LastError = e.GetType().Name + ": " + e.Message;
                IsRunning = false;
            }
            return ran;
        }

        /// <summary>Renders the current state and writes Width*Height RGBA32 pixels, bottom row
        /// first (Unity Texture2D.LoadRawTextureData layout).</summary>
        public bool RenderInto(byte[] rgbaBottomUp)
        {
            if (!IsRunning || disposed)
            {
                return false;
            }

            try
            {
                video.Render(doom, Fixed.FromInt(1));
                video.CopyBottomUpRgba(rgbaBottomUp);
                return true;
            }
            catch (Exception e)
            {
                LastError = e.GetType().Name + ": " + e.Message;
                IsRunning = false;
                return false;
            }
        }

        public bool TryDequeueSound(out int sfx, out float gain, out float pan)
        {
            if (sound.TryDequeue(out var e))
            {
                sfx = e.Sfx;
                gain = e.Gain;
                pan = e.Pan;
                return true;
            }
            sfx = -1;
            gain = 0f;
            pan = 0f;
            return false;
        }

        public float[] GetSfxSamples(int sfx, out int sampleRate) => sound.GetSamples(sfx, out sampleRate);

        /// <summary>Test/diagnostic hook: console player position (map units) and facing (radians).</summary>
        public bool TryGetPlayerPosition(out float x, out float y, out double angle)
        {
            x = 0f;
            y = 0f;
            angle = 0;
            if (doom.State != DoomState.Game || doom.Game?.World == null)
            {
                return false;
            }
            var mobj = doom.Game.World.ConsolePlayer?.Mobj;
            if (mobj == null)
            {
                return false;
            }
            x = mobj.X.ToFloat();
            y = mobj.Y.ToFloat();
            angle = mobj.Angle.ToRadian();
            return true;
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            IsRunning = false;
            content.Dispose();
        }
    }
}
