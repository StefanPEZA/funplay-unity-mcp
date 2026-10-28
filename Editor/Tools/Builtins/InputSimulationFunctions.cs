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

    }
}
#endif
