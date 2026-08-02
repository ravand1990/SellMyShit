using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;

namespace KeyboardInput
{
    internal static class Keyboard
    {
        private const uint InputKeyboard = 1;

        private const uint KeyEventKeyUp = 0x0002;
        private const uint KeyEventUnicode = 0x0004;
        private const uint KeyEventScanCode = 0x0008;

        private const ushort VkControl = 0x11;
        private const ushort VkA = 0x41;

        private const int VkMenu = 0x12;
        private const int VkLeftMenu = 0xA4;

        // Physical scan code for left Alt.
        private const ushort ScanCodeLeftAlt = 0x38;

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

            [FieldOffset(0)]
            public KeyboardInput Keyboard;

            [FieldOffset(0)]
            public HardwareInput Hardware;
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

        [StructLayout(LayoutKind.Sequential)]
        private struct KeyboardInput
        {
            public ushort VirtualKey;
            public ushort ScanCode;
            public uint Flags;
            public uint Time;
            public UIntPtr ExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HardwareInput
        {
            public uint Message;
            public ushort ParameterLow;
            public ushort ParameterHigh;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(
            uint inputCount,
            Input[] inputs,
            int inputSize);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(
            int virtualKey);

        public static bool ReplaceText(
            string text,
            int delayBeforeTypingMilliseconds = 30)
        {
            if (text == null)
                return false;

            if (!PressControlA())
                return false;

            Thread.Sleep(delayBeforeTypingMilliseconds);

            return TypeText(text);
        }

        public static bool PressControlA()
        {
            return Send(
            [
                CreateVirtualKeyInput(
                    VkControl,
                    keyUp: false),

                CreateVirtualKeyInput(
                    VkA,
                    keyUp: false),

                CreateVirtualKeyInput(
                    VkA,
                    keyUp: true),

                CreateVirtualKeyInput(
                    VkControl,
                    keyUp: true)
            ]);
        }

        /// <summary>
        /// Sends a left-Alt key-down event without releasing the key.
        /// Alt remains held until AltKeyUp() is called.
        /// </summary>
        public static bool AltKeyDown()
        {
            if (IsAltDown())
                return true;

            var sent = Send(
            [
                CreateScanCodeInput(
                    ScanCodeLeftAlt,
                    keyUp: false)
            ]);

            Thread.Sleep(30);

            return sent;
        }

        /// <summary>
        /// Releases left Alt.
        /// </summary>
        public static bool AltKeyUp()
        {
            var sent = Send(
            [
                CreateScanCodeInput(
                    ScanCodeLeftAlt,
                    keyUp: true)
            ]);

            Thread.Sleep(30);

            return sent;
        }

        public static bool IsAltDown()
        {
            return IsKeyDown(VkMenu) ||
                   IsKeyDown(VkLeftMenu);
        }

        public static bool TypeText(string text)
        {
            if (string.IsNullOrEmpty(text))
                return true;

            var inputs =
                new List<Input>(text.Length * 2);

            foreach (var character in text)
            {
                inputs.Add(
                    CreateUnicodeInput(
                        character,
                        keyUp: false));

                inputs.Add(
                    CreateUnicodeInput(
                        character,
                        keyUp: true));
            }

            return Send(inputs.ToArray());
        }

        private static bool IsKeyDown(int virtualKey)
        {
            return (GetAsyncKeyState(virtualKey) & 0x8000) != 0;
        }

        private static Input CreateVirtualKeyInput(
            ushort virtualKey,
            bool keyUp)
        {
            return new Input
            {
                Type = InputKeyboard,
                Data = new InputUnion
                {
                    Keyboard = new KeyboardInput
                    {
                        VirtualKey = virtualKey,
                        ScanCode = 0,
                        Flags = keyUp
                            ? KeyEventKeyUp
                            : 0,
                        Time = 0,
                        ExtraInfo = UIntPtr.Zero
                    }
                }
            };
        }

        private static Input CreateScanCodeInput(
            ushort scanCode,
            bool keyUp)
        {
            return new Input
            {
                Type = InputKeyboard,
                Data = new InputUnion
                {
                    Keyboard = new KeyboardInput
                    {
                        // Ignored when KEYEVENTF_SCANCODE is used.
                        VirtualKey = 0,
                        ScanCode = scanCode,
                        Flags =
                            KeyEventScanCode |
                            (keyUp ? KeyEventKeyUp : 0),
                        Time = 0,
                        ExtraInfo = UIntPtr.Zero
                    }
                }
            };
        }

        private static Input CreateUnicodeInput(
            char character,
            bool keyUp)
        {
            return new Input
            {
                Type = InputKeyboard,
                Data = new InputUnion
                {
                    Keyboard = new KeyboardInput
                    {
                        VirtualKey = 0,
                        ScanCode = character,
                        Flags =
                            KeyEventUnicode |
                            (keyUp ? KeyEventKeyUp : 0),
                        Time = 0,
                        ExtraInfo = UIntPtr.Zero
                    }
                }
            };
        }

        private static bool Send(Input[] inputs)
        {
            if (inputs == null ||
                inputs.Length == 0)
            {
                return true;
            }

            var sent = SendInput(
                (uint)inputs.Length,
                inputs,
                Marshal.SizeOf<Input>());

            if (sent == (uint)inputs.Length)
                return true;

            var error =
                Marshal.GetLastWin32Error();

            throw new Win32Exception(
                error,
                $"SendInput sent {sent} of " +
                $"{inputs.Length} keyboard events.");
        }
    }
}