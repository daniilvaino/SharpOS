// SharpUserInput — IUserInput for SharpOS (step143). Two halves:
//
//   PumpEvents: drains the AppHost key service; the kernel forwards RAW
//   set-1 make/break events (KeyInfo.Raw, step143 packing) so releases are
//   visible. Each event updates the held-key table and is posted to the
//   Doom core (menu/game key handling).
//
//   BuildTicCmd: port of src/Silk/SilkUserInput.cs minus the mouse half —
//   same movement/turn/weapon logic over the held-key table instead of a
//   live Silk keyboard.
//
// Keyboard-only for now; GrabMouse/ReleaseMouse are no-ops and mouse
// bindings never fire (config.mouse_* untouched).

using System;
using ManagedDoom;
using ManagedDoom.UserInput;
using SharpOS.AppSdk;

namespace DoomApp
{
    public sealed class SharpUserInput : IUserInput
    {
        private readonly Config config;

        // Held-key table indexed by DoomKey (0..Count-1).
        private readonly bool[] pressed;
        private readonly bool[] weaponKeys;
        private int turnHeld;

        public SharpUserInput(Config config)
        {
            this.config = config;
            pressed = new bool[256];
            weaponKeys = new bool[7];
            turnHeld = 0;
        }

        // Drain pending key events: update the held table, forward to the
        // core. Call once per frame before Doom.Update.
        public void PumpEvents(Doom doom)
        {
            while (AppHost.TryReadKey(out KeyInfo key) == AppServiceStatus.Ok)
            {
                if (!key.HasRaw)
                    continue; // pre-EBS legacy path — no raw events

                DoomKey doomKey = TranslateSet1(key.RawMake, key.RawExtended);
                if (doomKey == DoomKey.Unknown)
                    continue;

                bool down = key.RawDown;
                int index = (int)doomKey;
                if (index >= 0 && index < pressed.Length)
                    pressed[index] = down;

                doom.PostEvent(new DoomEvent(down ? EventType.KeyDown : EventType.KeyUp, doomKey));
            }
        }

        private bool IsDown(DoomKey key)
        {
            int index = (int)key;
            return index >= 0 && index < pressed.Length && pressed[index];
        }

        private bool IsPressed(KeyBinding binding)
        {
            var keys = binding.Keys;
            for (int i = 0; i < keys.Count; i++)
            {
                if (IsDown(keys[i]))
                    return true;
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

            weaponKeys[0] = IsDown(DoomKey.Num1);
            weaponKeys[1] = IsDown(DoomKey.Num2);
            weaponKeys[2] = IsDown(DoomKey.Num3);
            weaponKeys[3] = IsDown(DoomKey.Num4);
            weaponKeys[4] = IsDown(DoomKey.Num5);
            weaponKeys[5] = IsDown(DoomKey.Num6);
            weaponKeys[6] = IsDown(DoomKey.Num7);

            cmd.Clear();

            var strafe = keyStrafe;
            var speed = keyRun ? 1 : 0;
            var forward = 0;
            var side = 0;

            if (config.game_alwaysrun)
            {
                speed = 1 - speed;
            }

            if (keyTurnLeft || keyTurnRight)
            {
                turnHeld++;
            }
            else
            {
                turnHeld = 0;
            }

            int turnSpeed;
            if (turnHeld < PlayerBehavior.SlowTurnTics)
            {
                turnSpeed = 2;
            }
            else
            {
                turnSpeed = speed;
            }

            if (strafe)
            {
                if (keyTurnRight)
                {
                    side += PlayerBehavior.SideMove[speed];
                }
                if (keyTurnLeft)
                {
                    side -= PlayerBehavior.SideMove[speed];
                }
            }
            else
            {
                if (keyTurnRight)
                {
                    cmd.AngleTurn -= (short)PlayerBehavior.AngleTurn[turnSpeed];
                }
                if (keyTurnLeft)
                {
                    cmd.AngleTurn += (short)PlayerBehavior.AngleTurn[turnSpeed];
                }
            }

            if (keyForward)
            {
                forward += PlayerBehavior.ForwardMove[speed];
            }
            if (keyBackward)
            {
                forward -= PlayerBehavior.ForwardMove[speed];
            }

            if (keyStrafeLeft)
            {
                side -= PlayerBehavior.SideMove[speed];
            }
            if (keyStrafeRight)
            {
                side += PlayerBehavior.SideMove[speed];
            }

            if (keyFire)
            {
                cmd.Buttons |= TicCmdButtons.Attack;
            }

            if (keyUse)
            {
                cmd.Buttons |= TicCmdButtons.Use;
            }

            for (var i = 0; i < weaponKeys.Length; i++)
            {
                if (weaponKeys[i])
                {
                    cmd.Buttons |= TicCmdButtons.Change;
                    cmd.Buttons |= (byte)(i << TicCmdButtons.WeaponShift);
                    break;
                }
            }

            if (forward > PlayerBehavior.MaxMove)
            {
                forward = PlayerBehavior.MaxMove;
            }
            else if (forward < -PlayerBehavior.MaxMove)
            {
                forward = -PlayerBehavior.MaxMove;
            }
            if (side > PlayerBehavior.MaxMove)
            {
                side = PlayerBehavior.MaxMove;
            }
            else if (side < -PlayerBehavior.MaxMove)
            {
                side = -PlayerBehavior.MaxMove;
            }

            cmd.ForwardMove += (sbyte)forward;
            cmd.SideMove += (sbyte)side;
        }

        public void Reset()
        {
            for (int i = 0; i < pressed.Length; i++)
                pressed[i] = false;
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

        // Set-1 scancode -> DoomKey. US-QWERTY; extended (0xE0) codes cover
        // the arrow cluster + right-side modifiers. Unmapped keys ->
        // Unknown (dropped).
        private static DoomKey TranslateSet1(byte make, bool extended)
        {
            if (extended)
            {
                switch (make)
                {
                    case 0x48: return DoomKey.Up;
                    case 0x50: return DoomKey.Down;
                    case 0x4B: return DoomKey.Left;
                    case 0x4D: return DoomKey.Right;
                    case 0x1D: return DoomKey.RControl;
                    case 0x38: return DoomKey.RAlt;
                    case 0x1C: return DoomKey.Enter;   // keypad enter
                    case 0x53: return DoomKey.Delete;
                    case 0x47: return DoomKey.Home;
                    case 0x4F: return DoomKey.End;
                    case 0x49: return DoomKey.PageUp;
                    case 0x51: return DoomKey.PageDown;
                    case 0x52: return DoomKey.Insert;
                    default: return DoomKey.Unknown;
                }
            }

            switch (make)
            {
                case 0x01: return DoomKey.Escape;
                case 0x02: return DoomKey.Num1;
                case 0x03: return DoomKey.Num2;
                case 0x04: return DoomKey.Num3;
                case 0x05: return DoomKey.Num4;
                case 0x06: return DoomKey.Num5;
                case 0x07: return DoomKey.Num6;
                case 0x08: return DoomKey.Num7;
                case 0x09: return DoomKey.Num8;
                case 0x0A: return DoomKey.Num9;
                case 0x0B: return DoomKey.Num0;
                case 0x0C: return DoomKey.Hyphen;
                case 0x0D: return DoomKey.Equal;
                case 0x0E: return DoomKey.Backspace;
                case 0x0F: return DoomKey.Tab;
                case 0x10: return DoomKey.Q;
                case 0x11: return DoomKey.W;
                case 0x12: return DoomKey.E;
                case 0x13: return DoomKey.R;
                case 0x14: return DoomKey.T;
                case 0x15: return DoomKey.Y;
                case 0x16: return DoomKey.U;
                case 0x17: return DoomKey.I;
                case 0x18: return DoomKey.O;
                case 0x19: return DoomKey.P;
                case 0x1A: return DoomKey.LBracket;
                case 0x1B: return DoomKey.RBracket;
                case 0x1C: return DoomKey.Enter;
                case 0x1D: return DoomKey.LControl;
                case 0x1E: return DoomKey.A;
                case 0x1F: return DoomKey.S;
                case 0x20: return DoomKey.D;
                case 0x21: return DoomKey.F;
                case 0x22: return DoomKey.G;
                case 0x23: return DoomKey.H;
                case 0x24: return DoomKey.J;
                case 0x25: return DoomKey.K;
                case 0x26: return DoomKey.L;
                case 0x27: return DoomKey.Semicolon;
                case 0x28: return DoomKey.Quote;
                case 0x29: return DoomKey.Tilde;
                case 0x2A: return DoomKey.LShift;
                case 0x2B: return DoomKey.Backslash;
                case 0x2C: return DoomKey.Z;
                case 0x2D: return DoomKey.X;
                case 0x2E: return DoomKey.C;
                case 0x2F: return DoomKey.V;
                case 0x30: return DoomKey.B;
                case 0x31: return DoomKey.N;
                case 0x32: return DoomKey.M;
                case 0x33: return DoomKey.Comma;
                case 0x34: return DoomKey.Period;
                case 0x35: return DoomKey.Slash;
                case 0x36: return DoomKey.RShift;
                case 0x38: return DoomKey.LAlt;
                case 0x39: return DoomKey.Space;
                case 0x3A: return DoomKey.Unknown; // caps lock — modifier only
                case 0x3B: return DoomKey.F1;
                case 0x3C: return DoomKey.F2;
                case 0x3D: return DoomKey.F3;
                case 0x3E: return DoomKey.F4;
                case 0x3F: return DoomKey.F5;
                case 0x40: return DoomKey.F6;
                case 0x41: return DoomKey.F7;
                case 0x42: return DoomKey.F8;
                case 0x43: return DoomKey.F9;
                case 0x44: return DoomKey.F10;
                case 0x57: return DoomKey.F11;
                case 0x58: return DoomKey.F12;
                default: return DoomKey.Unknown;
            }
        }
    }
}
