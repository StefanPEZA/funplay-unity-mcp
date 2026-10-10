// Copyright (C) Funplay. Licensed under MIT.
using System;
using System.Collections.Generic;
using System.Linq;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using Funplay.Editor.Tools.Helpers;
using UnityEngine.UIElements;

namespace Funplay.Editor.Tools.Builtins
{
    internal static partial class UIToolkitFunctions
    {
        internal static readonly string[] SupportedPseudoStates = { "hover", "active", "focus", "checked", "disabled" };

        [Description("Explain why a UI Toolkit element looks the way it does. Per USS property: the resolved value, the inline value (element.style / UXML style), the winning rule (selector, specificity, sheet path, line) and the rules it overrode, in Unity's own cascade order (theme sheets first, then specificity, then sheet order). pseudo_states (hover, active, focus, checked, disabled) lists the rules that would additionally apply in that state, without changing the element (no transitions start); that part is read from the USS source and marked heuristic. Match by selector, text and/or element_path as for clicks. Edit or Play Mode.")]
        [ReadOnlyTool]
        public static object ExplainStyle(
            [ToolParam("Panel host GameObject name/path/ID", Required = false)] string target = null,
            [ToolParam("Selector: Type#name.class compounds joined by space (descendant) or > (child), optional :nth-of-type(n)/:nth-child(n)", Required = false)] string selector = null,
            [ToolParam("Exact element text, case-insensitive", Required = false)] string text = null,
            [ToolParam("Element path relative to one host: child indices or '/'-separated selectors over direct children", Required = false)] string element_path = null,
            [ToolParam("Comma-separated USS properties, e.g. 'background-color,color'; default: every property a matching rule sets", Required = false)] string properties = null,
            [ToolParam("Comma-separated hypothetical states: hover, active, focus, checked, disabled", Required = false)] string pseudo_states = null)
        {
            try
            {
                var states = (pseudo_states ?? "").Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim().TrimStart(':').ToLowerInvariant()).Distinct().ToList();
                var unknown = states.Where(s => !SupportedPseudoStates.Contains(s)).ToList();
                if (unknown.Count > 0) throw new UIToolkitException("INVALID_PSEUDO_STATE", $"Unsupported: {string.Join(", ", unknown)}. Use {string.Join(", ", SupportedPseudoStates)}.");
                var (host, path, element) = ResolveUniqueElement(target, selector, text, element_path);
                if (element.panel == null) throw new UIToolkitException("ROOT_NOT_READY", "The element is not attached to a panel.");
                UIToolkitPanels.ValidateLayout(element.panel);
                return Response.Success($"Style of {Label(element)}.", ExplainElement(element, path, properties, states));
            }
            catch (UIToolkitException ex) { return Response.Error(ex.Code, new { detail = ex.Message, details = ex.Details }); }
            catch (Exception ex) { return Response.Error("EXPLAIN_STYLE_FAILED", new { detail = ex.Message }); }
        }

        internal static object ExplainElement(VisualElement element, string path, string properties, List<string> states)
        {
            List<StyleIntrospection.Rule> rules = null;
            string matcherError = null;
            if (StyleIntrospection.MatcherAvailable)
            {
                try { rules = StyleIntrospection.MatchedRules(element); }
                catch (Exception ex) { matcherError = (ex.InnerException ?? ex).Message; }
            }
            else matcherError = "Unity's style matcher is not reachable in this Unity version.";

            var sheetPaths = new List<string>();
            if (rules != null) sheetPaths.AddRange(rules.Select(r => r.SheetPath));
            for (var current = element; current != null; current = current.hierarchy.parent)
                for (var i = 0; i < current.styleSheets.count; i++)
                    sheetPaths.Add(StyleIntrospection.SheetPath(current.styleSheets[i]));

            var hypothetical = states.Count > 0 ? StyleIntrospection.HypotheticalRules(element, sheetPaths, states) : new List<(StyleIntrospection.SourceRule rule, string sheet)>();
            // Without Unity's matcher, every rule comes from the source heuristic (current state).
            var heuristicCurrent = rules == null ? StyleIntrospection.HypotheticalRulesForCurrentState(element, sheetPaths) : null;

            var names = string.IsNullOrWhiteSpace(properties)
                ? (rules?.SelectMany(r => r.Declarations).Select(d => d.Name) ?? heuristicCurrent.SelectMany(r => r.rule.Declarations).Select(d => d.Name))
                    .Concat(hypothetical.SelectMany(h => h.rule.Declarations).Select(d => d.Name))
                    .Where(n => !n.StartsWith("--", StringComparison.Ordinal)).Distinct().OrderBy(n => n, StringComparer.Ordinal).ToList()
                : properties.Split(',').Select(p => p.Trim()).Where(p => p.Length > 0).Distinct().ToList();

            var explained = names.Select(name =>
            {
                var setters = rules != null
                    ? rules.SelectMany(r => r.Declarations.Where(d => d.Name == name).Select(d => (object)new
                        {
                            selector = r.Selector, specificity = r.Specificity, sheet = r.SheetPath, line = d.Line > 0 ? d.Line : r.Line,
                            theme = r.ThemeSheet ? (bool?)true : null, value = d.Value
                        })).ToList()
                    : heuristicCurrent.SelectMany(h => h.rule.Declarations.Where(d => d.Name == name).Select(d => (object)new
                        {
                            selector = h.rule.Selector, specificity = Spec(h.rule.Selector), sheet = h.sheet, line = d.Line, theme = (bool?)null, value = d.Value
                        })).ToList();
                var inline = StyleIntrospection.InlineValue(element, name);
                var wouldApply = hypothetical.SelectMany(h => h.rule.Declarations.Where(d => d.Name == name).Select(d => new
                {
                    selector = h.rule.Selector, specificity = Spec(h.rule.Selector), sheet = h.sheet, line = d.Line, value = d.Value
                })).ToList();
                return new
                {
                    property = name,
                    resolved = StyleIntrospection.ResolvedValue(element, name),
                    inline,
                    source = inline != null ? "inline" : setters.Count > 0 ? "rule" : "inherited_or_initial",
                    winner = inline == null ? setters.LastOrDefault() : null,
                    overridden = (inline == null ? setters.Take(Math.Max(0, setters.Count - 1)) : setters).Reverse().ToList(),
                    in_pseudo_states = wouldApply.Count > 0 ? wouldApply : null,
                };
            }).ToList();

            return new
            {
                element = UIToolkitPanels.Label(element),
                element_path = path,
                classes = element.GetClasses().ToArray(),
                matcher = rules != null ? "unity" : "heuristic",
                matcher_error = matcherError,
                properties = explained,
                pseudo_states = states.Count > 0 ? new
                {
                    states,
                    heuristic = true,
                    rules = hypothetical.Select(h => new { selector = h.rule.Selector, sheet = h.sheet, line = h.rule.Line, specificity = Spec(h.rule.Selector) }).ToList(),
                    note = "Read from the USS source of the sheets in the element's stack; theme rules without a source file (unity-theme://default) are not covered. A state rule wins over the current winner when its sheet is not a theme sheet and its specificity is higher, or equal and later."
                } : null,
                sheets = sheetPaths.Where(p => p != null).Distinct().ToList(),
                heuristic = rules == null ? (bool?)true : null
            };
        }

        private static string Spec(string selector)
        {
            var (ids, classes, types) = StyleIntrospection.SpecificityOf(selector);
            return $"{ids},{classes},{types}";
        }
    }
}
