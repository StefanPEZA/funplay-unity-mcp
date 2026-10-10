// Copyright (C) Funplay. Licensed under MIT.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using Funplay.Editor.State;
using Funplay.Editor.Tools.Helpers;

namespace Funplay.Editor.Tools.Builtins
{
    [ToolProvider("GameView")]
    internal static class GameViewFunctions
    {
        [Description("Set the Game View to a fixed resolution (any UI framework), or restore it. Pass width+height or a preset: desktop 1920x1080, phone-320 640x1136, phone-360 720x1280, phone-landscape 1280x720. The original size is journaled and comes back with restore=true or at the next Editor start; the temporary custom size (\"Funplay MCP …\" in your Game View size list, a user preference, not a project file) is removed then. Waits briefly and reports the render size reached. For UI Toolkit DPI emulation and captures across sizes, use preview_ui_toolkit.")]
        public static async Task<object> SetGameViewSize(
            [ToolParam("Width in pixels, 128..4096", Required = false)] int width = 0,
            [ToolParam("Height in pixels, 128..4096", Required = false)] int height = 0,
            [ToolParam("desktop, phone-320, phone-360 or phone-landscape (instead of width/height)", Required = false)] string preset = null,
            [ToolParam("Restore the size the Game View had before set_game_view_size", Required = false)] bool restore = false)
        {
            // A running preview journals the size it found and restores it afterwards; changing it underneath
            // would make that restore select a size that no longer exists.
            if (GameViewSizeOverride.PreviewActive)
                return Response.Error("PREVIEW_IN_PROGRESS", new { hint = "preview_ui_toolkit owns the Game View size until it finishes." });
            if (restore)
            {
                var warnings = new List<string>();
                var restored = GameViewSizeOverride.TryRestore(GameViewSizeOverride.SizeSlot, warnings);
                var after = await WaitForRenderSizeAsync(0, 0, 0.5);
                return Response.Success(restored ? "Game View size restored." : "Nothing to restore: set_game_view_size has not changed the Game View.", new
                {
                    restored,
                    render_width = after.width, render_height = after.height,
                    warnings = warnings.Count > 0 ? warnings : null
                });
            }

            if (!string.IsNullOrWhiteSpace(preset))
            {
                var found = GameViewSizeOverride.FindPreset(preset);
                if (found == null)
                    return Response.Error("UNKNOWN_PRESET", new { preset, presets = GameViewSizeOverride.Presets.Select(p => $"{p.Name} {p.Width}x{p.Height}").ToArray() });
                if (width != 0 || height != 0) return Response.Error("INVALID_ARGUMENT", new { detail = "Pass either preset or width/height." });
                width = found.Width; height = found.Height;
            }
            if (width == 0 && height == 0) return Response.Error("INVALID_ARGUMENT", new { detail = "Pass width and height, a preset, or restore=true." });

            GameViewSizeOverride.Journal journal;
            try { journal = GameViewSizeOverride.Apply(GameViewSizeOverride.SizeSlot, width, height); }
            catch (ArgumentException ex) { return Response.Error("INVALID_ARGUMENT", new { detail = ex.Message }); }
            catch (InvalidOperationException ex) { return Response.Error(ex.Message.StartsWith("GAME_VIEW_SETTINGS_UNSUPPORTED") ? "GAME_VIEW_SETTINGS_UNSUPPORTED" : "GAME_VIEW_SETTINGS_UNAVAILABLE", new { detail = ex.Message }); }
            catch (Exception ex) { return Response.Error("GAME_VIEW_SIZE_FAILED", new { detail = ex.Message }); }

            var reached = await WaitForRenderSizeAsync(width, height, 2.0);
            return Response.Success(reached.matched ? $"Game View set to {width}x{height}." : $"Game View size selected; the render is {reached.width}x{reached.height} so far.", new
            {
                width, height,
                render_width = reached.width, render_height = reached.height,
                matched = reached.matched,
                label = journal.label,
                restore = "set_game_view_size(restore=true), or automatically at the next Editor start",
                hint = reached.matched ? null : "The Game View may be hidden behind another tab; show it so it renders at the new size."
            });
        }

        /// <summary>
        /// Waits until the Game View renders at width x height (any size when both are 0) or the timeout
        /// passes; returns the last render size seen.
        /// </summary>
        internal static async Task<(bool matched, int width, int height)> WaitForRenderSizeAsync(int width, int height, double timeoutSeconds)
        {
            var clock = Stopwatch.StartNew();
            int w = 0, h = 0;
            while (true)
            {
                ScreenshotFunctions.RepaintMainGameView();
                await ScreenshotFunctions.WaitForEditorFramesAsync(1);
                try { var current = VisualCoordinates.Current(); w = current.render_width; h = current.render_height; }
                catch (InvalidOperationException) { w = h = 0; }
                var matched = width == 0 && height == 0 ? w > 0 : w == width && h == height;
                if (matched || clock.Elapsed.TotalSeconds >= timeoutSeconds) return (matched, w, h);
            }
        }
    }
}
