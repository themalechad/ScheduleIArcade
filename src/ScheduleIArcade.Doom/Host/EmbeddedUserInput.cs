// ScheduleIArcade.Doom - embedded host for Managed Doom (GPL-2.0, see ../LICENSE and ../NOTICE.md).
// Copyright (C) 2026 ScheduleIArcade contributors. Tic-command logic adapted from
// ManagedDoom.Silk.SilkUserInput by Nobuaki Tanaka.
//
// This program is free software; you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation; either version 2 of the
// License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY.

using System;
using System.Collections.Generic;
using ManagedDoom;
using ManagedDoom.UserInput;

namespace ScheduleIArcade.Doom
{
    /// <summary>Keyboard-state input: the host reports key down/up; Doom samples it every tic.</summary>
    internal sealed class EmbeddedUserInput : IUserInput
    {
        private readonly Config config;
        private readonly HashSet<DoomKey> pressed = new HashSet<DoomKey>();
        private readonly bool[] weaponKeys = new bool[7];
        private int turnHeld;

        public EmbeddedUserInput(Config config)
        {
            this.config = config;
        }

        public void SetKey(DoomKey key, bool down)
        {
            if (down)
            {
                pressed.Add(key);
            }
            else
            {
                pressed.Remove(key);
            }
        }

        public void ReleaseAll() => pressed.Clear();

        private bool IsPressed(KeyBinding binding)
        {
            foreach (var key in binding.Keys)
            {
                if (pressed.Contains(key))
                {
                    return true;
                }
            }
            return false;
        }

        public void BuildTicCmd(TicCmd cmd)
        {
            var keyForward = IsPressed(config.key_forward);
            var keyBackward = IsPressed(config.key_backward);
            var keyStrafeLeft = IsPressed(config.key_strafeleft);
            var keyStrafeRight = IsPressed(config.key_straferight);
            var keyTurnLeft = IsPressed(config.key_turnleft);
            var keyTurnRight = IsPressed(config.key_turnright);
            var keyFire = IsPressed(config.key_fire);
            var keyUse = IsPressed(config.key_use);
            var keyRun = IsPressed(config.key_run);
            var keyStrafe = IsPressed(config.key_strafe);

            weaponKeys[0] = pressed.Contains(DoomKey.Num1);
            weaponKeys[1] = pressed.Contains(DoomKey.Num2);
            weaponKeys[2] = pressed.Contains(DoomKey.Num3);
            weaponKeys[3] = pressed.Contains(DoomKey.Num4);
            weaponKeys[4] = pressed.Contains(DoomKey.Num5);
            weaponKeys[5] = pressed.Contains(DoomKey.Num6);
            weaponKeys[6] = pressed.Contains(DoomKey.Num7);

            cmd.Clear();

            var speed = keyRun ? 1 : 0;
            if (config.game_alwaysrun)
            {
                speed = 1 - speed;
            }

            var forward = 0;
            var side = 0;

            if (keyTurnLeft || keyTurnRight)
            {
                turnHeld++;
            }
            else
            {
                turnHeld = 0;
            }

            var turnSpeed = turnHeld < PlayerBehavior.SlowTurnTics ? 2 : speed;

            if (keyStrafe)
            {
                if (keyTurnRight) side += PlayerBehavior.SideMove[speed];
                if (keyTurnLeft) side -= PlayerBehavior.SideMove[speed];
            }
            else
            {
                if (keyTurnRight) cmd.AngleTurn -= (short)PlayerBehavior.AngleTurn[turnSpeed];
                if (keyTurnLeft) cmd.AngleTurn += (short)PlayerBehavior.AngleTurn[turnSpeed];
            }

            if (keyForward) forward += PlayerBehavior.ForwardMove[speed];
            if (keyBackward) forward -= PlayerBehavior.ForwardMove[speed];
            if (keyStrafeLeft) side -= PlayerBehavior.SideMove[speed];
            if (keyStrafeRight) side += PlayerBehavior.SideMove[speed];

            if (keyFire) cmd.Buttons |= TicCmdButtons.Attack;
            if (keyUse) cmd.Buttons |= TicCmdButtons.Use;

            for (var i = 0; i < weaponKeys.Length; i++)
            {
                if (weaponKeys[i])
                {
                    cmd.Buttons |= TicCmdButtons.Change;
                    cmd.Buttons |= (byte)(i << TicCmdButtons.WeaponShift);
                    break;
                }
            }

            forward = Math.Clamp(forward, -PlayerBehavior.MaxMove, PlayerBehavior.MaxMove);
            side = Math.Clamp(side, -PlayerBehavior.MaxMove, PlayerBehavior.MaxMove);
            cmd.ForwardMove += (sbyte)forward;
            cmd.SideMove += (sbyte)side;
        }

        public void Reset()
        {
            pressed.Clear();
            turnHeld = 0;
        }

        public void GrabMouse() { }
        public void ReleaseMouse() { }

        public int MaxMouseSensitivity => 15;

        public int MouseSensitivity
        {
            get => config.mouse_sensitivity;
            set => config.mouse_sensitivity = value;
        }
    }
}
