// Copyright (C) Funplay. Licensed under MIT.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using Funplay.Editor.Api;
using Funplay.Editor.Tools.Helpers;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Funplay.Editor.Tools.Builtins
{
    internal static partial class UIToolkitFunctions
    {
        [Description("Live style experiments on a UI Toolkit element (runtime only: nothing is saved, and Play Mode exit or a panel rebuild discards it). add_classes/remove_classes (comma-separated), inline {\"uss-property\": value | null} sets element.style the way UXML style=\"\" does (lengths px/%/auto, numbers, colours #hex/rgb()/rgba()/names, enum keywords like flex-end, translate/scale/rotate; margin, padding, border-width, border-color and border-radius shorthands; var() is not allowed inline; null removes the inline value so USS applies again). reload_stylesheets=true imports USS files changed on disk and re-applies them to live panels (auto-refresh is off in Play Mode, so edited USS otherwise stays stale). Afterwards it watches a few frames (and, up to 3 s, until transitions the change started have ended, so resolved_after is the end value; transitioning=true when one was still running) and reports reverted changes, which means project code (e.g. a responsive script toggling classes on geometry change) undid them. Match the element by selector, text and/or element_path as for clicks; reload_stylesheets alone needs no element.")]
        public static Task<object> StyleUiToolkit(
            [ToolParam("Panel host GameObject name/path/ID", Required = false)] string target = null,
            [ToolParam("Selector: Type#name.class compounds joined by space (descendant) or > (child), optional :nth-of-type(n)/:nth-child(n)", Required = false)] string selector = null,
            [ToolParam("Exact element text, case-insensitive", Required = false)] string text = null,
            [ToolParam("Element path relative to one host: child indices or '/'-separated selectors over direct children", Required = false)] string element_path = null,
            [ToolParam("Classes to add, comma-separated", Required = false)] string add_classes = null,
            [ToolParam("Classes to remove, comma-separated", Required = false)] string remove_classes = null,
            [ToolParam("JSON object of USS properties to set inline, null to remove, e.g. {\"background-color\":\"#ff0000\",\"width\":null}", Required = false)] string inline = null,
            [ToolParam("Import changed USS files and re-apply them to live panels", Required = false)] bool reload_stylesheets = false)
        {
            try
            {
                var add = SplitClasses(add_classes);
                var remove = SplitClasses(remove_classes);
                var both = add.Intersect(remove).ToList();
                if (both.Count > 0) throw new ArgumentException($"A class cannot be added and removed at once: {string.Join(", ", both)}.");
                var styles = ParseInline(inline);
                var hasQuery = !string.IsNullOrEmpty(selector) || !string.IsNullOrEmpty(text) || !string.IsNullOrEmpty(element_path);
                var changesElement = add.Count > 0 || remove.Count > 0 || styles.Count > 0;
                if (!changesElement && !reload_stylesheets) throw new ArgumentException("Pass add_classes, remove_classes, inline or reload_stylesheets.");
                if (changesElement && !hasQuery) throw new ArgumentException("Classes and inline styles need an element: pass selector, text or element_path.");

                object reload = null;
                if (reload_stylesheets) reload = ReloadStyleSheets(ResolveHosts(target));

                if (!changesElement)
                    return Task.FromResult(Response.Success("Style sheets reloaded.", new { reload, runtime_only = true }));

                var (host, path, element) = ResolveUniqueElement(target, selector, text, element_path);
                var panel = element.panel ?? throw new UIToolkitException("ROOT_NOT_READY", "The element is not attached to a panel.");
                UIToolkitPanels.ValidateLayout(panel);

                var before = styles.Select(s => (s.Uss, resolved: StyleIntrospection.ResolvedValue(element, s.Uss))).ToList();
                var classesBefore = element.GetClasses().ToArray();
                foreach (var cls in add) element.AddToClassList(cls);
                foreach (var cls in remove) element.RemoveFromClassList(cls);
                foreach (var style in styles) style.Property.SetValue(element.style, style.Value, null);
                UIToolkitPanels.ValidateLayout(panel);

                var watch = new StyleWatch(host, path, element, add, remove, styles, before, classesBefore, reload);
                return watch.Run();
            }
            catch (UIToolkitException ex) { return Task.FromResult(Response.Error(ex.Code, new { detail = ex.Message, details = ex.Details })); }
            catch (Exception ex) { return Task.FromResult(Response.Error("UI_TOOLKIT_STYLE_FAILED", new { detail = (ex as TargetInvocationException)?.InnerException?.Message ?? ex.Message })); }
        }

        private static List<string> SplitClasses(string value) =>
            (value ?? "").Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(c => c.Trim().TrimStart('.')).Where(c => c.Length > 0).Distinct().ToList();

        /// <summary>One inline assignment: the USS name, the IStyle property and the Style* value to set.</summary>
        internal sealed class InlineStyle
        {
            public string Uss;
            public PropertyInfo Property;
            public object Value;
            public bool Remove;
        }

        private static readonly Dictionary<string, string[]> InlineShorthands = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["margin"] = new[] { "margin-top", "margin-right", "margin-bottom", "margin-left" },
            ["padding"] = new[] { "padding-top", "padding-right", "padding-bottom", "padding-left" },
            ["border-width"] = new[] { "border-top-width", "border-right-width", "border-bottom-width", "border-left-width" },
            ["border-color"] = new[] { "border-top-color", "border-right-color", "border-bottom-color", "border-left-color" },
            ["border-radius"] = new[] { "border-top-left-radius", "border-top-right-radius", "border-bottom-right-radius", "border-bottom-left-radius" },
        };

        internal static List<InlineStyle> ParseInline(string inline)
        {
            var result = new List<InlineStyle>();
            if (string.IsNullOrWhiteSpace(inline)) return result;
            JObject json;
            try { json = JsonParse.ParseObject(inline); }
            catch (Exception ex) { throw new UIToolkitException("INVALID_INLINE_STYLE", "inline must be a JSON object of USS property → value or null: " + ex.Message); }
            foreach (var pair in json)
            {
                var name = pair.Key.Trim();
                var raw = pair.Value == null || pair.Value.Type == JTokenType.Null ? null : pair.Value.Type == JTokenType.String ? (string)pair.Value : pair.Value.ToString(Newtonsoft.Json.Formatting.None);
                if (raw != null && raw.IndexOf("var(", StringComparison.OrdinalIgnoreCase) >= 0)
                    throw new UIToolkitException("INVALID_INLINE_STYLE", $"{name}: var() is not allowed in inline styles; set the variable in USS or pass a literal value.");
                if (InlineShorthands.TryGetValue(name, out var longhands))
                {
                    var parts = raw == null ? null : SplitValues(raw);
                    if (parts != null && (parts.Count < 1 || parts.Count > 4)) throw new UIToolkitException("INVALID_INLINE_STYLE", $"{name} takes 1 to 4 values.");
                    for (var i = 0; i < 4; i++)
                    {
                        // CSS order: top right bottom left; missing values repeat the opposite side.
                        string part = null;
                        if (parts != null) part = parts.Count == 1 ? parts[0] : parts.Count == 2 ? parts[i % 2] : parts.Count == 3 ? parts[i == 3 ? 1 : i] : parts[i];
                        result.Add(ParseOne(longhands[i], part));
                    }
                    continue;
                }
                result.Add(ParseOne(name, raw));
            }
            return result;
        }

        private static InlineStyle ParseOne(string name, string raw)
        {
            // USS name → IStyle property (background-color → backgroundColor, -unity-font-style → unityFontStyleAndWeight).
            var property = typeof(IStyle).GetProperty(StyleIntrospection.CamelName(name.ToLowerInvariant()));
            if (property == null)
                throw new UIToolkitException("UNSUPPORTED_STYLE_PROPERTY", $"'{name}' is not a USS property that can be set inline (or not in this Unity version).");
            var type = property.PropertyType;
            if (raw == null) return new InlineStyle { Uss = name, Property = property, Value = Activator.CreateInstance(type, StyleKeyword.Null), Remove = true };
            var value = raw.Trim();
            try
            {
                if (string.Equals(value, "initial", StringComparison.OrdinalIgnoreCase))
                    return new InlineStyle { Uss = name, Property = property, Value = Activator.CreateInstance(type, StyleKeyword.Initial) };
                return new InlineStyle { Uss = name, Property = property, Value = ParseStyleValue(type, value) };
            }
            catch (UIToolkitException) { throw; }
            catch (Exception ex) { throw new UIToolkitException("INVALID_INLINE_STYLE", $"{name}: '{raw}' is not a valid value ({(ex as TargetInvocationException)?.InnerException?.Message ?? ex.Message})."); }
        }

        internal static object ParseStyleValue(Type type, string value)
        {
            if (type == typeof(StyleLength))
            {
                if (string.Equals(value, "auto", StringComparison.OrdinalIgnoreCase)) return new StyleLength(StyleKeyword.Auto);
                if (string.Equals(value, "none", StringComparison.OrdinalIgnoreCase)) return new StyleLength(StyleKeyword.None);
                return new StyleLength(ParseLength(value));
            }
            if (type == typeof(StyleFloat)) return new StyleFloat(ParseNumber(value));
            if (type == typeof(StyleInt)) return new StyleInt((int)ParseNumber(value));
            if (type == typeof(StyleColor))
            {
                if (!TryParseColor(value, out var color)) throw new UIToolkitException("INVALID_INLINE_STYLE", $"'{value}' is not a colour (#rrggbb[aa], rgb(), rgba() or a name).");
                return new StyleColor(color);
            }
            if (type == typeof(StyleTranslate))
            {
                var parts = SplitValues(value);
                if (parts.Count < 1 || parts.Count > 2) throw new UIToolkitException("INVALID_INLINE_STYLE", "translate takes one or two lengths.");
                return new StyleTranslate(new Translate(ParseLength(parts[0]), parts.Count > 1 ? ParseLength(parts[1]) : new Length(0)));
            }
            if (type == typeof(StyleScale))
            {
                var parts = SplitValues(value);
                if (parts.Count < 1 || parts.Count > 2) throw new UIToolkitException("INVALID_INLINE_STYLE", "scale takes one or two numbers.");
                var x = ParseNumber(parts[0]);
                return new StyleScale(new Scale(new Vector2(x, parts.Count > 1 ? ParseNumber(parts[1]) : x)));
            }
            if (type == typeof(StyleRotate))
            {
                var v = value.Trim().ToLowerInvariant();
                Angle angle = v.EndsWith("deg") ? Angle.Degrees(ParseNumber(v.Substring(0, v.Length - 3)))
                    : v.EndsWith("turn") ? Angle.Turns(ParseNumber(v.Substring(0, v.Length - 4)))
                    : v.EndsWith("grad") ? Angle.Gradians(ParseNumber(v.Substring(0, v.Length - 4)))
                    : v.EndsWith("rad") ? Angle.Radians(ParseNumber(v.Substring(0, v.Length - 3)))
                    : Angle.Degrees(ParseNumber(v));
                return new StyleRotate(new Rotate(angle));
            }
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(StyleEnum<>))
            {
                var enumType = type.GetGenericArguments()[0];
                var pascal = string.Concat(value.Split('-').Where(p => p.Length > 0).Select(p => char.ToUpperInvariant(p[0]) + p.Substring(1)));
                var name = Enum.GetNames(enumType).FirstOrDefault(n => string.Equals(n, pascal, StringComparison.OrdinalIgnoreCase));
                if (name == null)
                    throw new UIToolkitException("INVALID_INLINE_STYLE", $"'{value}' is not one of: {string.Join(", ", Enum.GetNames(enumType).Select(n => ToolRegistry.ToSnakeCase(n).Replace('_', '-')))}.");
                return Activator.CreateInstance(type, Enum.Parse(enumType, name));
            }
            throw new UIToolkitException("UNSUPPORTED_STYLE_PROPERTY", $"Inline values of type {type.Name} are not supported here; use USS or execute_code.");
        }

        private static List<string> SplitValues(string value)
        {
            // Split on spaces outside parentheses: rgb(1, 2, 3) stays one value.
            var parts = new List<string>();
            var depth = 0;
            var current = new System.Text.StringBuilder();
            foreach (var c in value.Trim())
            {
                if (c == '(') depth++;
                if (c == ')') depth--;
                if (depth == 0 && char.IsWhiteSpace(c)) { if (current.Length > 0) { parts.Add(current.ToString()); current.Clear(); } continue; }
                current.Append(c);
            }
            if (current.Length > 0) parts.Add(current.ToString());
            return parts;
        }

        private static Length ParseLength(string value)
        {
            var v = value.Trim().ToLowerInvariant();
            if (v == "auto") return Length.Auto();
            if (v.EndsWith("%")) return new Length(ParseNumber(v.Substring(0, v.Length - 1)), LengthUnit.Percent);
            if (v.EndsWith("px")) v = v.Substring(0, v.Length - 2);
            return new Length(ParseNumber(v), LengthUnit.Pixel);
        }

        private static float ParseNumber(string value)
        {
            if (!float.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || float.IsNaN(number) || float.IsInfinity(number))
                throw new UIToolkitException("INVALID_INLINE_STYLE", $"'{value}' is not a number.");
            return number;
        }

        private static bool TryParseColor(string value, out Color color)
        {
            var v = value.Trim();
            if (string.Equals(v, "transparent", StringComparison.OrdinalIgnoreCase)) { color = Color.clear; return true; }
            var match = System.Text.RegularExpressions.Regex.Match(v, @"^rgba?\(\s*([\d.]+)\s*,\s*([\d.]+)\s*,\s*([\d.]+)\s*(?:,\s*([\d.]+)\s*)?\)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (match.Success)
            {
                float Channel(int group) => float.Parse(match.Groups[group].Value, CultureInfo.InvariantCulture) / 255f;
                var alpha = match.Groups[4].Success ? float.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture) : 1f;
                color = new Color(Channel(1), Channel(2), Channel(3), Mathf.Clamp01(alpha));
                return true;
            }
            return ColorUtility.TryParseHtmlString(v, out color);
        }

        /// <summary>
        /// Imports the USS files the live panels use (unchanged files are skipped by the importer) and
        /// re-applies changed sheets: each holder's sheet list is rebuilt in its original order, which
        /// makes the panel re-resolve styles even where Live Reload does not reach runtime panels.
        /// </summary>
        internal static object ReloadStyleSheets(List<Component> hosts)
        {
            var holders = new Dictionary<StyleSheet, List<VisualElement>>();
            var hostRoots = UIToolkitPanels.HostRoots(hosts);
            foreach (var host in hosts)
            {
                var root = UIToolkitPanels.GetRoot(host);
                if (root == null) continue;
                foreach (var entry in UIToolkitPanels.Walk(root, exclude: hostRoots))
                    for (var i = 0; i < entry.Value.styleSheets.count; i++)
                    {
                        var sheet = entry.Value.styleSheets[i];
                        if (sheet == null) continue;
                        if (!holders.TryGetValue(sheet, out var list)) holders[sheet] = list = new List<VisualElement>();
                        if (!list.Contains(entry.Value)) list.Add(entry.Value);
                    }
            }

            var imported = new List<string>();
            var unchanged = new List<string>();
            foreach (var pair in holders)
            {
                var path = AssetDatabase.GetAssetPath(pair.Key);
                if (string.IsNullOrEmpty(path) || !path.EndsWith(".uss", StringComparison.OrdinalIgnoreCase)) continue;
                var hash = AssetDatabase.GetAssetDependencyHash(path);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                // The dependency hash covers the source file; an unchanged hash means nothing to re-apply.
                if (AssetDatabase.GetAssetDependencyHash(path) == hash) { unchanged.Add(path); continue; }
                imported.Add(path);
                foreach (var holder in pair.Value)
                {
                    var sheets = Enumerable.Range(0, holder.styleSheets.count).Select(i => holder.styleSheets[i]).ToList();
                    holder.styleSheets.Clear();
                    // The reimported asset may be a new object; re-load it by path.
                    var fresh = AssetDatabase.LoadAssetAtPath<StyleSheet>(path);
                    foreach (var sheet in sheets) holder.styleSheets.Add(ReferenceEquals(sheet, pair.Key) && fresh != null ? fresh : sheet);
                }
            }
            foreach (var host in hosts) UIToolkitPanels.ValidateLayout(UIToolkitPanels.GetRoot(host)?.panel);
            return new
            {
                imported,
                unchanged = unchanged.Count,
                note = "Theme sheets (.tss) and sheets referenced only by UXML templates that are not attached to a live element are not re-applied; re-enter Play Mode for those."
            };
        }

        /// <summary>After applying, watches a few frames for project code undoing the change.</summary>
        private sealed class StyleWatch
        {
            private const int WatchFrames = 3;
            private const double WatchSeconds = 0.5;
            private const double MaxTransitionWaitSeconds = 3.0;

            private readonly Component _host;
            private readonly string _path;
            private readonly VisualElement _element;
            private readonly List<string> _add, _remove;
            private readonly List<InlineStyle> _styles;
            private readonly List<(string uss, string resolved)> _before;
            private readonly string[] _classesBefore;
            private readonly object _reload;
            private readonly Stopwatch _clock = Stopwatch.StartNew();
            private readonly int _startFrame = Time.frameCount;
            private readonly TaskCompletionSource<object> _done = new TaskCompletionSource<object>();
            private int _ticks;
            private readonly HashSet<string> _running = new HashSet<string>(StringComparer.Ordinal);
            private int _transitionsObserved;

            public StyleWatch(Component host, string path, VisualElement element, List<string> add, List<string> remove, List<InlineStyle> styles,
                List<(string, string)> before, string[] classesBefore, object reload)
            {
                _host = host; _path = path; _element = element; _add = add; _remove = remove; _styles = styles; _before = before;
                _classesBefore = classesBefore; _reload = reload;
            }

            public Task<object> Run()
            {
                // A theme that animates the property would otherwise report a value halfway to the target.
                _element.RegisterCallback<TransitionRunEvent>(OnTransitionRun);
                _element.RegisterCallback<TransitionEndEvent>(OnTransitionEnd);
                _element.RegisterCallback<TransitionCancelEvent>(OnTransitionCancel);
                EditorApplication.update += Tick;
                return _done.Task;
            }

            private void OnTransitionRun(TransitionRunEvent evt)
            {
                if (!ReferenceEquals(evt.target, _element)) return;
                _transitionsObserved++;
                foreach (var property in evt.stylePropertyNames) _running.Add(property.ToString());
            }

            private void OnTransitionEnd(TransitionEndEvent evt) => Ended(evt.target, evt.stylePropertyNames);
            private void OnTransitionCancel(TransitionCancelEvent evt) => Ended(evt.target, evt.stylePropertyNames);

            private void Ended(IEventHandler target, StylePropertyNameCollection properties)
            {
                if (!ReferenceEquals(target, _element)) return;
                foreach (var property in properties) _running.Remove(property.ToString());
            }

            private void Tick()
            {
                _ticks++;
                var frames = EditorApplication.isPlaying ? Time.frameCount - _startFrame : _ticks;
                var elapsed = _clock.Elapsed.TotalSeconds;
                var watching = frames < WatchFrames && elapsed < WatchSeconds;
                var transitioning = _running.Count > 0 && elapsed < MaxTransitionWaitSeconds;
                if ((watching || transitioning) && _element.panel != null)
                {
                    if (!EditorApplication.isPlaying) EditorApplication.QueuePlayerLoopUpdate();
                    return;
                }
                EditorApplication.update -= Tick;
                _element.UnregisterCallback<TransitionRunEvent>(OnTransitionRun);
                _element.UnregisterCallback<TransitionEndEvent>(OnTransitionEnd);
                _element.UnregisterCallback<TransitionCancelEvent>(OnTransitionCancel);
                try { _done.TrySetResult(Report(frames)); }
                catch (Exception ex) { _done.TrySetResult(Response.Error("UI_TOOLKIT_STYLE_FAILED", new { detail = ex.Message })); }
            }

            private object Report(int frames)
            {
                var detached = _element.panel == null;
                if (!detached) UIToolkitPanels.ValidateLayout(_element.panel);
                var reverted = new List<string>();
                foreach (var cls in _add) if (!_element.ClassListContains(cls)) reverted.Add("." + cls + " was removed again");
                foreach (var cls in _remove) if (_element.ClassListContains(cls)) reverted.Add("." + cls + " was added back");
                foreach (var style in _styles)
                    if (!Equals(style.Property.GetValue(_element.style, null), style.Value)) reverted.Add(style.Uss + " inline value was changed again");
                var styles = _styles.Select((s, i) => new
                {
                    property = s.Uss,
                    inline = s.Remove ? null : StyleIntrospection.InlineValue(_element, s.Uss),
                    removed = s.Remove ? (bool?)true : null,
                    resolved_before = _before[i].resolved,
                    resolved_after = detached ? null : StyleIntrospection.ResolvedValue(_element, s.Uss),
                    transitioning = _running.Contains(s.Uss) ? (bool?)true : null
                }).ToList();
                var renderHeight = TryGetRenderHeight(out _);
                var mapping = detached ? null : MappingFor(_host, _element.panel);
                return Response.Success(
                    detached ? $"Styled {Label(_element)}, but it left its panel (rebuilt) while watching."
                    : reverted.Count > 0 ? $"Styled {Label(_element)}, but project code undid part of it." : $"Styled {Label(_element)}.",
                    new
                    {
                        host_id = ObjectIdHelper.GetSerializableId(_host.gameObject),
                        element = detached ? null : UIToolkitPanels.DescribeElement(_element, _path, mapping, renderHeight, false),
                        classes_before = _classesBefore,
                        classes_after = _element.GetClasses().ToArray(),
                        styles,
                        reverted = reverted.Count > 0 ? reverted : null,
                        watched_frames = frames,
                        watched_ms = (int)_clock.Elapsed.TotalMilliseconds,
                        transitions_observed = _transitionsObserved > 0 ? (int?)_transitionsObserved : null,
                        transitions_running = _running.Count > 0 ? _running.OrderBy(n => n, StringComparer.Ordinal).ToArray() : null,
                        reload = _reload,
                        runtime_only = true,
                        warning = reverted.Count > 0 ? "A script changed the element back within a few frames (for example a responsive layout script toggling classes on geometry change). Change the script or its inputs instead." : null,
                        note = "Runtime only: not saved to UXML/USS; lost on Play Mode exit or when the panel rebuilds."
                    });
            }
        }
    }
}
