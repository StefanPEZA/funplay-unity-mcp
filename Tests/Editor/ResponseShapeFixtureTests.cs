// Copyright (C) Funplay. Licensed under MIT.
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Funplay.Editor.Tools.Helpers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Funplay.Editor.Tests
{
    /// <summary>Compaction checked against stored result shapes: full is untouched, compact never loses a decision field.</summary>
    public sealed class ResponseShapeFixtureTests
    {
        // Keys an agent branches on or follows up with. Compaction must keep every one that the full reply has.
        private static readonly string[] Invariants =
        {
            "task_id", "request_key", "capture_id", "jobId", "status", "phase", "code", "error", "error_code", "compilation_errors",
            "failures", "blockingTests", "revision", "poll_after_ms", "wait_reason", "path", "clicked", "success"
        };

        private static IEnumerable<string> Fixtures()
        {
            var root = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(ResponseShapeFixtureTests).Assembly).resolvedPath;
            return Directory.GetFiles(Path.Combine(root, "Tests", "Editor", "Fixtures", "ResponseShapes"), "*.json");
        }

        private static IEnumerable<string> Names(JToken token)
            => token is JObject o ? o.Properties().SelectMany(p => new[] { p.Name }.Concat(Names(p.Value))) : token is JArray a ? a.SelectMany(Names) : Enumerable.Empty<string>();

        private static IEnumerable<(string path, JToken value)> Leaves(JToken token)
            => token is JContainer c && c.HasValues ? c.Children().SelectMany(Leaves) : new[] { (token.Path, token) };

        [Test]
        public void FixturesExist() => Assert.GreaterOrEqual(Fixtures().Count(), 8);

        [Test]
        public void FullDetailLeavesEveryFixtureUntouched()
        {
            foreach (var file in Fixtures())
            {
                var fixture = JObject.Parse(File.ReadAllText(file));
                var text = fixture["result"].ToString(Formatting.None);
                Assert.AreEqual(text, ResponsePruner.Prune((string)fixture["tool"], text, "full"), file);
            }
        }

        [Test]
        public void CompactKeepsEveryInvariantKeyTheFullReplyHas()
        {
            foreach (var file in Fixtures())
            {
                var fixture = JObject.Parse(File.ReadAllText(file));
                var text = fixture["result"].ToString(Formatting.None);
                var compact = JObject.Parse(ResponsePruner.Prune((string)fixture["tool"], text, "compact"));
                var before = Names(fixture["result"]).ToHashSet();
                var after = Names(compact).ToHashSet();
                foreach (var key in Invariants.Where(before.Contains))
                    Assert.IsTrue(after.Contains(key) || IsEmptyEverywhere(fixture["result"], key), key + " was dropped from " + Path.GetFileName(file));
            }
        }

        [Test]
        public void OnlyEmptyValuesAndListedFalseFlagsAreRemoved()
        {
            foreach (var file in Fixtures())
            {
                var fixture = JObject.Parse(File.ReadAllText(file));
                var full = (JObject)fixture["result"];
                var compact = JObject.Parse(ResponsePruner.Prune((string)fixture["tool"], full.ToString(Formatting.None), "compact"));
                foreach (var (fullPath, value) in Leaves(full))
                {
                    var path = fullPath.StartsWith("result.") ? fullPath.Substring(7) : fullPath;
                    if (compact.SelectToken(path) != null) continue;
                    var name = path.Substring(path.LastIndexOf('.') + 1).Split('[')[0];
                    var droppable = value.Type == JTokenType.Null || (value is JValue v && v.Value is string s && s.Length == 0) ||
                                    (value.Type == JTokenType.Boolean && !(bool)value && ResponsePruner.DefaultFalseFlags.Contains(name)) || value is JContainer;
                    Assert.IsTrue(droppable, $"{Path.GetFileName(file)}: {path} = {value} was removed");
                }
            }
        }

        [Test]
        public void SignalsThatAreFalseButNotListedSurvive()
        {
            var text = File.ReadAllText(Fixtures().First(f => f.EndsWith("click_not_activated.json")));
            var compact = JObject.Parse(ResponsePruner.Prune("click_ui_toolkit_element", JObject.Parse(text)["result"].ToString(Formatting.None), "compact"));
            Assert.AreEqual(false, (bool)compact["data"]["clicked"], "a click that did not activate must stay visible");
            Assert.AreEqual(true, (bool)compact["data"]["covered"]);
        }

        private static bool IsEmptyEverywhere(JToken root, string key)
            => root.SelectTokens("$.." + key).All(t => t.Type == JTokenType.Null || (t is JContainer c && !c.HasValues));
    }
}
