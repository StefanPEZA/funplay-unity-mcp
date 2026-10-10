// Copyright (C) Funplay. Licensed under MIT.

using System.Collections.Generic;
using Funplay.Editor.MCP.Server;
using NUnit.Framework;

namespace Funplay.Editor.Tests
{
    public sealed class MCPToolListChangeNotifierTests
    {
        [Test]
        public void TryConsumePending_ConsumesOnceUntilRestored()
        {
            MCPToolListChangeNotifier.RestorePending();

            Assert.IsTrue(MCPToolListChangeNotifier.TryConsumePending());
            Assert.IsFalse(MCPToolListChangeNotifier.TryConsumePending());

            MCPToolListChangeNotifier.RestorePending();

            Assert.IsTrue(MCPToolListChangeNotifier.TryConsumePending());
            Assert.IsFalse(MCPToolListChangeNotifier.TryConsumePending());
        }

        [Test]
        public void BuildSseBody_EmitsNotificationBeforeResponse()
        {
            var body = MCPToolListChangeNotifier.BuildSseBody("{\"jsonrpc\":\"2.0\",\"id\":\"1\",\"result\":{}}");

            Assert.That(body, Does.StartWith("data: " + MCPToolListChangeNotifier.NotificationJson + "\n\n"));
            Assert.That(body, Does.Contain("data: {\"jsonrpc\":\"2.0\",\"id\":\"1\",\"result\":{}}\n\n"));
        }

        [Test]
        public void Hash_ChangesWhenASchemaChangesAndIgnoresKeyOrder()
        {
            var baseline = MCPToolListChangeNotifier.ComputeToolListHash(new[] { Tool("b_tool", "x"), Tool("a_tool", "y") });
            var reordered = MCPToolListChangeNotifier.ComputeToolListHash(new[] { Tool("a_tool", "y"), Tool("b_tool", "x") });
            Assert.AreEqual(baseline, reordered);

            var withParameter = Tool("a_tool", "y");
            ((Dictionary<string, object>)((Dictionary<string, object>)withParameter["inputSchema"])["properties"])["extra"] =
                new Dictionary<string, object> { ["type"] = "string" };
            Assert.AreNotEqual(baseline, MCPToolListChangeNotifier.ComputeToolListHash(new[] { Tool("b_tool", "x"), withParameter }));

            var withAnnotation = Tool("a_tool", "y");
            withAnnotation["annotations"] = new Dictionary<string, object> { ["readOnlyHint"] = true };
            Assert.AreNotEqual(baseline, MCPToolListChangeNotifier.ComputeToolListHash(new[] { Tool("b_tool", "x"), withAnnotation }));
        }

        [Test]
        public void MarkChanged_ArmsTheNotification()
        {
            MCPToolListChangeNotifier.TryConsumePending();
            MCPToolListChangeNotifier.MarkChanged();

            Assert.IsTrue(MCPToolListChangeNotifier.TryConsumePending());
        }

        private static Dictionary<string, object> Tool(string name, string description)
        {
            // Keys deliberately inserted in non-canonical order.
            return new Dictionary<string, object>
            {
                ["inputSchema"] = new Dictionary<string, object>
                {
                    ["properties"] = new Dictionary<string, object>(),
                    ["type"] = "object"
                },
                ["name"] = name,
                ["description"] = description
            };
        }
    }
}
