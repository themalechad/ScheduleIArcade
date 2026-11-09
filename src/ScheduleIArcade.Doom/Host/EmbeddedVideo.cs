// ScheduleIArcade.Doom - embedded host for Managed Doom (GPL-2.0, see ../LICENSE and ../NOTICE.md).
// Copyright (C) 2026 ScheduleIArcade contributors. Based on ManagedDoom.Silk.SilkVideo by Nobuaki Tanaka.
//
// This program is free software; you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation; either version 2 of the
// License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY.

using System;
using ManagedDoom;
using ManagedDoom.Video;

namespace ScheduleIArcade.Doom
{
    /// <summary>
    /// Headless IVideo: runs Managed Doom's own software renderer into a byte buffer instead of an
    /// OpenGL window. The renderer writes RGBA column-major (index = (x * height + y) * 4).
    /// </summary>
    internal sealed class EmbeddedVideo : IVideo
    {
        private readonly Renderer renderer;
        private readonly byte[] columnMajor;

        public EmbeddedVideo(Config config, GameContent content)
        {
            renderer = new Renderer(config, content);
            columnMajor = new byte[4 * renderer.Width * renderer.Height];
        }

        public int Width => renderer.Width;
        public int Height => renderer.Height;
        public long FramesRendered { get; private set; }

        public void Render(ManagedDoom.Doom doom, Fixed frameFrac)
        {
            renderer.Render(doom, columnMajor, frameFrac);
            FramesRendered++;
        }

        /// <summary>
        /// Converts the last rendered frame to Unity's raw RGBA32 layout: row-major, bottom row
        /// first, opaque alpha.
        /// </summary>
        public void CopyBottomUpRgba(byte[] destination)
        {
            int width = renderer.Width;
            int height = renderer.Height;
            if (destination == null || destination.Length < width * height * 4)
            {
                throw new ArgumentException("Destination must hold width * height * 4 bytes.");
            }

            for (int x = 0; x < width; x++)
            {
                int src = x * height * 4;
                for (int y = 0; y < height; y++)
                {
                    int dst = ((height - 1 - y) * width + x) * 4;
                    destination[dst] = columnMajor[src];
                    destination[dst + 1] = columnMajor[src + 1];
                    destination[dst + 2] = columnMajor[src + 2];
                    destination[dst + 3] = 255;
                    src += 4;
                }
            }
        }

        public void InitializeWipe() => renderer.InitializeWipe();

        public bool HasFocus() => true;

        public int MaxWindowSize => renderer.MaxWindowSize;

        public int WindowSize
        {
            get => renderer.WindowSize;
            set => renderer.WindowSize = value;
        }

        public bool DisplayMessage
        {
            get => renderer.DisplayMessage;
            set => renderer.DisplayMessage = value;
        }

        public int MaxGammaCorrectionLevel => renderer.MaxGammaCorrectionLevel;

        public int GammaCorrectionLevel
        {
            get => renderer.GammaCorrectionLevel;
            set => renderer.GammaCorrectionLevel = value;
        }

        public int WipeBandCount => renderer.WipeBandCount;
        public int WipeHeight => renderer.WipeHeight;
    }
}
