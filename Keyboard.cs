using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace KeyboardInput
{
    /// <summary>
    /// Win32 <c>SendInput</c> keyboard helpers. These are used instead of
    /// <c>ExileCore.Input</c>, which does not deliver input in this setup.
    /// Text is typed as Unicode events so it works on any keyboard layout.
    /// </summary>
    internal static class Keyboard
    {
        private const uint InputKeyboard = 1;

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

        /// <summary>Presses and holds the Ctrl key.</summary>
        public static bool ControlDown()
        {
            return Send([CreateVirtualKeyInput(VkControl, keyUp: false)]);
        }

        /// <summary>Releases the Ctrl key.</summary>
        public static bool ControlUp()
        {
            return Send([CreateVirtualKeyInput(VkControl, keyUp: true)]);
        }

        /// <summary>Presses Ctrl+A to select the content of the focused input.</summary>
        public static bool PressControlA()
        {
            return Send(
            [
                CreateVirtualKeyInput(VkControl, keyUp: false),
                CreateVirtualKeyInput(VkA, keyUp: false),
                CreateVirtualKeyInput(VkA, keyUp: true),
                CreateVirtualKeyInput(VkControl, keyUp: true)
            ]);
        }

        /// <summary>Types a single character as a Unicode key press.</summary>
        public static bool TypeCharacter(char character)
        {
            return Send(
            [
                CreateUnicodeInput(character, keyUp: false),
                CreateUnicodeInput(character, keyUp: true)
            ]);
        }

        /// <summary>Types the given text as one batch of Unicode key presses.</summary>
        public static bool TypeText(string text)
        {
            if (string.IsNullOrEmpty(text))
                return true;

            var inputs = new List<Input>(text.Length * 2);

            foreach (var character in text)
            {
                inputs.Add(CreateUnicodeInput(character, keyUp: false));
                inputs.Add(CreateUnicodeInput(character, keyUp: true));
            }

            return Send(inputs.ToArray());
        }

        private static Input CreateVirtualKeyInput(ushort virtualKey, bool keyUp)
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

        private static Input CreateUnicodeInput(char character, bool keyUp)
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
                        Flags = KeyEventUnicode | (keyUp ? KeyEventKeyUp : 0),
                        Time = 0,
                        ExtraInfo = UIntPtr.Zero
                    }
                }
            };
        }

        /// <exception cref="Win32Exception">Not every event was delivered.</exception>
        private static bool Send(Input[] inputs)
        {
            if (inputs == null || inputs.Length == 0)
                return true;

            var sent = SendInput(
                (uint)inputs.Length,
                inputs,
                Marshal.SizeOf<Input>());

            if (sent == (uint)inputs.Length)
                return true;

            var error = Marshal.GetLastWin32Error();

            throw new Win32Exception(
                error,
                $"SendInput sent {sent} of {inputs.Length} keyboard events.");
        }
    }
}
