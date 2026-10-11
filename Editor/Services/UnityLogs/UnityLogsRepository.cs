// Copyright (C) Funplay. Licensed under MIT.

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Funplay.Editor.Tools.Helpers;
using UnityEngine;

namespace Funplay.Editor.Services.UnityLogs
{
    internal class UnityLogsRepository : IDisposable
    {
        private const int MaxLogs = 200;

        private readonly List<LogEntry> _logs = new List<LogEntry>();
        private readonly HashSet<string> _schedulerMessages = new HashSet<string>();
        private readonly object _lock = new object();
        private bool _isListening;
        private int _sequence;

        public void StartListening()
        {
            if (_isListening)
                return;

            _isListening = true;
            Application.logMessageReceived += OnLogReceived;
        }

        public void StopListening()
        {
            if (!_isListening)
                return;

            _isListening = false;
            Application.logMessageReceived -= OnLogReceived;
        }

        public string GetRecentLogs(string logType = "all", int count = 30, int sinceSeconds = 0,
            string filterText = null, bool groupDuplicates = false, bool includeStackTrace = false, int afterCursor = -1)
        {
            count = Mathf.Clamp(count, 1, 200);
            var filter = (logType ?? "all").ToLowerInvariant();
            var cutoff = sinceSeconds > 0 ? DateTime.Now.AddSeconds(-sinceSeconds) : (DateTime?)null;

            List<LogEntry> snapshot;
            int cursor;
            lock (_lock)
            {
                snapshot = new List<LogEntry>(_logs);
                cursor = _sequence;
            }
            // A cursor beyond the newest entry predates a domain reload (the cache starts over): read everything.
            if (afterCursor > cursor) afterCursor = -1;

            if (snapshot.Count == 0)
                return null;

            var lines = new List<string>();

            for (int i = snapshot.Count - 1; i >= 0 && lines.Count < count; i--)
            {
                var entry = snapshot[i];
                if (cutoff.HasValue && entry.Timestamp < cutoff.Value)
                    continue;

                if (!MatchesFilter(entry.Type, filter))
                    continue;

                if (afterCursor >= 0 && entry.Sequence <= afterCursor)
                    continue;

                if (!MatchesTextFilter(entry.Message, filterText))
                    continue;

                var stackSuffix = includeStackTrace ? FormatStackTrace(entry.StackTrace) : string.Empty;
                lines.Add($"[{ToLabel(entry.Type)}] {FormatMessage(entry.Message)}{stackSuffix}");
            }

            if (lines.Count == 0)
            {
                if (sinceSeconds > 0)
                    return $"No {filter} entries found in cached logs from the last {sinceSeconds} second(s)";

                return $"No {filter} entries found in cached logs";
            }

            var sb = new StringBuilder();
            var uniqueCount = AppendLines(sb, lines, groupDuplicates);

            var timeSuffix = sinceSeconds > 0 ? $", last {sinceSeconds}s" : string.Empty;
            var textSuffix = string.IsNullOrEmpty(filterText) ? string.Empty : $", text: '{filterText}'";
            var groupSuffix = groupDuplicates && uniqueCount < lines.Count
                ? $", {uniqueCount} unique"
                : string.Empty;
            return $"Console logs ({lines.Count} entries{groupSuffix}, filter: {filter}, source: cache{timeSuffix}{textSuffix}, cursor: {cursor}):\n{sb}{SchedulerSummary(filter)}";
        }

        /// <summary>Collapsed UI Toolkit scheduler exceptions (counted, not cached one by one).</summary>
        internal static string SchedulerSummary(string filter)
        {
            if (filter != "all" && filter != "error")
                return string.Empty;
            var entries = SchedulerStallMonitor.SchedulerExceptions;
            if (entries.Count == 0)
                return string.Empty;
            var sb = new StringBuilder("UI Toolkit scheduler exceptions (repeats collapsed; a throwing scheduled item stalls its panel's later scheduled work):\n");
            foreach (var entry in entries)
                sb.Append($"  {TruncateLine(entry.Message)} (x{entry.Count}, frames {entry.FirstFrame}..{entry.LastFrame})\n");
            return sb.ToString();
        }

        internal static bool MatchesTextFilter(string line, string filterText)
        {
            if (string.IsNullOrEmpty(filterText))
                return true;
            return line != null && line.IndexOf(filterText, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // A single log line can be enormous (e.g. an entire save-file JSON dumped to the console).
        // Cap emitted lines so one such entry cannot blow up the whole tool response.
        private const int MaxEmittedLineLength = 300;

        // A whole message (often several lines) is kept: a one-line cut hides the part that explains the error.
        private const int MaxMessageLength = 2000;
        private static readonly Regex StackFrame = new Regex(@"^(\s+at\s|[A-Za-z_][\w.+`<>]*:[\w.<>`]+ \(.*\)\s*$|.*\(at .+:\d+\)\s*$)", RegexOptions.Compiled);

        /// <summary>The whole message, capped, with continuation lines indented so entries stay distinguishable.</summary>
        internal static string FormatMessage(string message)
        {
            if (string.IsNullOrEmpty(message))
                return string.Empty;
            var text = message.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n');
            if (text.Length > MaxMessageLength)
                text = text.Substring(0, MaxMessageLength) + $"... (+{text.Length - MaxMessageLength} chars)";
            return text.Replace("\n", "\n  ");
        }

        /// <summary>
        /// Splits the "message\nstack" text the Editor console stores: the stack begins at the first line shaped like a
        /// frame ("Type:Method () (at File.cs:12)" or "   at ..."); everything before it is the message.
        /// </summary>
        internal static void SplitMessageAndStack(string text, out string message, out string stack)
        {
            message = text ?? string.Empty;
            stack = null;
            var lines = message.Replace("\r\n", "\n").Split('\n');
            for (var i = 1; i < lines.Length; i++)
            {
                if (!StackFrame.IsMatch(lines[i]))
                    continue;
                message = string.Join("\n", lines, 0, i);
                stack = string.Join("\n", lines, i, lines.Length - i);
                return;
            }
        }

        internal static string TruncateLine(string line)
        {
            if (string.IsNullOrEmpty(line) || line.Length <= MaxEmittedLineLength)
                return line;
            return line.Substring(0, MaxEmittedLineLength) + $"... (+{line.Length - MaxEmittedLineLength} chars)";
        }

        // Stack traces get their own, larger cap: MaxEmittedLineLength (300) is sized for a
        // single message line, but a stack trace is legitimately many lines and callers ask
        // for it specifically to see those frames -- still capped so one giant trace can't
        // blow up the response.
        private const int MaxStackTraceLength = 2000;

        internal static string FormatStackTrace(string stackTrace)
        {
            if (string.IsNullOrWhiteSpace(stackTrace))
                return string.Empty;

            var trimmed = stackTrace.TrimEnd();
            var truncated = trimmed.Length > MaxStackTraceLength
                ? trimmed.Substring(0, MaxStackTraceLength) + $"... (+{trimmed.Length - MaxStackTraceLength} chars)"
                : trimmed;

            var sb = new StringBuilder();
            var normalized = truncated.Replace("\r\n", "\n").Replace('\r', '\n');
            foreach (var line in normalized.Split('\n'))
            {
                sb.Append('\n');
                sb.Append("    ");
                sb.Append(line);
            }
            return sb.ToString();
        }

        // Appends lines to the builder; with grouping, identical lines collapse to one
        // "line (xN)" entry in first-seen order. Returns the number of lines written.
        internal static int AppendLines(StringBuilder sb, List<string> lines, bool groupDuplicates)
        {
            if (!groupDuplicates)
            {
                foreach (var line in lines)
                    sb.AppendLine(line);
                return lines.Count;
            }

            var order = new List<string>();
            var counts = new Dictionary<string, int>();
            foreach (var line in lines)
            {
                if (counts.TryGetValue(line, out var existing))
                {
                    counts[line] = existing + 1;
                }
                else
                {
                    counts[line] = 1;
                    order.Add(line);
                }
            }

            foreach (var line in order)
            {
                var n = counts[line];
                sb.AppendLine(n > 1 ? $"{line} (x{n})" : line);
            }
            return order.Count;
        }

        public void Clear()
        {
            lock (_lock)
            {
                _logs.Clear();
                _schedulerMessages.Clear();
            }
        }

        private void OnLogReceived(string message, string stackTrace, LogType type)
        {
            if (string.IsNullOrEmpty(message))
                return;

            if (message.StartsWith("[Funplay]", StringComparison.Ordinal) ||
                message.StartsWith("[Funplay MCP Server]", StringComparison.Ordinal) ||
                message.StartsWith("[Funplay]", StringComparison.Ordinal) ||
                message.StartsWith("[Funplay MCP Server]", StringComparison.Ordinal))
            {
                return;
            }

            lock (_lock)
            {
                // A scheduled item that throws is rethrown every tick; keep its first occurrence and count
                // the rest (SchedulerStallMonitor) so the flood doesn't evict every other log.
                if (SchedulerStallMonitor.IsSchedulerException(type, stackTrace) && !_schedulerMessages.Add(FirstLine(message)))
                    return;

                _logs.Add(new LogEntry
                {
                    Message = message,
                    StackTrace = stackTrace,
                    Type = type,
                    Timestamp = DateTime.Now,
                    Sequence = ++_sequence
                });

                while (_logs.Count > MaxLogs)
                    _logs.RemoveAt(0);
            }
        }

        private static bool MatchesFilter(LogType type, string filter)
        {
            switch (filter)
            {
                case "error":
                    return type == LogType.Error || type == LogType.Assert || type == LogType.Exception;
                case "warning":
                    return type == LogType.Warning;
                case "log":
                    return type == LogType.Log;
                default:
                    return true;
            }
        }

        private static string ToLabel(LogType type)
        {
            switch (type)
            {
                case LogType.Warning:
                    return "WARN";
                case LogType.Error:
                case LogType.Assert:
                case LogType.Exception:
                    return "ERROR";
                default:
                    return "LOG";
            }
        }

        private static string FirstLine(string message)
        {
            return string.IsNullOrEmpty(message) ? string.Empty : message.Split('\n')[0];
        }

        public void Dispose()
        {
            StopListening();
        }

        private class LogEntry
        {
            public string Message { get; set; }
            public string StackTrace { get; set; }
            public LogType Type { get; set; }
            public DateTime Timestamp { get; set; }
            public int Sequence { get; set; }
        }
    }
}
