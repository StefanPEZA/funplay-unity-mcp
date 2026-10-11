// Copyright (C) Funplay. Licensed under MIT.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace Funplay.Editor.Tools.Scripting
{
    /// <summary>Named execute_code snippets under Library/FunplayMcp/Snippets (never in Assets/).</summary>
    internal static class SnippetStore
    {
        private static readonly Regex ValidName = new Regex(@"^[A-Za-z0-9_-]{1,64}$", RegexOptions.Compiled);

        private static readonly string RootDirectory = Path.Combine("Library", "FunplayMcp", "Snippets");

        private static string PathOf(string name) => Path.Combine(RootDirectory, name + ".cs");

        internal static string Hash(string text)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text)), 0, 4).Replace("-", "").ToLowerInvariant();
        }

        /// <summary>The saved code, or null when the name is invalid or unknown.</summary>
        internal static string Load(string name)
            => name != null && ValidName.IsMatch(name) && File.Exists(PathOf(name)) ? File.ReadAllText(PathOf(name)) : null;

        internal static string[] Names()
            => Directory.Exists(RootDirectory)
                ? Directory.GetFiles(RootDirectory, "*.cs").Select(Path.GetFileNameWithoutExtension).OrderBy(n => n).ToArray()
                : new string[0];

        /// <summary>Saves a snippet; returns an error message, or null on success.</summary>
        internal static string Save(string name, string code)
        {
            if (name == null || !ValidName.IsMatch(name)) return "save_as must be 1-64 letters, digits, '_' or '-'; the snippet was not saved.";
            try
            {
                Directory.CreateDirectory(RootDirectory);
                File.WriteAllText(PathOf(name), code);
                return null;
            }
            catch (Exception ex) { return "The snippet was not saved: " + ex.Message; }
        }

        internal static bool TryParseArgs(string json, out IDictionary<string, object> args, out string error)
        {
            args = null; error = null;
            if (string.IsNullOrWhiteSpace(json)) return true;
            try
            {
                args = JObject.Parse(json).ToObject<Dictionary<string, object>>();
                return true;
            }
            catch (Exception ex) { error = "args must be a JSON object: " + ex.Message; return false; }
        }
    }
}
