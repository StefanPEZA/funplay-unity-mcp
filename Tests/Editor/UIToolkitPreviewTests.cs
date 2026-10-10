// Copyright (C) Funplay. Licensed under MIT.
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Funplay.Editor.State;
using Funplay.Editor.Tools;
using Funplay.Editor.Tools.Builtins;
using Funplay.Editor.Tools.Helpers;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace Funplay.Editor.Tests
{
    public sealed class UIToolkitPreviewTests
    {
        [Test]
        public void ToolsAreDiscoverable()
        {
            foreach (var name in new[] { "wait_for_ui_toolkit", "capture_ui_toolkit_element", "preview_ui_toolkit", "set_game_view_size" })
                Assert.IsNotNull(ToolRegistry.GetMethod(name), name);
        }

        [Test]
        public void ElementCropAddsPaddingAndFlipsToRenderPixels()
        {
            var identity = new UIToolkitPanels.ScreenMapping { Origin = Vector2.zero, Scale = Vector2.one };
            var crop = UIToolkitFunctions.ComputeElementCrop(new Rect(10, 20, 30, 40), identity, 5, 200, 100);
            Assert.AreEqual(new RectInt(5, 35, 40, 50), crop.Value, "screen top 20..60 is render bottom 40..80, plus 5 padding");

            var clamped = UIToolkitFunctions.ComputeElementCrop(new Rect(-10, -10, 30, 30), identity, 0, 200, 100);
            Assert.AreEqual(new RectInt(0, 80, 20, 20), clamped.Value, "parts outside the render are cut off");

            Assert.IsNull(UIToolkitFunctions.ComputeElementCrop(new Rect(300, 0, 10, 10), identity, 0, 200, 100));

            // Two panel units per screen pixel (PanelSettings scale 0.5): panel 20..80 is screen 10..40.
            var dense = new UIToolkitPanels.ScreenMapping { Origin = Vector2.zero, Scale = new Vector2(2, 2) };
            Assert.AreEqual(new RectInt(10, 60, 30, 30), UIToolkitFunctions.ComputeElementCrop(new Rect(20, 20, 60, 60), dense, 0, 200, 100).Value);
        }

        [Test]
        public void CropGeometryMapsImagePixelsBackToTheRender()
        {
            var geometry = VisualCoordinates.CreateCrop(200, 100, new RectInt(5, 35, 40, 50), 2);
            Assert.AreEqual("game_view", geometry.surface);
            Assert.IsTrue(geometry.interactive);
            Assert.AreEqual(80, geometry.output_width);
            Assert.AreEqual(100, geometry.output_height);

            Assert.IsTrue(VisualCoordinates.TryConvert(0, 0, "image_pixels", "top_left", geometry, out var topLeft, out var error), error);
            Assert.AreEqual(new Vector2(5, 85), topLeft, "image top-left is the crop's top-left render pixel");
            Assert.IsTrue(VisualCoordinates.TryConvert(80, 100, "image_pixels", "top_left", geometry, out var bottomRight, out error), error);
            Assert.AreEqual(new Vector2(45, 35), bottomRight);
            Assert.IsTrue(VisualCoordinates.TryConvert(20, 50, "image_pixels", "top_left", geometry, out var middle, out error), error);
            Assert.AreEqual(new Vector2(15, 60), middle, "scale 2: 20 image px = 10 render px");

            var back = VisualCoordinates.ToImage(new Vector2(15, 60), geometry, "top_left");
            Assert.AreEqual(new Vector2(20, 50), back);

            Assert.Throws<ArgumentException>(() => VisualCoordinates.CreateCrop(200, 100, new RectInt(190, 0, 20, 10), 1));
        }

        [Test]
        public void CropAndScaleRepeatsPixels()
        {
            var frame = new Texture2D(4, 4, TextureFormat.RGBA32, false);
            Texture2D result = null;
            try
            {
                for (var y = 0; y < 4; y++)
                for (var x = 0; x < 4; x++)
                    frame.SetPixel(x, y, new Color32((byte)(x * 10), (byte)(y * 10), 0, 255));
                frame.Apply();
                result = UIToolkitFunctions.CropAndScale(frame, new RectInt(1, 1, 2, 2), 2);
                Assert.AreEqual(4, result.width);
                Assert.AreEqual(4, result.height);
                Assert.AreEqual(new Color32(10, 10, 0, 255), (Color32)result.GetPixel(0, 0));
                Assert.AreEqual(new Color32(10, 10, 0, 255), (Color32)result.GetPixel(1, 1));
                Assert.AreEqual(new Color32(20, 10, 0, 255), (Color32)result.GetPixel(2, 0));
                Assert.AreEqual(new Color32(20, 20, 0, 255), (Color32)result.GetPixel(3, 3));
            }
            finally
            {
                Object.DestroyImmediate(frame);
                if (result != null) Object.DestroyImmediate(result);
            }
        }

        [Test]
        public void PreviewSizesAcceptPresetsAndObjects()
        {
            Assert.AreEqual(4, UIToolkitFunctions.ParsePreviewSizes(null).Count);

            var named = UIToolkitFunctions.ParsePreviewSizes("phone-360, desktop");
            Assert.AreEqual("phone-360", named[0].Name);
            Assert.AreEqual(720, named[0].Width);
            Assert.AreEqual(2f, named[0].DpScale);
            Assert.AreEqual(1920, named[1].Width);

            var mixed = UIToolkitFunctions.ParsePreviewSizes("[\"phone-landscape\", {\"w\": 800, \"h\": 600, \"dp_scale\": 1.5}]");
            Assert.AreEqual(1280, mixed[0].Width);
            Assert.AreEqual(800, mixed[1].Width);
            Assert.AreEqual(1.5f, mixed[1].DpScale);
            Assert.AreEqual("800x600@1.5", mixed[1].Name);

            foreach (var bad in new[] { "tablet", "[{\"w\": 50, \"h\": 600}]", "[{\"w\": 800, \"h\": 600, \"dp_scale\": 0}]", "[1]", "[",
                         "desktop,desktop,desktop,desktop,desktop,desktop,desktop" })
                Assert.Throws<ArgumentException>(() => UIToolkitFunctions.ParsePreviewSizes(bad), bad);
        }

        [Test]
        public void GameViewToolsValidateBeforeChangingAnything()
        {
            Assert.AreEqual("UNKNOWN_PRESET", Code(GameViewFunctions.SetGameViewSize(preset: "tablet")));
            Assert.AreEqual("INVALID_ARGUMENT", Code(GameViewFunctions.SetGameViewSize(width: 800, height: 600, preset: "desktop")));
            Assert.AreEqual("INVALID_ARGUMENT", Code(GameViewFunctions.SetGameViewSize()));
            if (!EditorApplication.isPlaying)
                Assert.AreEqual("PLAY_MODE_REQUIRED", Code(UIToolkitFunctions.PreviewUiToolkit()));
            Assert.AreEqual("INVALID_ARGUMENT", Code(UIToolkitFunctions.CaptureUiToolkitElement(selector: "#x", scale: 5)));
            Assert.AreEqual("INVALID_WAIT_STATE", Code(UIToolkitFunctions.WaitForUiToolkit(selector: "#x", state: "shown")));
        }

        [Test]
        public void PanelScaleOverrideRestoresValuesWithoutTouchingTheFile()
        {
            var folder = "Assets/FunplayPreviewTests_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", Path.GetFileName(folder));
            try
            {
                var settings = ScriptableObject.CreateInstance<PanelSettings>();
                settings.scaleMode = PanelScaleMode.ScaleWithScreenSize;
                settings.referenceResolution = new Vector2Int(1080, 1920);
                settings.match = 0.5f;
                AssetDatabase.CreateAsset(settings, folder + "/Panel.asset");
                AssetDatabase.SaveAssets();
                var hash = PanelScaleOverride.FileHash(folder + "/Panel.asset");
                Assert.IsNotNull(hash);
                Assert.IsNull(PanelScaleOverride.Load(), "no leftover journal from another test");

                PanelScaleOverride.Override(new[] { settings, settings }, 2f);
                Assert.AreEqual(PanelScaleMode.ConstantPixelSize, settings.scaleMode);
                Assert.AreEqual(2f, settings.scale);
                Assert.AreEqual(1, PanelScaleOverride.Load().entries.Count, "shared settings are journaled once");

                PanelScaleOverride.Override(new[] { settings }, 1.5f);
                Assert.AreEqual(1.5f, settings.scale);
                Assert.AreEqual("ScaleWithScreenSize", PanelScaleOverride.Load().entries[0].scale_mode, "a second size keeps the original snapshot");

                var warnings = new List<string>();
                var results = PanelScaleOverride.RestoreAll(warnings);
                Assert.IsEmpty(warnings, string.Join("; ", warnings));
                Assert.AreEqual(PanelScaleMode.ScaleWithScreenSize, settings.scaleMode);
                Assert.AreEqual(new Vector2Int(1080, 1920), settings.referenceResolution);
                Assert.AreEqual(0.5f, settings.match);
                Assert.IsFalse(File.Exists(PanelScaleOverride.JournalPath));
                var result = JObject.FromObject(results[0]);
                Assert.IsTrue((bool)result["byte_identical"], result.ToString());
                Assert.AreEqual(hash, PanelScaleOverride.FileHash(folder + "/Panel.asset"));
                Assert.IsFalse(EditorUtility.IsDirty(settings), "the override never marks the asset dirty");
            }
            finally
            {
                PanelScaleOverride.RestoreAll(new List<string>());
                AssetDatabase.DeleteAsset(folder);
            }
        }

        private static string Code(object response) => (string)JObject.FromObject(response)["code"];
        private static string Code(Task<object> response) => Code(response.Result);
        private static string Code(Task<string> response) => (string)JObject.Parse(response.Result)["code"];

        [Test]
        public void StartupRepairOnlyTouchesFilesThatStillHoldThePreviewScaling()
        {
            var folder = "Assets/FunplayRepairTests_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", Path.GetFileName(folder));
            try
            {
                var repaired = CreateSettings(folder + "/Repaired.asset");
                var edited = CreateSettings(folder + "/Edited.asset");
                var stranger = CreateSettings(folder + "/Stranger.asset");
                var originalHash = PanelScaleOverride.FileHash(folder + "/Repaired.asset");

                PanelScaleOverride.Override(new[] { repaired, edited }, 2f);
                // A crash after something saved the preview scaling into Repaired, and after the user
                // (or a git pull) changed Edited to something else.
                EditorUtility.SetDirty(repaired); AssetDatabase.SaveAssetIfDirty(repaired);
                edited.scaleMode = PanelScaleMode.ScaleWithScreenSize; edited.referenceResolution = new Vector2Int(800, 600);
                EditorUtility.SetDirty(edited); AssetDatabase.SaveAssetIfDirty(edited);

                // Pretend the journal comes from the previous Editor session, whose instance IDs mean nothing now.
                var journal = PanelScaleOverride.Load();
                journal.editor_session = "previous-session";
                journal.entries[0].instance_id = ObjectIdHelper.GetSerializableId(stranger);
                File.WriteAllText(PanelScaleOverride.JournalPath, Newtonsoft.Json.JsonConvert.SerializeObject(journal));

                var warnings = new List<string>();
                PanelScaleOverride.RestoreAll(warnings);

                Assert.AreEqual(PanelScaleMode.ScaleWithScreenSize, repaired.scaleMode, "the file holding the preview scaling is repaired");
                Assert.AreEqual(originalHash, PanelScaleOverride.FileHash(folder + "/Repaired.asset"), "and saved back byte-identical");
                Assert.AreEqual(PanelScaleMode.ConstantPixelSize, stranger.scaleMode, "a stale instance ID never redirects the restore");
                Assert.AreEqual(1f, stranger.scale);
                Assert.AreEqual(new Vector2Int(800, 600), edited.referenceResolution, "a later edit is left alone");
                StringAssert.Contains("left as it is", string.Join("; ", warnings));
                Assert.IsFalse(File.Exists(PanelScaleOverride.JournalPath));
            }
            finally
            {
                PanelScaleOverride.RestoreAll(new List<string>());
                AssetDatabase.DeleteAsset(folder);
            }
        }

        private static PanelSettings CreateSettings(string path)
        {
            var settings = ScriptableObject.CreateInstance<PanelSettings>();
            if (!path.EndsWith("Stranger.asset"))
            {
                settings.scaleMode = PanelScaleMode.ScaleWithScreenSize;
                settings.referenceResolution = new Vector2Int(1080, 1920);
            }
            else { settings.scaleMode = PanelScaleMode.ConstantPixelSize; settings.scale = 1f; }
            AssetDatabase.CreateAsset(settings, path);
            AssetDatabase.SaveAssets();
            return settings;
        }

        [Test]
        public void GameViewLabelsAreScopedToThisProject()
        {
            StringAssert.StartsWith(GameViewSizeOverride.LabelBase, GameViewSizeOverride.LabelPrefix);
            Assert.AreEqual(GameViewSizeOverride.ProjectTag("C:/Work/Game"), GameViewSizeOverride.ProjectTag("c:\\work\\game\\"), "same project, any spelling");
            Assert.AreNotEqual(GameViewSizeOverride.ProjectTag("C:/Work/Game"), GameViewSizeOverride.ProjectTag("C:/Work/Other"));
            Assert.AreEqual(6, GameViewSizeOverride.ProjectTag("x").Length);
        }

        [Test]
        public void PreviewNamesAreFileSafe()
        {
            Assert.AreEqual("phone-360", UIToolkitFunctions.FileSafe("phone-360"));
            Assert.AreEqual("800x600@1.5", UIToolkitFunctions.FileSafe("800x600@1.5"));
            foreach (var name in new[] { "../../Assets/x", "a/b", "c:d", "..\\x" })
            {
                var safe = UIToolkitFunctions.FileSafe(name);
                Assert.IsFalse(safe.Contains("/") || safe.Contains("\\") || safe.Contains("..") || safe.Contains(":"), safe);
            }
        }

        [UnityEngine.TestTools.UnityTest]
        public System.Collections.IEnumerator AWrongTargetIsNeverTakenForHidden()
        {
            var wait = UIToolkitFunctions.WaitForElementAsync("NoSuchHost_" + Guid.NewGuid().ToString("N"), "#dialog", null, null, "hidden", 0.3f, 3);
            // Time-based: batch-mode frames are fast, and .Result on an unfinished wait would block the
            // main thread the wait needs.
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (!wait.IsCompleted && clock.Elapsed.TotalSeconds < 15) yield return null;
            Assert.IsTrue(wait.IsCompleted, "wait did not complete");
            var result = JObject.FromObject(wait.Result);
            Assert.AreEqual("WAIT_TIMEOUT", (string)result["code"], result.ToString());
            Assert.AreEqual("TARGET_NOT_FOUND", (string)result["data"]["lookup"]);
        }
    }
}
