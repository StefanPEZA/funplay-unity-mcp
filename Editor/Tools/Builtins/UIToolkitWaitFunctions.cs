// Copyright (C) Funplay. Licensed under MIT.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using Funplay.Editor.State;
using Funplay.Editor.Tools.Helpers;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Funplay.Editor.Tools.Builtins
{
    internal static partial class UIToolkitFunctions
    {
        internal const float MaxWaitSeconds = 10f;

        [Description("Wait (up to 10 s) until a UI Toolkit element reaches a state, checking once per Editor update. States: exists, displayed, hidden (absent counts), enabled, has_class:<c>, lacks_class:<c>, text:<t> (exact, case-insensitive) and settled (the element's and its ancestors' opacity, translate, scale, rotate, colours and bounds unchanged for settle_frames consecutive frames, and no observed transition still running). Use settled before capturing an animated dialog. Match by selector, text and/or element_path as for clicks. Schedulers and transitions only advance while Play Mode frames tick; a stall is reported instead of waiting it out. Returns WAIT_TIMEOUT with the last observed state.")]
        [ReadOnlyTool]
        public static Task<object> WaitForUiToolkit(
            [ToolParam("Panel host GameObject name/path/ID", Required = false)] string target = null,
            [ToolParam("Selector: Type#name.class compounds joined by space (descendant) or > (child), optional :nth-of-type(n)/:nth-child(n)", Required = false)] string selector = null,
            [ToolParam("Exact element text, case-insensitive", Required = false)] string text = null,
            [ToolParam("Element path relative to one host: child indices or '/'-separated selectors over direct children", Required = false)] string element_path = null,
            [ToolParam("exists, displayed, hidden, enabled, has_class:<c>, lacks_class:<c>, text:<t> or settled", Required = false)] string state = "displayed",
            [ToolParam("Give up after this many seconds, 0.1..10", Required = false)] float timeout_seconds = 5f,
            [ToolParam("settled only: consecutive unchanged frames, 2..60", Required = false)] int settle_frames = 3)
        {
            return WaitForElementAsync(target, selector, text, element_path, state, timeout_seconds, settle_frames);
        }

        /// <summary>A parsed wait state: kind plus the argument after ':' (class name or text).</summary>
        internal sealed class WaitState
        {
            public string Kind, Argument;

            private static readonly string[] Plain = { "exists", "displayed", "hidden", "enabled", "settled" };
            private static readonly string[] WithArgument = { "has_class", "lacks_class", "text" };

            public static WaitState Parse(string state)
            {
                var value = string.IsNullOrWhiteSpace(state) ? "displayed" : state.Trim();
                var colon = value.IndexOf(':');
                var kind = (colon < 0 ? value : value.Substring(0, colon)).Trim().ToLowerInvariant();
                var argument = colon < 0 ? null : value.Substring(colon + 1);
                if (Plain.Contains(kind) && argument == null) return new WaitState { Kind = kind };
                if (WithArgument.Contains(kind) && argument != null)
                {
                    if (kind != "text") argument = argument.Trim();
                    if (kind != "text" && argument.Length == 0)
                        throw new UIToolkitException("INVALID_WAIT_STATE", $"'{kind}:' needs a class name, e.g. has_class:open.");
                    return new WaitState { Kind = kind, Argument = argument };
                }
                throw new UIToolkitException("INVALID_WAIT_STATE",
                    $"Unknown state '{state}'. Use exists, displayed, hidden, enabled, has_class:<c>, lacks_class:<c>, text:<t> or settled.");
            }

            /// <summary>Whether <paramref name="element"/> (null when absent) is in this state; settled is evaluated by the waiter.</summary>
            public bool IsMet(VisualElement element)
            {
                if (Kind == "hidden") return element == null || element.panel == null || !UIToolkitPanels.IsDisplayed(element);
                if (element == null || element.panel == null) return false;
                switch (Kind)
                {
                    case "exists": return true;
                    case "displayed": return UIToolkitPanels.IsDisplayed(element);
                    case "enabled": return element.enabledInHierarchy;
                    case "has_class": return element.ClassListContains(Argument);
                    case "lacks_class": return !element.ClassListContains(Argument);
                    case "text": return string.Equals(UIToolkitPanels.GetText(element) ?? string.Empty, Argument, StringComparison.OrdinalIgnoreCase);
                    default: return false;
                }
            }
        }

        /// <summary>
        /// Visual values of the element and its ancestors that animate during transitions and fades.
        /// Two equal signatures mean nothing visible moved between the frames.
        /// </summary>
        internal static float[] VisualSignature(VisualElement element)
        {
            var values = new List<float>();
            var bound = element.worldBound;
            values.Add(bound.x); values.Add(bound.y); values.Add(bound.width); values.Add(bound.height);
            for (var current = element; current != null; current = current.hierarchy.parent)
            {
                var style = current.resolvedStyle;
                values.Add(style.opacity);
                var translate = style.translate; values.Add(translate.x); values.Add(translate.y); values.Add(translate.z);
                var scale = style.scale.value; values.Add(scale.x); values.Add(scale.y); values.Add(scale.z);
                values.Add(style.rotate.angle.ToDegrees());
                AddColor(values, style.color);
                AddColor(values, style.backgroundColor);
                AddColor(values, style.unityBackgroundImageTintColor);
                AddColor(values, style.borderTopColor);
            }
            return values.ToArray();
        }

        private static void AddColor(List<float> values, Color color)
        {
            values.Add(color.r); values.Add(color.g); values.Add(color.b); values.Add(color.a);
        }

        internal static bool SameSignature(float[] a, float[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (var i = 0; i < a.Length; i++)
            {
                var same = float.IsNaN(a[i]) ? float.IsNaN(b[i]) : Mathf.Abs(a[i] - b[i]) <= 1e-4f;
                if (!same) return false;
            }
            return true;
        }

        internal static Task<object> WaitForElementAsync(string target, string selector, string text, string elementPath, string state, float timeoutSeconds, int settleFrames)
        {
            WaitState parsed;
            try
            {
                parsed = WaitState.Parse(state);
                if (float.IsNaN(timeoutSeconds) || float.IsInfinity(timeoutSeconds))
                    throw new ArgumentException("timeout_seconds must be a number.");
                // Validates selector syntax and the element query before waiting.
                UIToolkitPanels.Selector.Parse(selector, text);
                if (string.IsNullOrEmpty(selector) && string.IsNullOrEmpty(text) && string.IsNullOrEmpty(elementPath))
                    throw new ArgumentException("Pass selector, text or element_path.");
            }
            catch (UIToolkitException ex) { return Task.FromResult(Response.Error(ex.Code, new { detail = ex.Message, details = ex.Details })); }
            catch (ArgumentException ex) { return Task.FromResult(Response.Error("INVALID_ARGUMENT", new { detail = ex.Message })); }

            var waiter = new ElementWaiter(target, selector, text, elementPath, parsed,
                Mathf.Clamp(timeoutSeconds, 0.1f, MaxWaitSeconds), Mathf.Clamp(settleFrames, 2, 60));
            return waiter.Run();
        }

        /// <summary>
        /// Re-resolves the element on every Editor update (it may not exist yet, or be rebuilt) and
        /// completes when the state holds. Never blocks: the MCP call stays pending on a Task.
        /// </summary>
        private sealed class ElementWaiter
        {
            private const double StallFailSeconds = 2.0;
            private const double UnbalancedTransitionGraceSeconds = 2.0;

            private readonly string _target, _selector, _text, _elementPath;
            private readonly WaitState _state;
            private readonly float _timeout;
            private readonly int _settleFrames;
            private readonly TaskCompletionSource<object> _done = new TaskCompletionSource<object>();
            private readonly Stopwatch _clock = Stopwatch.StartNew();
            private readonly int _startPlayerFrame = Time.frameCount;

            private int _editorTicks, _lastPlayerFrame = -1, _stableFrames;
            private float[] _lastSignature;
            private double _stableSince;
            private string _lastLookupError;
            // The host resolved and the element is not in its tree; host-level misses (wrong target, no
            // panels yet) are not proof that an element is hidden.
            private bool _absentConfirmed;
            private Component _host;
            private string _path;
            private VisualElement _element;

            // Transition hint: transitions started after the wait began and not yet ended.
            private readonly List<VisualElement> _watched = new List<VisualElement>();
            private readonly HashSet<(VisualElement, string)> _running = new HashSet<(VisualElement, string)>();
            private int _transitionsObserved;

            public ElementWaiter(string target, string selector, string text, string elementPath, WaitState state, float timeout, int settleFrames)
            {
                _target = target; _selector = selector; _text = text; _elementPath = elementPath;
                _state = state; _timeout = timeout; _settleFrames = settleFrames;
            }

            public Task<object> Run()
            {
                EditorApplication.update += Tick;
                AssemblyReloadEvents.beforeAssemblyReload += Abort;
                EditorApplication.playModeStateChanged += OnPlayModeChanged;
                Tick();
                return _done.Task;
            }

            private bool PlayerTicking => EditorApplication.isPlaying && !EditorApplication.isPaused;

            private void Tick()
            {
                if (_done.Task.IsCompleted) return;
                _editorTicks++;
                try
                {
                    Resolve();
                    if (_element?.panel != null) UIToolkitPanels.ValidateLayout(_element.panel);

                    // Frames: in Play Mode only a new player frame counts, so a stalled loop never looks "settled".
                    var newFrame = true;
                    if (PlayerTicking)
                    {
                        newFrame = Time.frameCount != _lastPlayerFrame;
                        _lastPlayerFrame = Time.frameCount;
                    }

                    if (_state.Kind == "settled" && EditorApplication.isPlaying && EditorApplication.isPaused)
                    {
                        Finish(false, "PLAY_MODE_PAUSED", "Play Mode is paused: transitions don't advance, so 'settled' can't be judged. Unpause first.");
                        return;
                    }
                    if (_state.Kind == "settled")
                    {
                        if (_element != null && _element.panel != null && newFrame && TrackSettled()) { Finish(true); return; }
                    }
                    else if (_element == null && _state.Kind == "hidden" ? _absentConfirmed : _state.IsMet(_element)) { Finish(true); return; }

                    if (PlayerTicking && PlayModeFrameMonitor.SecondsSinceLastFrame is double stalled && stalled > StallFailSeconds)
                    {
                        Finish(false, "PLAY_MODE_FRAMES_STALLED",
                            "Play Mode frames stopped advancing, so schedulers and transitions cannot progress. Bring the Editor to the front, or enable Keep Play Mode running in background.");
                        return;
                    }
                    if (_clock.Elapsed.TotalSeconds >= _timeout) { Finish(false, "WAIT_TIMEOUT", null); return; }
                    if (!EditorApplication.isPlaying) EditorApplication.QueuePlayerLoopUpdate();
                }
                catch (UIToolkitException ex) when (ex.Code == "AMBIGUOUS_ELEMENT" || ex.Code == "UNSUPPORTED_SELECTOR" || ex.Code == "AMBIGUOUS_TARGET" || ex.Code == "NOT_A_PANEL_HOST")
                {
                    Complete(Response.Error(ex.Code, new { detail = ex.Message, details = ex.Details }));
                }
                catch (Exception ex)
                {
                    Complete(Response.Error("UI_TOOLKIT_WAIT_FAILED", new { detail = ex.Message }));
                }
            }

            /// <summary>Finds the element now; absence (not built yet, scene loading) is a normal waiting state.</summary>
            private void Resolve()
            {
                Component host = null; string path = null; VisualElement element = null;
                try
                {
                    (host, path, element) = ResolveUniqueElement(_target, _selector, _text, _elementPath);
                    _lastLookupError = null;
                    _absentConfirmed = false;
                }
                catch (UIToolkitException ex) when (ex.Code == "ELEMENT_NOT_FOUND" || ex.Code == "ROOT_NOT_READY" || ex.Code == "NO_UI_TOOLKIT_HOSTS" || ex.Code == "TARGET_NOT_FOUND")
                {
                    _lastLookupError = ex.Code;
                    _absentConfirmed = ex.Code == "ELEMENT_NOT_FOUND" || ex.Code == "ROOT_NOT_READY";
                }

                if (!ReferenceEquals(element, _element))
                {
                    _lastSignature = null;
                    _stableFrames = 0;
                    if (_state.Kind == "settled") Watch(element);
                }
                _host = host; _path = path; _element = element;
            }

            private bool TrackSettled()
            {
                var signature = VisualSignature(_element);
                var now = _clock.Elapsed.TotalSeconds;
                if (SameSignature(signature, _lastSignature)) _stableFrames++;
                else { _stableFrames = 0; _stableSince = now; }
                _lastSignature = signature;
                if (_stableFrames + 1 < _settleFrames) return false;
                // A transition still in its delay leaves values unchanged; trust the events unless they
                // look lost (the element was rebuilt mid-transition) and nothing moved for a while.
                return _running.Count == 0 || now - _stableSince >= UnbalancedTransitionGraceSeconds;
            }

            private void Watch(VisualElement element)
            {
                Unwatch();
                for (var current = element; current != null; current = current.hierarchy.parent)
                {
                    current.RegisterCallback<TransitionRunEvent>(OnTransitionRun);
                    current.RegisterCallback<TransitionEndEvent>(OnTransitionEnd);
                    current.RegisterCallback<TransitionCancelEvent>(OnTransitionCancel);
                    _watched.Add(current);
                }
            }

            private void Unwatch()
            {
                foreach (var element in _watched)
                {
                    element.UnregisterCallback<TransitionRunEvent>(OnTransitionRun);
                    element.UnregisterCallback<TransitionEndEvent>(OnTransitionEnd);
                    element.UnregisterCallback<TransitionCancelEvent>(OnTransitionCancel);
                }
                _watched.Clear();
                _running.Clear();
            }

            private void OnTransitionRun(TransitionRunEvent evt)
            {
                if (!ReferenceEquals(evt.target, evt.currentTarget)) return;
                _transitionsObserved++;
                foreach (var property in evt.stylePropertyNames) _running.Add(((VisualElement)evt.currentTarget, property.ToString()));
            }

            private void OnTransitionEnd(TransitionEndEvent evt) => Ended(evt.target, evt.currentTarget, evt.stylePropertyNames);
            private void OnTransitionCancel(TransitionCancelEvent evt) => Ended(evt.target, evt.currentTarget, evt.stylePropertyNames);

            private void Ended(IEventHandler target, IEventHandler current, StylePropertyNameCollection properties)
            {
                if (!ReferenceEquals(target, current)) return;
                foreach (var property in properties) _running.Remove(((VisualElement)current, property.ToString()));
            }

            private void OnPlayModeChanged(PlayModeStateChange change)
            {
                if (change == PlayModeStateChange.ExitingPlayMode || change == PlayModeStateChange.ExitingEditMode)
                    Finish(false, "PLAY_MODE_CHANGED", "The Play Mode state changed while waiting; the panels were rebuilt. Wait again.");
            }

            private void Abort() =>
                Finish(false, "INTERRUPTED_BY_RELOAD", "A script reload started while waiting. Wait again after the Editor is ready.");

            private void Finish(bool met, string code = null, string hint = null)
            {
                if (_done.Task.IsCompleted) return;
                try { FinishCore(met, code, hint); }
                catch (Exception ex) { Complete(Response.Error(code ?? "UI_TOOLKIT_WAIT_FAILED", new { detail = ex.Message, hint })); }
            }

            private void FinishCore(bool met, string code, string hint)
            {
                var frames = EditorApplication.isPlaying ? Time.frameCount - _startPlayerFrame : _editorTicks;
                var waitedMs = (int)_clock.Elapsed.TotalMilliseconds;
                object element = null;
                if (_element != null)
                {
                    var mapping = _element.panel != null && _host != null ? MappingFor(_host, _element.panel) : null;
                    element = UIToolkitPanels.DescribeElement(_element, _path, mapping, TryGetRenderHeight(out _), _state.Kind == "settled");
                }

                if (met)
                {
                    Complete(Response.Success(
                        _element == null ? $"State '{Describe()}' reached: no matching element." : $"{Label(_element)} reached '{Describe()}'.",
                        new
                        {
                            state = Describe(),
                            waited_ms = waitedMs,
                            frames,
                            frame_source = PlayerTicking ? "player" : "editor",
                            host_id = _host != null ? ObjectIdHelper.GetSerializableId(_host.gameObject) : null,
                            element,
                            transitions_observed = _state.Kind == "settled" ? _transitionsObserved : (int?)null,
                            unbalanced_transitions = _state.Kind == "settled" && _running.Count > 0 ? _running.Count : (int?)null
                        }));
                    return;
                }

                Complete(Response.Error(code, new
                {
                    state = Describe(),
                    waited_ms = waitedMs,
                    timeout_seconds = _timeout,
                    frames,
                    found = _element != null,
                    lookup = _lastLookupError,
                    element,
                    stable_frames = _state.Kind == "settled" ? _stableFrames + 1 : (int?)null,
                    transitions_running = _state.Kind == "settled" ? _running.Count : (int?)null,
                    is_playing = EditorApplication.isPlaying,
                    editor_focused = PlayModeFrameMonitor.EditorFocused,
                    hint = hint ?? (EditorApplication.isPlaying && !EditorApplication.isPaused ? null
                        : EditorApplication.isPaused ? "Play Mode is paused: schedulers and transitions do not advance."
                        : "Runtime UI scripts (schedulers, ExecuteLater) only run in Play Mode.")
                }));
            }

            private string Describe() => _state.Argument == null ? _state.Kind : _state.Kind + ":" + _state.Argument;

            private void Complete(object result)
            {
                EditorApplication.update -= Tick;
                AssemblyReloadEvents.beforeAssemblyReload -= Abort;
                EditorApplication.playModeStateChanged -= OnPlayModeChanged;
                Unwatch();
                _done.TrySetResult(result);
            }
        }
    }
}
