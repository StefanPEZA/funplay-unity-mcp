// Copyright (C) Funplay. Licensed under MIT.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using Funplay.Editor.State;
using Funplay.Editor.Tools.Helpers;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Funplay.Editor.Tools.Builtins
{
    internal static partial class UIToolkitFunctions
    {
        internal const int MaxPreviewSizes = 6;

        internal static readonly string[] CannotEmulate =
        {
            "Touch: UnityEngine.Device/Input touch support is the desktop Editor's; projects that decide touch layouts once (e.g. a pg-touch class) need extra_root_classes.",
            "Safe area: Screen.safeArea is the whole Game View.",
            "Device DPI: Screen.dpi stays the desktop value; the PanelSettings scale override stands in for it."
        };

        internal sealed class PreviewSize
        {
            public string Name; public int Width, Height; public float DpScale;
        }

        [Description("Play Mode responsive preview of UI Toolkit screens: for each size, sets the Game View resolution, overrides the PanelSettings of the live screen-space panels to ConstantPixelSize with scale=dp_scale (emulating device density), waits until layout settles, and captures the Game View to a file. Everything is restored afterwards (also on Play exit, script reload and next Editor start); PanelSettings are never marked dirty, and the response reports each asset's file hash before/after. Sizes: presets desktop (1920x1080@1), phone-320 (640x1136@2), phone-360 (720x1280@2), phone-landscape (1280x720@2), or JSON objects {\"w\":720,\"h\":1280,\"dp_scale\":2}; default all four presets. The whole call is capped at 10 s. Lists what can't be emulated (touch detection, safe area).")]
        public static async Task<object> PreviewUiToolkit(
            [ToolParam("Comma-separated presets or a JSON array of presets and {w, h, dp_scale} objects; default: all four presets", Required = false)] string sizes = null,
            [ToolParam("Classes added to every host's root while previewing (comma/space separated), e.g. a project's touch class", Required = false)] string extra_root_classes = null,
            [ToolParam("Capture each size to a PNG file", Required = false)] bool capture = true,
            [ToolParam("Consecutive unchanged frames that count as settled, 2..30", Required = false)] int settle_frames = 3)
        {
            if (!EditorApplication.isPlaying)
                return Response.Error("PLAY_MODE_REQUIRED", new { hint = "Runtime panels are built by Play Mode; use prepare_editor(target=play) first." });
            if (EditorApplication.isPaused)
                return Response.Error("PLAY_MODE_PAUSED", new { hint = "Layout and transitions don't advance while paused; unpause first." });
            if (PlayModeFrameMonitor.FramesStuck)
                return Response.Error("PLAY_MODE_FRAMES_STALLED", new { hint = "Play Mode frames are not advancing; bring the Editor to the front." });
            if (_previewRunning || GameViewSizeOverride.PreviewActive)
                return Response.Error("PREVIEW_IN_PROGRESS", new { hint = "Another preview_ui_toolkit call is running; wait for it to finish." });

            List<PreviewSize> parsed;
            try { parsed = ParsePreviewSizes(sizes); }
            catch (ArgumentException ex) { return Response.Error("INVALID_SIZES", new { detail = ex.Message, presets = GameViewSizeOverride.Presets.Select(p => $"{p.Name} {p.Width}x{p.Height}@{p.DpScale}").ToArray() }); }
            var classes = (extra_root_classes ?? string.Empty).Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            settle_frames = Mathf.Clamp(settle_frames, 2, 30);

            var panels = UIToolkitPanels.GetLivePanels(UIToolkitPanels.FindHosts(false)).Where(p => !p.WorldSpace && p.Settings != null).ToList();
            var settings = panels.Select(p => p.Settings).Where(s => !(AssetDatabase.GetAssetPath(s) ?? "").StartsWith("Packages/", StringComparison.Ordinal)).Distinct().ToList();
            if (settings.Count == 0)
                return Response.Error("NO_UI_TOOLKIT_PANELS", new { hint = "No live screen-space PanelRenderer/UIDocument panel with project PanelSettings in Play Mode." });

            var originalView = PreviewGameView.Capture();
            if (originalView == null)
                return Response.Error("GAME_VIEW_SETTINGS_UNAVAILABLE", new { hint = "The main play view is not a Game View (e.g. the Device Simulator is in front); show a Game tab." });

            var clock = Stopwatch.StartNew();
            var addedClasses = new List<KeyValuePair<VisualElement, string>>();
            var results = new List<object>();
            var previewed = 0;
            var warnings = new List<string>();
            List<object> restoredSettings;
            bool viewRestored;
            string stoppedBy = null;
            _previewRunning = true;
            try
            {
                foreach (var panel in panels)
                foreach (var host in panel.Hosts)
                {
                    var root = UIToolkitPanels.GetRoot(host);
                    if (root == null) continue;
                    foreach (var cls in classes)
                        if (!root.ClassListContains(cls)) { root.AddToClassList(cls); addedClasses.Add(new KeyValuePair<VisualElement, string>(root, cls)); }
                }

                for (var index = 0; index < parsed.Count; index++)
                {
                    var size = parsed[index];
                    var remaining = MaxWaitSeconds - 1.0 - clock.Elapsed.TotalSeconds; // keep a second for the restore
                    if (remaining < 0.5) { results.Add(new { name = size.Name, skipped = "time budget (10 s) used up; preview fewer sizes per call" }); continue; }
                    var budget = remaining / (parsed.Count - index);
                    var sizeClock = Stopwatch.StartNew();

                    GameViewSizeOverride.Apply(GameViewSizeOverride.PreviewSlot, size.Width, size.Height);
                    PanelScaleOverride.Override(settings, size.DpScale);
                    var reached = await GameViewFunctions.WaitForRenderSizeAsync(size.Width, size.Height, Math.Min(2.0, budget * 0.5));
                    // Leaving Play Mode restores everything (Play-exit handlers); never re-apply afterwards.
                    if (!EditorApplication.isPlaying) { stoppedBy = "Play Mode ended during the preview"; break; }
                    var settle = await WaitForPanelsSettledAsync(panels, settle_frames, Math.Max(0.2, budget - sizeClock.Elapsed.TotalSeconds - 0.3));
                    if (!EditorApplication.isPlaying) { stoppedBy = "Play Mode ended during the preview"; break; }

                    string path = null; object captureError = null;
                    if (capture)
                    {
                        var frame = await ScreenshotFunctions.ReadGameViewFramebufferAsync();
                        try
                        {
                            if (!EditorApplication.isPlaying) { stoppedBy = "Play Mode ended during the preview"; break; }
                            if (frame == null) captureError = "GAME_VIEW_NOT_AVAILABLE";
                            else
                            {
                                var geometry = VisualCoordinates.Create("game_view", frame.width, frame.height, frame.width, frame.height, false);
                                var saved = JObject.Parse(ScreenshotFunctions.FinishCapture(frame.EncodeToPNG(), true, null, "ui-preview-" + FileSafe(size.Name), geometry));
                                path = (string)saved["data"]?["path"];
                                if (path == null) captureError = saved["code"]?.ToString() ?? "SCREENSHOT_SAVE_FAILED";
                            }
                        }
                        finally { if (frame != null) UnityEngine.Object.DestroyImmediate(frame); }
                    }

                    previewed++;
                    results.Add(new
                    {
                        name = size.Name, width = size.Width, height = size.Height, dp_scale = size.DpScale,
                        render_width = reached.width, render_height = reached.height, size_matched = reached.matched,
                        settled = settle.settled, settle_frames = settle.frames,
                        capture_path = path, capture_error = captureError,
                        elapsed_ms = (int)sizeClock.Elapsed.TotalMilliseconds
                    });
                }
            }
            catch (Exception ex)
            {
                stoppedBy = ex.Message;
            }
            finally
            {
                _previewRunning = false;
                foreach (var pair in addedClasses) pair.Key.RemoveFromClassList(pair.Value);
                restoredSettings = PanelScaleOverride.RestoreAll(warnings);
                GameViewSizeOverride.TryRestore(GameViewSizeOverride.PreviewSlot, warnings);
            }

            await GameViewFunctions.WaitForRenderSizeAsync(0, 0, 0.3);
            var nowView = PreviewGameView.Capture();
            viewRestored = originalView == null || nowView != null && nowView.size_signature == originalView.size_signature;
            if (!viewRestored) warnings.Add("The Game View size differs from before the preview.");
            if (stoppedBy != null)
            {
                warnings.Insert(0, "Preview stopped early: " + stoppedBy);
                if (previewed == 0)
                    return Response.Error("PREVIEW_FAILED", new { detail = stoppedBy, panel_settings = restoredSettings, game_view_restored = viewRestored, warnings });
            }

            return Response.Success($"Previewed {previewed} of {parsed.Count} size(s); everything restored{(warnings.Count > 0 ? " with warnings" : "")}.", new
            {
                sizes = results,
                panel_settings = restoredSettings,
                game_view_restored = viewRestored,
                extra_root_classes = classes.Length > 0 ? classes : null,
                cannot_emulate = CannotEmulate,
                elapsed_ms = (int)clock.Elapsed.TotalMilliseconds,
                warnings = warnings.Count > 0 ? warnings : null
            });
        }

        private static bool _previewRunning;

        /// <summary>A size name as a file-name part: no separators, traversal or reserved characters.</summary>
        internal static string FileSafe(string name)
        {
            var invalid = System.IO.Path.GetInvalidFileNameChars();
            var chars = (name ?? "size").Select(c => invalid.Contains(c) || c == '/' || c == '\\' ? '_' : c).ToArray();
            var safe = new string(chars).Replace("..", "_").Trim('.', ' ');
            return safe.Length == 0 ? "size" : safe.Length > 40 ? safe.Substring(0, 40) : safe;
        }

        internal static List<PreviewSize> ParsePreviewSizes(string sizes)
        {
            var result = new List<PreviewSize>();
            if (string.IsNullOrWhiteSpace(sizes))
            {
                result.AddRange(GameViewSizeOverride.Presets.Select(p => new PreviewSize { Name = p.Name, Width = p.Width, Height = p.Height, DpScale = p.DpScale }));
                return result;
            }

            var trimmed = sizes.Trim();
            var items = new List<JToken>();
            if (trimmed.StartsWith("[", StringComparison.Ordinal))
            {
                try { items.AddRange(JArray.Parse(trimmed)); }
                catch (Newtonsoft.Json.JsonException ex) { throw new ArgumentException("sizes is not a valid JSON array: " + ex.Message); }
            }
            else items.AddRange(trimmed.Split(',').Select(s => (JToken)s.Trim()).Where(s => ((string)s).Length > 0));

            foreach (var item in items)
            {
                if (item.Type == JTokenType.String)
                {
                    var preset = GameViewSizeOverride.FindPreset((string)item) ?? throw new ArgumentException($"Unknown preset '{item}'.");
                    result.Add(new PreviewSize { Name = preset.Name, Width = preset.Width, Height = preset.Height, DpScale = preset.DpScale });
                    continue;
                }
                if (!(item is JObject obj)) throw new ArgumentException("Each size is a preset name or {w, h, dp_scale}.");
                var width = (int?)(obj["w"] ?? obj["width"]) ?? 0;
                var height = (int?)(obj["h"] ?? obj["height"]) ?? 0;
                var dp = (float?)obj["dp_scale"] ?? 1f;
                if (width < 128 || width > 4096 || height < 128 || height > 4096) throw new ArgumentException($"Size {width}x{height}: width and height must be 128..4096.");
                if (float.IsNaN(dp) || dp < 0.25f || dp > 8f) throw new ArgumentException($"dp_scale {dp} must be 0.25..8.");
                result.Add(new PreviewSize { Name = (string)obj["name"] ?? $"{width}x{height}@{dp}", Width = width, Height = height, DpScale = dp });
            }
            if (result.Count == 0) throw new ArgumentException("sizes is empty.");
            if (result.Count > MaxPreviewSizes) throw new ArgumentException($"At most {MaxPreviewSizes} sizes per call (10 s budget).");
            return result;
        }

        /// <summary>Layout and opacity of every displayed element on the panels; equal across frames means settled.</summary>
        internal static float[] PanelsSignature(IEnumerable<UIToolkitPanels.LivePanel> panels, int maxElements = 4000)
        {
            var values = new List<float>();
            var count = 0;
            foreach (var panel in panels)
            {
                var root = panel.Panel?.visualTree;
                if (root == null) continue;
                UIToolkitPanels.ValidateLayout(panel.Panel);
                foreach (var entry in UIToolkitPanels.Walk(root))
                {
                    if (++count > maxElements) return values.ToArray();
                    var element = entry.Value;
                    if (element.resolvedStyle.display == DisplayStyle.None) continue;
                    var bound = element.worldBound;
                    values.Add(bound.x); values.Add(bound.y); values.Add(bound.width); values.Add(bound.height);
                    values.Add(element.resolvedStyle.opacity);
                }
            }
            return values.ToArray();
        }

        /// <summary>Waits until <see cref="PanelsSignature"/> is unchanged for <paramref name="settleFrames"/> player frames.</summary>
        private static async Task<(bool settled, int frames)> WaitForPanelsSettledAsync(List<UIToolkitPanels.LivePanel> panels, int settleFrames, double timeoutSeconds)
        {
            var clock = Stopwatch.StartNew();
            float[] last = null;
            int stable = 0, frames = 0, lastFrame = Time.frameCount;
            while (clock.Elapsed.TotalSeconds < timeoutSeconds)
            {
                await ScreenshotFunctions.WaitForEditorFramesAsync(1);
                if (!EditorApplication.isPlaying) return (false, frames);
                if (EditorApplication.isPaused) return (false, frames); // nothing advances; don't call it settled
                if (Time.frameCount == lastFrame) continue;
                lastFrame = Time.frameCount;
                frames++;
                var signature = PanelsSignature(panels);
                stable = SameSignature(signature, last) ? stable + 1 : 0;
                last = signature;
                if (stable + 1 >= settleFrames) return (true, frames);
            }
            return (false, frames);
        }
    }
}
