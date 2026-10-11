// Copyright (C) Funplay. Licensed under MIT.
using System.IO;
using Funplay.Editor.Tools.Helpers;
using NUnit.Framework;

namespace Funplay.Editor.Tests
{
    public sealed class ToolUsageCounterTests
    {
        private string _backup;

        [SetUp]
        public void SetUp()
        {
            ToolUsageCounter.Save();
            _backup = File.Exists(ToolUsageCounter.FilePath) ? File.ReadAllText(ToolUsageCounter.FilePath) : null;
            ToolUsageCounter.Reset();
        }

        [TearDown]
        public void TearDown()
        {
            ToolUsageCounter.Reset();
            if (_backup != null) File.WriteAllText(ToolUsageCounter.FilePath, _backup);
        }

        [Test]
        public void CountsCallsAndErrorEnvelopes()
        {
            ToolUsageCounter.Record("t_ok", "{\"success\":true,\"message\":\"x\"}");
            ToolUsageCounter.Record("t_ok", "{\"success\":false,\"code\":\"A\",\"error\":\"A\"}");
            var snapshot = ToolUsageCounter.Snapshot();
            Assert.AreEqual(2, snapshot["t_ok"].calls);
            Assert.AreEqual(1, snapshot["t_ok"].errors);
        }

        [Test]
        public void CountsPersistToTheLocalFileAndReset()
        {
            ToolUsageCounter.Record("t_file", "{\"success\":true}");
            ToolUsageCounter.Save();
            StringAssert.Contains("t_file", File.ReadAllText(ToolUsageCounter.FilePath));
            ToolUsageCounter.Reset();
            Assert.IsEmpty(ToolUsageCounter.Snapshot());
        }

        [Test]
        public void InvokerCountsKnownToolsOnly()
        {
            var invoker = new Funplay.Editor.Tools.FunctionInvokerController();
            invoker.Invoke(new Funplay.Editor.Tools.FunctionCall { FunctionName = "definitely_not_a_tool_xyz" });
            Assert.IsEmpty(ToolUsageCounter.Snapshot());
        }
    }
}
