// Copyright (C) Funplay. Licensed under MIT.

using System;
using System.Security.Cryptography;
using System.Text;

namespace Funplay.Editor.Tools.Helpers
{
    /// <summary>
    /// Lets a repeated read answer "unchanged" in a few bytes. Replies of <see cref="MinimumBytes"/> or more get a
    /// short content <c>revision</c>; a caller passing the same <c>if_revision</c> gets only <c>{unchanged, revision}</c>.
    /// </summary>
    internal static class ResponseRevision
    {
        internal const int MinimumBytes = 1024;

        internal static readonly string[] Tools =
            { "get_visual_tree", "get_hierarchy", "get_scene_info", "get_component_properties", "get_console_logs" };

        internal static string Compute(string text)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text)), 0, 8).Replace("-", "").ToLowerInvariant();
        }

        internal static string Apply(string result, string ifRevision)
        {
            if (result == null || result.Length < MinimumBytes) return result;
            var revision = Compute(result);
            if (string.Equals(ifRevision, revision, StringComparison.Ordinal))
                return "{\"success\":true,\"unchanged\":true,\"revision\":\"" + revision + "\"}";
            // A JSON envelope gets the field spliced in (still valid JSON); plain text gets a trailing line.
            return result[0] == '{' ? "{\"revision\":\"" + revision + "\"," + result.Substring(1) : result + "\nrevision: " + revision;
        }
    }
}
