// Copyright (C) Funplay. Licensed under MIT.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
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
        // ---- set_ui_toolkit_value ----------------------------------------------------------

        [Description("Set the value of a UI Toolkit control the way user input does, so ChangeEvent fires (not SetValueWithoutNotify): Slider/SliderInt (number, clamped by the control), Toggle (true/false), TextField (string), DropdownField (a choice or its index), EnumField (name), Integer/Float fields, ToggleButtonGroup (active button indices or texts, comma-separated). Matching an element inside a control sets the control, and a caption Label beside exactly one control (a settings row) sets that control. type_text types into a TextField instead: focus, select all, then one KeyDownEvent per character (falls back to value when the field does not take synthesized keys; reported as typed=false). submit=true sends Return to a TextField afterwards (commits delayed fields). Plain Buttons, including segmented controls built from Buttons, are clicked with click_ui_toolkit_element instead. Reports old and new value and change_event. Runtime state only; works in Edit and Play Mode.")]
        public static object SetUiToolkitValue(
            [ToolParam("Panel host GameObject name/path/ID", Required = false)] string target = null,
            [ToolParam("Selector: Type#name.class compounds joined by space (descendant) or > (child), optional :nth-of-type(n)/:nth-child(n)", Required = false)] string selector = null,
            [ToolParam("Exact element text, case-insensitive (matches the element, not the new value)", Required = false)] string text = null,
            [ToolParam("Element path relative to one host: child indices or '/'-separated selectors over direct children", Required = false)] string element_path = null,
            [ToolParam("New value: number, true/false, string, a dropdown choice or index, an enum name, or ToggleButtonGroup indices/texts", Required = false)] string value = null,
            [ToolParam("Text to type into a TextField (replaces its content)", Required = false)] string type_text = null,
            [ToolParam("Send Return to a TextField after setting/typing", Required = false)] bool submit = false)
        {
            try
            {
                if (value == null && type_text == null) throw new ArgumentException("Pass value or type_text.");
                if (value != null && type_text != null) throw new ArgumentException("Pass value or type_text, not both.");
                var (host, path, matched) = ResolveUniqueElement(target, selector, text, element_path);
                var panel = matched.panel ?? throw new UIToolkitException("ROOT_NOT_READY", "The element is not attached to a panel.");
                UIToolkitPanels.ValidateLayout(panel);
                var (control, valueType) = FindValueControl(matched);
                VisualElement labelledBy = null;
                if (control == null && matched is TextElement && !(matched is Button))
                {
                    // A caption beside its control (a settings row: Label + Slider) names that control.
                    (control, valueType) = FindControlBesideLabel(matched);
                    if (control != null) labelledBy = matched;
                }
                if (control == null)
                {
                    if (UIToolkitPanels.FindClickReceiver(matched) is Button)
                        throw new UIToolkitException("USE_CLICK", "This is a Button (segmented controls built from Buttons included); click it with click_ui_toolkit_element.");
                    throw new UIToolkitException("NOT_A_VALUE_CONTROL", "Neither the element nor an ancestor holds a value (INotifyValueChanged).");
                }
                if (!UIToolkitPanels.IsDisplayed(control)) throw new UIToolkitException("ELEMENT_NOT_DISPLAYED", "The control is hidden (display:none or visibility:hidden on it or an ancestor).");
                if (!control.enabledInHierarchy) throw new UIToolkitException("ELEMENT_DISABLED", "The control or an ancestor is disabled.");
                if (control is TextField readOnly && readOnly.isReadOnly) throw new UIToolkitException("ELEMENT_READ_ONLY", "The text field is read-only; user input can't change it.");

                var valueProperty = typeof(INotifyValueChanged<>).MakeGenericType(valueType).GetProperty("value");
                var before = valueProperty.GetValue(control, null);
                var tracker = (Func<bool>)typeof(UIToolkitFunctions).GetMethod(nameof(TrackChange), BindingFlags.NonPublic | BindingFlags.Static)
                    .MakeGenericMethod(valueType).Invoke(null, new object[] { control });
                bool? typed = null;
                string note = null;
                bool changeEvent;
                try
                {
                    if (type_text != null)
                    {
                        if (!(control is TextField field)) throw new UIToolkitException("TYPE_TEXT_UNSUPPORTED", "type_text needs a TextField; pass value for other controls.");
                        typed = TypeInto(field, type_text);
                        if (field.text != type_text || !field.isDelayed && field.value != type_text)
                        {
                            typed = false;
                            field.value = type_text;
                            note = "The field did not take synthesized key events; its value was set instead.";
                        }
                        if (submit && field.multiline) note = "Return in a multiline field inserts a line break, so it was not sent.";
                        else if (submit) SendReturn(field);
                        else if (field.isDelayed && field.value != type_text)
                            note = "The field is delayed: the text is typed but value commits on Return or blur; pass submit=true.";
                    }
                    else
                    {
                        valueProperty.SetValue(control, ConvertValue(control, valueType, before, value), null);
                        if (submit && control is TextField field && !field.multiline) SendReturn(field);
                    }
                }
                finally
                {
                    changeEvent = tracker();
                }

                var after = valueProperty.GetValue(control, null);
                return Response.Success(changeEvent ? $"Set {Label(control)}." : $"{Label(control)} already had this value; no ChangeEvent.", new
                {
                    host_id = ObjectIdHelper.GetSerializableId(host.gameObject),
                    element = Label(control),
                    element_path = ReferenceEquals(control, matched) ? path : UIToolkitPanels.PathOf(UIToolkitPanels.GetRoot(host), control),
                    matched_label = labelledBy != null ? Label(labelledBy) : null,
                    value_type = valueType.Name,
                    old_value = DescribeControlValue(before),
                    new_value = DescribeControlValue(after),
                    change_event = changeEvent,
                    typed,
                    submitted = submit && control is TextField ? (bool?)true : null,
                    clamped = type_text == null && IsNumeric(valueType) && Math.Abs(Convert.ToDouble(after, CultureInfo.InvariantCulture) - double.Parse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture)) > 1e-6 ? (bool?)true : null,
                    note
                });
            }
            catch (UIToolkitException ex) { return Response.Error(ex.Code, new { detail = ex.Message, details = ex.Details }); }
            catch (TargetInvocationException ex) { return Response.Error("UI_TOOLKIT_SET_VALUE_FAILED", new { detail = (ex.InnerException ?? ex).Message }); }
            catch (Exception ex) { return Response.Error("UI_TOOLKIT_SET_VALUE_FAILED", new { detail = ex.Message }); }
        }

        private static Func<bool> TrackChange<T>(VisualElement element)
        {
            var received = false;
            EventCallback<ChangeEvent<T>> callback = evt => { if (ReferenceEquals(evt.target, element)) received = true; };
            element.RegisterCallback(callback);
            return () => { element.UnregisterCallback(callback); return received; };
        }

        /// <summary>
        /// The nearest self-or-ancestor that implements INotifyValueChanged&lt;T&gt;, and T. Every TextElement
        /// (a field's Label, a TextField's inner text) implements INotifyValueChanged&lt;string&gt; for its
        /// text; those are skipped in favour of the control around them, and a bare Label is no value control.
        /// </summary>
        internal static (VisualElement control, Type valueType) FindValueControl(VisualElement element)
        {
            for (var current = element; current != null; current = current.hierarchy.parent)
            {
                if (current is Button) return (null, null);
                if (current is TextElement) continue;
                var notify = current.GetType().GetInterfaces().FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(INotifyValueChanged<>));
                if (notify != null) return (current, notify.GetGenericArguments()[0]);
            }
            return (null, null);
        }

        /// <summary>
        /// The one value control in the nearest ancestor of <paramref name="label"/> that holds any, for a
        /// caption laid out beside its control. Two or more controls there are ambiguous: none is returned.
        /// </summary>
        internal static (VisualElement control, Type valueType) FindControlBesideLabel(VisualElement label)
        {
            for (var container = label.hierarchy.parent; container != null && container.hierarchy.parent != null; container = container.hierarchy.parent)
            {
                var found = new List<(VisualElement, Type)>();
                CollectValueControls(container, found);
                if (found.Count == 1) return found[0];
                if (found.Count > 1) break;
            }
            return (null, null);
        }

        private static void CollectValueControls(VisualElement parent, List<(VisualElement, Type)> found)
        {
            for (var i = 0; i < parent.hierarchy.childCount && found.Count < 2; i++)
            {
                var child = parent.hierarchy[i];
                if (child is Button) continue;
                var notify = child is TextElement ? null : child.GetType().GetInterfaces().FirstOrDefault(t => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(INotifyValueChanged<>));
                if (notify != null) found.Add((child, notify.GetGenericArguments()[0]));
                else CollectValueControls(child, found);
            }
        }

        internal static object ConvertValue(VisualElement control, Type type, object current, string raw)
        {
            var text = raw.Trim();
            if (type == typeof(string))
            {
                if (control is DropdownField dropdown && dropdown.choices != null)
                {
                    var choice = dropdown.choices.FirstOrDefault(c => string.Equals(c, raw, StringComparison.Ordinal))
                                 ?? dropdown.choices.FirstOrDefault(c => string.Equals(c, text, StringComparison.OrdinalIgnoreCase));
                    if (choice != null) return choice;
                    if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) && index >= 0 && index < dropdown.choices.Count)
                        return dropdown.choices[index];
                    throw new UIToolkitException("INVALID_VALUE", "value must be one of the dropdown's choices or its index.", dropdown.choices.Take(50).ToArray());
                }
                return raw;
            }
            if (type == typeof(bool))
            {
                switch (text.ToLowerInvariant())
                {
                    case "true": case "1": case "on": case "yes": return true;
                    case "false": case "0": case "off": case "no": return false;
                }
                throw new UIToolkitException("INVALID_VALUE", "value must be true or false.");
            }
            if (type == typeof(int) || type == typeof(long) || type == typeof(float) || type == typeof(double) || type == typeof(uint) || type == typeof(ulong))
            {
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || double.IsNaN(number) || double.IsInfinity(number))
                    throw new UIToolkitException("INVALID_VALUE", "value must be a number.");
                if ((type == typeof(int) || type == typeof(long) || type == typeof(uint) || type == typeof(ulong)) && Math.Abs(number % 1) > 0)
                    throw new UIToolkitException("INVALID_VALUE", "value must be a whole number for this control.");
                return Convert.ChangeType(number, type, CultureInfo.InvariantCulture);
            }
            if (type == typeof(Enum) || type.IsEnum)
            {
                var enumType = type.IsEnum ? type : current?.GetType();
                if (enumType == null || !enumType.IsEnum) throw new UIToolkitException("INVALID_VALUE", "The enum field has no enum type yet.");
                var name = Enum.GetNames(enumType).FirstOrDefault(n => string.Equals(n, text, StringComparison.OrdinalIgnoreCase) || string.Equals(ToolRegistry.ToSnakeCase(n), text, StringComparison.OrdinalIgnoreCase));
                if (name == null) throw new UIToolkitException("INVALID_VALUE", $"value must be one of: {string.Join(", ", Enum.GetNames(enumType))}.");
                return Enum.Parse(enumType, name);
            }
            if (type.Name == "ToggleButtonGroupState") return ToggleGroupState(control, type, current, text);
            throw new UIToolkitException("UNSUPPORTED_VALUE_TYPE", $"Setting a {type.Name} value is not supported; use execute_code for this control.");
        }

        /// <summary>A ToggleButtonGroupState with the given buttons (indices or button texts) active. Reflection: the type is 2023.2+.</summary>
        private static object ToggleGroupState(VisualElement control, Type type, object current, string text)
        {
            var length = (int)(type.GetProperty("length")?.GetValue(current, null) ?? 0);
            var buttons = control.Query<Button>().ToList();
            var active = new List<int>();
            foreach (var part in text.Trim('[', ']').Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim().Trim('"')))
            {
                if (part.Length == 0) continue;
                var index = int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i
                    : buttons.FindIndex(b => string.Equals(b.text, part, StringComparison.OrdinalIgnoreCase));
                if (index < 0 || index >= length) throw new UIToolkitException("INVALID_VALUE", $"'{part}' is not a button of this group (0..{length - 1} or a button text).");
                active.Add(index);
            }
            var state = Activator.CreateInstance(type, 0UL, length);
            var indexer = type.GetProperty("Item", new[] { typeof(int) }) ?? throw new UIToolkitException("UNSUPPORTED_VALUE_TYPE", "ToggleButtonGroupState has no indexer in this Unity version.");
            foreach (var index in active) indexer.SetValue(state, true, new object[] { index });
            return state;
        }

        private static object DescribeControlValue(object value)
        {
            if (value == null) return null;
            if (value is string || value is bool || value is Enum) return value is Enum ? value.ToString() : value;
            if (value is float f) return (float)Math.Round(f, 4);
            if (value is double d) return Math.Round(d, 6);
            if (value.GetType().IsPrimitive) return value;
            if (value.GetType().Name == "ToggleButtonGroupState")
            {
                // Active button indices.
                var length = (int)(value.GetType().GetProperty("length")?.GetValue(value, null) ?? 0);
                var indexer = value.GetType().GetProperty("Item", new[] { typeof(int) });
                if (indexer != null) return Enumerable.Range(0, length).Where(i => (bool)indexer.GetValue(value, new object[] { i })).ToArray();
            }
            return value.ToString();
        }

        private static bool IsNumeric(Type type) => type == typeof(int) || type == typeof(long) || type == typeof(float) || type == typeof(double) || type == typeof(uint) || type == typeof(ulong);

        /// <summary>Focus, select all, then one KeyDownEvent per character to the focused text input. False when nothing arrived.</summary>
        private static bool TypeInto(TextField field, string text)
        {
            field.Focus();
            field.SelectAll();
            var receiver = field.panel?.focusController?.focusedElement as VisualElement ?? field;
            foreach (var character in text)
            {
                using (var down = KeyDownEvent.GetPooled(character, KeyCode.None, EventModifiers.None))
                    receiver.SendEvent(down);
            }
            return field.text == text;
        }

        private static void SendReturn(TextField field)
        {
            var receiver = field.panel?.focusController?.focusedElement as VisualElement ?? field;
            using (var down = KeyDownEvent.GetPooled('\n', KeyCode.Return, EventModifiers.None)) receiver.SendEvent(down);
            using (var up = KeyUpEvent.GetPooled('\0', KeyCode.Return, EventModifiers.None)) receiver.SendEvent(up);
        }

        // ---- navigate_ui_toolkit -----------------------------------------------------------

        [Description("Keyboard/gamepad-style UI Toolkit navigation without the Input System: focus (element.Focus()), move:<up|down|left|right|next|previous> (NavigationMoveEvent from the matched or focused element; the panel moves focus), submit (NavigationSubmitEvent: activates Buttons/Toggles) and cancel (NavigationCancelEvent). Without an element the panel's focused element is used. Reports the focused element before and after and, for submit, whether a click handler ran. Independent of Game View focus. Edit or Play Mode.")]
        public static object NavigateUiToolkit(
            [ToolParam("focus, move:up, move:down, move:left, move:right, move:next, move:previous, submit or cancel")] string action,
            [ToolParam("Panel host GameObject name/path/ID", Required = false)] string target = null,
            [ToolParam("Selector: Type#name.class compounds joined by space (descendant) or > (child), optional :nth-of-type(n)/:nth-child(n)", Required = false)] string selector = null,
            [ToolParam("Exact element text, case-insensitive", Required = false)] string text = null,
            [ToolParam("Element path relative to one host: child indices or '/'-separated selectors over direct children", Required = false)] string element_path = null)
        {
            try
            {
                var verb = (action ?? "").Trim().ToLowerInvariant();
                NavigationMoveEvent.Direction direction = NavigationMoveEvent.Direction.None;
                if (verb.StartsWith("move:", StringComparison.Ordinal))
                {
                    var name = verb.Substring(5).Trim();
                    if (!Enum.TryParse(name, true, out direction) || direction == NavigationMoveEvent.Direction.None || !Enum.IsDefined(typeof(NavigationMoveEvent.Direction), direction))
                        throw new UIToolkitException("INVALID_ACTION", "move needs a direction: up, down, left, right, next or previous.");
                    verb = "move";
                }
                if (verb != "focus" && verb != "move" && verb != "submit" && verb != "cancel")
                    throw new UIToolkitException("INVALID_ACTION", "action must be focus, move:<direction>, submit or cancel.");

                Component host = null; VisualElement element = null; string path = null;
                var hasQuery = !string.IsNullOrEmpty(selector) || !string.IsNullOrEmpty(text) || !string.IsNullOrEmpty(element_path);
                if (hasQuery) (host, path, element) = ResolveUniqueElement(target, selector, text, element_path);
                else if (verb == "focus") throw new ArgumentException("focus needs selector, text or element_path.");
                else element = FocusedElement(target, out host);
                if (element == null) throw new UIToolkitException("NOTHING_FOCUSED", "No element has focus in the panel(s); pass an element or focus one first (action=focus).");
                var panel = element.panel ?? throw new UIToolkitException("ROOT_NOT_READY", "The element is not attached to a panel.");
                UIToolkitPanels.ValidateLayout(panel);
                var focusBefore = panel.focusController?.focusedElement as VisualElement;

                UIToolkitPanels.ClickResult clicked = null;
                switch (verb)
                {
                    case "focus":
                        if (!element.focusable) throw new UIToolkitException("ELEMENT_NOT_FOCUSABLE", "The element is not focusable (focusable=false).", new { tab_index = element.tabIndex });
                        element.Focus();
                        break;
                    case "move":
                        if (hasQuery && !ReferenceEquals(focusBefore, element) && element.focusable) element.Focus();
                        using (var evt = NavigationMoveEvent.GetPooled(direction)) SendFrom(element, evt);
                        break;
                    case "submit":
                        clicked = WatchClick(element, () => { using (var evt = NavigationSubmitEvent.GetPooled()) SendFrom(element, evt); });
                        break;
                    case "cancel":
                        using (var evt = NavigationCancelEvent.GetPooled()) SendFrom(element, evt);
                        break;
                }

                var focusAfter = panel.focusController?.focusedElement as VisualElement;
                var root = host != null ? UIToolkitPanels.GetRoot(host) : null;
                return Response.Success(verb == "focus" ? $"Focused {Label(focusAfter ?? element)}."
                        : verb == "move" ? (ReferenceEquals(focusAfter, focusBefore) ? "Focus did not move (no focusable element in that direction)." : $"Focus moved to {Label(focusAfter)}.")
                        : $"Sent {verb} to {Label(element)}.",
                    new
                    {
                        action = verb == "move" ? "move:" + direction.ToString().ToLowerInvariant() : verb,
                        element = Label(element),
                        element_path = path,
                        focused_before = focusBefore != null ? Label(focusBefore) : null,
                        focused_after = focusAfter != null ? Label(focusAfter) : null,
                        focused_after_path = focusAfter != null && root != null ? UIToolkitPanels.PathOf(root, focusAfter) : null,
                        handler_invoked = clicked?.HandlerInvoked,
                        click_event_received = clicked?.ClickEventReceived
                    });
            }
            catch (UIToolkitException ex) { return Response.Error(ex.Code, new { detail = ex.Message, details = ex.Details }); }
            catch (Exception ex) { return Response.Error("UI_TOOLKIT_NAVIGATE_FAILED", new { detail = ex.Message }); }
        }

        /// <summary>Sends a navigation event from <paramref name="element"/>; a delegating control (TextField) forwards to its focused child.</summary>
        private static void SendFrom(VisualElement element, EventBase evt)
        {
            var focused = element.panel?.focusController?.focusedElement as VisualElement;
            var sender = focused != null && element.Contains(focused) ? focused : element;
            sender.SendEvent(evt);
        }

        private static VisualElement FocusedElement(string target, out Component host)
        {
            host = null;
            foreach (var candidate in ResolveHosts(target))
            {
                var root = UIToolkitPanels.GetRoot(candidate);
                if (root?.panel?.focusController?.focusedElement is VisualElement focused && root.Contains(focused))
                {
                    host = candidate;
                    return focused;
                }
            }
            return null;
        }

        /// <summary>Runs <paramref name="send"/> while watching the element's click receiver (Clickable.clicked, ClickEvent, Toggle change).</summary>
        private static UIToolkitPanels.ClickResult WatchClick(VisualElement element, Action send)
        {
            var result = new UIToolkitPanels.ClickResult();
            var receiver = UIToolkitPanels.FindClickReceiver(element);
            var clickable = receiver?.GetType().GetProperty("clickable", BindingFlags.Public | BindingFlags.Instance)?.GetValue(receiver, null) as Clickable;
            Action onClicked = () => result.HandlerInvoked = true;
            EventCallback<ClickEvent> onClick = _ => result.ClickEventReceived = true;
            EventCallback<ChangeEvent<bool>> onToggled = _ => result.HandlerInvoked = true;
            if (clickable != null) clickable.clicked += onClicked;
            else receiver?.RegisterCallback(onToggled);
            element.RegisterCallback(onClick, TrickleDown.TrickleDown);
            try { send(); }
            finally
            {
                if (clickable != null) clickable.clicked -= onClicked;
                else receiver?.UnregisterCallback(onToggled);
                element.UnregisterCallback(onClick, TrickleDown.TrickleDown);
            }
            return result;
        }

        // ---- pointer_ui_toolkit ------------------------------------------------------------

        internal const int MaxPointerDurationMs = 5000;

        [Description("Pointer gestures on a UI Toolkit element through the panel dispatcher (like real input: :hover, pointer capture, manipulators). hover: one move to the element's center (the real mouse over the Game View overrides it, so capture right after). drag: down at the center, moves spread over duration_ms (default 300) to the center of the 'to' element (selector) or by 'delta' panel pixels ('dx,dy'), then up; reports the element under the drop point. long_press: down, held for duration_ms (default 800) while frames run, then up. pointer_type touch builds touch events (reported pointer_id). Match by selector, text and/or element_path as for clicks. Waits at most 5 s; Play Mode frames must advance for time-based game code.")]
        public static Task<object> PointerUiToolkit(
            [ToolParam("hover, drag or long_press")] string action,
            [ToolParam("Panel host GameObject name/path/ID", Required = false)] string target = null,
            [ToolParam("Selector: Type#name.class compounds joined by space (descendant) or > (child), optional :nth-of-type(n)/:nth-child(n)", Required = false)] string selector = null,
            [ToolParam("Exact element text, case-insensitive", Required = false)] string text = null,
            [ToolParam("Element path relative to one host: child indices or '/'-separated selectors over direct children", Required = false)] string element_path = null,
            [ToolParam("drag: selector of the element to drop on (same host)", Required = false)] string to = null,
            [ToolParam("drag: offset in panel pixels 'dx,dy' (instead of to)", Required = false)] string delta = null,
            [ToolParam("drag/long_press duration in milliseconds, 0..5000", Required = false)] int duration_ms = -1,
            [ToolParam("mouse or touch", Required = false)] string pointer_type = "mouse",
            [ToolParam("compact (default) or full: full echoes the complete element description", Required = false)] string detail = null)
        {
            try
            {
                var verb = (action ?? "").Trim().ToLowerInvariant();
                if (verb != "hover" && verb != "drag" && verb != "long_press") throw new UIToolkitException("INVALID_ACTION", "action must be hover, drag or long_press.");
                UnityEngine.PointerType type;
                switch ((pointer_type ?? "mouse").Trim().ToLowerInvariant())
                {
                    case "mouse": type = UnityEngine.PointerType.Mouse; break;
                    case "touch": type = UnityEngine.PointerType.Touch; break;
                    default: throw new UIToolkitException("INVALID_POINTER_TYPE", "pointer_type must be mouse or touch.");
                }
                var (host, path, element) = ResolveUniqueElement(target, selector, text, element_path);
                var panel = element.panel ?? throw new UIToolkitException("ROOT_NOT_READY", "The element is not attached to a panel.");
                UIToolkitPanels.ValidateLayout(panel);
                if (!UIToolkitPanels.IsDisplayed(element)) throw new UIToolkitException("ELEMENT_NOT_DISPLAYED", "The element is hidden (display:none or visibility:hidden on it or an ancestor).");
                if (!UIToolkitPanels.HasLayout(element) || element.worldBound.width < 1f || element.worldBound.height < 1f)
                    throw new UIToolkitException("ELEMENT_HAS_NO_AREA", "The element has no laid-out area.");
                var visibility = UIToolkitPanels.GetVisibility(element);
                if (visibility.Fraction <= 0f) throw new UIToolkitException("ELEMENT_CLIPPED", "The element is outside its clipping area; scroll it into view first (scroll_ui_toolkit).");
                var start = visibility.FullyVisible ? element.worldBound.center : visibility.Visible.center;

                Vector2? end = null;
                if (verb == "drag")
                {
                    if (!string.IsNullOrEmpty(to) == !string.IsNullOrEmpty(delta)) throw new ArgumentException("drag needs exactly one of to or delta.");
                    if (!string.IsNullOrEmpty(to))
                    {
                        var (_, _, drop) = ResolveUniqueElement(ObjectIdHelper.GetSerializableId(host.gameObject), to, null, null);
                        if (!UIToolkitPanels.HasLayout(drop)) throw new UIToolkitException("ELEMENT_HAS_NO_AREA", "The 'to' element has no layout.");
                        end = drop.worldBound.center;
                    }
                    else
                    {
                        var parts = delta.Split(',');
                        if (parts.Length != 2 || !float.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var dx) ||
                            !float.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var dy) ||
                            float.IsNaN(dx) || float.IsNaN(dy) || Mathf.Abs(dx) > 100000f || Mathf.Abs(dy) > 100000f)
                            throw new ArgumentException("delta must be 'dx,dy' in panel pixels.");
                        end = start + new Vector2(dx, dy);
                    }
                }
                var duration = duration_ms >= 0 ? Mathf.Clamp(duration_ms, 0, MaxPointerDurationMs) : verb == "drag" ? 300 : verb == "long_press" ? 800 : 0;
                var gesture = new PointerGesture(host, path, element, panel, verb, type, start, end, duration, ResponseDetail.IsCompact(detail));
                return gesture.Run();
            }
            catch (UIToolkitException ex) { return Task.FromResult(Response.Error(ex.Code, new { detail = ex.Message, details = ex.Details })); }
            catch (Exception ex) { return Task.FromResult(Response.Error("UI_TOOLKIT_POINTER_FAILED", new { detail = ex.Message })); }
        }

        /// <summary>A pointer sequence played over Editor updates on a wall clock, so game code sees it across frames.</summary>
        private sealed class PointerGesture
        {
            private readonly Component _host;
            private readonly string _path, _verb;
            private readonly VisualElement _element;
            private readonly IPanel _panel;
            private readonly UnityEngine.PointerType _type;
            private readonly Vector2 _start;
            private readonly Vector2? _end;
            private readonly int _durationMs;
            private readonly Stopwatch _clock = new Stopwatch();
            private readonly TaskCompletionSource<object> _done = new TaskCompletionSource<object>();
            private readonly int _startFrame = Time.frameCount;
            private int _steps, _sent, _pointerId = -1, _framesHeld;
            private bool _down, _up;
            private string _capturedBy;
            private Vector2? _lastPosition;
            private EventCallback<ClickEvent> _onClick;
            private bool _clickEvent;
            private readonly bool _compact;

            public PointerGesture(Component host, string path, VisualElement element, IPanel panel, string verb, UnityEngine.PointerType type, Vector2 start, Vector2? end, int durationMs, bool compact = false)
            {
                _compact = compact;
                _host = host; _path = path; _element = element; _panel = panel; _verb = verb; _type = type; _start = start; _end = end; _durationMs = durationMs;
                _steps = verb == "drag" ? Mathf.Clamp(durationMs / 16, 4, 60) : 0;
            }

            public Task<object> Run()
            {
                _onClick = _ => _clickEvent = true;
                _element.RegisterCallback(_onClick, TrickleDown.TrickleDown);
                _clock.Start();
                Send(EventType.MouseMove, _start, -1);
                if (_verb == "hover") { Finish(null); return _done.Task; }
                Send(EventType.MouseDown, _start, 0);
                _down = true;
                _capturedBy = Captured();
                EditorApplication.update += Tick;
                EditorApplication.playModeStateChanged += OnPlayModeChanged;
                AssemblyReloadEvents.beforeAssemblyReload += OnReload;
                Tick();
                return _done.Task;
            }

            private void Tick()
            {
                if (_done.Task.IsCompleted) return;
                try
                {
                    if (_element.panel == null && !_up) { Release(_end ?? _start); Finish("The element left its panel during the gesture; the pointer was released."); return; }
                    var elapsed = _clock.Elapsed.TotalMilliseconds;
                    var t = _durationMs <= 0 ? 1f : Mathf.Clamp01((float)(elapsed / _durationMs));
                    if (_verb == "drag")
                    {
                        // Evenly spaced moves; the last lands exactly on the end point.
                        var due = Mathf.Min(_steps, Mathf.FloorToInt(t * _steps));
                        while (_sent < due)
                        {
                            _sent++;
                            Send(EventType.MouseDrag, Vector2.Lerp(_start, _end.Value, (float)_sent / _steps), 0);
                        }
                        if (_sent >= _steps && t >= 1f) { Release(_end.Value); Finish(null); }
                        return;
                    }
                    if (Time.frameCount != _startFrame) _framesHeld = Time.frameCount - _startFrame;
                    if (t >= 1f)
                    {
                        Release(_start);
                        Finish(EditorApplication.isPlaying && _framesHeld == 0 ? "No Play Mode frame ran while the pointer was held; time-based long-press handlers may not have fired." : null);
                    }
                    else if (!EditorApplication.isPlaying) EditorApplication.QueuePlayerLoopUpdate();
                }
                catch (Exception ex)
                {
                    if (_down && !_up) TryRelease();
                    Complete(Response.Error("UI_TOOLKIT_POINTER_FAILED", new { detail = ex.Message }));
                }
            }

            private void Release(Vector2 position)
            {
                if (_up) return;
                _up = true;
                Send(EventType.MouseUp, position, 0);
            }

            private void TryRelease()
            {
                try { Release(_end ?? _start); } catch (Exception) { /* the panel is gone */ }
            }

            private void Send(EventType type, Vector2 position, int button)
            {
                // A captured pointer goes to its capturing element, as the panel routes real input.
                var captured = _pointerId >= 0 ? _panel.GetCapturingElement(_pointerId) as VisualElement : null;
                var target = captured ?? _panel.Pick(position) ?? _element;
                _pointerId = UIToolkitPanels.SendPointer(_panel, target, type, position, button, _type, _lastPosition.HasValue ? position - _lastPosition.Value : Vector2.zero);
                _lastPosition = position;
            }

            private string Captured()
            {
                var captured = _pointerId >= 0 ? _panel.GetCapturingElement(_pointerId) as VisualElement : null;
                return captured != null ? Label(captured) : null;
            }

            private void OnPlayModeChanged(PlayModeStateChange change)
            {
                if (change != PlayModeStateChange.ExitingPlayMode && change != PlayModeStateChange.ExitingEditMode) return;
                TryRelease();
                Complete(Response.Error("PLAY_MODE_CHANGED", new { detail = "The Play Mode state changed during the gesture; the pointer was released." }));
            }

            private void OnReload()
            {
                TryRelease();
                Complete(Response.Error("INTERRUPTED_BY_RELOAD", new { detail = "A script reload started during the gesture." }));
            }

            private void Finish(string note)
            {
                if (_done.Task.IsCompleted) return;
                var pseudo = _element.panel != null ? UIToolkitPanels.PseudoStates(_element) : null;
                var drop = _end.HasValue ? _panel.Pick(_end.Value) : null;
                var renderHeight = TryGetRenderHeight(out _);
                var mapping = _element.panel != null ? MappingFor(_host, _panel) : null;
                Complete(Response.Success(
                    _verb == "hover" ? $"Pointer over {Label(_element)}." : _verb == "drag" ? $"Dragged {Label(_element)}." : $"Long-pressed {Label(_element)} for {_durationMs} ms.",
                    new
                    {
                        action = _verb,
                        host_id = ObjectIdHelper.GetSerializableId(_host.gameObject),
                        element = _element.panel == null ? null : _compact ? UIToolkitFunctions.EchoElement(_element, _path) : UIToolkitPanels.DescribeElement(_element, _path, mapping, renderHeight, false),
                        pointer_type = _type.ToString().ToLowerInvariant(),
                        pointer_id = _pointerId,
                        pseudo_states = pseudo,
                        captured_by = _capturedBy,
                        start = new { x = _start.x, y = _start.y },
                        end = _end.HasValue ? new { x = _end.Value.x, y = _end.Value.y } : null,
                        drop_target = drop != null ? Label(drop) : null,
                        moves = _verb == "drag" ? _sent : (int?)null,
                        duration_ms = _verb == "hover" ? (int?)null : (int)_clock.Elapsed.TotalMilliseconds,
                        player_frames = EditorApplication.isPlaying ? Time.frameCount - _startFrame : (int?)null,
                        click_event_received = _verb == "hover" ? (bool?)null : _clickEvent,
                        frames_stuck = PlayModeFrameMonitor.FramesStuck ? (bool?)true : null,
                        note
                    }));
            }

            private void Complete(object result)
            {
                EditorApplication.update -= Tick;
                EditorApplication.playModeStateChanged -= OnPlayModeChanged;
                AssemblyReloadEvents.beforeAssemblyReload -= OnReload;
                _element.UnregisterCallback(_onClick, TrickleDown.TrickleDown);
                _done.TrySetResult(result);
            }
        }
    }
}
