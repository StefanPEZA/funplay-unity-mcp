// Copyright (C) Funplay. Licensed under MIT.

using Funplay.Editor.Services.UnityLogs;
using NUnit.Framework;
using UnityEngine;

namespace Funplay.Editor.Tests
{
    public sealed class UnityLogsRepositoryTests
    {
        [Test]
        public void GetRecentLogs_FiltersGroupsAndTruncatesCachedEntries()
        {
            var token = "FunplayConsoleGrouping_" + System.Guid.NewGuid().ToString("N");
            var longPayload = new string('x', 2100);

            using (var repository = new UnityLogsRepository())
            {
                repository.StartListening();
                repository.Clear();

                Debug.Log(token + " duplicate");
                Debug.Log(token + " duplicate");
                Debug.Log(token + " unique");
                Debug.Log(token + " " + longPayload);

                var grouped = repository.GetRecentLogs(
                    logType: "log",
                    count: 10,
                    sinceSeconds: 0,
                    filterText: token,
                    groupDuplicates: true);

                Assert.That(grouped, Does.Contain("Console logs (4 entries, 3 unique, filter: log, source: cache"));
                Assert.That(grouped, Does.Contain("[LOG] " + token + " duplicate (x2)"));
                Assert.That(grouped, Does.Contain("[LOG] " + token + " unique"));
                Assert.That(grouped, Does.Contain("... (+"));
                Assert.That(grouped.Length, Is.LessThan(2600));

                var filtered = repository.GetRecentLogs(
                    logType: "log",
                    count: 10,
                    sinceSeconds: 0,
                    filterText: token + " unique",
                    groupDuplicates: true);

                Assert.That(filtered, Does.Contain("Console logs (1 entries, filter: log, source: cache"));
                Assert.That(filtered, Does.Contain("[LOG] " + token + " unique"));
                Assert.That(filtered, Does.Not.Contain("duplicate"));

                var ungrouped = repository.GetRecentLogs(
                    logType: "log",
                    count: 10,
                    sinceSeconds: 0,
                    filterText: token + " duplicate",
                    groupDuplicates: false);

                Assert.That(ungrouped, Does.Contain("Console logs (2 entries, filter: log, source: cache"));
                Assert.That(ungrouped, Does.Not.Contain("(x2)"));
            }
        }

        [Test]
        public void MultiLineMessagesAreKeptWholeAndCursorReturnsOnlyNewEntries()
        {
            var token = "FunplayMultiLine_" + System.Guid.NewGuid().ToString("N");
            using (var repository = new UnityLogsRepository())
            {
                repository.StartListening();
                repository.Clear();
                Debug.Log(token + " a\nb\nc");
                var first = repository.GetRecentLogs("log", 10, 0, token);
                Assert.That(first, Does.Contain("[LOG] " + token + " a\n  b\n  c"));
                var cursor = int.Parse(System.Text.RegularExpressions.Regex.Match(first, @"cursor: (\d+)").Groups[1].Value);

                Debug.Log(token + " second");
                var next = repository.GetRecentLogs("log", 10, 0, token, afterCursor: cursor);
                Assert.That(next, Does.Contain("second").And.Not.Contain(" a\n"));
                Assert.That(next, Does.Contain("(1 entries"));

                // A cursor from before a reload (greater than anything cached) reads everything again.
                Assert.That(repository.GetRecentLogs("log", 10, 0, token, afterCursor: cursor + 1000), Does.Contain("(2 entries"));
            }
        }

        [Test]
        public void ConsoleTextSplitsAtTheFirstStackFrame()
        {
            UnityLogsRepository.SplitMessageAndStack("first\nsecond\nUnityEngine.Debug:Log (object)\nFoo:Bar () (at Assets/Foo.cs:12)", out var message, out var stack);
            Assert.AreEqual("first\nsecond", message);
            StringAssert.StartsWith("UnityEngine.Debug:Log", stack);
            UnityLogsRepository.SplitMessageAndStack("only text", out message, out stack);
            Assert.AreEqual("only text", message);
            Assert.IsNull(stack);
        }

        [Test]
        public void SinceSecondsOnAnEmptyCacheIsZeroEntriesNotAnError()
        {
            var repository = Funplay.Editor.DI.RootScopeServices.Services?.GetService(typeof(UnityLogsRepository)) as UnityLogsRepository;
            repository?.StartListening();
            var result = Funplay.Editor.Tools.Builtins.VisualFeedbackFunctions.GetConsoleLogs(clear_cache: true, since_seconds: 5);
            StringAssert.DoesNotContain("INVALID_SINCE_SECONDS", result);
            StringAssert.Contains("No all entries found", result);
        }

        [Test]
        public void HelperMethods_HandleEmptyTextAndLongLines()
        {
            Assert.IsTrue(UnityLogsRepository.MatchesTextFilter("Hello Console", "console"));
            Assert.IsFalse(UnityLogsRepository.MatchesTextFilter("Hello Console", "missing"));
            Assert.IsTrue(UnityLogsRepository.MatchesTextFilter(null, null));
            Assert.IsFalse(UnityLogsRepository.MatchesTextFilter(null, "missing"));

            var line = new string('a', 305);
            var truncated = UnityLogsRepository.TruncateLine(line);

            Assert.That(truncated, Does.StartWith(new string('a', 300)));
            Assert.That(truncated, Does.EndWith("... (+5 chars)"));
        }

        [Test]
        public void FormatStackTrace_NormalizesLineEndingsAndCapsLength()
        {
            var normalized = UnityLogsRepository.FormatStackTrace("First\r\nSecond\rThird\n");

            Assert.AreEqual("\n    First\n    Second\n    Third", normalized);
            Assert.That(normalized, Does.Not.Contain("\r"));

            var longTrace = new string('s', 2105);
            var truncated = UnityLogsRepository.FormatStackTrace(longTrace);
            Assert.That(truncated, Does.StartWith("\n    " + new string('s', 2000)));
            Assert.That(truncated, Does.EndWith("... (+105 chars)"));
        }
    }
}
