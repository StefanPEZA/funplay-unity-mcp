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

        [Description("Audit live UI Toolkit screens (Edit or Play Mode, current Game View size) and report problems a reviewer would flag: ZERO_SIZE_CONTROL, CONTROL_PICKING_IGNORED, OUTSIDE_PANEL, DUPLICATE_NAME, TEXT_OVERFLOWS (unwrapped), TEXT_HEIGHT_OVERFLOW (wrapped text taller than its box), TEXT_ELIDED, SMALL_TOUCH_TARGET (interactive controls under min_touch_size panel px), COVERED_CONTROL (another element, on any panel, receives the pointer at its center; controls behind a full-panel modal backdrop that dims the screen are listed per host under blocked_by_modal instead and not counted), LOW_CONTRAST (WCAG ratio below 4.5, or 3 for large text, composited in linear space with opacity; only where every layer behind the text is a solid colour, otherwise counted as indeterminate, never reported), MISSING_GLYPHS (characters missing from the element's -unity-font-definition font asset and its fallbacks; never adds glyphs to dynamic atlases; text on the theme default font is not judged), OUTSIDE_SAFE_AREA / OUTSIDE_PLAY_AREA (only with rects you pass; Screen.safeArea in the Editor is the whole view). For several screen sizes, run preview_ui_toolkit(audit=true).")]
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
            var glyphCache = new Dictionary<(FontAsset, uint), bool>();
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
                var blockedByModal = new List<object>();
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

                    // Parts of a composite control (a ScrollView's scroller slider, a field's inner input) are
                    // judged through the outer control, not on their own.
                    var interactive = UIToolkitPanels.IsInteractive(element) && element.enabledInHierarchy && !IsInnerPart(element, root);
                    if (interactive)
                    {
                        if (bound.width < options.MinTouchSize - 0.5f || bound.height < options.MinTouchSize - 0.5f)
                            Add("SMALL_TOUCH_TARGET", new { code = "SMALL_TOUCH_TARGET", element_path = entry.Key, element = Label(element),
                                detail = $"{bound.width:F0}x{bound.height:F0} panel px, under {options.MinTouchSize:F0}." });
                        if (mapping.HasValue && renderHeight > 0)
                        {
                            var covering = CoveringElement(element, visibility, mapping.Value, renderHeight, allPanels);
                            var modal = covering != null ? ModalBackdrop(covering, element) : null;
                            // Blocking the screen behind an open dialog is the backdrop's job, not a defect.
                            if (modal != null)
                            {
                                if (blockedByModal.Count < MaxFindingsPerHost)
                                    blockedByModal.Add(new { element_path = entry.Key, element = Label(element), modal = Label(modal) });
                            }
                            else if (covering != null)
                                Add("COVERED_CONTROL", new { code = "COVERED_CONTROL", element_path = entry.Key, element = Label(element), covered_by = Label(covering),
                                    detail = "Another element receives the pointer at this control's visible center." });
                        }
                    }

                    if (interactive && mapping.HasValue && renderHeight > 0 && (options.SafeArea.HasValue || options.PlayArea.HasValue))
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
                    if (textElement.isElided)
                        Add("TEXT_ELIDED", new { code = "TEXT_ELIDED", element_path = entry.Key, element = Label(element), detail = "The text is cut off with an ellipsis." });
                    else if (style.whiteSpace != WhiteSpace.NoWrap)
                    {
                        // contentRect is local (no ancestor scale from press/hover animations) and excludes padding and border.
                        var content = element.contentRect;
                        var measured = textElement.MeasureTextSize(textElement.text, content.width, VisualElement.MeasureMode.Exactly, 0, VisualElement.MeasureMode.Undefined);
                        if (measured.y > content.height + 1f)
                            Add("TEXT_HEIGHT_OVERFLOW", new { code = "TEXT_HEIGHT_OVERFLOW", element_path = entry.Key, element = Label(element),
                                detail = $"Wrapped text needs {measured.y:F0}px but the box has {content.height:F0}px." });
                    }

                    // WCAG 1.4.3 exempts inactive controls, and themes fade disabled ones on purpose.
                    if (element.enabledInHierarchy)
                    {
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
                    }

                    var font = EffectiveFontAsset(style);
                    var missing = MissingGlyphs(textElement.enableRichText ? StripRichText(textElement.text) : textElement.text, font, fallbacks, glyphCache);
                    if (missing != null && missing.Count > 0)
                        Add("MISSING_GLYPHS", new { code = "MISSING_GLYPHS", element_path = entry.Key, element = Label(element),
                            missing = missing.Select(DescribeCodePoint).ToArray(),
                            font = font != null ? font.name : null,
                            detail = "Missing in the font and its fallbacks; rendered as a placeholder." });
                }

                report.Hosts.Add(new
                {
                    host_id = ObjectIdHelper.GetSerializableId(host.gameObject),
                    host = host.gameObject.name,
                    panel_settings = settings != null ? settings.name : null,
                    elements = walked.Count,
                    findings,
                    blocked_by_modal = blockedByModal.Count > 0 ? blockedByModal : null
                });
            }
            return report;
        }

        /// <summary>
        /// The full-panel overlay that <paramref name="covering"/> belongs to (a modal backdrop), or null.
        /// A displayed, pickable element that covers its whole panel, draws a visible background (dims
        /// the screen) and does not contain the control is taken as a modal backdrop. A transparent
        /// full-panel element is not: an invisible layer that swallows clicks is the defect to report.
        /// </summary>
        internal static VisualElement ModalBackdrop(VisualElement covering, VisualElement control)
        {
            var panelRoot = covering.panel?.visualTree;
            if (panelRoot == null) return null;
            var panelBound = panelRoot.worldBound;
            VisualElement modal = null;
            for (var current = covering; current != null && !ReferenceEquals(current, panelRoot); current = current.hierarchy.parent)
            {
                if (current.Contains(control)) break;
                if (current.pickingMode == PickingMode.Position && UIToolkitPanels.IsDisplayed(current) &&
                    Contains(current.worldBound, panelBound) && DrawsBackdrop(current))
                    modal = current;
            }
            return modal;
        }

        private static bool DrawsBackdrop(VisualElement element)
        {
            var style = element.resolvedStyle;
            return style.opacity > 0.01f && (style.backgroundColor.a > 0.01f || HasImageContent(element));
        }

        /// <summary>Inside another control or a ScrollView (below the host root): a part, not a target of its own.</summary>
        internal static bool IsInnerPart(VisualElement element, VisualElement root)
        {
            for (var parent = element.hierarchy.parent; parent != null && !ReferenceEquals(parent, root); parent = parent.hierarchy.parent)
                if (parent is ScrollView || UIToolkitPanels.IsControl(parent)) return true;
            return false;
        }

        private static string DescribeCodePoint(uint c) =>
            c >= 0xD800 && c <= 0xDFFF ? $"U+{c:X4} (unpaired surrogate)" : $"U+{c:X4} '{char.ConvertFromUtf32((int)c)}'";

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
        /// WCAG contrast of the text colour over what is drawn behind it, in linear space. UI Toolkit applies
        /// opacity to an element's whole group (its background and children flattened), so each ancestor's
        /// group is composited at full alpha first and then scaled by its opacity. Null (indeterminate) when
        /// anything behind the text that is still visible is an image, custom vector content, or an earlier
        /// sibling subtree overlapping the text, or when the game view shows through (final alpha below 1).
        /// </summary>
        internal static float? ContrastRatio(VisualElement element)
        {
            var bound = element.worldBound;
            var text = element.resolvedStyle.color;
            // Premultiplied linear colours of the text pixel and of the background pixel next to it.
            var textPixel = Premultiply(text.linear, text.a);
            var backPixel = new Color(0, 0, 0, 0);
            VisualElement child = null;
            for (var current = element; current != null; current = current.hierarchy.parent)
            {
                var covered = backPixel.a >= 0.999f && textPixel.a >= 0.999f;
                if (!covered)
                {
                    if (HasImageContent(current)) return null;
                    if (child != null && EarlierSiblingDraws(current, child, bound)) return null;
                    var background = current.resolvedStyle.backgroundColor;
                    var layer = Premultiply(background.linear, background.a);
                    textPixel = Over(textPixel, layer);
                    backPixel = Over(backPixel, layer);
                }
                var opacity = current.resolvedStyle.opacity;
                if (opacity < 0.999f) { textPixel *= opacity; backPixel *= opacity; }
                child = current;
            }
            if (backPixel.a < 0.999f || textPixel.a < 0.999f) return null;
            return ContrastRatio(textPixel, backPixel);
        }

        private static Color Premultiply(Color c, float a) => new Color(c.r * a, c.g * a, c.b * a, a);
        private static Color Over(Color top, Color bottom) => top + bottom * (1f - top.a);

        /// <summary>Whether a sibling drawn before <paramref name="child"/> inside <paramref name="parent"/> paints under <paramref name="bound"/>.</summary>
        private static bool EarlierSiblingDraws(VisualElement parent, VisualElement child, Rect bound)
        {
            var budget = 200;
            for (var i = 0; i < parent.hierarchy.childCount; i++)
            {
                var sibling = parent.hierarchy[i];
                if (ReferenceEquals(sibling, child)) return false;
                if (Draws(sibling, bound, ref budget)) return true;
            }
            return false;
        }

        private static bool Draws(VisualElement element, Rect bound, ref int budget)
        {
            if (--budget < 0) return true; // too large to inspect: treat as unknown content
            if (element.resolvedStyle.display == DisplayStyle.None || element.resolvedStyle.visibility == Visibility.Hidden || element.resolvedStyle.opacity <= 0f) return false;
            if (!UIToolkitPanels.HasLayout(element) || !element.worldBound.Overlaps(bound)) return false;
            if (element.resolvedStyle.backgroundColor.a > 0f || HasImageContent(element) || element is TextElement t && !string.IsNullOrEmpty(t.text)) return true;
            for (var i = 0; i < element.hierarchy.childCount; i++)
                if (Draws(element.hierarchy[i], bound, ref budget)) return true;
            return false;
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

        /// <summary>Characters no font needs a glyph for: controls, spaces, format characters (ZWJ, bidi marks), variation selectors.</summary>
        internal static bool IsInvisible(uint c)
        {
            if (c <= 0x20 || c == 0xA0 || c == 0x00AD) return true;
            if (c >= 0xFE00 && c <= 0xFE0F || c >= 0xE0100 && c <= 0xE01EF) return true; // variation selectors
            if (c >= 0xD800 && c <= 0xDFFF) return false; // unpaired surrogate: report it
            var category = c <= 0xFFFF ? CharUnicodeInfo.GetUnicodeCategory((char)c) : CharUnicodeInfo.GetUnicodeCategory(char.ConvertFromUtf32((int)c), 0);
            return category == UnicodeCategory.Control || category == UnicodeCategory.Format || category == UnicodeCategory.SpaceSeparator ||
                   category == UnicodeCategory.LineSeparator || category == UnicodeCategory.ParagraphSeparator;
        }

        /// <summary>Code points of <paramref name="text"/>, unpaired surrogates as themselves (never throws).</summary>
        internal static List<uint> CodePoints(string text)
        {
            var result = new List<uint>();
            for (var i = 0; i < text.Length; i++)
            {
                if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    result.Add((uint)char.ConvertToUtf32(text[i], text[i + 1]));
                    i++;
                }
                else result.Add(text[i]);
            }
            return result;
        }

        /// <summary>
        /// Code points of <paramref name="text"/> that neither the font, its fallback chain nor the panel's
        /// fallbacks can draw. Never adds characters, so dynamic font assets and their .asset files stay
        /// untouched; a character not yet in a dynamic atlas is checked in the source font file, loading each
        /// source font once per call. <paramref name="cache"/> carries (font, code point) answers across calls
        /// (one audit). Null when the font is unknown (legacy Font or theme default).
        /// </summary>
        internal static List<uint> MissingGlyphs(string text, FontAsset font, IEnumerable<FontAsset> panelFallbacks, Dictionary<(FontAsset, uint), bool> cache = null)
        {
            if (font == null || string.IsNullOrEmpty(text)) return null;
            cache = cache ?? new Dictionary<(FontAsset, uint), bool>();
            var unresolved = CodePoints(text).Where(c => !IsInvisible(c)).Distinct().ToList();

            var fonts = new List<FontAsset>();
            void Collect(FontAsset asset)
            {
                if (asset == null || fonts.Contains(asset)) return;
                fonts.Add(asset);
                if (asset.fallbackFontAssetTable != null) foreach (var fallback in asset.fallbackFontAssetTable) Collect(fallback);
            }
            Collect(font);
            if (panelFallbacks != null) foreach (var fallback in panelFallbacks) Collect(fallback);

            foreach (var asset in fonts)
            {
                if (unresolved.Count == 0) break;
                var still = new List<uint>();
                var needSource = new List<uint>();
                foreach (var c in unresolved)
                {
                    if (cache.TryGetValue((asset, c), out var known)) { if (!known) still.Add(c); continue; }
                    if (DrawsFromAsset(asset, c)) cache[(asset, c)] = true;
                    else needSource.Add(c);
                }
                if (needSource.Count > 0)
                {
                    var faceLoaded = LoadSourceFace(asset);
                    foreach (var c in needSource)
                    {
                        var draws = faceLoaded && FontEngine.TryGetGlyphIndex(c, out var index) && index != 0;
                        cache[(asset, c)] = draws;
                        if (!draws) still.Add(c);
                    }
                }
                unresolved = still;
            }
            return unresolved;
        }

        private static bool DrawsFromAsset(FontAsset font, uint c)
        {
            if (FontTryGetGlyphIndex != null)
            {
                var args = new object[] { c, 0u };
                try { if ((bool)FontTryGetGlyphIndex.Invoke(font, args) && (uint)args[1] != 0) return true; }
                catch (Exception) { }
            }
            try { if (font.HasCharacter(c, false, false)) return true; }
            catch (NullReferenceException) { } // the 6000.6 text backend keeps no character table
            return false;
        }

        /// <summary>
        /// Loads a dynamic font asset's source face (with its face index, for .ttc collections) into the
        /// FontEngine. Static atlases (deprecated in 6000.6) can't grow, so they have no source to consult.
        /// TextCore reloads the face it needs before every glyph operation, so changing the current face is safe.
        /// </summary>
        private static bool LoadSourceFace(FontAsset font)
        {
            if (font.atlasPopulationMode.ToString() == "Static") return false;
            var source = SourceFontFile?.GetValue(font) as Font;
            if (source == null) return false;
            try
            {
                var face = font.faceInfo;
                var faceIndex = face.GetType().GetProperty("faceIndex")?.GetValue(face) is int index ? index : 0;
                return FontEngine.LoadFontFace(source, Mathf.Max(1, Mathf.RoundToInt(face.pointSize)), faceIndex) == FontEngineError.Success;
            }
            catch (Exception) { return false; }
        }
    }
}
