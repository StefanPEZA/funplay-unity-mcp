// Copyright (C) Funplay. Licensed under MIT.
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using Funplay.Editor.Tools.Helpers;
using UnityEngine;
using UnityEngine.UIElements;

namespace Funplay.Editor.Tools.Builtins
{
    internal static partial class UIToolkitFunctions
    {
        internal const int MaxElementCaptureScale = 4;

        [Description("Capture just one UI Toolkit element from the live Game View: its screen bounds plus padding, optionally upscaled (nearest neighbour, up to 4x) to inspect small text, icons and borders. Match by selector, text and/or element_path as for clicks (screen-space panels only). The result's geometry stays a game_view capture, so its capture_id works with image_pixels in pick_ui_toolkit_element/simulate_mouse_click. Large crops are saved to a file.")]
        public static async Task<string> CaptureUiToolkitElement(
            [ToolParam("Panel host GameObject name/path/ID", Required = false)] string target = null,
            [ToolParam("Selector: Type#name.class compounds joined by space (descendant) or > (child), optional :nth-of-type(n)/:nth-child(n)", Required = false)] string selector = null,
            [ToolParam("Exact element text, case-insensitive", Required = false)] string text = null,
            [ToolParam("Element path relative to one host: child indices or '/'-separated selectors over direct children", Required = false)] string element_path = null,
            [ToolParam("Render pixels added around the element, 0..256", Required = false)] int padding = 8,
            [ToolParam("Integer upscale, 1..4 (nearest neighbour)", Required = false)] int scale = 1,
            [ToolParam("Save the PNG to disk and return its path instead of inline image data", Required = false)] bool save_to_file = false,
            [ToolParam("Optional output .png path under the project root; only with save_to_file", Required = false)] string output_path = null,
            [ToolParam("compact (default) or full: full returns the complete capture geometry; the full contract is also available from get_visual_coordinates(capture_id)", Required = false)] string detail = null)
        {
            Component host; string path; VisualElement element; RectInt crop; object described;
            int renderWidth, renderHeight;
            try
            {
                if (scale < 1 || scale > MaxElementCaptureScale) throw new ArgumentException("scale must be 1..4.");
                padding = Mathf.Clamp(padding, 0, 256);
                (host, path, element) = ResolveUniqueElement(target, selector, text, element_path);
                var panel = element.panel ?? throw new UIToolkitException("ROOT_NOT_READY", "The element is not attached to a panel.");
                UIToolkitPanels.ValidateLayout(panel);
                if (!UIToolkitPanels.IsDisplayed(element)) throw new UIToolkitException("ELEMENT_NOT_DISPLAYED", "The element is hidden (display:none or visibility:hidden on it or an ancestor).");
                if (!UIToolkitPanels.HasLayout(element)) throw new UIToolkitException("ELEMENT_HAS_NO_AREA", "The element has no layout yet.");
                var mapping = MappingFor(host, panel)
                    ?? throw new UIToolkitException("NO_SCREEN_MAPPING", "The element's panel is world-space or not a runtime panel; use capture_game_view.");
                var geometry = VisualCoordinates.Current();
                renderWidth = geometry.render_width; renderHeight = geometry.render_height;
                crop = ComputeElementCrop(element.worldBound, mapping, padding, renderWidth, renderHeight)
                       ?? throw new UIToolkitException("ELEMENT_OUTSIDE_VIEW", "The element lies outside the Game View.");
                described = UIToolkitPanels.DescribeElement(element, path, mapping, renderHeight, false);
            }
            catch (UIToolkitException ex) { return ToolResultFormatter.Error(ex.Code, new { detail = ex.Message, details = ex.Details }); }
            catch (InvalidOperationException ex) when (ex.Message == "GAME_VIEW_GEOMETRY_UNAVAILABLE") { return ToolResultFormatter.Error("GAME_VIEW_NOT_AVAILABLE", new { hint = "Open the Game View." }); }
            catch (ArgumentException ex) { return ToolResultFormatter.Error("INVALID_ARGUMENT", new { detail = ex.Message }); }

            Texture2D frame = null, cropped = null;
            try
            {
                frame = await ScreenshotFunctions.ReadGameViewFramebufferAsync();
                if (frame == null) return ToolResultFormatter.Error("GAME_VIEW_NOT_AVAILABLE", new { hint = "Open the Game View; it has not rendered yet." });
                if (frame.width != renderWidth || frame.height != renderHeight)
                    return ToolResultFormatter.Error("CAPTURE_GEOMETRY_CHANGED", new { hint = "The Game View size changed during the capture; retry." });

                cropped = CropAndScale(frame, crop, scale);
                var geometry = VisualCoordinates.CreateCrop(renderWidth, renderHeight, crop, scale);
                // Compact: the element's path and label, since the geometry's viewport already places the crop.
                var compact = ResponseDetail.IsCompact(detail);
                var extra = new Dictionary<string, object>
                {
                    ["host_id"] = ObjectIdHelper.GetSerializableId(host.gameObject),
                    ["element"] = compact ? EchoElement(element, path) : described
                };
                if (!compact)
                    extra["crop"] = new
                    {
                        render_bottom_left = new { x = crop.x, y = crop.y, width = crop.width, height = crop.height },
                        render_top_left = new { x = crop.x, y = renderHeight - crop.yMax, width = crop.width, height = crop.height },
                        padding,
                        scale
                    };
                return ScreenshotFunctions.FinishCapture(cropped.EncodeToPNG(), save_to_file, output_path, "ui-element", geometry, extra, detail);
            }
            catch (Exception ex) { return ToolResultFormatter.Error("SCREENSHOT_CAPTURE_FAILED", new { message = ex.Message }); }
            finally
            {
                if (frame != null) UnityEngine.Object.DestroyImmediate(frame);
                if (cropped != null) UnityEngine.Object.DestroyImmediate(cropped);
            }
        }

        /// <summary>
        /// Render-pixel rect (bottom-left origin) around a panel-space bound plus padding, rounded outward
        /// and clamped to the render; null when nothing of it is inside.
        /// </summary>
        internal static RectInt? ComputeElementCrop(Rect panelBound, UIToolkitPanels.ScreenMapping mapping, int padding, int renderWidth, int renderHeight)
        {
            var topLeft = mapping.ToScreen(new Vector2(panelBound.xMin, panelBound.yMin));
            var bottomRight = mapping.ToScreen(new Vector2(panelBound.xMax, panelBound.yMax));
            var xMin = Mathf.Max(0, Mathf.FloorToInt(topLeft.x - padding));
            var xMax = Mathf.Min(renderWidth, Mathf.CeilToInt(bottomRight.x + padding));
            // Screen space is top-left; render pixels are bottom-left.
            var yMin = Mathf.Max(0, Mathf.FloorToInt(renderHeight - bottomRight.y - padding));
            var yMax = Mathf.Min(renderHeight, Mathf.CeilToInt(renderHeight - topLeft.y + padding));
            if (xMax - xMin < 1 || yMax - yMin < 1) return null;
            return new RectInt(xMin, yMin, xMax - xMin, yMax - yMin);
        }

        /// <summary>Copies <paramref name="crop"/> (bottom-left) out of an upright frame, repeating each pixel scale x scale times.</summary>
        internal static Texture2D CropAndScale(Texture2D frame, RectInt crop, int scale)
        {
            var source = frame.GetPixels32();
            int width = crop.width * scale, height = crop.height * scale;
            var pixels = new Color32[width * height];
            for (var y = 0; y < height; y++)
            {
                var sourceRow = (crop.y + y / scale) * frame.width + crop.x;
                var row = y * width;
                for (var x = 0; x < width; x++)
                    pixels[row + x] = source[sourceRow + x / scale];
            }
            var result = new Texture2D(width, height, TextureFormat.RGB24, false);
            result.SetPixels32(pixels);
            result.Apply();
            return result;
        }
    }
}
