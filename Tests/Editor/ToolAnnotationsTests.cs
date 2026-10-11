// Copyright (C) Funplay. Licensed under MIT.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Funplay.Editor.MCP.Server;
using Funplay.Editor.Tools;
using NUnit.Framework;
using UnityEngine;

namespace Funplay.Editor.Tests
{
    public sealed class ToolAnnotationsTests
    {
        private const string SnapshotFile = "ToolAnnotations.snapshot.txt";

        [Test]
        public void EveryRegisteredToolIsClassifiedDeliberately()
        {
            var unclassified = ToolRegistry.MethodCache
                .Where(kvp => !ToolAnnotations.IsClassified(kvp.Key, kvp.Value))
                .Select(kvp => kvp.Key)
                .OrderBy(name => name)
                .ToArray();

            CollectionAssert.IsEmpty(unclassified,
                "Add each write tool to ToolAnnotations.Destructive or NonDestructive (exactly one); read-only tools to neither.");
        }

        [Test]
        public void NoToolSchemaDefaultIsNonFiniteNumber()
        {
            // NaN/Infinity are not valid JSON; a strict client (Node) rejects the whole tools/list.
            var bad = ToolSchemaBuilder.BuildAll()
                .SelectMany(tool => tool.function.parameters.properties
                    .Where(p => p.Value.defaultValue is double d && (double.IsNaN(d) || double.IsInfinity(d)))
                    .Select(p => tool.function.name + "." + p.Key))
                .ToArray();

            CollectionAssert.IsEmpty(bad);
        }

        [Test]
        public void ClassificationListsOnlyNameRegisteredTools()
        {
            var lists = ToolAnnotations.Destructive
                .Concat(ToolAnnotations.NonDestructive)
                .Concat(ToolAnnotations.Idempotent)
                .Concat(ToolAnnotations.OpenWorld);
            var unknown = lists.Where(name => ToolRegistry.GetMethod(name) == null).Distinct().ToArray();

            CollectionAssert.IsEmpty(unknown, "Annotation lists name tools that don't exist");
        }

        [TestCase("show_dialog")][TestCase("simulate_key_press")][TestCase("simulate_key_combo")]
        [TestCase("request_recompile")][TestCase("log_message")][TestCase("select_object")]
        [TestCase("focus_on_object")][TestCase("memory_take_full_snapshot")][TestCase("memory_open_snapshot_in_profiler")]
        [TestCase("wait_for_compilation")][TestCase("capture_simulator_view")][TestCase("get_console_logs")]
        [TestCase("capture_game_view")][TestCase("capture_scene_view")]
        public void ToolsWithSideEffectsAreNotReadOnly(string tool)
        {
            Assert.IsFalse(ToolRegistry.IsReadOnly(ToolRegistry.GetMethod(tool)), tool);
        }

        [Test]
        public void ExportEmitsExplicitHintsForEveryTool()
        {
            var tools = new MCPToolExporter(TempSettingsScope.Create("full")).ExportTools();
            Assert.Greater(tools.Count, 0);

            foreach (var tool in tools)
            {
                var name = (string)tool["name"];
                Assert.IsTrue(tool.ContainsKey("annotations"), name);
                var annotations = (Dictionary<string, object>)tool["annotations"];
                foreach (var key in new[] { "title", "readOnlyHint", "destructiveHint", "idempotentHint", "openWorldHint" })
                    Assert.IsTrue(annotations.ContainsKey(key), name + " lacks " + key);
            }

            var byName = tools.ToDictionary(t => (string)t["name"], t => (Dictionary<string, object>)t["annotations"]);
            Assert.AreEqual(true, byName["get_hierarchy"]["readOnlyHint"]);
            Assert.AreEqual(false, byName["get_hierarchy"]["destructiveHint"]);
            Assert.AreEqual(false, byName["get_hierarchy"]["openWorldHint"]);
            Assert.AreEqual(true, byName["delete_game_object"]["destructiveHint"]);
            Assert.AreEqual(true, byName["install_package"]["openWorldHint"]);
            Assert.AreEqual("Get UI Toolkit Info", byName["get_ui_toolkit_info"]["title"]);
        }

        [Test]
        public void ExportCarriesTypedParameterDefaults()
        {
            var tools = new MCPToolExporter(TempSettingsScope.Create("full")).ExportTools();
            var capture = tools.First(t => (string)t["name"] == "capture_game_view");
            var properties = (Dictionary<string, object>)((Dictionary<string, object>)capture["inputSchema"])["properties"];
            var saveToFile = (Dictionary<string, object>)properties["save_to_file"];

            Assert.AreEqual(false, saveToFile["default"]);
        }

        [Test]
        public void ManualToolAnnotationsAreExported()
        {
            var exported = MCPToolExporter.ConvertAnnotations(new Api.Models.ToolAnnotationsDef { readOnlyHint = true, destructiveHint = false });

            Assert.AreEqual(true, exported["readOnlyHint"]);
            Assert.AreEqual(false, exported["destructiveHint"]);
            Assert.IsFalse(exported.ContainsKey("openWorldHint"));
            Assert.IsNull(MCPToolExporter.ConvertAnnotations(null));
        }

        [TestCase("2025-06-18", "2025-06-18")]
        [TestCase("2025-03-26", "2025-03-26")]
        [TestCase("2024-11-05", "2024-11-05")]
        [TestCase("2099-01-01", "2025-06-18")]
        [TestCase(null, "2025-06-18")]
        public void ProtocolVersionIsNegotiated(string requested, string expected)
        {
            var request = new MCPRequest { Method = "initialize", Params = new Dictionary<string, object>() };
            if (requested != null)
                request.Params["protocolVersion"] = requested;

            Assert.AreEqual(expected, MCPRequestHandler.NegotiateProtocolVersion(request));
        }

        /// <summary>
        /// Snapshot of every tool's annotations, so a changed hint shows up in review. On mismatch the
        /// actual snapshot is written to Library/FunplayMcp/ToolAnnotations.snapshot.actual.txt.
        /// </summary>
        [Test]
        public void AnnotationsMatchSnapshot()
        {
            var actual = string.Join("\n", ToolRegistry.MethodCache.Keys
                .OrderBy(name => name, StringComparer.Ordinal)
                .Select(name =>
                {
                    var a = ToolAnnotations.ForMethod(name, ToolRegistry.GetMethod(name));
                    return name + " " + Flag("ro", a.readOnlyHint) + Flag("destructive", a.destructiveHint) +
                           Flag("idempotent", a.idempotentHint) + Flag("openworld", a.openWorldHint);
                })
                .Select(line => line.TrimEnd())) + "\n";

            var snapshotPath = Path.Combine(TestsDirectory(), SnapshotFile);
            var expected = File.Exists(snapshotPath) ? File.ReadAllText(snapshotPath).Replace("\r\n", "\n") : string.Empty;
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
            {
                var actualPath = Path.Combine(Path.GetDirectoryName(Application.dataPath), "Library", "FunplayMcp", "ToolAnnotations.snapshot.actual.txt");
                Directory.CreateDirectory(Path.GetDirectoryName(actualPath));
                File.WriteAllText(actualPath, actual);
                var expectedLines = new HashSet<string>(expected.Split('\n'));
                var actualLines = new HashSet<string>(actual.Split('\n'));
                Assert.Fail("Tool annotations changed. Review and copy " + actualPath + " to " + snapshotPath +
                            "\nNew: " + string.Join("; ", actualLines.Except(expectedLines).Take(20)) +
                            "\nGone: " + string.Join("; ", expectedLines.Except(actualLines).Take(20)));
            }
        }

        private static string Flag(string label, bool? value)
        {
            return value == true ? label + " " : string.Empty;
        }

        private static string TestsDirectory()
        {
            var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(ToolAnnotationsTests).Assembly);
            return info != null
                ? Path.Combine(info.resolvedPath, "Tests", "Editor")
                : Path.GetFullPath(Path.Combine("Packages", "com.gamebooom.unity.mcp", "Tests", "Editor"));
        }
    }
}
