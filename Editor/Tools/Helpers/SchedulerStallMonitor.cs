// Copyright (C) Funplay. Licensed under MIT.
using System;
using System.Collections.Generic;
using System.Linq;
using Funplay.Editor.State;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Funplay.Editor.Tools.Helpers
{
    /// <summary>
    /// Detects a UI Toolkit panel whose scheduler threw. A scheduled item that throws is rethrown every
    /// tick: it floods the console (evicting other logs) and the items queued after it on that panel
    /// never run, so the UI freezes without naming the panel. Two independent signals are combined:
    /// exceptions whose stack runs through the scheduler (counted here, collapsed in the log cache),
    /// and a per-panel liveness probe (a no-op scheduled item that must run within a few frames while
    /// Play Mode frames advance). A panel is reported stalled only when both agree.
    /// </summary>
    [InitializeOnLoad]
    internal static class SchedulerStallMonitor
    {
        /// <summary>Player frames a probe may take before the panel counts as stalled.</summary>
        internal const int ProbeFrames = 10;
        private const int MaxDistinctExceptions = 20;

        internal sealed class ExceptionEntry
        {
            public string Message;
            public int Count, FirstFrame, LastFrame;
            public DateTime LastTime;
        }

        private sealed class Probe
        {
            public int PlantedFrame;
            public bool Fired;
        }

        private static readonly Dictionary<string, ExceptionEntry> Exceptions = new Dictionary<string, ExceptionEntry>();
        private static readonly Dictionary<IPanel, Probe> Probes = new Dictionary<IPanel, Probe>();

        static SchedulerStallMonitor()
        {
            Application.logMessageReceived += OnLog;
            EditorApplication.playModeStateChanged += change =>
            {
                if (change == PlayModeStateChange.ExitingEditMode) Exceptions.Clear();
                Probes.Clear();
            };
        }

        /// <summary>True for an exception thrown from a UI Toolkit scheduled item (TimerEventScheduler / ScheduledItem frames).</summary>
        internal static bool IsSchedulerException(LogType type, string stackTrace) =>
            (type == LogType.Exception || type == LogType.Error || type == LogType.Assert) && !string.IsNullOrEmpty(stackTrace) &&
            (stackTrace.IndexOf("TimerEventScheduler", StringComparison.Ordinal) >= 0 || stackTrace.IndexOf("ScheduledItem", StringComparison.Ordinal) >= 0);

        private static void OnLog(string message, string stackTrace, LogType type)
        {
            if (!IsSchedulerException(type, stackTrace)) return;
            Record(message);
        }

        internal static void Record(string message)
        {
            var key = FirstLine(message);
            if (!Exceptions.TryGetValue(key, out var entry))
            {
                if (Exceptions.Count >= MaxDistinctExceptions) return;
                Exceptions[key] = entry = new ExceptionEntry { Message = key, FirstFrame = Time.frameCount };
            }
            entry.Count++;
            entry.LastFrame = Time.frameCount;
            entry.LastTime = DateTime.Now;
        }

        internal static List<ExceptionEntry> SchedulerExceptions => Exceptions.Values.OrderByDescending(e => e.LastTime).ToList();

        internal static void Reset()
        {
            Exceptions.Clear();
            Probes.Clear();
        }

        private static string FirstLine(string message)
        {
            var text = message ?? "";
            var newline = text.IndexOf('\n');
            return (newline >= 0 ? text.Substring(0, newline) : text).Trim();
        }

        /// <summary>
        /// Checks the panel's previous probe and plants a new one. "alive" (the last probe ran), "pending"
        /// (no verdict yet), "overdue" (a probe has not run for ProbeFrames advancing frames) or null
        /// outside Play Mode or while frames are stuck. Each read only plants a probe if none is waiting.
        /// </summary>
        internal static string ProbePanel(VisualElement root)
        {
            var panel = root?.panel;
            if (panel == null || !EditorApplication.isPlaying || EditorApplication.isPaused) return null;
            string verdict = null;
            if (Probes.TryGetValue(panel, out var probe))
            {
                if (probe.Fired) verdict = "alive";
                else if (PlayModeFrameMonitor.FramesStuck) return null;
                else verdict = Time.frameCount - probe.PlantedFrame >= ProbeFrames ? "overdue" : "pending";
            }
            if (probe == null || probe.Fired)
            {
                var next = new Probe { PlantedFrame = Time.frameCount };
                Probes[panel] = next;
                root.schedule.Execute(() => next.Fired = true);
            }
            return verdict ?? "pending";
        }

        /// <summary>
        /// The stalled-scheduler report for a panel, or null: only when its probe is overdue and a
        /// scheduler exception was logged in this Play session.
        /// </summary>
        internal static object Report(VisualElement root)
        {
            var verdict = ProbePanel(root);
            if (verdict != "overdue" || Exceptions.Count == 0) return null;
            var latest = SchedulerExceptions.First();
            return new
            {
                stalled = true,
                exception = latest.Message,
                count = latest.Count,
                first_frame = latest.FirstFrame,
                last_frame = latest.LastFrame,
                other_exceptions = Exceptions.Count - 1,
                hint = "A scheduled item (schedule.Execute) on this panel throws every tick, so later scheduled work on the panel no longer runs. Fix the exception (see get_console_logs) and re-enter Play Mode."
            };
        }
    }
}
