// Copyright (C) Funplay. Licensed under MIT.
using System.Linq;
using Funplay.Editor.Tools.Builtins;
using Funplay.Editor.Tools.Helpers;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Funplay.Editor.Tests
{
    public sealed class ResponseCompactionTests
    {
        private const string TaskJson =
            "{\"success\":true,\"message\":\"Inspect operation.status\",\"data\":{\"task\":{\"task_id\":\"abc\",\"status\":\"running\",\"revision\":\"0123\"," +
            "\"phase\":null,\"wait_complete\":false,\"poll_after_ms\":2000},\"operation\":{\"status\":\"ready\",\"error_code\":null," +
            "\"compilation_errors\":[],\"journal_error\":null,\"request_key\":\"k1\",\"is_compiling\":false,\"refresh_pending\":false,\"is_playing\":false}}}";

        [TestCase("full", false)]
        [TestCase("FULL", false)]
        [TestCase("compact", true)]
        public void DetailArgumentOverridesTheSetting(string detail, bool compact) => Assert.AreEqual(compact, ResponseDetail.IsCompact(detail));

        [Test]
        public void ReceiptToolsDropNullEmptyAndDefaultFalseFlags()
        {
            var pruned = JObject.Parse(ResponsePruner.Prune("get_task", TaskJson, "compact"));
            var data = (JObject)pruned["data"];
            Assert.IsNull(data["task"]["phase"]);
            Assert.IsNull(data["operation"]["error_code"]);
            Assert.IsNull(data["operation"]["compilation_errors"]);
            Assert.IsNull(data["operation"]["is_compiling"]);
            Assert.IsNull(data["operation"]["refresh_pending"]);
        }

        [Test]
        public void FollowUpFieldsAndUnlistedFalseValuesSurvive()
        {
            var data = (JObject)JObject.Parse(ResponsePruner.Prune("get_task", TaskJson, "compact"))["data"];
            Assert.AreEqual("abc", (string)data["task"]["task_id"]);
            Assert.AreEqual("running", (string)data["task"]["status"]);
            Assert.AreEqual("0123", (string)data["task"]["revision"]);
            Assert.AreEqual(2000, (int)data["task"]["poll_after_ms"]);
            Assert.AreEqual("k1", (string)data["operation"]["request_key"]);
            Assert.AreEqual(false, (bool)data["task"]["wait_complete"], "wait_complete is a decision input, not on the opt-in list");
            Assert.AreEqual(false, (bool)data["operation"]["is_playing"]);
        }

        [Test]
        public void ErrorEnvelopeKeepsCodeAndError()
        {
            const string json = "{\"success\":false,\"code\":\"X\",\"error\":\"X\",\"data\":{\"hint\":\"h\",\"details\":null}}";
            var pruned = JObject.Parse(ResponsePruner.Prune("prepare_editor", json, "compact"));
            Assert.AreEqual("X", (string)pruned["code"]);
            Assert.AreEqual("X", (string)pruned["error"]);
            Assert.AreEqual("h", (string)pruned["data"]["hint"]);
            Assert.IsNull(pruned["data"]["details"]);
        }

        [Test]
        public void FullDetailReturnsTheInputUnchanged() => Assert.AreEqual(TaskJson, ResponsePruner.Prune("get_task", TaskJson, "full"));

        [TestCase("execute_code")]
        [TestCase("get_visual_tree")]
        [TestCase("get_component_properties")]
        [TestCase("get_editor_state")]
        public void DataReadersAreNeverPruned(string tool)
        {
            const string json = "{\"success\":true,\"data\":{\"returnValue\":null,\"children\":[],\"text\":\"\",\"is_compiling\":false}}";
            Assert.AreEqual(json, ResponsePruner.Prune(tool, json, "compact"));
        }

        [Test]
        public void NonJsonTextPassesThrough()
        {
            Assert.AreEqual("plain text", ResponsePruner.Prune("get_task", "plain text", "compact"));
            Assert.AreEqual("{broken", ResponsePruner.Prune("get_task", "{broken", "compact"));
            Assert.AreEqual("{\"x\":null}", ResponsePruner.Prune("get_task", "{\"x\":null}", "compact"), "no success envelope");
        }

        [Test]
        public void EveryPrunedToolIsAReceiptOrEchoTool()
        {
            foreach (var reader in new[] { "execute_code", "get_visual_tree", "get_component_properties", "get_scene_info", "get_console_logs", "get_editor_state", "get_tool_capabilities" })
                Assert.IsFalse(ResponsePruner.Tools.Contains(reader), reader + " returns data where empty values are answers");
        }

        private static VisualGeometry FullView() => new VisualGeometry
        {
            capture_id = "c1", surface = "game_view", interactive = true, render_width = 1920, render_height = 1080,
            output_width = 1920, output_height = 1080, viewport = new VisualRect { width = 1920, height = 1080 }
        };

        [Test]
        public void PlainCaptureGeometryIsIdAndSizeOnly()
        {
            var compact = FullView().ToCompact();
            Assert.AreEqual(new[] { "capture_id", "size" }, compact.Properties().Select(p => p.Name).ToArray());
            Assert.AreEqual(new[] { 1920, 1080 }, compact["size"].Select(t => (int)t).ToArray());
            Assert.Less(compact.ToString(Newtonsoft.Json.Formatting.None).Length, 200);
        }

        [Test]
        public void CompactGeometryKeepsWhatDiffersFromAPlainCapture()
        {
            var scaled = FullView(); scaled.output_width = 960; scaled.output_height = 540; scaled.viewport = new VisualRect { width = 960, height = 540 };
            Assert.AreEqual(new[] { 1920, 1080 }, scaled.ToCompact()["render"].Select(t => (int)t).ToArray());

            var crop = VisualCoordinates.CreateCrop(1920, 1080, new RectInt(100, 200, 50, 40), 4);
            var compact = crop.ToCompact();
            Assert.AreEqual(-400f, (float)compact["viewport"]["x"], "a crop's viewport offset is what maps image pixels back to render pixels");

            var recording = FullView(); recording.surface = "recording_frame"; recording.interactive = false;
            Assert.AreEqual("recording_frame", (string)recording.ToCompact()["surface"]);
            Assert.AreEqual(false, (bool)recording.ToCompact()["interactive"]);
        }

        [Test]
        public void CaptureReplyIsCompactByDefaultButTheFullContractStaysRegistered()
        {
            var texture = new Texture2D(8, 8);
            try
            {
                var png = texture.EncodeToPNG();
                var compact = JObject.Parse(ScreenshotFunctions.FinishCapture(png, false, null, "game-view", FullView(), null, "compact"));
                var full = JObject.Parse(ScreenshotFunctions.FinishCapture(png, false, null, "game-view", FullView(), null, "full"));
                Assert.IsNull(compact["data"]["geometry"]["note"]);
                Assert.IsNull(compact["data"]["geometry"]["scene_signature"]);
                Assert.AreEqual("c1", (string)compact["data"]["geometry"]["capture_id"]);
                Assert.IsNotNull(full["data"]["geometry"]["note"], "detail=full restores the 0.8.6 geometry");
                Assert.IsNotNull(VisualCoordinates.Find("c1"), "get_visual_coordinates and click-by-capture_id use the registered geometry");
                Assert.AreEqual(1, (int)compact["_meta"]["funplay_capture"]);
            }
            finally { Object.DestroyImmediate(texture); }
        }

        private static RecordingFramesJob FramesJob(int count)
        {
            var job = new RecordingFramesJob { job_id = "j1", recording_id = "r1", status = "completed", complete = true, timestamps = new[] { 0.0, 1.0, 2.0, 3.0, 4.0, 5.0 } };
            for (var i = 0; i < count; i++)
                job.frames.Add(new RecordingFrame
                {
                    index = i, requested_seconds = i, actual_seconds = i + 0.01, delta_seconds = 0.01, path = "f" + i + ".png",
                    width = 1920, height = 1080, geometry = FullView()
                });
            return job;
        }

        [Test]
        public void RecordingFramesShareOneGeometryAndSizeInCompactDetail()
        {
            var job = FramesJob(6);
            var compact = JObject.FromObject(RecordingEvidenceService.View(job, "compact"));
            var frame = (JObject)compact["frames"][0];
            Assert.AreEqual(new[] { "index", "actual_seconds", "delta_seconds", "path" }, frame.Properties().Select(p => p.Name).ToArray());
            Assert.AreEqual(new[] { 1920, 1080 }, compact["frame_size"].Select(t => (int)t).ToArray());
            Assert.IsNull(compact["selection_policy"]);
            Assert.AreEqual("j1", (string)compact["job_id"]);
            Assert.AreEqual(6, ((JArray)compact["timestamps"]).Count, "requested times stay in timestamps");
            Assert.Less(compact.ToString(Newtonsoft.Json.Formatting.None).Length, 1500);
        }

        [Test]
        public void RecordingFramesFullDetailIsTheJobItself()
        {
            var job = FramesJob(2);
            Assert.AreSame(job, RecordingEvidenceService.View(job, "full"));
        }
    }
}
