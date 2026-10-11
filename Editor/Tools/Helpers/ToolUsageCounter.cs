// Copyright (C) Funplay. Licensed under MIT.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using UnityEditor;

namespace Funplay.Editor.Tools.Helpers
{
    /// <summary>
    /// Per-tool call and error counts kept in Library/FunplayMcp/usage.json. Local only: never sent anywhere.
    /// They show which tools a project really uses, so tool-surface changes rest on data, not on one sample.
    /// </summary>
    [InitializeOnLoad]
    internal static class ToolUsageCounter
    {
        internal sealed class Entry { public int calls, errors; }

        private static readonly object Gate = new object();
        private static Dictionary<string, Entry> counts;
        private static bool dirty;
        private static double lastSave;
        private const double SaveIntervalSeconds = 30;

        static ToolUsageCounter()
        {
            AssemblyReloadEvents.beforeAssemblyReload += Save;
            EditorApplication.quitting += Save;
        }

        internal static string FilePath => Path.Combine("Library", "FunplayMcp", "usage.json");

        private static Dictionary<string, Entry> Load()
        {
            if (counts != null) return counts;
            try { counts = JsonConvert.DeserializeObject<Dictionary<string, Entry>>(File.ReadAllText(FilePath)); }
            catch { /* missing or unreadable: start empty */ }
            return counts = counts ?? new Dictionary<string, Entry>();
        }

        /// <summary>An error envelope is <c>{"success":false,...}</c>; anything else counts as a successful call.</summary>
        internal static bool IsErrorResult(string result) =>
            result != null && result.StartsWith("{\"success\":false", StringComparison.Ordinal);

        internal static void Record(string tool, string result)
        {
            if (string.IsNullOrEmpty(tool)) return;
            lock (Gate)
            {
                var all = Load();
                if (!all.TryGetValue(tool, out var entry)) all[tool] = entry = new Entry();
                entry.calls++;
                if (IsErrorResult(result)) entry.errors++;
                dirty = true;
                var now = EditorApplication.timeSinceStartup;
                if (now - lastSave > SaveIntervalSeconds) SaveLocked(now);
            }
        }

        internal static Dictionary<string, Entry> Snapshot()
        {
            lock (Gate) return Load().ToDictionary(p => p.Key, p => new Entry { calls = p.Value.calls, errors = p.Value.errors });
        }

        internal static void Reset()
        {
            lock (Gate) { counts = new Dictionary<string, Entry>(); dirty = true; SaveLocked(EditorApplication.timeSinceStartup); }
        }

        internal static void Save() { lock (Gate) SaveLocked(0); }

        private static void SaveLocked(double now)
        {
            if (!dirty || counts == null) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllText(FilePath, JsonConvert.SerializeObject(counts, Formatting.Indented));
                dirty = false; lastSave = now;
            }
            catch { /* best effort: a failed save must never fail a tool call */ }
        }
    }
}
