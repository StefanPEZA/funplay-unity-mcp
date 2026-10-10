// Copyright (C) Funplay. Licensed under MIT.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using Funplay.Editor.Tools.Helpers;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;
using UnityEngine.UIElements;
using FontAsset = UnityEngine.TextCore.Text.FontAsset;

namespace Funplay.Editor.Tools.Builtins
{
    internal static partial class UIToolkitFunctions
    {
        internal const int MaxAuditFindings = 200;

        [Description("Audit live UI Toolkit screens (Edit or Play Mode, current Game View size) and report problems a reviewer would flag: ZERO_SIZE_CONTROL, CONTROL_PICKING_IGNORED, OUTSIDE_PANEL, DUPLICATE_NAME, TEXT_OVERFLOWS (unwrapped), TEXT_HEIGHT_OVERFLOW (wrapped text taller than its box), TEXT_ELIDED, SMALL_TOUCH_TARGET (interactive controls under min_touch_size panel px), COVERED_CONTROL (another element, on any panel, receives the pointer at its center), LOW_CONTRAST (WCAG ratio below 4.5, or 3 for large text, composited in linear space with opacity; only where every layer behind the text is a solid colour, otherwise counted as indeterminate, never reported), MISSING_GLYPHS (characters missing from the element's -unity-font-definition font asset and its fallbacks; never adds glyphs to dynamic atlases; text on the theme default font is not judged), OUTSIDE_SAFE_AREA / OUTSIDE_PLAY_AREA (only with rects you pass; Screen.safeArea in the Editor is the whole view). For several screen sizes, run preview_ui_toolkit(audit=true).")]
        [ReadOnlyTool]
        public static object AuditUiToolkit(
            [ToolParam("Panel host GameObject name/path/ID; default: every live screen-space host", Required = false)] string target = null,
            [ToolParam("Minimum touch target size in panel px (dp)", Required = false)] float min_touch_size = 44f,
            [ToolParam("Safe area as 'x,y,width,height' in render pixels, top-left origin", Required = false)] string safe_area = null,
            [ToolParam("Play area as 'x,y,width,height' in render pixels, top-left origin", Required = false)] string play_area = null,
            [ToolParam("Comma-separated codes to skip, e.g. 'LOW_CONTRAST,DUPLICATE_NAME'", Required = false)] string skip = null)
        {
            try
            {
                var options = new AuditOptions
                {
                    MinTouchSize = min_touch_size > 0 ? min_touch_size : 44f,
                    SafeArea = ParseRect(safe_area, "safe_area"),
                    PlayArea = ParseRect(play_area, "play_area"),
                    Skip = new HashSet<string>((skip ?? "").Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim().ToUpperInvariant()))
                };
                var hosts = string.IsNullOrWhiteSpace(target) ? UIToolkitPanels.FindHosts(false) : ResolveHosts(target);
                var report = AuditHosts(hosts, options);
                return Response.Success($"{report.Total} finding(s) on {report.Hosts.Count} host(s).", report.ToResponse());
            }
            catch (UIToolkitException ex) { return Response.Error(ex.Code, new { detail = ex.Message, details = ex.Details }); }
            catch (ArgumentException ex) { return Response.Error("INVALID_ARGUMENT", new { detail = ex.Message }); }
            catch (Exception ex) { return Response.Error("UI_TOOLKIT_AUDIT_FAILED", new { detail = ex.Message }); }
        }

        internal sealed class AuditOptions
        {
            public float MinTouchSize = 44f;
            public Rect? SafeArea, PlayArea;
            public ISet<string> Skip = new HashSet<string>();
        }

        internal sealed class AuditReport
        {
            public readonly List<object> Hosts = new List<object>();
            public readonly Dictionary<string, int> Counts = new Dictionary<string, int>();
            public int IndeterminateContrast, Total, Omitted;

            public object ToResponse() => new
            {
                hosts = Hosts,
                counts = Counts.OrderBy(c => c.Key, StringComparer.Ordinal).ToDictionary(c => c.Key, c => c.Value),
                indeterminate_contrast = IndeterminateContrast,
                findings_omitted = Omitted > 0 ? (int?)Omitted : null,
                notes = new[]
                {
                    "Contrast is only judged over solid colours; text over images, vector content or a see-through panel counts as indeterminate.",
                    "Touch detection, the device safe area and DPI are not emulated in the Editor; pass safe_area/play_area, or use preview_ui_toolkit(audit=true) for sizes."
                }
            };
        }

        internal static Rect? ParseRect(string value, string name)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var parts = value.Split(',').Select(p => float.TryParse(p.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : float.NaN).ToArray();
            if (parts.Length != 4 || parts.Any(float.IsNaN) || parts[2] <= 0 || parts[3] <= 0)
                throw new ArgumentException($"{name} must be 'x,y,width,height' with a positive size.");
            return new Rect(parts[0], parts[1], parts[2], parts[3]);
        }

        internal static AuditReport AuditHosts(List<Component> hosts, AuditOptions options)
        {
            var report = new AuditReport();
            var renderHeight = TryGetRenderHeight(out _);
            var allPanels = UIToolkitPanels.GetLivePanels(UIToolkitPanels.FindHosts(false, false)).Where(p => !p.WorldSpace).ToList();
            var hostRoots = UIToolkitPanels.HostRoots(hosts);
            foreach (var host in hosts)
            {
                var root = UIToolkitPanels.GetRoot(host);
                if (root?.panel == null || !host.gameObject.activeInHierarchy || !UIToolkitPanels.IsEnabled(host)) continue;
                var settings = UIToolkitPanels.GetPanelSettings(host);
                if (UIToolkitPanels.IsWorldSpace(settings)) continue;
                UIToolkitPanels.ValidateLayout(root.panel);
                var walked = UIToolkitPanels.Walk(root, exclude: hostRoots).ToList();
                var displayCache = new UIToolkitPanels.DisplayCache();
                var clipCache = new UIToolkitPanels.ClipCache();
                var findings = new List<object>();
                void Add(string code, object finding)
                {
                    if (options.Skip.Contains(code)) return;
                    report.Counts[code] = report.Counts.TryGetValue(code, out var n) ? n + 1 : 1;
                    report.Total++;
                    if (report.Total > MaxAuditFindings) { report.Omitted++; return; }
                    findings.Add(finding);
                }

                foreach (var existing in Audit(root, walked, displayCache, clipCache))
                    Add((string)existing.GetType().GetProperty("code").GetValue(existing), existing);

                var mapping = MappingFor(host, root.panel);
                var textSettings = EffectiveTextSettings(settings);
                var fallbacks = textSettings != null ? textSettings.fallbackFontAssets : null;
                foreach (var entry in walked)
                {
                    var element = entry.Value;
                    if (ReferenceEquals(element, root) || !displayCache.IsDisplayed(element) || !UIToolkitPanels.HasLayout(element)) continue;
                    var bound = element.worldBound;
                    if (bound.width < 1f || bound.height < 1f) continue;
                    var visibility = clipCache.Of(element);
                    if (visibility.Fraction <= 0f) continue;

                    if (UIToolkitPanels.IsInteractive(element) && element.enabledInHierarchy)
                    {
                        if (bound.width < options.MinTouchSize - 0.5f || bound.height < options.MinTouchSize - 0.5f)
                            Add("SMALL_TOUCH_TARGET", new { code = "SMALL_TOUCH_TARGET", element_path = entry.Key, element = Label(element),
                                detail = $"{bound.width:F0}x{bound.height:F0} panel px, under {options.MinTouchSize:F0}." });
                        if (mapping.HasValue && renderHeight > 0)
                        {
                            var covering = CoveringElement(element, visibility, mapping.Value, renderHeight, allPanels);
                            if (covering != null)
                                Add("COVERED_CONTROL", new { code = "COVERED_CONTROL", element_path = entry.Key, element = Label(element), covered_by = Label(covering),
                                    detail = "Another element receives the pointer at this control's visible center." });
                        }
                    }

                    if (mapping.HasValue && renderHeight > 0 && (options.SafeArea.HasValue || options.PlayArea.HasValue) && UIToolkitPanels.IsInteractive(element))
                    {
                        var topLeft = mapping.Value.ToScreen(new Vector2(bound.xMin, bound.yMin));
                        var bottomRight = mapping.Value.ToScreen(new Vector2(bound.xMax, bound.yMax));
                        var screen = Rect.MinMaxRect(topLeft.x, topLeft.y, bottomRight.x, bottomRight.y);
                        if (options.SafeArea.HasValue && !Contains(options.SafeArea.Value, screen))
                            Add("OUTSIDE_SAFE_AREA", new { code = "OUTSIDE_SAFE_AREA", element_path = entry.Key, element = Label(element), detail = "Interactive element extends outside the safe area." });
                        if (options.PlayArea.HasValue && !Contains(options.PlayArea.Value, screen))
                            Add("OUTSIDE_PLAY_AREA", new { code = "OUTSIDE_PLAY_AREA", element_path = entry.Key, element = Label(element), detail = "Interactive element extends outside the play area." });
                    }

                    if (!(element is TextElement textElement) || string.IsNullOrEmpty(textElement.text)) continue;
                    var style = element.resolvedStyle;
                    if (style.whiteSpace != WhiteSpace.NoWrap)
                    {
                        var measured = textElement.MeasureTextSize(textElement.text, bound.width - Horizontal(style), VisualElement.MeasureMode.Exactly, 0, VisualElement.MeasureMode.Undefined);
                        var available = bound.height - Vertical(style);
                        if (measured.y > available + 1f)
                            Add("TEXT_HEIGHT_OVERFLOW", new { code = "TEXT_HEIGHT_OVERFLOW", element_path = entry.Key, element = Label(element),
                                detail = $"Wrapped text needs {measured.y:F0}px but the box has {available:F0}px." });
                    }
                    if (textElement.isElided)
                        Add("TEXT_ELIDED", new { code = "TEXT_ELIDED", element_path = entry.Key, element = Label(element), detail = "The text is cut off with an ellipsis." });

                    var contrast = ContrastRatio(element);
                    if (contrast == null) report.IndeterminateContrast++;
                    else
                    {
                        var large = style.fontSize >= 24f || style.fontSize >= 18.66f && (style.unityFontStyleAndWeight == FontStyle.Bold || style.unityFontStyleAndWeight == FontStyle.BoldAndItalic);
                        var required = large ? 3f : 4.5f;
                        if (contrast.Value < required)
                            Add("LOW_CONTRAST", new { code = "LOW_CONTRAST", element_path = entry.Key, element = Label(element), ratio = Math.Round(contrast.Value, 2), required,
                                detail = $"Text contrast {contrast.Value:F2}:1 is below {required}:1." });
                    }

                    var font = EffectiveFontAsset(style);
                    var missing = MissingGlyphs(textElement.enableRichText ? StripRichText(textElement.text) : textElement.text, font, fallbacks);
                    if (missing != null && missing.Count > 0)
                        Add("MISSING_GLYPHS", new { code = "MISSING_GLYPHS", element_path = entry.Key, element = Label(element),
                            missing = missing.Select(c => $"U+{c:X4} '{char.ConvertFromUtf32((int)c)}'").ToArray(),
                            font = font != null ? font.name : null,
                            detail = "Missing in the font and its fallbacks; rendered as a placeholder." });
                }

                report.Hosts.Add(new
                {
                    host_id = ObjectIdHelper.GetSerializableId(host.gameObject),
                    host = host.gameObject.name,
                    panel_settings = settings != null ? settings.name : null,
                    elements = walked.Count,
                    findings
                });
            }
            return report;
        }

        private static float Horizontal(IResolvedStyle s) => s.paddingLeft + s.paddingRight + s.borderLeftWidth + s.borderRightWidth;
        private static float Vertical(IResolvedStyle s) => s.paddingTop + s.paddingBottom + s.borderTopWidth + s.borderBottomWidth;

        private static bool Contains(Rect outer, Rect inner) =>
            inner.xMin >= outer.xMin - 0.5f && inner.yMin >= outer.yMin - 0.5f && inner.xMax <= outer.xMax + 0.5f && inner.yMax <= outer.yMax + 0.5f;

        /// <summary>The topmost element (any panel) at the control's visible center when it is not the control, its subtree or its click receiver.</summary>
        private static VisualElement CoveringElement(VisualElement element, UIToolkitPanels.Visibility visibility, UIToolkitPanels.ScreenMapping mapping, int renderHeight, List<UIToolkitPanels.LivePanel> panels)
        {
            var center = visibility.FullyVisible ? element.worldBound.center : visibility.Visible.center;
            var screen = mapping.ToScreen(center);
            foreach (var live in panels)
            {
                if (!UIToolkitPanels.TryGetScreenMapping(live.Panel, out var liveMapping)) continue;
                var picked = live.Panel.Pick(liveMapping.ToPanel(screen));
                if (picked == null) continue;
                if (ReferenceEquals(picked, element) || element.Contains(picked)) return null;
                var receiver = UIToolkitPanels.FindClickReceiver(picked);
                return ReferenceEquals(receiver, element) ? null : picked;
            }
            return null;
        }

        /// <summary>
        /// WCAG contrast of the text colour over the layers behind it, composited in linear space with each
        /// layer's opacity chain. Null (indeterminate) when any layer has an image or custom vector content,
        /// or nothing opaque sits behind the text (the game view shows through).
        /// </summary>
        internal static float? ContrastRatio(VisualElement element)
        {
            var chain = new List<VisualElement>();
            for (var current = element; current != null; current = current.hierarchy.parent) chain.Add(current);
            var opacity = new float[chain.Count];
            for (var i = chain.Count - 1; i >= 0; i--)
                opacity[i] = chain[i].resolvedStyle.opacity * (i + 1 < chain.Count ? opacity[i + 1] : 1f);

            // Find the nearest layer that fully hides everything behind it, then composite upward from there.
            var start = -1;
            for (var i = 0; i < chain.Count; i++)
            {
                var layer = chain[i];
                if (HasImageContent(layer)) return null;
                var background = layer.resolvedStyle.backgroundColor;
                if (background.a * opacity[i] >= 0.999f) { start = i; break; }
            }
            if (start < 0) return null;

            var color = chain[start].resolvedStyle.backgroundColor.linear;
            for (var i = start - 1; i >= 0; i--)
            {
                var background = chain[i].resolvedStyle.backgroundColor;
                var alpha = background.a * opacity[i];
                if (alpha > 0f) color = Color.Lerp(color, background.linear, alpha);
            }
            var text = element.resolvedStyle.color;
            var textColor = Color.Lerp(color, text.linear, text.a * opacity[0]);
            return ContrastRatio(textColor, color);
        }

        /// <summary>WCAG ratio of two linear-space colours.</summary>
        internal static float ContrastRatio(Color linearA, Color linearB)
        {
            float Luminance(Color c) => 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;
            float a = Luminance(linearA), b = Luminance(linearB);
            return (Mathf.Max(a, b) + 0.05f) / (Mathf.Min(a, b) + 0.05f);
        }

        private static bool HasImageContent(VisualElement element)
        {
            var image = element.resolvedStyle.backgroundImage;
            if (image.texture != null || image.sprite != null || image.vectorImage != null || image.renderTexture != null || element is Image) return true;
            // Built-in elements draw their own text/borders through generateVisualContent; only project code
            // (vector icons, charts) paints unknown content behind the text.
            return element.generateVisualContent != null && element.GetType().Namespace != "UnityEngine.UIElements";
        }

        private static readonly PropertyInfo DefaultPanelTextSettings = typeof(PanelTextSettings).GetProperty("defaultPanelTextSettings",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

        /// <summary>The panel's text settings, or Unity's default ones when the PanelSettings names none.</summary>
        internal static PanelTextSettings EffectiveTextSettings(PanelSettings settings) =>
            (settings != null ? settings.textSettings : null) ?? DefaultPanelTextSettings?.GetValue(null) as PanelTextSettings;

        /// <summary>
        /// The FontAsset a style names (-unity-font-definition). Null when it names a legacy Font or nothing:
        /// in 6000.6 the theme default is resolved by the text backend at render time, so there is no verdict.
        /// </summary>
        internal static FontAsset EffectiveFontAsset(IResolvedStyle style) => style.unityFontDefinition.fontAsset;

        private static readonly Regex RichTextTag = new Regex(@"<[^<>]{1,64}>");

        internal static string StripRichText(string text) => RichTextTag.Replace(text ?? string.Empty, "");

        private static readonly PropertyInfo SourceFontFile = typeof(FontAsset).GetProperty("sourceFontFile", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        // 6000.6's text backend: FontAsset.TryGetGlyphIndex(uint, out uint) answers from the font itself, without adding.
        private static readonly MethodInfo FontTryGetGlyphIndex = typeof(FontAsset).GetMethod("TryGetGlyphIndex", BindingFlags.Public | BindingFlags.Instance, null,
            new[] { typeof(uint), typeof(uint).MakeByRefType() }, null);

        /// <summary>
        /// Code points of <paramref name="text"/> that neither the font, its fallback chain nor the panel's
        /// fallbacks can draw. Never adds characters (tryAddCharacter: false), so dynamic font assets and
        /// their .asset files stay untouched; for dynamic fonts a character not yet in the atlas is checked
        /// in the source font file instead. Null when the font is unknown (legacy Font or panel default).
        /// </summary>
        internal static List<uint> MissingGlyphs(string text, FontAsset font, IEnumerable<FontAsset> panelFallbacks)
        {
            if (font == null || string.IsNullOrEmpty(text)) return null;
            var candidates = new List<uint>();
            for (var i = 0; i < text.Length; i++)
            {
                var c = (uint)char.ConvertToUtf32(text, i);
                if (char.IsSurrogatePair(text, i)) i++;
                if (c <= 0x20 || c == 0xA0 || c == 0x200B || c == 0xFEFF || c == 0x00AD || char.IsControl((char)Math.Min(c, 0xFFFF))) continue;
                if (!candidates.Contains(c)) candidates.Add(c);
            }
            if (candidates.Count == 0) return new List<uint>();

            var fonts = new List<FontAsset>();
            void Collect(FontAsset asset)
            {
                if (asset == null || fonts.Contains(asset)) return;
                fonts.Add(asset);
                if (asset.fallbackFontAssetTable != null) foreach (var fallback in asset.fallbackFontAssetTable) Collect(fallback);
            }
            Collect(font);
            if (panelFallbacks != null) foreach (var fallback in panelFallbacks) Collect(fallback);

            var missing = new List<uint>();
            foreach (var c in candidates)
                if (!fonts.Any(f => Draws(f, c))) missing.Add(c);
            return missing;
        }

        private static bool Draws(FontAsset font, uint c)
        {
            if (FontTryGetGlyphIndex != null)
            {
                var args = new object[] { c, 0u };
                try { if ((bool)FontTryGetGlyphIndex.Invoke(font, args) && (uint)args[1] != 0) return true; }
                catch (Exception) { }
            }
            try { if (font.HasCharacter(c, false, false)) return true; }
            catch (NullReferenceException) { } // the 6000.6 text backend keeps no character table
            // Static atlases (deprecated in 6000.6) can't grow: what isn't in them is missing.
            if (font.atlasPopulationMode.ToString() == "Static") return false;
            // Dynamic: the character may not be rasterized yet; ask the source font without adding it.
            var source = SourceFontFile?.GetValue(font) as Font;
            if (source == null) return false;
            try
            {
                return FontEngine.LoadFontFace(source) == FontEngineError.Success && FontEngine.TryGetGlyphIndex(c, out var index) && index != 0;
            }
            catch (Exception) { return false; }
        }
    }
}
