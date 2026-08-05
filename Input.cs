using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using ExileCore.Shared;
using InputHumanizer.Input;
using NumVector2 = System.Numerics.Vector2;

namespace SellMyShit
{
    /// <summary>
    /// Delivers mouse and keyboard input to the game, either through the
    /// InputHumanizer plugin (when enabled and available) or through the raw
    /// Win32 <c>SendInput</c> helpers.
    /// </summary>
    /// <remarks>
    /// Keyboard input always uses the Win32 helpers, even with InputHumanizer
    /// enabled: the InputHumanizer controller's key presses go through
    /// <c>ExileCore.Input</c>, which does not work in this setup, and Unicode
    /// typing keeps working on any keyboard layout. InputHumanizer then only
    /// contributes its humanized delays.
    /// </remarks>
    public static class Input
    {
        private static Core Plugin => Core.Instance;

        private static Settings Settings => Plugin.Settings;

        private static IInputController _inputHumanizerControllerInstance;

        /// <summary>Left-clicks the given screen position.</summary>
        public static async SyncTask<bool> Click(
            NumVector2 targetPosition,
            CancellationToken cancellationToken = default)
        {
            return await ClickButton(
                MouseButtons.Left,
                targetPosition,
                cancellationToken);
        }

        /// <summary>
        /// Ctrl+right-clicks the given screen position, e.g. to collect
        /// placed-order items. Ctrl is always released afterwards, even when
        /// the click fails.
        /// </summary>
        public static async SyncTask<bool> CtrlRightClick(
            NumVector2 targetPosition,
            CancellationToken cancellationToken = default)
        {
            if (!KeyboardInput.Keyboard.ControlDown())
                return false;

            try
            {
                await Task.Delay(
                    Settings.GetRandomizedActionDelay(),
                    cancellationToken);

                return await ClickButton(
                    MouseButtons.Right,
                    targetPosition,
                    cancellationToken);
            }
            finally
            {
                try
                {
                    KeyboardInput.Keyboard.ControlUp();
                }
                catch
                {
                }
            }
        }

        private static async SyncTask<bool> ClickButton(
            MouseButtons mouseButton,
            NumVector2 targetPosition,
            CancellationToken cancellationToken)
        {
            if (Settings.UseInputHumanizer)
            {
                var controller = GetInputHumanizerController();

                if (controller != null)
                {
                    return await controller.Click(
                        mouseButton,
                        targetPosition,
                        cancellationToken);
                }
            }

            await Task.Delay(
                Settings.GetRandomizedActionDelay(),
                cancellationToken);

            if (!MouseInput.Mouse.MoveMouse(
                    new SharpDX.Vector2(targetPosition.X, targetPosition.Y)))
            {
                return false;
            }

            await Task.Delay(
                Settings.GetRandomizedActionDelay(),
                cancellationToken);

            if (mouseButton == MouseButtons.Right)
                MouseInput.Mouse.RightDown();
            else
                MouseInput.Mouse.LeftDown();

            await Task.Delay(
                Settings.GetRandomizedActionDelay(),
                cancellationToken);

            if (mouseButton == MouseButtons.Right)
                MouseInput.Mouse.RightUp();
            else
                MouseInput.Mouse.LeftUp();

            await Task.Delay(
                Settings.GetRandomizedActionDelay(),
                cancellationToken);

            return true;
        }

        /// <summary>Moves the cursor to the given screen position.</summary>
        public static async SyncTask<bool> MoveMouse(
            NumVector2 targetPosition,
            CancellationToken cancellationToken = default)
        {
            if (Settings.UseInputHumanizer)
            {
                var controller = GetInputHumanizerController();

                if (controller != null)
                {
                    return await controller.MoveMouse(
                        targetPosition,
                        cancellationToken);
                }
            }

            await Task.Delay(
                Settings.GetRandomizedActionDelay(),
                cancellationToken);

            var moved = MouseInput.Mouse.MoveMouse(
                new SharpDX.Vector2(targetPosition.X, targetPosition.Y));

            await Task.Delay(
                Settings.GetRandomizedActionDelay(),
                cancellationToken);

            return moved;
        }

        /// <summary>
        /// Selects the current content of the focused input with Ctrl+A and
        /// types the replacement text over it.
        /// </summary>
        public static async SyncTask<bool> ReplaceText(
            string text,
            CancellationToken cancellationToken = default)
        {
            if (text == null)
                return false;

            if (Settings.UseInputHumanizer)
            {
                var controller = GetInputHumanizerController();

                if (controller != null)
                {
                    await Task.Delay(
                        controller.GenerateDelay(),
                        cancellationToken);

                    if (!KeyboardInput.Keyboard.PressControlA())
                        return false;

                    await Task.Delay(
                        controller.GenerateDelay(),
                        cancellationToken);

                    foreach (var character in text)
                    {
                        await Task.Delay(
                            controller.GenerateDelay(),
                            cancellationToken);

                        KeyboardInput.Keyboard.TypeCharacter(character);
                    }

                    return true;
                }
            }

            await Task.Delay(
                Settings.GetRandomizedActionDelay(),
                cancellationToken);

            if (!KeyboardInput.Keyboard.PressControlA())
                return false;

            await Task.Delay(
                Settings.GetRandomizedActionDelay(),
                cancellationToken);

            var typed = KeyboardInput.Keyboard.TypeText(text);

            await Task.Delay(
                Settings.GetRandomizedActionDelay(),
                cancellationToken);

            return typed;
        }

        /// <summary>
        /// Releases the InputHumanizer input lock if one is held. Intentionally
        /// ignores the <c>UseInputHumanizer</c> toggle: a held controller must be
        /// released even when the user disabled the toggle mid-sequence.
        /// </summary>
        public static async SyncTask<bool> ReleaseControl()
        {
            if (_inputHumanizerControllerInstance != null)
            {
                return await _inputHumanizerControllerInstance.ReleaseControl();
            }

            return true;
        }

        /// <summary>
        /// Returns the cached InputHumanizer controller, acquiring one through the
        /// plugin bridge on first use.
        /// </summary>
        /// <remarks>
        /// When InputHumanizer is not loaded at all, the <c>UseInputHumanizer</c>
        /// toggle is switched off so the sequence permanently falls back to raw
        /// input. When another plugin merely holds the input lock, this returns
        /// null and the acquisition is retried on the next input action.
        /// </remarks>
        private static IInputController GetInputHumanizerController()
        {
            if (_inputHumanizerControllerInstance == null)
            {
                var tryGetInputController =
                    Plugin.GameController.PluginBridge
                        .GetMethod<Func<string, IInputController>>(
                            "InputHumanizer.TryGetInputController");

                if (tryGetInputController == null)
                {
                    Settings.UseInputHumanizer.Value = false;
                    return null;
                }

                _inputHumanizerControllerInstance =
                    tryGetInputController("SellMyShit");
            }

            return _inputHumanizerControllerInstance;
        }

        /// <summary>Disposes the cached InputHumanizer controller.</summary>
        public static void ReleaseResources()
        {
            if (_inputHumanizerControllerInstance != null)
            {
                _inputHumanizerControllerInstance.Dispose();
                _inputHumanizerControllerInstance = null;
            }
        }
    }
}
