// Copyright (C) Funplay. Licensed under MIT.

#if ENABLE_INPUT_SYSTEM
using System;
using System.ComponentModel;
using System.Threading.Tasks;
using Funplay.Editor.Tools.Helpers;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.LowLevel;

namespace Funplay.Editor.Tools.Builtins
{
    [ToolProvider("InputSimulation")]
    internal static class InputSimulationFunctions
    {
        [Description("Simulate keyboard input in Play Mode that game code sees, also while the Editor or Game View is unfocused. Events go through the player loop's input update, so Keyboard.current.<key>.wasPressedThisFrame is true in the game's Update() (delivery is confirmed: delivered, delivered_frame, edge_seen). tap: down, then up one player frame after delivery (or after a 0.5 s cap, or on Play exit, so stuck frames never leave a key down); returns after the release. duration > 0 holds the key (max 5 s) and returns once the press is delivered. press/release: hold down or let go. For a chord (e.g. Ctrl+Z) pass keys instead of key: all keys go down together and are released together. For Play Mode the Input System's focus settings (backgroundBehavior, editorInputBehaviorInPlayMode) are switched so input reaches the game, and restored when Play Mode ends.")]
        public static Task<object> SimulateKeyPress(
            [ToolParam("Key name, for example W, Space, LeftShift, E, or 1", Required = false)] string key = null,
            [ToolParam("Duration in seconds to hold the key, max 5. Use 0 for a tap.", Required = false)] float duration = 0f,
            [ToolParam("Action type: press, release, or tap", Required = false)] string action = "tap",
            [ToolParam("Chord: JSON array or comma-separated key names held together, e.g. [\"LeftCtrl\",\"Z\"]. Replaces key.", Required = false)] string keys = null)
        {
            if (!EditorApplication.isPlaying)
                return Done(ToolResultFormatter.ErrorMessage("PLAY_MODE_REQUIRED", "SimulateKeyPress only works in Play Mode."));

            try
            {
                var keyboard = EnsureKeyboard();
                if (keyboard == null)
                    return Done(ToolResultFormatter.ErrorMessage("INPUT_DEVICE_NOT_FOUND", "No keyboard device found in Input System"));

                var names = !string.IsNullOrWhiteSpace(keys) ? ParseKeyList(keys) : string.IsNullOrWhiteSpace(key) ? null : new[] { key.Trim() };
                if (names == null)
                    return Done(ToolResultFormatter.ErrorMessage("KEY_REQUIRED", "Pass key, or keys for a chord."));
                if (names.Length == 0)
                    return Done(ToolResultFormatter.ErrorMessage("KEY_REQUIRED", "keys is empty."));
                var codes = new Key[names.Length];
                for (var i = 0; i < names.Length; i++)
                {
                    var control = FindKey(keyboard, names[i]);
                    if (control == null)
                        return Done(ToolResultFormatter.ErrorMessage("KEY_NOT_RECOGNIZED", $"Key '{names[i]}' not recognized. Examples: W, A, S, D, Space, LeftShift, LeftCtrl, Z, Escape, 1, 2"));
                    codes[i] = control.keyCode;
                }

                var mode = (action ?? "tap").Trim().ToLowerInvariant();
                if (mode != "press" && mode != "release") mode = duration > 0f ? "hold" : "tap";
                return KeyInjector.Run(keyboard, codes, string.Join("+", names), mode, duration);
            }
            catch (Exception ex)
            {
                return Done(ToolResultFormatter.Exception(ex));
            }
        }

        [Description("Simulate multiple keys held simultaneously in Play Mode, for example W plus LeftShift. Same delivery as simulate_key_press(keys=[...], duration).")]
        public static Task<object> SimulateKeyCombo(
            [ToolParam("Comma-separated key names, for example 'W,LeftShift' or 'A,Space'")] string keys,
            [ToolParam("Duration in seconds to hold the combo", Required = false)] float duration = 0.5f)
        {
            return SimulateKeyPress(null, Mathf.Clamp(duration, 0.05f, 5f), "tap", keys);
        }

        private static Task<object> Done(object result) => Task.FromResult(result);

        [Description("Simulate a mouse drag from one screen position to another in Play Mode using the Unity Input System.")]
        public static string SimulateMouseDrag(
            [ToolParam("Start X coordinate (pixels by default)")] float start_x,
            [ToolParam("Start Y coordinate (pixels by default)")] float start_y,
            [ToolParam("End X coordinate (pixels by default)")] float end_x,
            [ToolParam("End Y coordinate (pixels by default)")] float end_y,
            [ToolParam("Duration of the drag in seconds", Required = false)] float duration = 0.5f,
            [ToolParam("Mouse button: left, right, or middle", Required = false)] string button = "left",
            [ToolParam("render_pixels, image_pixels or normalized", Required = false)] string coordinate_space = "render_pixels",
            [ToolParam("top_left (default for image_pixels) or bottom_left (default otherwise)", Required = false)] string origin = null,
            [ToolParam("Fresh screenshot ID, required for image_pixels", Required = false)] string capture_id = null)
        {
            if (!EditorApplication.isPlaying)
                return ToolResultFormatter.ErrorMessage("PLAY_MODE_REQUIRED", "SimulateMouseDrag only works in Play Mode.");

            try
            {
                if (!VisualCoordinates.TryResolve(start_x, start_y, coordinate_space, origin, capture_id, out var startPoint, out var geometry, out var coordinateError) ||
                    !VisualCoordinates.TryResolve(end_x, end_y, coordinate_space, origin, capture_id, out var endPoint, out _, out coordinateError))
                    return ToolResultFormatter.Error(coordinateError);
                start_x = startPoint.x; start_y = startPoint.y; end_x = endPoint.x; end_y = endPoint.y;
                var mouse = EnsureMouse();
                if (mouse == null)
                    return ToolResultFormatter.ErrorMessage("INPUT_DEVICE_NOT_FOUND", "No mouse device found in Input System");

                duration = Mathf.Clamp(duration, 0.1f, 3f);
                // The mouse must reach the game while the Editor is unfocused, like simulated keys.
                Funplay.Editor.State.InputFocusOverride.Apply();
                var pressButton = GetMouseButton(mouse, button);
                VideoRecordingFunctions.RecordMarker("drag", "Mouse drag events queued", startPoint, endPoint, geometry);

                InputState.Change(mouse.position, new Vector2(start_x, start_y));
                QueueStateEvent(mouse, pressEvent =>
                {
                    mouse.position.WriteValueIntoEvent(new Vector2(start_x, start_y), pressEvent);
                    pressButton.WriteValueIntoEvent(1f, pressEvent);
                });

                int steps = Mathf.Max(5, Mathf.RoundToInt(duration * 30));
                for (int i = 1; i < steps; i++)
                {
                    float t = (float)i / steps;
                    float curX = Mathf.Lerp(start_x, end_x, t);
                    float curY = Mathf.Lerp(start_y, end_y, t);

                    QueueStateEvent(mouse, moveEvent =>
                    {
                        mouse.position.WriteValueIntoEvent(new Vector2(curX, curY), moveEvent);
                        pressButton.WriteValueIntoEvent(1f, moveEvent);
                    });
                }

                QueueStateEvent(mouse, releaseEvent =>
                {
                    mouse.position.WriteValueIntoEvent(new Vector2(end_x, end_y), releaseEvent);
                });

                return Newtonsoft.Json.JsonConvert.SerializeObject(Response.Success("Mouse drag events dispatched.", new
                { start = new { x = start_x, y = start_y }, end = new { x = end_x, y = end_y }, geometry, steps,
                    timing = "Legacy synchronous dispatch: duration sets the interpolation step count, not wall-clock playback. Verify scrolling with a recording." }));
            }
            catch (Exception ex)
            {
                return ToolResultFormatter.Exception(ex);
            }
        }

        /// <summary>Key names from a JSON array (["LeftCtrl","Z"]) or a comma/plus-separated list ("LeftCtrl,Z").</summary>
        internal static string[] ParseKeyList(string keys)
        {
            var text = (keys ?? string.Empty).Trim();
            if (text.StartsWith("[", StringComparison.Ordinal))
            {
                var parsed = Newtonsoft.Json.JsonConvert.DeserializeObject<string[]>(text) ?? Array.Empty<string>();
                return Array.FindAll(Array.ConvertAll(parsed, k => (k ?? string.Empty).Trim()), k => k.Length > 0);
            }
            return Array.FindAll(Array.ConvertAll(text.Split(',', '+'), k => k.Trim()), k => k.Length > 0);
        }

        private static void QueueStateEvent(InputDevice device, Action<InputEventPtr> writeState)
        {
            if (device == null || writeState == null)
                return;

            using (StateEvent.From(device, out var eventPtr))
            {
                writeState(eventPtr);
                InputSystem.QueueEvent(eventPtr);
            }

            // Mouse drags keep the immediate update: without it every step would merge into one
            // player update. Keyboard input goes through KeyInjector instead; while simulated keys wait
            // for the game's input update, an extra update would consume their wasPressedThisFrame edge.
            if (!KeyInjector.HasPending)
                KeyInjector.RunManualUpdate();
        }

        private static Keyboard EnsureKeyboard()
        {
            var keyboard = Keyboard.current;
            if (keyboard != null)
                return keyboard;

            try
            {
                keyboard = InputSystem.GetDevice<Keyboard>();
                if (keyboard != null)
                    return keyboard;

                return InputSystem.AddDevice<Keyboard>();
            }
            catch
            {
                return null;
            }
        }

        private static Mouse EnsureMouse()
        {
            var mouse = Mouse.current;
            if (mouse != null)
                return mouse;

            try
            {
                mouse = InputSystem.GetDevice<Mouse>();
                if (mouse != null)
                    return mouse;

                return InputSystem.AddDevice<Mouse>();
            }
            catch
            {
                return null;
            }
        }

        private static KeyControl FindKey(Keyboard keyboard, string keyName)
        {
            if (keyboard == null || string.IsNullOrWhiteSpace(keyName))
                return null;

            try
            {
                var control = keyboard[keyName.ToLowerInvariant()] as KeyControl;
                if (control != null)
                    return control;
            }
            catch
            {
            }

            switch (keyName.Trim().ToLowerInvariant())
            {
                case "w": return keyboard.wKey;
                case "a": return keyboard.aKey;
                case "s": return keyboard.sKey;
                case "d": return keyboard.dKey;
                case "e": return keyboard.eKey;
                case "q": return keyboard.qKey;
                case "r": return keyboard.rKey;
                case "f": return keyboard.fKey;
                case "space": return keyboard.spaceKey;
                case "leftshift":
                case "lshift":
                case "shift":
                    return keyboard.leftShiftKey;
                case "rightshift":
                case "rshift":
                    return keyboard.rightShiftKey;
                case "leftctrl":
                case "lctrl":
                case "ctrl":
                    return keyboard.leftCtrlKey;
                case "leftalt":
                case "lalt":
                case "alt":
                    return keyboard.leftAltKey;
                case "tab": return keyboard.tabKey;
                case "escape":
                case "esc":
                    return keyboard.escapeKey;
                case "enter":
                case "return":
                    return keyboard.enterKey;
                case "backspace":
                    return keyboard.backspaceKey;
                case "1": return keyboard.digit1Key;
                case "2": return keyboard.digit2Key;
                case "3": return keyboard.digit3Key;
                case "4": return keyboard.digit4Key;
                case "5": return keyboard.digit5Key;
                case "6": return keyboard.digit6Key;
                case "7": return keyboard.digit7Key;
                case "8": return keyboard.digit8Key;
                case "9": return keyboard.digit9Key;
                case "0": return keyboard.digit0Key;
                default:
                    return null;
            }
        }

        private static ButtonControl GetMouseButton(Mouse mouse, string button)
        {
            switch ((button ?? "left").Trim().ToLowerInvariant())
            {
                case "right":
                    return mouse.rightButton;
                case "middle":
                    return mouse.middleButton;
                default:
                    return mouse.leftButton;
            }
        }
    }
}
#endif
