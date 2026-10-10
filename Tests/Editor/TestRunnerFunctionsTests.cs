// Copyright (C) Funplay. Licensed under MIT.

using System;
using System.Collections;
using System.Diagnostics;
using System.Threading;
using Funplay.Editor.State;
using Funplay.Editor.Tools.Builtins;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Funplay.Editor.Tests
{
    public sealed class TestRunnerFunctionsTests
    {
        private static readonly DateTime Now = new DateTime(2026, 7, 13, 12, 0, 0);

        [Test]
        public void StuckAssessment_FlagsRunnerStartAtThirtySeconds()
        {
            Assert.IsNull(TestRunnerFunctions.AssessStuckState(Now.AddSeconds(-29), null, Now));

            var assessment = TestRunnerFunctions.AssessStuckState(Now.AddSeconds(-30), null, Now);

            Assert.IsNotNull(assessment);
            Assert.AreEqual("runner_start_or_transition", assessment.Phase);
            Assert.AreEqual(30, assessment.SecondsSinceActivity);
            StringAssert.Contains("another caller", assessment.Hint);
        }

        [Test]
        public void StuckAssessment_GivesKnownTestLongerThreshold()
        {
            const string testName = "Game.Tests.LoadsLargeScene";
            Assert.IsNull(TestRunnerFunctions.AssessStuckState(Now.AddSeconds(-60), testName, Now));
            Assert.IsNull(TestRunnerFunctions.AssessStuckState(Now.AddSeconds(-119), testName, Now));

            var assessment = TestRunnerFunctions.AssessStuckState(Now.AddSeconds(-120), testName, Now);

            Assert.IsNotNull(assessment);
            Assert.AreEqual("test_execution", assessment.Phase);
            Assert.AreEqual(120, assessment.SecondsSinceActivity);
            StringAssert.Contains(testName, assessment.Hint);
            StringAssert.Contains("legitimate long-running test", assessment.Hint);
        }

        [Test]
        public void StuckAssessment_IgnoresFutureActivity()
        {
            Assert.IsNull(TestRunnerFunctions.AssessStuckState(Now.AddSeconds(60), null, Now));
        }

        [Test]
        public void BlockingTests_KeepsOnlyHoldsOverThresholdOrderedAndCapped()
        {
            var job = new JObject();
            TestRunnerFunctions.RecordBlockingTest(job, "Fast", 0.4);
            Assert.IsNull(job["blockingTests"]);

            for (var i = 0; i < TestRunnerFunctions.MaxBlockingTests + 2; i++)
                TestRunnerFunctions.RecordBlockingTest(job, "Slow" + i, 1 + i);

            var list = (JArray)job["blockingTests"];
            Assert.AreEqual(TestRunnerFunctions.MaxBlockingTests, list.Count);
            Assert.AreEqual("Slow" + (TestRunnerFunctions.MaxBlockingTests + 1), list[0].Value<string>("test"));
            Assert.AreEqual(TestRunnerFunctions.MaxBlockingTests + 2, list[0].Value<double>("blocked_seconds"));
            Assert.AreEqual("Slow2", list[list.Count - 1].Value<string>("test"));
        }

        [Test]
        public void BusyDescription_NamesTheBlockingTest()
        {
            var started = Stopwatch.GetTimestamp();
            var test = new EditorMainThreadMonitor.ActiveTest("job-1", "Game.Tests.SyncLoad", 7, started);

            Assert.IsNull(EditorMainThreadMonitor.DescribeBusy(0.2, test, started));

            var busy = JObject.FromObject(EditorMainThreadMonitor.DescribeBusy(3.04, test, started + 4 * Stopwatch.Frequency));
            Assert.AreEqual(3.0, busy.Value<double>("main_thread_blocked_seconds"));
            Assert.AreEqual("job-1", busy.Value<string>("test_job_id"));
            Assert.AreEqual("Game.Tests.SyncLoad", busy.Value<string>("current_test"));
            Assert.AreEqual(4.0, busy.Value<double>("current_test_seconds"));
            Assert.AreEqual(7, busy.Value<int>("tests_completed"));
            StringAssert.Contains("[UnityTest]", busy.Value<string>("hint"));

            var idle = JObject.FromObject(EditorMainThreadMonitor.DescribeBusy(2, null, started));
            Assert.IsNull(idle["current_test"]);
            StringAssert.Contains("modal dialog", idle.Value<string>("hint"));
        }

        [UnityTest]
        public IEnumerator HoldWindow_MeasuresTheLongestMainThreadHold()
        {
            yield return null;
            EditorMainThreadMonitor.BeginWindow();
            yield return null;
            Thread.Sleep(300);
            yield return null;
            yield return null;

            var held = EditorMainThreadMonitor.LongestHoldInWindowSeconds();
            Assert.GreaterOrEqual(held, 0.25);
            Assert.Less(held, 5);
        }
    }
}
