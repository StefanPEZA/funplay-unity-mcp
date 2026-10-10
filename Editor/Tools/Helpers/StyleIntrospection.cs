// Copyright (C) Funplay. Licensed under MIT.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Funplay.Editor.Tools.Helpers
{
    /// <summary>
    /// Which USS rules style an element, in cascade order. Uses Unity's own matcher (the UI Toolkit
    /// Debugger's MatchedRulesExtractor) and its own ordering (StyleSelectorMatch.Compare: theme sheets
    /// first, then specificity, then sheet order), all internal: when a Unity update removes them,
    /// <see cref="MatcherAvailable"/> is false and callers fall back to <see cref="SourceRules"/>,
    /// a heuristic over the USS source text.
    /// </summary>
    internal static class StyleIntrospection
    {
        private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        private static readonly Assembly Ui = typeof(VisualElement).Assembly;
        private static readonly Type ExtractorType = Ui.GetType("UnityEngine.UIElements.MatchedRulesExtractor");
        private static readonly Type MatchType = Ui.GetType("UnityEngine.UIElements.StyleSheets.StyleSelectorMatch");
        private static readonly MethodInfo CompareMethod = MatchType?.GetMethod("Compare", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        private static readonly MethodInfo FindMatchingRulesMethod = ExtractorType?.GetMethod("FindMatchingRules", Members);
        private static readonly MethodInfo GetMatchedRulesMethod = ExtractorType?.GetMethod("GetMatchedRules", Members);

        internal static bool MatcherAvailable =>
            ExtractorType != null && MatchType != null && CompareMethod != null && FindMatchingRulesMethod != null && GetMatchedRulesMethod != null;

        internal sealed class Declaration
        {
            public string Name, Value;
            public int Line;
        }

        internal sealed class Rule
        {
            public StyleSheet Sheet;
            public string SheetPath, Selector;
            public int Line, StackIndex;
            public bool ThemeSheet;
            public int Ids, Classes, Types;
            public List<Declaration> Declarations = new List<Declaration>();
            public string Specificity => $"{Ids},{Classes},{Types}";
        }

        internal static string SheetPath(StyleSheet sheet)
        {
            if (sheet == null) return null;
            var path = AssetDatabase.GetAssetPath(sheet);
            return string.IsNullOrEmpty(path) ? sheet.name : path;
        }

        /// <summary>Rules matching the element in its current state, lowest priority first (the last one wins).</summary>
        internal static List<Rule> MatchedRules(VisualElement element)
        {
            if (!MatcherAvailable) throw new NotSupportedException("Unity's style matcher is not reachable in this version.");
            Func<StyleSheet, string> path = SheetPath;
            var extractor = Activator.CreateInstance(ExtractorType, path);
            FindMatchingRulesMethod.Invoke(extractor, new object[] { element });
            var matches = new List<(object match, Rule rule)>();
            foreach (var matched in (IEnumerable)GetMatchedRulesMethod.Invoke(extractor, null))
            {
                var record = Get(matched, "matchRecord");
                var sheet = (StyleSheet)Get(record, "sheet");
                var stackIndex = (int)Get(record, "styleSheetIndexInStack");
                var importedIndex = (int)Get(record, "importedStyleSheetIndex");
                var complex = Get(record, "complexSelector");
                var specificity = Get(complex, "specificity");
                var rule = new Rule
                {
                    Sheet = sheet, SheetPath = SheetPath(sheet), StackIndex = stackIndex,
                    ThemeSheet = Get(sheet, "isDefaultStyleSheet") is bool theme && theme,
                    Line = (int)Get(matched, "lineNumber"),
                    Selector = SelectorText(complex),
                    Ids = Convert.ToInt32(Get(specificity, "idScore")),
                    Classes = Convert.ToInt32(Get(specificity, "classScore")),
                    Types = Convert.ToInt32(Get(specificity, "typeScore"))
                };
                var styleRule = Get(complex, "rule");
                foreach (var property in (IEnumerable)Get(styleRule, "properties"))
                    rule.Declarations.Add(new Declaration
                    {
                        Name = (string)Get(property, "name"),
                        Line = (int)Get(property, "line"),
                        Value = FormatValues(sheet, (Array)Get(property, "values"))
                    });
                matches.Add((Activator.CreateInstance(MatchType, sheet, stackIndex, importedIndex, complex), rule));
            }
            matches.Sort((a, b) => (int)CompareMethod.Invoke(null, new[] { a.match, b.match }));
            return matches.Select(m => m.rule).ToList();
        }

        private static object Get(object target, string name)
        {
            if (target == null) return null;
            var type = target.GetType();
            var field = type.GetField(name, Members);
            if (field != null) return field.GetValue(target);
            var property = type.GetProperty(name, Members);
            if (property != null) return property.GetValue(target);
            throw new MissingMemberException(type.Name, name);
        }

        private static string SelectorText(object complex)
        {
            var text = new StringBuilder();
            foreach (var selector in (IEnumerable)Get(complex, "selectors"))
            {
                var relationship = Get(selector, "previousRelationship")?.ToString();
                if (text.Length > 0) text.Append(relationship == "Child" ? " > " : " ");
                foreach (var part in (IEnumerable)Get(selector, "parts"))
                {
                    var value = (string)Get(part, "value");
                    switch (Get(part, "type")?.ToString())
                    {
                        case "Wildcard": text.Append('*'); break;
                        case "Class": text.Append('.').Append(value); break;
                        case "ID": text.Append('#').Append(value); break;
                        case "PseudoClass":
                        case "RecursivePseudoClass": text.Append(':').Append(value); break;
                        default: text.Append(value); break;
                    }
                }
            }
            return text.ToString();
        }

        private static readonly Dictionary<string, MethodInfo> Readers = new Dictionary<string, MethodInfo>();

        private static object Read(StyleSheet sheet, string method, object handle)
        {
            if (!Readers.TryGetValue(method, out var reader))
            {
                reader = typeof(StyleSheet).GetMethods(Members).FirstOrDefault(m => m.Name == method && !m.IsGenericMethod && m.GetParameters().Length == 1);
                Readers[method] = reader;
            }
            return reader?.Invoke(sheet, new[] { handle });
        }

        /// <summary>A declaration's value as USS-like text (colours as #RRGGBBAA, var(--x), keywords lower-case).</summary>
        internal static string FormatValues(StyleSheet sheet, Array handles)
        {
            var parts = new List<string>();
            var openFunctions = 0;
            var skipArgCount = false;
            foreach (var handle in handles)
            {
                string text;
                try
                {
                    var type = Get(handle, "valueType")?.ToString();
                    if (skipArgCount && type == "Float") { skipArgCount = false; continue; }
                    skipArgCount = false;
                    switch (type)
                    {
                        case "Color": text = "#" + ColorUtility.ToHtmlStringRGBA((Color)Read(sheet, "ReadColor", handle)); break;
                        case "Keyword": text = Read(sheet, "ReadKeyword", handle)?.ToString().ToLowerInvariant(); break;
                        case "Float": text = Convert.ToSingle(Read(sheet, "ReadFloat", handle)).ToString(CultureInfo.InvariantCulture); break;
                        case "Dimension": text = Read(sheet, "ReadDimension", handle)?.ToString(); break;
                        case "Enum": text = (string)Read(sheet, "ReadEnum", handle); break;
                        case "String": text = "\"" + Read(sheet, "ReadString", handle) + "\""; break;
                        case "Variable": text = (string)Read(sheet, "ReadVariable", handle); break;
                        case "Function":
                            text = Read(sheet, "ReadFunctionName", handle) + "(";
                            openFunctions++; skipArgCount = true; break;
                        case "CommaSeparator": text = ","; break;
                        case "AssetReference": text = (Read(sheet, "ReadAssetReference", handle) as UnityEngine.Object)?.name; break;
                        case "MissingAssetReference": text = "url(\"" + Read(sheet, "ReadMissingAssetReferenceUrl", handle) + "\")"; break;
                        case "ResourcePath": text = Read(sheet, "ReadResourcePath", handle)?.ToString(); break;
                        default: text = type?.ToLowerInvariant(); break;
                    }
                }
                catch (Exception) { text = "?"; }
                if (!string.IsNullOrEmpty(text)) parts.Add(text);
            }
            var joined = string.Join(" ", parts).Replace("( ", "(").Replace(" ,", ",");
            return joined + new string(')', openFunctions);
        }

        // ---- Heuristic: rules from the USS source text ------------------------------------------------

        internal sealed class SourceRule
        {
            public string Selector;
            public int Line;
            public List<Declaration> Declarations = new List<Declaration>();
        }

        private static readonly Regex Comments = new Regex(@"/\*.*?\*/", RegexOptions.Singleline);

        /// <summary>Top-level `selectors { declarations }` blocks of a USS text, one entry per selector, with 1-based lines.</summary>
        internal static List<SourceRule> ParseUss(string uss)
        {
            var rules = new List<SourceRule>();
            // Keep line numbers: replace comments by the same number of newlines.
            var text = Comments.Replace(uss ?? string.Empty, m => new string('\n', m.Value.Count(c => c == '\n')));
            var position = 0;
            while (position < text.Length)
            {
                var open = text.IndexOf('{', position);
                if (open < 0) break;
                var close = text.IndexOf('}', open);
                if (close < 0) break;
                var selectorText = text.Substring(position, open - position);
                // Skip @import and similar statements that precede the block.
                var lastStatement = selectorText.LastIndexOf(';');
                var selectorStart = position + (lastStatement >= 0 ? lastStatement + 1 : 0);
                selectorText = text.Substring(selectorStart, open - selectorStart);
                var leading = selectorText.Length - selectorText.TrimStart().Length;
                var line = 1 + text.Take(selectorStart + leading).Count(c => c == '\n');
                var declarations = new List<Declaration>();
                var body = text.Substring(open + 1, close - open - 1);
                var declarationLine = 1 + text.Take(open + 1).Count(c => c == '\n');
                foreach (var raw in body.Split(';'))
                {
                    var colon = raw.IndexOf(':');
                    var lineOffset = raw.Take(raw.Length - raw.TrimStart().Length).Count(c => c == '\n');
                    if (colon > 0)
                        declarations.Add(new Declaration { Name = raw.Substring(0, colon).Trim(), Value = raw.Substring(colon + 1).Trim(), Line = declarationLine + lineOffset });
                    declarationLine += raw.Count(c => c == '\n');
                }
                foreach (var selector in selectorText.Split(','))
                    if (selector.Trim().Length > 0)
                        rules.Add(new SourceRule { Selector = Regex.Replace(selector.Trim(), @"\s+", " "), Line = line, Declarations = declarations });
                position = close + 1;
            }
            return rules;
        }

        /// <summary>(ids, classes + pseudo-classes, types) of a USS selector text.</summary>
        internal static (int ids, int classes, int types) SpecificityOf(string selector)
        {
            int ids = 0, classes = 0, types = 0;
            foreach (Match token in Regex.Matches(selector, @"#[\w-]+|\.[\w-]+|:[\w-]+|(?<![\w-])[A-Za-z_][\w-]*"))
            {
                var value = token.Value;
                if (value[0] == '#') ids++;
                else if (value[0] == '.' || value[0] == ':') classes++;
                else types++;
            }
            return (ids, classes, types);
        }

        private static readonly string[] PseudoClasses = { "hover", "active", "focus", "checked", "disabled", "inactive", "enabled", "root", "selected" };

        /// <summary>
        /// USS rules that would match <paramref name="element"/> with <paramref name="states"/> added: selectors
        /// naming at least one of those pseudo-classes and matching once they are removed. Other pseudo-classes
        /// are kept only when the element is currently in that state. Heuristic: no :root, *, attribute or
        /// pseudo-classes on ancestors beyond the subject's.
        /// </summary>
        internal static List<(SourceRule rule, string sheet)> HypotheticalRules(VisualElement element, IEnumerable<string> sheetPaths, ICollection<string> states) =>
            SourceMatches(element, sheetPaths, states, requireState: true);

        /// <summary>Fallback when Unity's matcher is unavailable: source rules matching the current state, lowest priority first.</summary>
        internal static List<(SourceRule rule, string sheet)> HypotheticalRulesForCurrentState(VisualElement element, IEnumerable<string> sheetPaths) =>
            SourceMatches(element, sheetPaths, Array.Empty<string>(), requireState: false)
                .Select((m, index) => (m, index))
                .OrderBy(x => SpecificityOf(x.m.rule.Selector).ids).ThenBy(x => SpecificityOf(x.m.rule.Selector).classes)
                .ThenBy(x => SpecificityOf(x.m.rule.Selector).types).ThenBy(x => x.index)
                .Select(x => x.m).ToList();

        private static List<(SourceRule rule, string sheet)> SourceMatches(VisualElement element, IEnumerable<string> sheetPaths, ICollection<string> states, bool requireState)
        {
            var result = new List<(SourceRule, string)>();
            foreach (var path in sheetPaths.Distinct())
            {
                if (path == null || !path.EndsWith(".uss", StringComparison.OrdinalIgnoreCase)) continue;
                var full = Path.GetFullPath(path);
                if (!File.Exists(full)) continue;
                foreach (var rule in ParseUss(File.ReadAllText(full)))
                {
                    var pseudo = Regex.Matches(rule.Selector, @":([\w-]+)").Cast<Match>().Select(m => m.Groups[1].Value.ToLowerInvariant()).ToList();
                    if (requireState && !pseudo.Any(states.Contains)) continue;
                    if (pseudo.Any(p => !states.Contains(p) && !(PseudoClasses.Contains(p) && CurrentlyIn(element, p)))) continue;
                    var stripped = Regex.Replace(rule.Selector, @":[\w-]+", "");
                    try
                    {
                        if (UIToolkitPanels.Selector.Parse(stripped, null).Matches(element)) result.Add((rule, path));
                    }
                    catch (UIToolkitException) { }
                }
            }
            return result;
        }

        private static bool CurrentlyIn(VisualElement element, string pseudo)
        {
            switch (pseudo)
            {
                case "enabled": return element.enabledInHierarchy;
                case "disabled": return !element.enabledInHierarchy;
                case "focus": return element.focusController?.focusedElement == element;
                case "checked": return element is Toggle toggle && toggle.value;
                default: return false;
            }
        }

        // ---- Resolved and inline values ------------------------------------------------------------

        /// <summary>"background-color" → "backgroundColor", "-unity-font-definition" → "unityFontDefinition".</summary>
        internal static string CamelName(string uss)
        {
            var parts = uss.TrimStart('-').Split('-');
            return parts[0] + string.Concat(parts.Skip(1).Select(p => p.Length == 0 ? p : char.ToUpperInvariant(p[0]) + p.Substring(1)));
        }

        internal static string ResolvedValue(VisualElement element, string uss)
        {
            var property = typeof(IResolvedStyle).GetProperty(CamelName(uss));
            return property == null ? null : FormatObject(property.GetValue(element.resolvedStyle));
        }

        private static readonly FieldInfo InlineStyleAccessField = typeof(VisualElement).GetField("inlineStyleAccess", BindingFlags.NonPublic | BindingFlags.Instance);

        /// <summary>Inline (element.style / UXML style attribute) value, or null when not set inline.</summary>
        internal static string InlineValue(VisualElement element, string uss)
        {
            return ScriptInlineValue(element, uss) ?? UxmlInlineValue(element, uss);
        }

        /// <summary>
        /// A UXML style="..." attribute: Unity keeps it as an inline StyleRule (with its StyleSheet) inside the
        /// element's internal InlineStyleAccess, separate from values set through element.style. Found by type,
        /// not member name, so a renamed field doesn't break it.
        /// </summary>
        internal static string UxmlInlineValue(VisualElement element, string uss)
        {
            try
            {
                var access = InlineStyleAccessField?.GetValue(element);
                if (access == null) return null;
                // 6000.6: InlineStyleAccess.inlineRule holds the sheet and the rule.
                var holder = access.GetType().GetProperty("inlineRule", Members)?.GetValue(access) ?? access;
                StyleSheet sheet = null; object rule = null;
                var ruleType = Ui.GetType("UnityEngine.UIElements.StyleRule");
                foreach (var field in holder.GetType().GetFields(Members))
                {
                    if (sheet == null && field.FieldType == typeof(StyleSheet)) sheet = field.GetValue(holder) as StyleSheet;
                    if (rule == null && field.FieldType == ruleType) rule = field.GetValue(holder);
                }
                foreach (var property in holder.GetType().GetProperties(Members))
                {
                    if (property.GetIndexParameters().Length > 0) continue;
                    if (sheet == null && property.PropertyType == typeof(StyleSheet)) sheet = property.GetValue(holder) as StyleSheet;
                    if (rule == null && property.PropertyType == ruleType) rule = property.GetValue(holder);
                }
                if (sheet == null || rule == null) return null;
                foreach (var declaration in (IEnumerable)Get(rule, "properties"))
                    if ((string)Get(declaration, "name") == uss)
                        return FormatValues(sheet, (Array)Get(declaration, "values"));
            }
            catch (Exception) { }
            return null;
        }

        private static string ScriptInlineValue(VisualElement element, string uss)
        {
            var property = typeof(IStyle).GetProperty(CamelName(uss));
            if (property == null) return null;
            var value = property.GetValue(element.style);
            if (value == null) return null;
            // StyleKeyword.Null: no inline value. Undefined: an inline value is set. Others: an inline keyword (auto, none, initial).
            var keyword = value.GetType().GetProperty("keyword")?.GetValue(value);
            if (keyword is StyleKeyword k)
            {
                if (k == StyleKeyword.Null) return null;
                if (k != StyleKeyword.Undefined) return k.ToString().ToLowerInvariant();
            }
            return FormatObject(value.GetType().GetProperty("value")?.GetValue(value));
        }

        internal static string FormatObject(object value)
        {
            switch (value)
            {
                case null: return null;
                case Color color: return "#" + ColorUtility.ToHtmlStringRGBA(color);
                case float number: return number.ToString("0.###", CultureInfo.InvariantCulture);
                case Enum e: return e.ToString().ToLowerInvariant();
                case string s: return s;
                case IEnumerable list: return string.Join(", ", list.Cast<object>().Select(FormatObject));
                default: return value.ToString();
            }
        }
    }
}
