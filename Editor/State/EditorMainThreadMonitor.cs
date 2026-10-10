// Copyright (C) Funplay. Licensed under MIT.
using System;
using System.Diagnostics;
using System.Threading;
using UnityEditor;

namespace Funplay.Editor.State
{
    /// <summary>
    /// Editor main-thread liveness that can be read from any thread. An EditorApplication.update
    /// sampler stamps each tick, so the MCP transport can explain a status read that timed out
    /// because Unity's main thread is held (Unity shows its "Hold on" busy dialog then), and the
    /// test-run tracker can measure the longest uninterrupted hold while each test ran.
    /// </summary>
    [InitializeOnLoad]
    internal static class EditorMainThreadMonitor
    {
        /// <summary>Holds shorter than this are ordinary editor work; Unity's busy dialog appears after about a second.</summary>
        public const double BlockedThresholdSeconds = 1.0;

        private static long _lastTick = Stopwatch.GetTimestamp();
        private static long _windowStart;
        private static long _windowMaxGap;
        private static ActiveTest _activeTest;

        static EditorMainThreadMonitor()
        {
            EditorApplication.update += Tick;
        }

        /// <summary>Seconds since the last EditorApplication.update tick. Thread-safe.</summary>
        public static double SecondsSinceLastUpdate => ToSeconds(Stopwatch.GetTimestamp() - Interlocked.Read(ref _lastTick));

        /// <summary>The test that is running now (set by the test-run tracker), or null. Thread-safe.</summary>
        public static ActiveTest CurrentTest => Volatile.Read(ref _activeTest);

        internal static void SetCurrentTest(string jobId, string testName, int testsCompleted)
        {
            Volatile.Write(ref _activeTest, string.IsNullOrEmpty(testName)
                ? null
                : new ActiveTest(jobId, testName, testsCompleted, Stopwatch.GetTimestamp()));
        }

        /// <summary>Starts measuring the longest main-thread hold (main thread only).</summary>
        internal static void BeginWindow()
        {
            _windowStart = Stopwatch.GetTimestamp();
            _windowMaxGap = 0;
        }

        /// <summary>
        /// Longest time in seconds since <see cref="BeginWindow"/> during which no update tick ran,
        /// including the hold that is still in progress (main thread only).
        /// </summary>
        internal static double LongestHoldInWindowSeconds()
        {
            if (_windowStart == 0) return 0;
            var lastTick = Math.Max(Interlocked.Read(ref _lastTick), _windowStart);
            return ToSeconds(Math.Max(_windowMaxGap, Stopwatch.GetTimestamp() - lastTick));
        }

        /// <summary>
        /// Describes why the main thread is not answering, or null when it ticked recently.
        /// Safe to call from the MCP transport's thread pool threads.
        /// </summary>
        public static object DescribeBusy() => DescribeBusy(SecondsSinceLastUpdate, CurrentTest, Stopwatch.GetTimestamp());

        internal static object DescribeBusy(double secondsSinceUpdate, ActiveTest test, long now)
        {
            if (secondsSinceUpdate < BlockedThresholdSeconds) return null;
            var blocked = Math.Round(secondsSinceUpdate, 1);
            if (test == null)
                return new
                {
                    main_thread_blocked_seconds = blocked,
                    hint = "Unity's main thread has not run an Editor update for this long (a modal dialog, an import, " +
                           "compilation or synchronous code). Status reads resume when it returns."
                };
            return new
            {
                main_thread_blocked_seconds = blocked,
                test_job_id = test.JobId,
                current_test = test.Name,
                current_test_seconds = Math.Round(ToSeconds(now - test.StartedTimestamp), 1),
                tests_completed = test.TestsCompleted,
                hint = "A test is holding Unity's main thread (Unity shows its 'Hold on' dialog). The run continues when the " +
                       "test returns. A plain [Test] runs inside one Editor update; make long tests [UnityTest] and yield " +
                       "while waiting so the Editor and MCP stay responsive."
            };
        }

        private static void Tick()
        {
            var now = Stopwatch.GetTimestamp();
            var previous = Interlocked.Exchange(ref _lastTick, now);
            if (_windowStart != 0)
                _windowMaxGap = Math.Max(_windowMaxGap, now - Math.Max(previous, _windowStart));
        }

        private static double ToSeconds(long stopwatchTicks) => Math.Max(0, stopwatchTicks) / (double)Stopwatch.Frequency;

        internal sealed class ActiveTest
        {
            public ActiveTest(string jobId, string name, int testsCompleted, long startedTimestamp)
            {
                JobId = jobId;
                Name = name;
                TestsCompleted = testsCompleted;
                StartedTimestamp = startedTimestamp;
            }

            public string JobId { get; }
            public string Name { get; }
            public int TestsCompleted { get; }
            public long StartedTimestamp { get; }
        }
    }
}
