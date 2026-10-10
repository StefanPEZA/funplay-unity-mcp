// Copyright (C) Funplay. Licensed under MIT.
using System.Reflection;
using Funplay.Editor.Services.UnityLogs;
using Funplay.Editor.Tools.Helpers;
using NUnit.Framework;
using UnityEngine;

namespace Funplay.Editor.Tests
{
    public sealed class SchedulerStallMonitorTests
    {
        private const string SchedulerStack =
            "MenuController.<OnUIReload>b__3_0 () (at Assets/E2E/MenuController.cs:20)\n" +
            "UnityEngine.UIElements.TimerEventScheduler+ScheduledItem.PerformTimerUpdate (UnityEngine.UIElements.TimerState state)\n" +
            "UnityEngine.UIElements.TimerEventScheduler.UpdateScheduledEvents ()\n";

        [SetUp]
        public void SetUp() => SchedulerStallMonitor.Reset();

        [TearDown]
        public void TearDown() => SchedulerStallMonitor.Reset();

        [Test]
        public void SchedulerExceptionsAreRecognisedByTheirStack()
        {
            Assert.IsTrue(SchedulerStallMonitor.IsSchedulerException(LogType.Exception, SchedulerStack));
            Assert.IsFalse(SchedulerStallMonitor.IsSchedulerException(LogType.Log, SchedulerStack), "only errors");
            Assert.IsFalse(SchedulerStallMonitor.IsSchedulerException(LogType.Exception, "Game.Update () (at Assets/Game.cs:3)"));
        }

        [Test]
        public void AFloodIsCollapsedInsteadOfEvictingOtherLogs()
        {
            var repository = new UnityLogsRepository();
            var onLog = typeof(UnityLogsRepository).GetMethod("OnLogReceived", BindingFlags.NonPublic | BindingFlags.Instance);
            onLog.Invoke(repository, new object[] { "Player saved", "", LogType.Log });
            for (var i = 0; i < 500; i++)
            {
                const string message = "NullReferenceException: Object reference not set to an instance of an object";
                onLog.Invoke(repository, new object[] { message, SchedulerStack, LogType.Exception });
                SchedulerStallMonitor.Record(message);
            }

            var logs = repository.GetRecentLogs("all", 200);
            StringAssert.Contains("Player saved", logs, "the earlier log survives the flood");
            Assert.AreEqual(1, CountOf(logs, "[ERROR] NullReferenceException"), logs);
            StringAssert.Contains("UI Toolkit scheduler exceptions", logs);
            StringAssert.Contains("(x500", logs);
            Assert.AreEqual(1, SchedulerStallMonitor.SchedulerExceptions.Count);
            Assert.AreEqual(500, SchedulerStallMonitor.SchedulerExceptions[0].Count);
        }

        [Test]
        public void NoVerdictOutsidePlayMode()
        {
            Assert.IsNull(SchedulerStallMonitor.ProbePanel(new UnityEngine.UIElements.VisualElement()), "no panel, no Play Mode");
        }

        private static int CountOf(string text, string value)
        {
            var count = 0;
            for (var index = text.IndexOf(value, System.StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + 1, System.StringComparison.Ordinal)) count++;
            return count;
        }
    }
}
