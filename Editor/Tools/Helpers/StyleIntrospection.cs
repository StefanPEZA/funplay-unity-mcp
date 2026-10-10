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
                object match = null;
                try { match = record.GetType() == MatchType ? record : Activator.CreateInstance(MatchType, sheet, stackIndex, importedIndex, complex); }
                catch (Exception) { }
                matches.Add((match, rule));
            }
            try
            {
                if (matches.Any(m => m.match == null)) throw new MissingMethodException();
                matches.Sort((a, b) => (int)CompareMethod.Invoke(null, new[] { a.match, b.match }));
            }
            catch (Exception)
            {
                // Same order as Unity's Compare: theme sheets first, then specificity, then sheet order and position.
                matches = matches.OrderBy(m => m.rule.ThemeSheet ? 0 : 1).ThenBy(m => m.rule.Ids).ThenBy(m => m.rule.Classes).ThenBy(m => m.rule.Types)
                    .ThenBy(m => m.rule.StackIndex).ThenBy(m => m.rule.Line).ToList();
            }
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

        /// <summary>
        /// A declaration's value as USS-like text (colours as #RRGGBBAA, var(--x), keywords lower-case). A
        /// function handle is followed by its argument count; each function closes after that many handles
        /// (a nested function counts as one argument of its parent).
        /// </summary>
        internal static string FormatValues(StyleSheet sheet, Array handles)
        {
            var text = new StringBuilder();
            var open = new List<int>(); // arguments still expected by each open function, innermost last
            var expectArgCount = false;
            void Emit(string value)
            {
                if (value != "," && text.Length > 0 && text[text.Length - 1] != '(') text.Append(' ');
                text.Append(value);
            }
            void ArgumentDone()
            {
                while (open.Count > 0)
                {
                    open[open.Count - 1]--;
                    if (open[open.Count - 1] > 0) return;
                    open.RemoveAt(open.Count - 1);
                    text.Append(')');
                }
            }
            foreach (var handle in handles)
            {
                string type;
                try { type = Get(handle, "valueType")?.ToString(); }
                catch (Exception) { Emit("?"); ArgumentDone(); continue; }
                if (expectArgCount)
                {
                    expectArgCount = false;
                    var count = 0;
                    try { count = (int)Convert.ToSingle(Read(sheet, "ReadFloat", handle)); } catch (Exception) { }
                    if (count > 0) open.Add(count);
                    else { text.Append(')'); ArgumentDone(); }
                    continue;
                }
                if (type == "Function")
                {
                    string name;
                    try { name = (string)Read(sheet, "ReadFunctionName", handle); } catch (Exception) { name = "?"; }
                    Emit(name + "(");
                    expectArgCount = true;
                    continue;
                }
                string value;
                try
                {
                    switch (type)
                    {
                        case "Color": value = "#" + ColorUtility.ToHtmlStringRGBA((Color)Read(sheet, "ReadColor", handle)); break;
                        case "Keyword": value = Read(sheet, "ReadKeyword", handle)?.ToString().ToLowerInvariant(); break;
                        case "Float": value = Convert.ToSingle(Read(sheet, "ReadFloat", handle)).ToString(CultureInfo.InvariantCulture); break;
                        case "Dimension": value = Read(sheet, "ReadDimension", handle)?.ToString(); break;
                        case "Enum": value = (string)Read(sheet, "ReadEnum", handle); break;
                        case "String": value = "\"" + Read(sheet, "ReadString", handle) + "\""; break;
                        case "Variable": value = (string)Read(sheet, "ReadVariable", handle); break;
                        case "CommaSeparator": value = ","; break;
                        case "AssetReference": value = (Read(sheet, "ReadAssetReference", handle) as UnityEngine.Object)?.name; break;
                        case "MissingAssetReference": value = "url(\"" + Read(sheet, "ReadMissingAssetReferenceUrl", handle) + "\")"; break;
                        case "ResourcePath": value = Read(sheet, "ReadResourcePath", handle)?.ToString(); break;
                        default: value = type?.ToLowerInvariant(); break;
                    }
                }
                catch (Exception) { value = "?"; }
                Emit(string.IsNullOrEmpty(value) ? "?" : value);
                ArgumentDone();
            }
            text.Append(new string(')', open.Count));
            return text.ToString();
        }

        // ---- Heuristic: rules from the USS source text ------------------------------------------------

        internal sealed class SourceRule
        {
            public string Selector;
            public int Line;
            public List<Declaration> Declarations = new List<Declaration>();
        }

        /// <summary>
        /// Top-level `selectors { declarations }` blocks of a USS text, one entry per selector, with 1-based
        /// lines. One pass with a running line counter; comments and quoted strings (url("a;b")) are skipped,
        /// and statements such as @import end at ';'.
        /// </summary>
        internal static List<SourceRule> ParseUss(string uss)
        {
            var rules = new List<SourceRule>();
            var text = uss ?? string.Empty;
            var line = 1;
            var inBody = false;
            var selector = new StringBuilder(); var selectorLine = 0;
            var declaration = new StringBuilder(); var declarationLine = 0;
            var declarations = new List<Declaration>();
            void FlushDeclaration()
            {
                var raw = declaration.ToString();
                var colon = raw.IndexOf(':');
                if (colon > 0) declarations.Add(new Declaration { Name = raw.Substring(0, colon).Trim(), Value = raw.Substring(colon + 1).Trim(), Line = declarationLine });
                declaration.Clear(); declarationLine = 0;
            }
            void Append(char c)
            {
                var target = inBody ? declaration : selector;
                if (!char.IsWhiteSpace(c))
                {
                    if (inBody && declarationLine == 0) declarationLine = line;
                    if (!inBody && selectorLine == 0) selectorLine = line;
                }
                if (target.Length > 0 || !char.IsWhiteSpace(c)) target.Append(c);
            }
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
                {
                    var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    end = end < 0 ? text.Length : end + 2;
                    for (var j = i; j < end; j++) if (text[j] == '\n') line++;
                    i = end - 1;
                    continue;
                }
                if (c == '"' || c == '\'')
                {
                    var j = i + 1;
                    while (j < text.Length && text[j] != c) { if (text[j] == '\\') j++; j++; }
                    for (var k = i; k <= Math.Min(j, text.Length - 1); k++) { if (text[k] == '\n') line++; Append(text[k]); }
                    i = j;
                    continue;
                }
                if (c == '\n') { line++; Append(c); continue; }
                if (!inBody)
                {
                    if (c == ';') { selector.Clear(); selectorLine = 0; continue; }
                    if (c == '{') { inBody = true; declarations = new List<Declaration>(); continue; }
                    Append(c);
                    continue;
                }
                if (c == ';' || c == '}')
                {
                    FlushDeclaration();
                    if (c == '}')
                    {
                        foreach (var one in selector.ToString().Split(','))
                            if (one.Trim().Length > 0)
                                rules.Add(new SourceRule { Selector = Regex.Replace(one.Trim(), @"\s+", " "), Line = selectorLine, Declarations = declarations });
                        inBody = false; selector.Clear(); selectorLine = 0;
                    }
                    continue;
                }
                Append(c);
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
                // Packages/... paths live in the PackageCache or a file: folder, not under the project.
                string full;
                try { full = FileUtil.GetPhysicalPath(path); }
                catch (Exception) { full = path; }
                full = Path.GetFullPath(string.IsNullOrEmpty(full) ? path : full);
                if (!File.Exists(full)) continue;
                foreach (var rule in ParseUss(File.ReadAllText(full)))
                {
                    var subjectSelector = SubjectSelector(rule.Selector, element, states, requireState);
                    if (subjectSelector == null) continue;
                    try
                    {
                        if (UIToolkitPanels.Selector.Parse(subjectSelector, null).Matches(element)) result.Add((rule, path));
                    }
                    catch (UIToolkitException) { }
                }
            }
            return result;
        }

        /// <summary>
        /// The selector to test against <paramref name="element"/> with state pseudo-classes resolved, or null
        /// when the rule can't apply. State pseudo-classes count only on the subject (last) compound; a rule
        /// with pseudo-classes on an ancestor compound is skipped (which ancestor is hovered is unknowable).
        /// A subject that is only pseudo-classes or '*' matches any element.
        /// </summary>
        internal static string SubjectSelector(string selector, VisualElement element, ICollection<string> states, bool requireState)
        {
            var parts = Regex.Split(selector.Trim(), @"(\s*>\s*|\s+)");
            var subject = parts[parts.Length - 1];
            for (var i = 0; i < parts.Length - 1; i += 2)
                if (parts[i].Contains(":") || parts[i].Contains("*")) return null;
            var pseudo = Regex.Matches(subject, @":([\w-]+)").Cast<Match>().Select(m => m.Groups[1].Value.ToLowerInvariant()).ToList();
            if (requireState && !pseudo.Any(states.Contains)) return null;
            if (pseudo.Any(p => !states.Contains(p) && !(PseudoClasses.Contains(p) && CurrentlyIn(element, p)))) return null;
            var stripped = Regex.Replace(subject, @":[\w-]+(\([^)]*\))?", "");
            if (stripped.StartsWith("*", StringComparison.Ordinal)) stripped = stripped.Substring(1);
            if (stripped.Length == 0) stripped = element.GetType().Name; // wildcard subject
            var rebuilt = new StringBuilder();
            for (var i = 0; i < parts.Length - 1; i += 2)
                rebuilt.Append(parts[i]).Append(parts[i + 1].Trim() == ">" ? " > " : " ");
            return rebuilt.Append(stripped).ToString();
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

        /// <summary>USS shorthands and the longhands they set.</summary>
        internal static readonly Dictionary<string, string[]> Shorthands = new Dictionary<string, string[]>
        {
            ["margin"] = new[] { "margin-top", "margin-right", "margin-bottom", "margin-left" },
            ["padding"] = new[] { "padding-top", "padding-right", "padding-bottom", "padding-left" },
            ["border-width"] = new[] { "border-top-width", "border-right-width", "border-bottom-width", "border-left-width" },
            ["border-color"] = new[] { "border-top-color", "border-right-color", "border-bottom-color", "border-left-color" },
            ["border-radius"] = new[] { "border-top-left-radius", "border-top-right-radius", "border-bottom-right-radius", "border-bottom-left-radius" },
            ["flex"] = new[] { "flex-grow", "flex-shrink", "flex-basis" },
            ["transition"] = new[] { "transition-property", "transition-duration", "transition-timing-function", "transition-delay" },
            ["-unity-text-outline"] = new[] { "-unity-text-outline-width", "-unity-text-outline-color" },
            ["background-position"] = new[] { "background-position-x", "background-position-y" },
        };

        /// <summary>Whether a declaration of <paramref name="declared"/> sets (part of) <paramref name="property"/>.</summary>
        internal static bool Sets(string declared, string property) =>
            declared == property ||
            Shorthands.TryGetValue(declared, out var longhands) && longhands.Contains(property) ||
            Shorthands.TryGetValue(property, out var parts) && parts.Contains(declared);

        /// <summary>"background-color" → "backgroundColor", "-unity-font-definition" → "unityFontDefinition".</summary>
        internal static string CamelName(string uss)
        {
            // The one USS name that differs from its C# property.
            if (string.Equals(uss, "-unity-font-style", StringComparison.Ordinal)) return "unityFontStyleAndWeight";
            var parts = uss.TrimStart('-').Split('-');
            return parts[0] + string.Concat(parts.Skip(1).Select(p => p.Length == 0 ? p : char.ToUpperInvariant(p[0]) + p.Substring(1)));
        }

        internal static string ResolvedValue(VisualElement element, string uss)
        {
            if (Shorthands.TryGetValue(uss, out var longhands))
                return string.Join(" ", longhands.Select(l => ResolvedValue(element, l) ?? "?"));
            var property = typeof(IResolvedStyle).GetProperty(CamelName(uss));
            return property == null ? null : FormatObject(property.GetValue(element.resolvedStyle));
        }

        private static readonly FieldInfo InlineStyleAccessField = typeof(VisualElement).GetField("inlineStyleAccess", BindingFlags.NonPublic | BindingFlags.Instance);

        /// <summary>Inline (element.style / UXML style attribute) value, or null when not set inline.</summary>
        internal static string InlineValue(VisualElement element, string uss)
        {
            if (Shorthands.TryGetValue(uss, out var longhands))
            {
                var values = longhands.Select(l => ScriptInlineValue(element, l)).ToList();
                if (values.Any(v => v != null)) return string.Join(" ", values.Select(v => v ?? "unset"));
                return UxmlInlineValue(element, uss);
            }
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
                    if (Sets((string)Get(declaration, "name"), uss))
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
