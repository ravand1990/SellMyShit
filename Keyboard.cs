using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;

namespace KeyboardInput
{
    internal static class Keyboard
    {
        private const uint InputMouse = 0;
        private const uint InputKeyboard = 1;
        private const uint InputHardware = 2;

        private const uint KeyEventKeyUp = 0x0002;
        private const uint KeyEventUnicode = 0x0004;

        private const ushort VkControl = 0x11;
        private const ushort VkA = 0x41;

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
            var inputs = new[]
            {
                CreateVirtualKeyInput(VkControl, keyUp: false),
                CreateVirtualKeyInput(VkA, keyUp: false),
                CreateVirtualKeyInput(VkA, keyUp: true),
                CreateVirtualKeyInput(VkControl, keyUp: true)
            };

            return Send(inputs);
        }

        public static bool TypeText(string text)
        {
            if (string.IsNullOrEmpty(text))
                return true;

            var inputs = new List<Input>(text.Length * 2);

            /*
             * char is one UTF-16 code unit. SendInput's Unicode mode
             * accepts UTF-16 scan-code values.
             */
            foreach (var character in text)
            {
                inputs.Add(
                    CreateUnicodeInput(character, keyUp: false));

                inputs.Add(
                    CreateUnicodeInput(character, keyUp: true));
            }

            return Send(inputs.ToArray());
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
                        Flags = keyUp ? KeyEventKeyUp : 0,
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
                        Flags = KeyEventUnicode |
                                (keyUp ? KeyEventKeyUp : 0),
                        Time = 0,
                        ExtraInfo = UIntPtr.Zero
                    }
                }
            };
        }

        private static bool Send(Input[] inputs)
        {
            if (inputs.Length == 0)
                return true;

            var sent = SendInput(
                (uint)inputs.Length,
                inputs,
                Marshal.SizeOf<Input>());

            if (sent == inputs.Length)
                return true;

            var error = Marshal.GetLastWin32Error();

            throw new Win32Exception(
                error,
                $"SendInput sent {sent} of {inputs.Length} keyboard events.");
        }
    }
}