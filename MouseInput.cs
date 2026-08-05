using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using SharpDX;

namespace MouseInput
{
    /// <summary>
    /// Win32 <c>SendInput</c>/<c>SetCursorPos</c> mouse helpers. These are used
    /// instead of <c>ExileCore.Input</c>, which does not deliver input in this setup.
    /// </summary>
    internal static class Mouse
    {
        private const uint InputMouse = 0;

        private const uint MouseEventLeftDown = 0x0002;
        private const uint MouseEventLeftUp = 0x0004;
        private const uint MouseEventRightDown = 0x0008;
        private const uint MouseEventRightUp = 0x0010;

        [StructLayout(LayoutKind.Sequential)]
        private struct Input
        {
            public uint Type;
            public InputUnion Data;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)]
            public MouseInput Mouse;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MouseInput
        {
            public int Dx;
            public int Dy;
            public uint MouseData;
            public uint Flags;
            public uint Time;
            public UIntPtr ExtraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetCursorPos(out SharpDX.Point point);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(
            uint inputCount,
            Input[] inputs,
            int inputSize);

        /// <summary>Returns the cursor position in screen coordinates.</summary>
        /// <exception cref="Win32Exception">The position could not be read.</exception>
        public static SharpDX.Point GetCursorPosition()
        {
            if (!GetCursorPos(out var point))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            return point;
        }

        /// <summary>Moves the cursor to the given screen position.</summary>
        public static bool MoveMouse(Vector2 position)
        {
            return SetCursorPos(
                (int)Math.Round(position.X),
                (int)Math.Round(position.Y));
        }

        /// <summary>Presses the left mouse button at the current cursor position.</summary>
        public static bool LeftDown() => SendMouseEvent(MouseEventLeftDown);

        /// <summary>Releases the left mouse button at the current cursor position.</summary>
        public static bool LeftUp() => SendMouseEvent(MouseEventLeftUp);

        /// <summary>Presses the right mouse button at the current cursor position.</summary>
        public static bool RightDown() => SendMouseEvent(MouseEventRightDown);

        /// <summary>Releases the right mouse button at the current cursor position.</summary>
        public static bool RightUp() => SendMouseEvent(MouseEventRightUp);

        private static bool SendMouseEvent(uint flags)
        {
            var inputs = new[]
            {
                new Input
                {
                    Type = InputMouse,
                    Data = new InputUnion
                    {
                        Mouse = new MouseInput
                        {
                            Flags = flags
                        }
                    }
                }
            };

            var sent = SendInput(
                (uint)inputs.Length,
                inputs,
                Marshal.SizeOf<Input>());

            return sent == inputs.Length;
        }
    }
}
