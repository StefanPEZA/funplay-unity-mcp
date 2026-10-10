// Copyright (C) Funplay. Licensed under MIT.
#if ENABLE_INPUT_SYSTEM
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Funplay.Editor.State;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

namespace Funplay.Editor.Tools.Helpers
{
    /// <summary>
    /// Keyboard input that game code sees. Events are queued without calling InputSystem.Update():
    /// an update outside the player loop would consume the press, and the next player update would
    /// clear wasPressedThisFrame before any game Update() polls it. The player loop's own input update
    /// processes them instead, and delivery is confirmed in InputSystem.onAfterUpdate during that
    /// update (reading device state from an editor callback would read the editor buffers).
    /// A tap is released at least one player frame after delivery, or after a wall-clock cap, or on
    /// Play exit, so stuck frames never leave a key down.
    /// </summary>
    internal static class KeyInjector
    {
        internal const double DeliveryTimeoutSeconds = 2.0;
        internal const double ReleaseCapSeconds = 0.5;
        internal const double MaxHoldSeconds = 5.0;

        // Simulated key state queued events must carry until the Input System has processed them:
        // StateEvent.From snapshots the device's current state, so a second event queued before the
        // first was processed would silently undo it (press A, press B → B's event has A up).
        private static readonly Dictionary<Key, bool> Overlay = new Dictionary<Key, bool>();
        private static readonly List<Delivery> Pending = new List<Delivery>();
        // Keys down as the game last saw them. An editor callback may find the editor's state buffers
        // current, so events are built from this game-side copy, not from StateEvent.From's snapshot.
        private static readonly HashSet<Key> GameKeys = new HashSet<Key>();
        private static bool _gameKeysValid;
        private static int _manualUpdateDepth;
        private static bool _hooked;

        internal sealed class Delivery
        {
            public Key[] Keys;
            public bool Pressed;
            public int Frame = -1;
            public bool EdgeSeen;
            public bool Done => Frame >= 0;
        }

        /// <summary>Keys MCP currently holds down (pressed and not released).</summary>
        internal static Key[] HeldKeys => Overlay.Where(p => p.Value).Select(p => p.Key).ToArray();

        private static void Hook()
        {
            if (_hooked) return;
            _hooked = true;
            InputSystem.onAfterUpdate += OnAfterUpdate;
            EditorApplication.playModeStateChanged += change =>
            {
                if (change == PlayModeStateChange.ExitingPlayMode || change == PlayModeStateChange.EnteredEditMode) Reset();
            };
        }

        /// <summary>Forget simulated state (devices are reset when Play Mode ends).</summary>
        internal static void Reset()
        {
            Overlay.Clear();
            Pending.Clear();
            GameKeys.Clear();
            _gameKeysValid = false;
        }

        /// <summary>Simulated keys are queued and not yet confirmed by a game input update.</summary>
        internal static bool HasPending => Pending.Count > 0;

        /// <summary>
        /// Runs an immediate InputSystem.Update() (mouse simulation) without letting it count as the
        /// game's update: an update outside the player loop is not one that game Update() observes.
        /// </summary>
        internal static void RunManualUpdate()
        {
            _manualUpdateDepth++;
            try { InputSystem.Update(); }
            finally { _manualUpdateDepth--; }
        }

        /// <summary>The update type that runs game code for the current Input System update mode.</summary>
        private static InputUpdateType GameUpdateType
        {
            get
            {
                switch (InputSystem.settings != null ? InputSystem.settings.updateMode : InputSettings.UpdateMode.ProcessEventsInDynamicUpdate)
                {
                    case InputSettings.UpdateMode.ProcessEventsInFixedUpdate: return InputUpdateType.Fixed;
                    case InputSettings.UpdateMode.ProcessEventsManually: return InputUpdateType.Manual;
                    default: return InputUpdateType.Dynamic;
                }
            }
        }

        private static void OnAfterUpdate()
        {
            if (_manualUpdateDepth > 0 || InputState.currentUpdateType != GameUpdateType || !EditorApplication.isPlaying) return;
            var keyboard = Keyboard.current;
            if (keyboard == null) return;
            GameKeys.Clear();
            foreach (var control in keyboard.allKeys)
                if (control != null && control.isPressed) GameKeys.Add(control.keyCode);
            _gameKeysValid = true;
            if (Pending.Count == 0) return;
            foreach (var delivery in Pending.ToArray())
            {
                if (!delivery.Keys.All(k => keyboard[k].isPressed == delivery.Pressed)) continue;
                delivery.Frame = Time.frameCount;
                delivery.EdgeSeen = delivery.Keys.All(k => delivery.Pressed ? keyboard[k].wasPressedThisFrame : keyboard[k].wasReleasedThisFrame);
                if (!delivery.Pressed)
                    foreach (var key in delivery.Keys)
                        if (Overlay.TryGetValue(key, out var held) && !held) Overlay.Remove(key);
                Pending.Remove(delivery);
            }
        }

        /// <summary>Queues one keyboard state event carrying every simulated key, and tracks its delivery.</summary>
        internal static Delivery Queue(Keyboard keyboard, Key[] keys, bool pressed)
        {
            Hook();
            foreach (var key in keys) Overlay[key] = pressed;
            using (StateEvent.From(keyboard, out var eventPtr))
            {
                if (_gameKeysValid && EditorApplication.isPlaying)
                    foreach (var control in keyboard.allKeys)
                        if (control != null) control.WriteValueIntoEvent(GameKeys.Contains(control.keyCode) ? 1f : 0f, eventPtr);
                foreach (var pair in Overlay) keyboard[pair.Key].WriteValueIntoEvent(pair.Value ? 1f : 0f, eventPtr);
                InputSystem.QueueEvent(eventPtr);
            }
            var delivery = new Delivery { Keys = keys, Pressed = pressed };
            Pending.Add(delivery);
            return delivery;
        }

        /// <summary>
        /// press: down and kept down. release: up. tap (hold 0): down, then up one player frame after
        /// delivery; completes after the release. hold &gt; 0: completes once the press is delivered and
        /// releases in the background after the hold time.
        /// </summary>
        internal static Task<object> Run(Keyboard keyboard, Key[] keys, string label, string mode, float holdSeconds)
        {
            var focus = InputFocusOverride.Apply();
            var session = new Session(keyboard, keys, label, mode, Mathf.Clamp(holdSeconds, 0f, (float)MaxHoldSeconds), focus);
            return session.Start();
        }

        private sealed class Session
        {
            private readonly Key[] _keys;
            private readonly string _label, _mode, _focus;
            private readonly double _hold;
            private readonly Stopwatch _clock = Stopwatch.StartNew();
            private readonly TaskCompletionSource<object> _result = new TaskCompletionSource<object>();
            private readonly int _startFrame = Time.frameCount;
            private Delivery _press, _release;
            private double _releaseQueuedAt;
            private string _releaseReason;

            public Session(Keyboard keyboard, Key[] keys, string label, string mode, double hold, string focus)
            {
                _keys = keys;
                _label = label; _mode = mode; _hold = hold; _focus = focus;
                if (mode == "release") _release = Queue(keyboard, keys, false);
                else _press = Queue(keyboard, keys, true);
            }

            public Task<object> Start()
            {
                EditorApplication.update += Tick;
                EditorApplication.playModeStateChanged += OnPlayModeChanged;
                return _result.Task;
            }

            private double Now => _clock.Elapsed.TotalSeconds;

            private void Tick()
            {
                try { TickCore(); }
                catch (Exception ex)
                {
                    EditorApplication.update -= Tick;
                    EditorApplication.playModeStateChanged -= OnPlayModeChanged;
                    if (_press != null) Pending.Remove(_press);
                    if (_release != null) Pending.Remove(_release);
                    _result.TrySetResult(Response.Error("KEY_SIMULATION_FAILED", new { detail = ex.Message, held_keys = HeldKeys.Select(k => k.ToString()).ToArray() }));
                }
            }

            private void TickCore()
            {
                if (!EditorApplication.isPlaying) { Stop("Play Mode ended."); return; }
                switch (_mode)
                {
                    case "press":
                        if (_press.Done || Now >= DeliveryTimeoutSeconds) Stop(null);
                        return;
                    case "release":
                        if (_release.Done || Now >= DeliveryTimeoutSeconds) Stop(null);
                        return;
                }

                // tap / hold: answer once the press is in (hold) or the tap is over.
                if (_mode == "hold" && !_result.Task.IsCompleted && (_press.Done || Now >= DeliveryTimeoutSeconds)) Report(null);
                if (_release == null)
                {
                    var minimumHeld = Now >= _hold;
                    var frameAfterDelivery = _press.Done && Time.frameCount > _press.Frame;
                    if (minimumHeld && frameAfterDelivery) QueueRelease("one player frame after delivery");
                    else if (Now >= _hold + ReleaseCapSeconds) QueueRelease(_press.Done ? "wall-clock cap (no new player frame)" : "wall-clock cap (press not delivered)");
                    return;
                }
                if (_release.Done || Now >= _releaseQueuedAt + DeliveryTimeoutSeconds) Stop(null);
            }

            private void QueueRelease(string reason)
            {
                var keyboard = Keyboard.current;
                if (keyboard == null) { Stop("The keyboard device is gone."); return; }
                _release = Queue(keyboard, _keys, false);
                _releaseQueuedAt = Now;
                _releaseReason = reason;
            }

            private void OnPlayModeChanged(PlayModeStateChange change)
            {
                if (change == PlayModeStateChange.ExitingPlayMode) Stop("Play Mode ended; the Input System resets devices, so no key stays down.");
            }

            private void Stop(string note)
            {
                EditorApplication.update -= Tick;
                EditorApplication.playModeStateChanged -= OnPlayModeChanged;
                // A release that never got processed must not leave the overlay holding the key.
                if (_release != null && !_release.Done) Pending.Remove(_release);
                if (_press != null && !_press.Done) Pending.Remove(_press);
                Report(note);
            }

            private void Report(string note)
            {
                if (_result.Task.IsCompleted) return;
                var delivered = _mode == "release" ? _release.Done : _press.Done;
                var stuck = PlayModeFrameMonitor.FramesStuck;
                string hint = null;
                if (!delivered)
                    hint = EditorApplication.isPaused
                        ? "Play Mode is paused, so no game input update ran. The key was released (it lands when Play Mode resumes); unpause and retry."
                        : stuck || !PlayModeFrameMonitor.EditorFocused && !Application.runInBackground
                        ? "Play Mode frames are not advancing, so the Input System never processed the key. Bring the Editor to the front or enable Keep Play Mode running in background."
                        : _focus.StartsWith("failed", StringComparison.Ordinal)
                            ? "The Input System may route keys to the Editor while the Game View is unfocused, and the focus settings could not be switched: " + _focus
                            : "The key event was not seen in a game input update. Check that the game reads Keyboard.current or actions bound to this keyboard.";
                var message = !delivered ? $"Keys '{_label}' were queued but not delivered to the game."
                    : _mode == "press" ? $"Key '{_label}' pressed (held down until released)."
                    : _mode == "release" ? $"Key '{_label}' released."
                    : _mode == "hold" ? $"Key '{_label}' down; released after {_hold:F2}s."
                    : _release != null && _release.Done ? $"Key '{_label}' tapped." : $"Key '{_label}' pressed; release queued.";
                _result.TrySetResult(Response.Success(message, new
                {
                    keys = _keys.Select(k => k.ToString()).ToArray(),
                    action = _mode,
                    delivered,
                    delivered_frame = delivered ? (_mode == "release" ? _release.Frame : _press.Frame) : (int?)null,
                    // wasPressedThisFrame (or wasReleasedThisFrame) is true in the frame game Update() runs.
                    edge_seen = delivered ? (_mode == "release" ? _release.EdgeSeen : _press.EdgeSeen) : (bool?)null,
                    released = _mode == "tap" ? _release != null && _release.Done : (bool?)null,
                    released_frame = _mode == "tap" && _release != null && _release.Done ? _release.Frame : (int?)null,
                    release = _mode == "tap" ? _releaseReason : _mode == "hold" ? "scheduled" : null,
                    frames_waited = Time.frameCount - _startFrame,
                    waited_ms = (int)_clock.Elapsed.TotalMilliseconds,
                    held_keys = HeldKeys.Select(k => k.ToString()).ToArray(),
                    input_focus_override = _focus,
                    frames_stuck = stuck ? (bool?)true : null,
                    editor_focused = PlayModeFrameMonitor.EditorFocused,
                    note,
                    hint
                }));
            }
        }
    }
}
#endif
