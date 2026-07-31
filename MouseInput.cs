using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using SharpDX;

namespace MouseInput
{
    internal static class Mouse
    {
        private const uint INPUT_MOUSE = 0;

        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP = 0x0004;
        private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
        private const uint MOUSEEVENTF_RIGHTUP = 0x0010;

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public InputUnion input;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)]
            public MOUSEINPUT mouseInput;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint flags;
            public uint time;
            public UIntPtr extraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out SharpDX.Point point);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(
            uint numberOfInputs,
            INPUT[] inputs,
            int sizeOfInput);

        public static SharpDX.Point GetCursorPosition()
        {
            if (!GetCursorPos(out var point))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            return point;
        }

        public static bool MoveMouse(Vector2 position)
        {
            return SetCursorPos(
                (int)Math.Round(position.X),
                (int)Math.Round(position.Y));
        }

        public static bool LeftClick(int holdMilliseconds = 30)
        {
            if (!SendMouseEvent(MOUSEEVENTF_LEFTDOWN))
                return false;

            Thread.Sleep(holdMilliseconds);

            return SendMouseEvent(MOUSEEVENTF_LEFTUP);
        }

        public static bool RightClick(int holdMilliseconds = 30)
        {
            if (!SendMouseEvent(MOUSEEVENTF_RIGHTDOWN))
                return false;

            Thread.Sleep(holdMilliseconds);

            return SendMouseEvent(MOUSEEVENTF_RIGHTUP);
        }

        private static bool SendMouseEvent(uint flags)
        {
            var inputs = new[]
            {
                new INPUT
                {
                    type = INPUT_MOUSE,
                    input = new InputUnion
                    {
                        mouseInput = new MOUSEINPUT
                        {
                            flags = flags
                        }
                    }
                }
            };

            var sent = SendInput(
                (uint)inputs.Length,
                inputs,
                Marshal.SizeOf<INPUT>());

            return sent == inputs.Length;
        }
    }
}