// Copyright (C) Funplay. Licensed under MIT.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Funplay.Editor.Tools.Builtins
{
    internal static class ExecuteCodeSafetyPolicy
    {
        // Project folders strict filesystem safety still lets a snippet write into. Both are
        // Unity-generated and unversioned; Library/ also holds the package's own output
        // (Library/FunplayMcp/Screenshots, Recordings), e.g. for saving a cropped screenshot.
        private static readonly string[] WritableProjectFolders = { "Library", "Temp" };

        // Words that can precede an identifier without declaring it (`return path;`, `x in path)`).
        private static readonly HashSet<string> NonTypeKeywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "return", "in", "await", "out", "ref", "new", "is", "as", "case", "yield", "throw", "else",
            "nameof", "typeof", "when", "and", "or", "not", "params", "this", "goto", "using", "lock"
        };

        private static readonly SafetyRule[] BaseRules =
        {
            new SafetyRule(@"\bFile\.Delete\b", "File.Delete blocked by safety_checks"),
            new SafetyRule(@"\bDirectory\.Delete\b", "Directory.Delete blocked by safety_checks"),
            new SafetyRule(@"\bSystem\.IO\.File\.Delete\b", "System.IO.File.Delete blocked by safety_checks"),
            new SafetyRule(@"\bProcess\.Start\b", "Process.Start blocked by safety_checks"),
            new SafetyRule(@"\bSystem\.Diagnostics\.Process\b", "System.Diagnostics.Process blocked by safety_checks"),
            new SafetyRule(@"\bEnvironment\.Exit\b", "Environment.Exit blocked by safety_checks"),
            new SafetyRule(@"\bApplication\.Quit\b", "Application.Quit blocked by safety_checks"),
            new SafetyRule(@"\bAssetDatabase\.DeleteAsset\b", "AssetDatabase.DeleteAsset blocked by safety_checks"),
            new SafetyRule(@"\bwhile\s*\(\s*true\s*\)", "Infinite while(true) loop blocked by safety_checks"),
            new SafetyRule(@"\bfor\s*\(\s*;\s*;\s*\)", "Infinite for(;;) loop blocked by safety_checks"),
        };

        // Rules with path arguments are skipped for a call whose listed arguments all resolve
        // statically (literals, Path.Combine, Application.dataPath, simple locals) to a path
        // under a WritableProjectFolders entry; any other call is still blocked.
        private const string FileWriteReason = "File write/move operation blocked by strict filesystem safety";
        private const string DirectoryWriteReason = "Directory write/destructive operation blocked by strict filesystem safety";

        private static readonly SafetyRule[] StrictRules =
        {
            new SafetyRule(@"(?<![\w.])(?:System\.IO\.)?File\.(?:WriteAllText|WriteAllBytes|WriteAllLines|AppendAllText|AppendAllLines|Create|CreateText|OpenWrite|SetAttributes|SetCreationTime|SetLastAccessTime|SetLastWriteTime)\b", FileWriteReason, pathArguments: new[] { 0 }),
            new SafetyRule(@"(?<![\w.])(?:System\.IO\.)?File\.Copy\b", FileWriteReason, pathArguments: new[] { 1 }),
            new SafetyRule(@"(?<![\w.])(?:System\.IO\.)?File\.Move\b", FileWriteReason, pathArguments: new[] { 0, 1 }),
            new SafetyRule(@"(?<![\w.])(?:System\.IO\.)?File\.Replace\b", FileWriteReason, pathArguments: new[] { 0, 1, 2 }),
            new SafetyRule(@"(?<![\w.])(?:System\.IO\.)?Directory\.CreateDirectory\b", DirectoryWriteReason, pathArguments: new[] { 0 }),
            new SafetyRule(@"(?<![\w.])(?:System\.IO\.)?Directory\.Move\b", DirectoryWriteReason, pathArguments: new[] { 0, 1 }),
            new SafetyRule(@"(?<![\w.])(?:System\.IO\.)?Directory\.Delete\b", DirectoryWriteReason),
            // The Library/Temp allowance resolves relative paths against the project root, which is
            // only sound while the process working directory stays there.
            new SafetyRule(@"(?<![\w.])(?:System\.IO\.)?Directory\.SetCurrentDirectory\b|(?<![\w.])(?:System\.)?Environment\.CurrentDirectory\s*=(?!=)", "Changing the working directory blocked by strict filesystem safety"),
            new SafetyRule(@"(?<![\w.])(?:System\.IO\.)?FileInfo\.(?:CopyTo|Create|CreateText|Delete|MoveTo|Replace)\b", "FileInfo write/destructive operation blocked by strict filesystem safety"),
            new SafetyRule(@"(?<![\w.])(?:System\.IO\.)?DirectoryInfo\.(?:Create|CreateSubdirectory|Delete|MoveTo)\b", "DirectoryInfo write/destructive operation blocked by strict filesystem safety"),
            new SafetyRule(@"(?<![\w.])(?:System\.IO\.)?FileStream(?=\s*\()", "Raw FileStream construction blocked by strict filesystem safety", pathArguments: new[] { 0 }),
            new SafetyRule(@"(?<![\w.])(?:System\.IO\.)?StreamWriter(?=\s*\()", "Raw StreamWriter construction blocked by strict filesystem safety", pathArguments: new[] { 0 }),
            new SafetyRule(@"(?<![\w.])(?:System\.IO\.)?StreamReader(?=\s*\()", "Raw StreamReader construction blocked by strict filesystem safety", pathArguments: new[] { 0 }),
            new SafetyRule("\"(?:~|%USERPROFILE%|%APPDATA%|%LOCALAPPDATA%|\\$HOME)(?:/|\\\\|\\\\\\\\|\"|$)", "User home/config path blocked by strict filesystem safety"),
            new SafetyRule("\"(?:[A-Za-z]:\\\\|\\\\\\\\|/Users/|/home/|/root/|/System/|/Library/|/Applications/|/bin/|/sbin/|/usr/|/etc/|/var/|/private/|/tmp/)", "Absolute or system path blocked by strict filesystem safety", allowsWritableProjectLiteral: true),
            new SafetyRule("\"[^\"]*(?:\\.\\./|\\.\\.\\\\)[^\"]*\"", "Path traversal blocked by strict filesystem safety"),
        };

        public static bool TryFindViolation(string code, bool strictFilesystemChecks, out string pattern, out string reason)
        {
            return TryFindViolation(code, strictFilesystemChecks, GetProjectRoot(), out pattern, out reason);
        }

        internal static bool TryFindViolation(string code, bool strictFilesystemChecks, string projectRoot, out string pattern, out string reason)
        {
            code = code ?? string.Empty;

            if (TryFindViolation(code, BaseRules, projectRoot, out pattern, out reason))
                return true;

            if (strictFilesystemChecks &&
                TryFindViolation(code, StrictRules, projectRoot, out pattern, out reason))
            {
                return true;
            }

            pattern = null;
            reason = null;
            return false;
        }

        private static bool TryFindViolation(string code, SafetyRule[] rules, string projectRoot, out string pattern, out string reason)
        {
            foreach (var rule in rules)
            {
                foreach (Match match in Regex.Matches(code, rule.Pattern))
                {
                    if (IsAllowedMatch(code, match, rule, projectRoot))
                        continue;

                    pattern = rule.Pattern;
                    reason = rule.Reason;
                    return true;
                }
            }

            pattern = null;
            reason = null;
            return false;
        }

        private static bool IsAllowedMatch(string code, Match match, SafetyRule rule, string projectRoot)
        {
            if (rule.PathArguments != null)
            {
                if (!TryReadArguments(code, match.Index + match.Length, out var arguments))
                    return false;

                foreach (var index in rule.PathArguments)
                {
                    if (index >= arguments.Count || arguments[index] == "null")
                        continue;
                    if (!IsWritableProjectPath(EvaluatePath(arguments[index], code, projectRoot, 0), projectRoot))
                        return false;
                }

                return true;
            }

            return rule.AllowsWritableProjectLiteral &&
                   TryReadStringLiteral(code, match.Index, out var literal, out _) &&
                   IsWritableProjectPath(literal, projectRoot);
        }

        // True when `path` (relative paths resolve against the project root, Unity's working
        // directory) canonicalizes to a location strictly inside a writable project folder.
        internal static bool IsWritableProjectPath(string path, string projectRoot)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(projectRoot))
                return false;

            try
            {
                var fullPath = NormalizeFullPath(Path.IsPathRooted(path) ? path : Path.Combine(projectRoot, path));
                // Windows paths are case-insensitive; Path.GetFullPath returns '\' while
                // Application.dataPath uses '/', so both sides are canonicalized first.
                var comparison = Path.DirectorySeparatorChar == '\\'
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal;
                foreach (var folder in WritableProjectFolders)
                {
                    var root = NormalizeFullPath(Path.Combine(projectRoot, folder));
                    if (fullPath.StartsWith(root + "/", comparison))
                        return true;
                }
            }
            catch (Exception)
            {
            }

            return false;
        }

        private static string NormalizeFullPath(string path)
        {
            return Path.GetFullPath(path).Replace('\\', '/').TrimEnd('/');
        }

        private static string GetProjectRoot()
        {
            try
            {
                return Path.GetDirectoryName(Application.dataPath);
            }
            catch (Exception)
            {
                return null;
            }
        }

        // Best-effort static evaluation of a path expression. Returns null for anything it cannot
        // resolve, which keeps the call blocked.
        private static string EvaluatePath(string expression, string code, string projectRoot, int depth)
        {
            expression = (expression ?? string.Empty).Trim();
            if (expression.Length == 0 || depth > 8)
                return null;

            if (expression[0] == '(' && FindClosing(expression, 0) == expression.Length - 1)
                return EvaluatePath(expression.Substring(1, expression.Length - 2), code, projectRoot, depth + 1);

            var parts = SplitTopLevel(expression, '+');
            if (parts == null)
                return null;
            if (parts.Count > 1)
            {
                var builder = new StringBuilder();
                foreach (var part in parts)
                {
                    var value = EvaluatePath(part, code, projectRoot, depth + 1);
                    if (value == null)
                        return null;
                    builder.Append(value);
                }

                return builder.ToString();
            }

            var literalStart = expression[0] == '@' ? 1 : 0;
            if (literalStart < expression.Length && expression[literalStart] == '"')
            {
                return TryReadStringLiteral(expression, literalStart, out var literal, out var end) && end == expression.Length
                    ? literal
                    : null;
            }

            if (Regex.IsMatch(expression, @"^(?:UnityEngine\.)?Application\.dataPath$"))
                return string.IsNullOrEmpty(projectRoot) ? null : Path.Combine(projectRoot, "Assets");
            if (Regex.IsMatch(expression, @"^(?:System\.IO\.)?Directory\.GetCurrentDirectory\s*\(\s*\)$"))
                return projectRoot;

            var call = Regex.Match(expression, @"^(?:System\.IO\.)?Path\.(Combine|GetFullPath)(?=\s*\()");
            if (call.Success)
            {
                if (!TryReadArguments(expression, call.Length, out var arguments, out var callEnd) ||
                    callEnd != expression.Length || arguments.Count == 0)
                {
                    return null;
                }

                var values = new string[arguments.Count];
                for (var i = 0; i < arguments.Count; i++)
                {
                    values[i] = EvaluatePath(arguments[i], code, projectRoot, depth + 1);
                    if (values[i] == null)
                        return null;
                }

                if (call.Groups[1].Value == "GetFullPath")
                    return values.Length == 1 ? values[0] : null;

                try
                {
                    return Path.Combine(values);
                }
                catch (ArgumentException)
                {
                    return null;
                }
            }

            if (Regex.IsMatch(expression, @"^[A-Za-z_]\w*$"))
            {
                var assigned = FindSingleAssignment(code, expression);
                return assigned == null ? null : EvaluatePath(assigned, code, projectRoot, depth + 1);
            }

            return null;
        }

        // The right-hand side of the only assignment to a local/const named `name`; null when it
        // is assigned more than once or mutated in any other way.
        private static string FindSingleAssignment(string code, string name)
        {
            var escaped = Regex.Escape(name);
            if (Regex.IsMatch(code, @"(?<![\w.])" + escaped + @"\s*(?:\+\+|--|[-+*/%&|^]=|\?\?=)") ||
                Regex.IsMatch(code, @"(?:\+\+|--|\bref\s+|\bout\s+(?:[\w.<>\[\]?]+\s+)?)" + escaped + @"\b") ||
                // Lambda parameters and tuple deconstruction bind the name without a plain `name =`.
                Regex.IsMatch(code, @"(?<![\w.])" + escaped + @"\s*=>") ||
                Regex.IsMatch(code, @"\((?:[^()]*,)?\s*(?:[\w.<>\[\]?]+\s+)?" + escaped + @"\s*(?:,[^()]*)?\)\s*=(?![=>])") ||
                Regex.IsMatch(code, @"\((?:[^()]*,)?\s*(?:[\w.<>\[\]?]+\s+)?" + escaped + @"\s*(?:,[^()]*)?\)\s*=>"))
            {
                return null;
            }

            // A second declaration of the same name in another scope (foreach variable, parameter,
            // `is` pattern) can hold any value; only one declaration is resolvable.
            var declarations = 0;
            foreach (Match declaration in Regex.Matches(code,
                         @"(?<![\w.])([A-Za-z_][\w.]*)(?:<[^<>;]*>)?(?:\[\])?\??\s+" + escaped + @"\b(?=\s*(?:[,);=]|in\b))"))
            {
                if (!NonTypeKeywords.Contains(declaration.Groups[1].Value) && ++declarations > 1)
                    return null;
            }

            var assignments = Regex.Matches(code, @"(?<![\w.])" + escaped + @"\s*=(?![=>])");
            if (assignments.Count != 1)
                return null;

            var start = assignments[0].Index + assignments[0].Length;
            var end = FindTopLevelTerminator(code, start, ";,)");
            return end < 0 ? null : code.Substring(start, end - start);
        }

        private static bool TryReadArguments(string code, int index, out List<string> arguments)
        {
            return TryReadArguments(code, index, out arguments, out _);
        }

        // Reads the parenthesized argument list starting at `index` (after optional whitespace).
        private static bool TryReadArguments(string code, int index, out List<string> arguments, out int end)
        {
            arguments = null;
            end = -1;
            while (index < code.Length && char.IsWhiteSpace(code[index]))
                index++;
            if (index >= code.Length || code[index] != '(')
                return false;

            var close = FindClosing(code, index);
            if (close < 0)
                return false;

            var inner = code.Substring(index + 1, close - index - 1);
            arguments = inner.Trim().Length == 0 ? new List<string>() : SplitTopLevel(inner, ',');
            end = close + 1;
            return arguments != null;
        }

        private static int FindClosing(string text, int openIndex)
        {
            return FindTopLevelTerminator(text, openIndex + 1, ")");
        }

        private static List<string> SplitTopLevel(string text, char separator)
        {
            var parts = new List<string>();
            var start = 0;
            while (true)
            {
                var end = FindTopLevelTerminator(text, start, separator.ToString(), allowEnd: true);
                if (end < 0)
                    return null;
                parts.Add(text.Substring(start, end - start).Trim());
                if (end >= text.Length)
                    return parts;
                start = end + 1;
            }
        }

        // Index of the first terminator character outside nested brackets, strings and char
        // literals; text.Length when allowEnd and the text ends first; -1 when unbalanced.
        private static int FindTopLevelTerminator(string text, int start, string terminators, bool allowEnd = false)
        {
            var depth = 0;
            for (var i = start; i < text.Length; i++)
            {
                var c = text[i];
                if (c == '"')
                {
                    if (!TrySkipStringLiteral(text, i, out var literalEnd))
                        return -1;
                    i = literalEnd - 1;
                    continue;
                }

                if (c == '\'')
                {
                    var close = i + 1;
                    while (close < text.Length && text[close] != '\'')
                        close += text[close] == '\\' ? 2 : 1;
                    if (close >= text.Length)
                        return -1;
                    i = close;
                    continue;
                }

                if (depth == 0 && terminators.IndexOf(c) >= 0)
                    return i;
                if (c == '(' || c == '[' || c == '{')
                    depth++;
                else if (c == ')' || c == ']' || c == '}')
                {
                    if (--depth < 0)
                        return -1;
                }
            }

            return allowEnd && depth == 0 ? text.Length : -1;
        }

        private static bool TrySkipStringLiteral(string text, int quoteIndex, out int end)
        {
            var verbatim = IsVerbatimAt(text, quoteIndex);
            var i = quoteIndex + 1;
            while (i < text.Length)
            {
                var c = text[i];
                if (!verbatim && c == '\\')
                {
                    i += 2;
                    continue;
                }

                if (c == '"')
                {
                    if (verbatim && i + 1 < text.Length && text[i + 1] == '"')
                    {
                        i += 2;
                        continue;
                    }

                    end = i + 1;
                    return true;
                }

                i++;
            }

            end = -1;
            return false;
        }

        // Decodes the regular or verbatim string literal whose opening quote is at quoteIndex.
        // Interpolated strings and escapes other than \\ \" \' are not path literals.
        private static bool TryReadStringLiteral(string text, int quoteIndex, out string value, out int end)
        {
            value = null;
            end = -1;
            if (quoteIndex < 0 || quoteIndex >= text.Length || text[quoteIndex] != '"')
                return false;

            var verbatim = IsVerbatimAt(text, quoteIndex);
            var prefixIndex = quoteIndex - (verbatim ? 2 : 1);
            if (prefixIndex >= 0 && text[prefixIndex] == '$')
                return false;

            var builder = new StringBuilder();
            var i = quoteIndex + 1;
            while (i < text.Length)
            {
                var c = text[i];
                if (c == '\r' || c == '\n')
                {
                    if (!verbatim)
                        return false;
                }
                else if (!verbatim && c == '\\')
                {
                    if (i + 1 >= text.Length)
                        return false;
                    var escaped = text[i + 1];
                    if (escaped != '\\' && escaped != '"' && escaped != '\'')
                        return false;
                    builder.Append(escaped);
                    i += 2;
                    continue;
                }
                else if (c == '"')
                {
                    if (verbatim && i + 1 < text.Length && text[i + 1] == '"')
                    {
                        builder.Append('"');
                        i += 2;
                        continue;
                    }

                    value = builder.ToString();
                    end = i + 1;
                    return true;
                }

                builder.Append(c);
                i++;
            }

            return false;
        }

        private static bool IsVerbatimAt(string text, int quoteIndex)
        {
            return quoteIndex > 0 && text[quoteIndex - 1] == '@';
        }

        private sealed class SafetyRule
        {
            public SafetyRule(string pattern, string reason, int[] pathArguments = null, bool allowsWritableProjectLiteral = false)
            {
                Pattern = pattern;
                Reason = reason;
                PathArguments = pathArguments;
                AllowsWritableProjectLiteral = allowsWritableProjectLiteral;
            }

            public string Pattern { get; }
            public string Reason { get; }
            public int[] PathArguments { get; }
            public bool AllowsWritableProjectLiteral { get; }
        }
    }
}
