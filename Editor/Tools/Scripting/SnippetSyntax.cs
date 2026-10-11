// Copyright (C) Funplay. Licensed under MIT.

using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace Funplay.Editor.Tools.Scripting
{
    /// <summary>Just enough C# lexing to tell a type declaration from a statement body, without a compiler.</summary>
    internal static class SnippetSyntax
    {
        private static readonly Regex Declaration = new Regex(@"\b(?:class|struct|interface|enum|record)\s+([A-Za-z_]\w*)", RegexOptions.Compiled);
        private static readonly Regex UsingLine = new Regex(@"^\s*using\s+(?:static\s+)?[\w.]+(?:\s*=\s*[\w.<>,\s]+)?\s*;\s*$", RegexOptions.Compiled);

        /// <summary>Blanks comments, string and char literals (keeping line breaks and length), so keywords inside them are ignored.</summary>
        internal static string Mask(string code)
        {
            var sb = new StringBuilder(code.Length);
            for (var i = 0; i < code.Length; i++)
            {
                var c = code[i];
                var next = i + 1 < code.Length ? code[i + 1] : '\0';
                if (c == '/' && next == '/')
                {
                    while (i < code.Length && code[i] != '\n') { sb.Append(' '); i++; }
                    if (i < code.Length) sb.Append('\n');
                }
                else if (c == '/' && next == '*')
                {
                    for (i += 2; i < code.Length && !(code[i] == '*' && i + 1 < code.Length && code[i + 1] == '/'); i++) sb.Append(code[i] == '\n' ? '\n' : ' ');
                    sb.Append("  "); i++;
                }
                else if (c == '"' || c == '\'' || ((c == '@' || c == '$') && (next == '"' || (next == '@' || next == '$') && i + 2 < code.Length && code[i + 2] == '"')))
                {
                    var verbatim = c == '@' || next == '@' && c == '$' || (i > 0 && code[i - 1] == '@');
                    while (code[i] == '@' || code[i] == '$') { sb.Append(' '); i++; }
                    var quote = code[i];
                    sb.Append(' ');
                    for (i++; i < code.Length; i++)
                    {
                        if (code[i] == quote)
                        {
                            if (verbatim && i + 1 < code.Length && code[i + 1] == quote) { sb.Append("  "); i++; continue; }
                            break;
                        }
                        if (code[i] == '\\' && !verbatim) { sb.Append(' '); i++; }
                        sb.Append(i < code.Length && code[i] == '\n' ? '\n' : ' ');
                    }
                    sb.Append(' ');
                }
                else sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>True when the snippet declares a type at brace depth 0; <paramref name="name"/> is the first one.</summary>
        internal static bool TryFindTypeDeclaration(string code, out string name)
        {
            name = null;
            var masked = Mask(code);
            var depth = 0;
            var start = 0;
            for (var i = 0; i <= masked.Length; i++)
            {
                if (i < masked.Length && masked[i] != '{' && masked[i] != '}') continue;
                if (depth == 0)
                {
                    var match = Declaration.Match(masked, start, i - start);
                    if (match.Success) { name = match.Groups[1].Value; return true; }
                }
                if (i < masked.Length) depth += masked[i] == '{' ? 1 : -1;
                start = i + 1;
            }
            return false;
        }

        private static readonly Regex UguiNamespace = new Regex(@"\bUnityEngine\s*\.\s*UI\b", RegexOptions.Compiled);

        /// <summary>True when code (outside comments and strings) names the uGUI namespace itself, not <c>UnityEngine.UIElements</c>.</summary>
        internal static bool NamesUguiNamespace(string code) => UguiNamespace.IsMatch(Mask(code));

        /// <summary>Splits leading <c>using</c> directives from the statements after them.</summary>
        internal static string SplitLeadingUsings(string code, out string usings) => SplitLeadingUsings(code, out usings, out _);

        /// <summary>As above; <paramref name="bodyFirstLine"/> is the 1-based snippet line where the statements start.</summary>
        internal static string SplitLeadingUsings(string code, out string usings, out int bodyFirstLine)
        {
            var kept = new List<string>();
            var found = new StringBuilder();
            var lines = code.Replace("\r\n", "\n").Split('\n');
            var i = 0;
            for (; i < lines.Length && (lines[i].Trim().Length == 0 || UsingLine.IsMatch(lines[i])); i++)
                if (lines[i].Trim().Length > 0) found.AppendLine(lines[i].Trim());
            bodyFirstLine = i + 1;
            for (; i < lines.Length; i++) kept.Add(lines[i]);
            usings = found.ToString();
            return string.Join("\n", kept);
        }
    }
}
