// Copyright (C) Funplay. Licensed under MIT.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using Funplay.Editor.Tools.Helpers;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace Funplay.Editor.Tools.Builtins
{
    /// <summary>
    /// UI Toolkit (UXML/USS/VisualElement) authoring and live inspection. Unity 6.6+ hosts runtime
    /// panels with PanelRenderer; older versions (and Legacy menus) use UIDocument. Both are
    /// supported through <see cref="UIToolkitPanels"/>; the uGUI Canvas tools do not apply here.
    /// </summary>
    [ToolProvider("UIToolkit")]
    internal static partial class UIToolkitFunctions
    {
        [Description("Read-only UI Toolkit overview: whether this Unity has PanelRenderer (6.6+, UIDocument is Legacy) and which host to create, every PanelRenderer/UIDocument in loaded scenes/Prefab Stage with PanelSettings, UXML, sorting, parent and live-root state, per-scene framework (ui_toolkit/ugui/mixed), PanelSettings/theme assets and setup issues. Use before choosing Canvas vs UI Toolkit tools.")]
        [ReadOnlyTool]
        public static object GetUiToolkitInfo(
            [ToolParam("Include inactive hosts", Required = false)] bool include_inactive = true,
            [ToolParam("Cap for listed PanelSettings/theme/UXML assets, 1..500", Required = false)] int asset_limit = 50)
        {
            try
            {
                asset_limit = Mathf.Clamp(asset_limit, 1, 500);
                var hosts = UIToolkitPanels.FindHosts(include_inactive);
                var hostRoots = UIToolkitPanels.HostRoots(hosts);
                var canvases = UIToolkitPanels.FindRootCanvases();
                var scenes = new List<object>();
                for (var i = 0; i < SceneManager.sceneCount; i++)
                {
                    var scene = SceneManager.GetSceneAt(i);
                    if (!scene.isLoaded) continue;
                    int uitk = hosts.Count(h => h.gameObject.scene == scene), ugui = canvases.Count(c => c.gameObject.scene == scene);
                    scenes.Add(new
                    {
                        path = scene.path, name = scene.name, ui_toolkit_hosts = uitk, ugui_root_canvases = ugui,
                        framework = uitk > 0 && ugui > 0 ? "mixed" : uitk > 0 ? "ui_toolkit" : ugui > 0 ? "ugui" : "none"
                    });
                }

                var panelSettingsPaths = UIToolkitPanels.FindPanelSettingsPaths(asset_limit);
                var uxml = UIToolkitPanels.FindAssetPaths("VisualTreeAsset").ToArray();
                var uss = UIToolkitPanels.FindAssetPaths("StyleSheet").Where(p => p.EndsWith(".uss", StringComparison.OrdinalIgnoreCase)).ToArray();
                var eventSystems = SceneObjectLookup.FindAll<EventSystem>(true)
                    .Count(e => !EditorUtility.IsPersistent(e));
                var inputConfigurations = UIToolkitPanels.PanelInputConfigurationType != null
                    ? SceneObjectLookup.FindAll(UIToolkitPanels.PanelInputConfigurationType, true).Length
                    : (int?)null;

                return Response.Success($"{hosts.Count} UI Toolkit panel host(s) in loaded scenes.", new
                {
                    unity_version = Application.unityVersion,
                    panel_renderer_available = UIToolkitPanels.PanelRendererAvailable,
                    recommended_component = UIToolkitPanels.ComponentName(UIToolkitPanels.RecommendedKind),
                    ui_document_status = UIToolkitPanels.PanelRendererAvailable ? "legacy" : "current",
                    hosts = hosts.Select(h => DescribeHost(h, nestedRoots: hostRoots)).ToArray(),
                    scenes,
                    event_systems = eventSystems,
                    panel_input_configurations = inputConfigurations,
                    assets = new
                    {
                        panel_settings = panelSettingsPaths.Select(p => UIToolkitPanels.DescribePanelSettings(AssetDatabase.LoadAssetAtPath<PanelSettings>(p))).ToArray(),
                        theme_style_sheets = UIToolkitPanels.FindThemePaths(asset_limit),
                        uxml_count = uxml.Length, uxml = uxml.Take(asset_limit).ToArray(),
                        uss_count = uss.Length, uss = uss.Take(asset_limit).ToArray()
                    },
                    guidance = UIToolkitPanels.PanelRendererAvailable
                        ? "Create new runtime UI Toolkit screens with PanelRenderer (create_ui_toolkit_panel). Keep existing UIDocument screens unless asked to migrate: UIDocument.rootVisualElement code does not port as-is; PanelRenderer exposes its tree through RegisterUIReloadCallback."
                        : "This Unity version hosts runtime UI Toolkit with UIDocument. PanelRenderer requires Unity 6.6+."
                });
            }
            catch (Exception ex) { return Response.Error("UI_TOOLKIT_INFO_FAILED", new { detail = ex.Message }); }
        }

        [Description("Create a UI Toolkit panel host in Edit Mode: PanelRenderer on Unity 6.6+ (auto), else UIDocument; ui_document is Legacy on 6.6+. Assigns UXML and PanelSettings (explicit path, the project's only PanelSettings, or a new one with the default runtime theme; several candidates require a choice). A parent that is itself a panel host nests the new panel and inherits its PanelSettings. Undoable; does not save the scene. Not for uGUI Canvas UI.")]
        [SceneEditingTool]
        public static object CreateUiToolkitPanel(
            [ToolParam("GameObject name")] string name,
            [ToolParam("UXML VisualTreeAsset path to display", Required = false)] string visual_tree_asset = null,
            [ToolParam("Existing PanelSettings asset path", Required = false)] string panel_settings = null,
            [ToolParam("auto, panel_renderer or ui_document", Required = false)] string component = "auto",
            [ToolParam("Unique parent name/path/ID; a panel-host parent nests this panel", Required = false)] string parent = null,
            [ToolParam("Sorting order among panels sharing PanelSettings", Required = false)] string sorting_order = null,
            [ToolParam("Where to create PanelSettings when the project has none (default Assets/UI Toolkit/PanelSettings.asset)", Required = false)] string new_panel_settings_path = null)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                return Response.Error("EDIT_MODE_REQUIRED", new { hint = "Author UI Toolkit hosts in Edit Mode or a Prefab Stage." });
            var createdAssets = new List<string>();
            try
            {
                if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("name must not be empty.");
                var kind = ResolveKind(component);
                var componentType = kind == UIToolkitPanels.KindPanelRenderer ? UIToolkitPanels.PanelRendererType : typeof(UIDocument);
                GameObject parentObject = null;
                if (!string.IsNullOrWhiteSpace(parent))
                {
                    var matches = ObjectsHelper.FindObjects(parent, null, true, searchInactive: true);
                    if (matches.Count != 1 || EditorUtility.IsPersistent(matches[0]))
                        throw new UIToolkitException("PARENT_NOT_UNIQUE", "parent must identify exactly one live GameObject (prefer its ID).", new { count = matches.Count });
                    parentObject = matches[0];
                }

                // Nearest host ancestor of either kind, inactive included (as Unity resolves parentUI).
                Component parentHost = null;
                for (var t = parentObject != null ? parentObject.transform : null; t != null && parentHost == null; t = t.parent)
                    parentHost = UIToolkitPanels.GetHost(t.gameObject);
                if (parentHost != null && UIToolkitPanels.KindOf(parentHost) != kind)
                    throw new UIToolkitException("HOST_KIND_MISMATCH",
                        $"A {UIToolkitPanels.ComponentName(kind)} under a {UIToolkitPanels.ComponentName(UIToolkitPanels.KindOf(parentHost))} does not nest; Unity only nests hosts of the same kind. Match the parent's component or use a parent outside it.");
                var nested = parentHost != null;
                var tree = LoadVisualTree(visual_tree_asset);
                float? order = ParseOrder(sorting_order);
                PanelSettings settings = null;
                if (!string.IsNullOrEmpty(panel_settings))
                {
                    if (nested) throw new UIToolkitException("NESTED_PANEL_SETTINGS", "Nested panels inherit PanelSettings from their parent host; omit panel_settings or use a non-host parent.");
                    settings = LoadPanelSettings(panel_settings);
                }
                else if (!nested)
                {
                    settings = ResolveProjectPanelSettings(new_panel_settings_path, createdAssets);
                }

                Undo.IncrementCurrentGroup();
                var undoGroup = Undo.GetCurrentGroup();
                Undo.SetCurrentGroupName("Create UI Toolkit panel " + name);
                var go = ObjectFactory.CreateGameObject(name, componentType);
                StageUtility.PlaceGameObjectInCurrentStage(go);
                var stage = PrefabStageUtility.GetCurrentPrefabStage();
                if (parentObject != null) Undo.SetTransformParent(go.transform, parentObject.transform, "Parent UI Toolkit panel");
                else if (stage != null) Undo.SetTransformParent(go.transform, stage.prefabContentsRoot.transform, "Parent UI Toolkit panel");
                go.transform.localPosition = Vector3.zero; go.transform.localRotation = Quaternion.identity; go.transform.localScale = Vector3.one;
                go.layer = parentObject != null ? parentObject.layer : Math.Max(0, LayerMask.NameToLayer("UI"));
                GameObjectUtility.EnsureUniqueNameForSibling(go);

                var host = go.GetComponent(componentType);
                if (parentObject != null) UIToolkitPanels.SyncHierarchy(host);
                if (settings != null && UIToolkitPanels.GetParentUI(host) == null) UIToolkitPanels.SetPanelSettings(host, settings);
                if (tree != null) UIToolkitPanels.SetVisualTreeAsset(host, tree);
                if (order.HasValue) UIToolkitPanels.SetSortingOrder(host, order.Value);
                UIToolkitPanels.MarkHostModified(host);
                Selection.activeGameObject = go;
                Undo.CollapseUndoOperations(undoGroup);

                var warnings = new List<string>();
                if (kind == UIToolkitPanels.KindUiDocument && UIToolkitPanels.PanelRendererAvailable)
                    warnings.Add("UIDocument is Legacy on this Unity version; PanelRenderer is the default host for new screens.");
                if (tree == null) warnings.Add("No UXML assigned; the panel renders nothing until configure_ui_toolkit_panel sets visual_tree_asset.");
                if (createdAssets.Count > 0) warnings.Add("Created assets are saved; the scene is not.");
                return Response.Success($"Created {UIToolkitPanels.ComponentName(kind)} '{go.name}'.", new
                {
                    host = DescribeHost(host),
                    created_assets = createdAssets,
                    warnings
                });
            }
            catch (UIToolkitException ex) { return Response.Error(ex.Code, new { detail = ex.Message, details = ex.Details, created_assets = createdAssets }); }
            catch (Exception ex) { return Response.Error("UI_TOOLKIT_PANEL_NOT_CREATED", new { detail = ex.Message, created_assets = createdAssets }); }
        }

        [Description("Change an existing PanelRenderer/UIDocument in Edit Mode: UXML (\"none\" clears), PanelSettings (top-level hosts only) and sorting order. Uses the component's own setters so the live panel reloads; undoable; does not save the scene.")]
        [SceneEditingTool]
        public static object ConfigureUiToolkitPanel(
            [ToolParam("Panel host GameObject name/path/ID")] string target,
            [ToolParam("UXML VisualTreeAsset path, or none to clear", Required = false)] string visual_tree_asset = null,
            [ToolParam("PanelSettings asset path", Required = false)] string panel_settings = null,
            [ToolParam("Sorting order", Required = false)] string sorting_order = null)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                return Response.Error("EDIT_MODE_REQUIRED", new { hint = "Play Mode changes to hosts are lost on exit; configure in Edit Mode." });
            try
            {
                var host = ResolveSingleHost(target);
                if (string.IsNullOrWhiteSpace(visual_tree_asset) && string.IsNullOrWhiteSpace(panel_settings) && string.IsNullOrWhiteSpace(sorting_order))
                    throw new ArgumentException("Pass at least one of visual_tree_asset, panel_settings or sorting_order.");
                var clearTree = string.Equals(visual_tree_asset, "none", StringComparison.OrdinalIgnoreCase);
                var tree = clearTree ? null : LoadVisualTree(visual_tree_asset);
                var settings = string.IsNullOrEmpty(panel_settings) ? null : LoadPanelSettings(panel_settings);
                if (settings != null && UIToolkitPanels.GetParentUI(host) != null)
                    throw new UIToolkitException("NESTED_PANEL_SETTINGS", "This host is nested and inherits PanelSettings from its parent host.");
                float? order = ParseOrder(sorting_order);

                Undo.RecordObject(host, "Configure UI Toolkit panel");
                if (clearTree || tree != null) UIToolkitPanels.SetVisualTreeAsset(host, tree);
                if (settings != null) UIToolkitPanels.SetPanelSettings(host, settings);
                if (order.HasValue) UIToolkitPanels.SetSortingOrder(host, order.Value);
                UIToolkitPanels.MarkHostModified(host);
                return Response.Success($"Configured '{host.gameObject.name}'.", new { host = DescribeHost(host, treeChanged: clearTree || tree != null) });
            }
            catch (UIToolkitException ex) { return Response.Error(ex.Code, new { detail = ex.Message, details = ex.Details }); }
            catch (Exception ex) { return Response.Error("UI_TOOLKIT_PANEL_NOT_CONFIGURED", new { detail = ex.Message }); }
        }

        [Description("Create a PanelSettings asset the way Unity's Create > UI Toolkit menu does (theme: explicit, project default, first .tss, or a new default runtime theme). Optional scale mode, reference resolution/match, render mode (screen_space_overlay/world_space where supported) and sorting order. Never overwrites an existing asset.")]
        public static object CreatePanelSettings(
            [ToolParam("New asset path under Assets/, .asset appended if missing")] string path,
            [ToolParam("ThemeStyleSheet (.tss) path", Required = false)] string theme_style_sheet = null,
            [ToolParam("constant_pixel_size, constant_physical_size or scale_with_screen_size", Required = false)] string scale_mode = null,
            [ToolParam("Reference resolution width,height (scale_with_screen_size)", Required = false)] string reference_resolution = null,
            [ToolParam("Width/height match 0..1 (scale_with_screen_size)", Required = false)] string match = null,
            [ToolParam("screen_space_overlay or world_space", Required = false)] string render_mode = null,
            [ToolParam("Panel sorting order", Required = false)] string sorting_order = null)
        {
            var createdAssets = new List<string>();
            try
            {
                PanelScaleMode? scale = null;
                if (!string.IsNullOrEmpty(scale_mode))
                {
                    var enumName = ToolRegistry.ToPascalCase(scale_mode.Trim());
                    if (!Enum.TryParse(enumName, out PanelScaleMode parsed) || !Enum.IsDefined(typeof(PanelScaleMode), parsed))
                        throw new ArgumentException("scale_mode must be constant_pixel_size, constant_physical_size or scale_with_screen_size.");
                    scale = parsed;
                }
                Vector2Int? resolution = null;
                if (!string.IsNullOrEmpty(reference_resolution))
                {
                    var parts = reference_resolution.Split(',');
                    if (parts.Length != 2 || !int.TryParse(parts[0].Trim(), out var w) || !int.TryParse(parts[1].Trim(), out var h) || w <= 0 || h <= 0)
                        throw new ArgumentException("reference_resolution must be positive width,height.");
                    resolution = new Vector2Int(w, h);
                }
                float? matchValue = null;
                if (!string.IsNullOrEmpty(match))
                {
                    if (!float.TryParse(match, NumberStyles.Float, CultureInfo.InvariantCulture, out var m) || m < 0 || m > 1)
                        throw new ArgumentException("match must be within 0..1.");
                    matchValue = m;
                }
                float? order = ParseOrder(sorting_order);
                var renderMode = string.IsNullOrEmpty(render_mode) ? null : render_mode.Trim().ToLowerInvariant();
                // Validate before the asset exists: a late failure would leave a saved, half-configured
                // asset behind and make the retry fail with ASSET_ALREADY_EXISTS.
                if (renderMode != null) UIToolkitPanels.ParseRenderMode(renderMode);

                var settings = UIToolkitPanels.CreatePanelSettingsAsset(path, theme_style_sheet, createdAssets);
                if (scale.HasValue) settings.scaleMode = scale.Value;
                if (resolution.HasValue) settings.referenceResolution = resolution.Value;
                if (matchValue.HasValue) settings.match = matchValue.Value;
                if (order.HasValue) settings.sortingOrder = order.Value;
                if (renderMode != null) UIToolkitPanels.SetRenderMode(settings, renderMode);
                EditorUtility.SetDirty(settings);
                AssetDatabase.SaveAssets();
                return Response.Success($"Created PanelSettings at {AssetDatabase.GetAssetPath(settings)}.", new
                {
                    panel_settings = UIToolkitPanels.DescribePanelSettings(settings),
                    created_assets = createdAssets
                });
            }
            catch (UIToolkitException ex) { return Response.Error(ex.Code, new { detail = ex.Message, created_assets = createdAssets }); }
            catch (Exception ex) { return Response.Error("PANEL_SETTINGS_NOT_CREATED", new { detail = ex.Message, created_assets = createdAssets }); }
        }

        [Description("Read the live VisualElement tree of PanelRenderer/UIDocument hosts (Edit or Play Mode): type, name, classes, text/value, displayed/enabled/picking state, visible_fraction/clipped_by (ScrollView viewport or overflow clipping), panel bounds and Game View screen bounds (render bottom-left + image top-left, same contract as clicks/screenshots), optional resolved styles. Filter with selector (Type#name.class) and/or exact text; a text match inside a Button reports the Button's click_receiver_path. Includes findings such as zero-size or picking-ignored controls, elements outside the panel and duplicate names, limited to the matched elements' subtrees unless all_findings. In Play Mode a host reports scheduler.stalled when a scheduled item on its panel keeps throwing (its later scheduled work no longer runs). Element paths feed click_ui_toolkit_element; they are child indices that shift when the tree changes, so re-read after UI changes. snapshot=true returns a snapshot_id for diff_visual_tree.")]
        [ReadOnlyTool]
        public static object GetVisualTree(
            [ToolParam("Panel host GameObject name/path/ID; omitted reads every host", Required = false)] string target = null,
            [ToolParam("Selector: Type#name.class compounds, descendant (space) or child (>) combinators, :nth-of-type(n)/:nth-child(n); omitted returns the tree", Required = false)] string selector = null,
            [ToolParam("Exact element text, case-insensitive", Required = false)] string text = null,
            [ToolParam("Maximum depth below each root (or below element_path), 0..64. A selector/text query searches the whole tree regardless", Required = false)] int max_depth = 24,
            [ToolParam("Maximum elements returned across hosts, 1..2000", Required = false)] int max_elements = 300,
            [ToolParam("Include resolved styles per element", Required = false)] bool include_styles = false,
            [ToolParam("Report findings for the whole tree, uncapped, instead of the matched subtrees (at most 50 per host)", Required = false)] bool all_findings = false,
            [ToolParam("Omit null/empty fields, duplicate bounds and most host details", Required = false)] bool compact = false,
            [ToolParam("Skip hidden elements (e.g. closed dialogs)", Required = false)] bool displayed_only = false,
            [ToolParam("Also store the hosts' whole trees (regardless of selector/max_depth) and return snapshot_id for diff_visual_tree", Required = false)] bool snapshot = false,
            [ToolParam("Element path relative to one host (as for clicks): read only that element's subtree", Required = false)] string element_path = null,
            [ToolParam("Revision from a previous reply: if the result is unchanged, only {unchanged:true, revision} returns. Replies of 1 KB or more carry a revision.", Required = false)] string if_revision = null)
        {
            try
            {
                max_depth = Mathf.Clamp(max_depth, 0, 64);
                max_elements = Mathf.Clamp(max_elements, 1, 2000);
                var filter = UIToolkitPanels.Selector.Parse(selector, text);
                var hosts = ResolveHosts(target);
                if (!string.IsNullOrEmpty(element_path) && hosts.Count != 1)
                    throw new UIToolkitException("TARGET_REQUIRED", "element_path is relative to one host; pass target.");
                var hostRoots = UIToolkitPanels.HostRoots(hosts);
                // A targeted parent still lists nested panels' elements, but they are not its content.
                var allHosts = string.IsNullOrWhiteSpace(target) ? hosts : UIToolkitPanels.FindHosts(sorted: false);
                var allRoots = string.IsNullOrWhiteSpace(target) ? hostRoots : UIToolkitPanels.HostRoots(allHosts);
                var renderHeight = TryGetRenderHeight(out var geometryError);
                var matched = 0;
                var results = new List<object>();
                foreach (var host in hosts)
                {
                    var root = UIToolkitPanels.GetRoot(host);
                    if (root == null || root.panel == null)
                    {
                        results.Add(new { host = DescribeHost(host, nestedRoots: hostRoots), root_ready = false, elements = Array.Empty<object>(), findings = Array.Empty<object>() });
                        continue;
                    }

                    UIToolkitPanels.ValidateLayout(root.panel);
                    var mapping = MappingFor(host, root.panel);
                    var elements = new List<object>();
                    // max_depth limits a listing; a query must not miss deeper matches.
                    var walked = WalkForTree(root, element_path, filter.IsEmpty ? max_depth : int.MaxValue, hostRoots);
                    var displayCache = new UIToolkitPanels.DisplayCache();
                    var clipCache = new UIToolkitPanels.ClipCache();
                    var hostMatches = new HashSet<VisualElement>();
                    foreach (var entry in walked)
                    {
                        if (!filter.IsEmpty && !filter.Matches(entry.Value)) continue;
                        if (displayed_only && !displayCache.IsDisplayed(entry.Value)) continue;
                        hostMatches.Add(entry.Value);
                        if (++matched > max_elements) continue;
                        var described = UIToolkitPanels.DescribeElement(entry.Value, entry.Key, mapping, renderHeight, include_styles, displayCache, clipCache);
                        elements.Add(compact ? CompactElement(described) : described);
                    }

                    // A query's findings cover what it matched (and their subtrees), not the whole tree.
                    var scope = filter.IsEmpty || all_findings ? null : hostMatches;
                    var findings = Audit(root, walked, displayCache, clipCache, scope);
                    var findingsOmitted = all_findings ? 0 : Math.Max(0, findings.Count - MaxFindingsPerHost);
                    var hostInfo = DescribeHost(host, walked.Count, allRoots);
                    results.Add(new
                    {
                        host = compact ? CompactHost(hostInfo) : hostInfo,
                        root_ready = true,
                        layout_ready = UIToolkitPanels.HasLayout(root),
                        screen_mapping = mapping.HasValue ? "screen_space" : UIToolkitPanels.IsWorldSpace(UIToolkitPanels.GetPanelSettings(host)) ? "world_space_unmapped" : "unavailable",
                        element_count = walked.Count,
                        nested_hosts = NestedHosts(root, allHosts),
                        elements,
                        findings = findingsOmitted > 0 ? findings.Take(MaxFindingsPerHost).ToList() : findings,
                        findings_omitted = findingsOmitted > 0 ? (int?)findingsOmitted : null,
                        findings_scope = scope != null ? "matched_subtrees" : "tree",
                        scheduler = SchedulerStallMonitor.Report(root)
                    });
                }

                var snapshotId = snapshot ? VisualTreeSnapshots.Take(target, hosts).Id : null;
                object payload = new
                {
                    hosts = results, truncated = matched > max_elements, render_height = renderHeight > 0 ? (int?)renderHeight : null, geometry_error = geometryError, snapshot_id = snapshotId,
                    snapshot_scope = snapshotId != null ? "whole_tree" : null
                };
                return Response.Success(filter.IsEmpty
                        ? $"Visual tree for {hosts.Count} host(s)."
                        : $"{matched} element(s) matched.",
                    compact ? CompactTreePayload(payload) : payload);
            }
            catch (UIToolkitException ex) { return Response.Error(ex.Code, new { detail = ex.Message, details = ex.Details }); }
            catch (Exception ex) { return Response.Error("VISUAL_TREE_UNAVAILABLE", new { detail = ex.Message }); }
        }

        /// <summary>The host tree, or the subtree at <paramref name="elementPath"/> with host-relative paths.</summary>
        private static List<KeyValuePair<string, VisualElement>> WalkForTree(VisualElement root, string elementPath, int maxDepth, ISet<VisualElement> hostRoots)
        {
            if (string.IsNullOrEmpty(elementPath))
                return UIToolkitPanels.Walk(root, maxDepth, hostRoots).ToList();
            var start = UIToolkitPanels.ResolvePath(root, elementPath) ?? throw new UIToolkitException("ELEMENT_NOT_FOUND",
                "element_path does not resolve in the current tree; re-read get_visual_tree or use a selector.");
            var prefix = UIToolkitPanels.PathOf(root, start) ?? elementPath;
            return UIToolkitPanels.Walk(start, maxDepth, hostRoots)
                .Select(e => new KeyValuePair<string, VisualElement>(e.Key.Length == 0 ? prefix : prefix.Length == 0 ? e.Key : prefix + "/" + e.Key, e.Value))
                .ToList();
        }

        [Description("Pick the UI Toolkit elements under a Game View point (Edit or Play Mode) across all screen-space PanelRenderer/UIDocument panels, topmost panel first. Returns the topmost element, its ancestor chain, the click receiver (Button/Toggle/Clickable owner) or the element swallowing the click. Uses the same coordinate contract as raycast_at_point/simulate_mouse_click. World-space panels are reported but not picked.")]
        [ReadOnlyTool]
        public static object PickUiToolkitElement(
            [ToolParam("X coordinate")] float x,
            [ToolParam("Y coordinate")] float y,
            [ToolParam("render_pixels, image_pixels or normalized", Required = false)] string coordinate_space = "render_pixels",
            [ToolParam("top_left (default for image_pixels) or bottom_left (default otherwise)", Required = false)] string origin = null,
            [ToolParam("Screenshot ID; required for image_pixels", Required = false)] string capture_id = null)
        {
            if (!VisualCoordinates.TryResolve(x, y, coordinate_space, origin, capture_id, out var point, out var geometry, out var coordinateError))
                return Response.Error(coordinateError);
            var picks = UIToolkitPanels.PickAt(point, geometry.render_height);
            var top = UIToolkitPanels.TopPick(picks);
            var described = picks.Select(p => UIToolkitPanels.DescribePick(p, geometry.render_height)).ToArray();
            return Response.Success(top == null ? "No UI Toolkit element at this point." : $"Topmost UI Toolkit element: {Label(top.Element)}.", new
            {
                screen_position = new { x = point.x, y = point.y },
                geometry,
                top = top != null ? described[picks.IndexOf(top)] : null,
                panels = described
            });
        }

        [Description("Click a UI Toolkit element in Play Mode by selector (Type#name.class), exact text and/or element_path from get_visual_tree, optionally scoped to one host. A text match on a Button's inner Label clicks the Button. Requires a unique, displayed, enabled match; clicks the visible part of an element partly clipped by a ScrollView/overflow, refuses ELEMENT_CLIPPED when fully clipped (scroll_into_view scrolls it in first) and ELEMENT_COVERED when another element receives the pointer, unless allow_covered. Dispatches pointer move/down/up through the panel like real mouse input (Clickable, :hover/:active, ClickEvent). Reports clicked, handler_invoked and click_event_received. Works without an EventSystem.")]
        public static object ClickUiToolkitElement(
            [ToolParam("Panel host GameObject name/path/ID", Required = false)] string target = null,
            [ToolParam("Selector: Type#name.class compounds joined by space (descendant) or > (child), optional :nth-of-type(n)/:nth-child(n), e.g. '#settings .row > Button'", Required = false)] string selector = null,
            [ToolParam("Exact element text, case-insensitive", Required = false)] string text = null,
            [ToolParam("Element path relative to one host: child indices from get_visual_tree (shift when the tree changes) or '/'-separated selectors over direct children, e.g. '#menu/Button:nth-of-type(2)'", Required = false)] string element_path = null,
            [ToolParam("left, right or middle", Required = false)] string button = "left",
            [ToolParam("Click even when another element covers the center", Required = false)] bool allow_covered = false,
            [ToolParam("Scroll enclosing ScrollViews so a clipped element is in view before clicking", Required = false)] bool scroll_into_view = false,
            [ToolParam("compact (default) or full: full echoes the complete element description", Required = false)] string detail = null)
        {
            if (!EditorApplication.isPlaying)
                return Response.Error("PLAY_MODE_REQUIRED", new { hint = "UI callbacks are registered by runtime scripts; enter Play Mode first." });
            return ClickElement(target, selector, text, element_path, button, allow_covered, scroll_into_view, detail);
        }

        [Description("Scroll a UI Toolkit ScrollView (Edit or Play Mode; scroll state is not saved). Without deltas, scrolls every ScrollView around the matched element until it is in view (ScrollView.ScrollTo): the fix for ELEMENT_CLIPPED from click_ui_toolkit_element. With delta_x/delta_y (panel pixels; positive scrolls right/down), offsets the matched ScrollView, or the nearest one around the matched element; the ScrollView clamps the offset. Match by selector (Type#name.class), exact text and/or element_path as for clicks. Reports offsets and the element's visible_fraction afterwards.")]
        public static object ScrollUiToolkit(
            [ToolParam("Panel host GameObject name/path/ID", Required = false)] string target = null,
            [ToolParam("Selector: Type#name.class compounds joined by space (descendant) or > (child), optional :nth-of-type(n)/:nth-child(n), e.g. '#settings .row > Button'", Required = false)] string selector = null,
            [ToolParam("Exact element text, case-insensitive", Required = false)] string text = null,
            [ToolParam("Element path relative to one host: child indices from get_visual_tree (shift when the tree changes) or '/'-separated selectors over direct children, e.g. '#menu/Button:nth-of-type(2)'", Required = false)] string element_path = null,
            [ToolParam("Horizontal scroll in panel pixels; 0 with delta_y 0 scrolls the element into view", Required = false)] float delta_x = 0,
            [ToolParam("Vertical scroll in panel pixels, positive scrolls down", Required = false)] float delta_y = 0,
            [ToolParam("compact (default) or full: full echoes the complete element description", Required = false)] string detail = null)
        {
            return ScrollElement(target, selector, text, element_path, delta_x, delta_y, detail);
        }

        // ---- Shared implementation (also used by tests in Edit Mode) -------------------------

        internal static object ScrollElement(string target, string selector, string text, string elementPath, float deltaX, float deltaY, string detail = null)
        {
            try
            {
                if (float.IsNaN(deltaX) || float.IsNaN(deltaY) || float.IsInfinity(deltaX) || float.IsInfinity(deltaY) ||
                    Mathf.Abs(deltaX) > 100000f || Mathf.Abs(deltaY) > 100000f)
                    throw new UIToolkitException("INVALID_SCROLL_DELTA", "delta_x and delta_y must be finite and within 100000 panel pixels.");
                var (matchHost, matchPath, element) = ResolveUniqueElement(target, selector, text, elementPath);
                var panel = element.panel ?? throw new UIToolkitException("ROOT_NOT_READY", "The element is not attached to a panel.");
                UIToolkitPanels.ValidateLayout(panel);
                // A hidden ScrollView has no layout to clamp against, so an offset would be reported but never applied.
                if (!UIToolkitPanels.IsDisplayed(element)) throw new UIToolkitException("ELEMENT_NOT_DISPLAYED", "The element is hidden (display:none or visibility:hidden on it or an ancestor).");

                List<KeyValuePair<ScrollView, Vector2>> scrolled;
                if (deltaX != 0f || deltaY != 0f)
                {
                    var scrollView = element as ScrollView;
                    for (var current = element.hierarchy.parent; scrollView == null && current != null; current = current.hierarchy.parent)
                        if (current is ScrollView ancestor && ancestor.contentContainer.Contains(element)) scrollView = ancestor;
                    if (scrollView == null) throw new UIToolkitException("NO_SCROLL_VIEW", "The element is not a ScrollView and is not inside one.");
                    scrolled = new List<KeyValuePair<ScrollView, Vector2>> { new KeyValuePair<ScrollView, Vector2>(scrollView, scrollView.scrollOffset) };
                    scrollView.scrollOffset += new Vector2(deltaX, deltaY);
                    UIToolkitPanels.ValidateLayout(panel);
                }
                else
                {
                    if (element is ScrollView) throw new ArgumentException("Pass delta_x/delta_y to scroll a ScrollView, or match an element inside it to scroll it into view.");
                    scrolled = UIToolkitPanels.ScrollIntoView(element);
                    if (scrolled.Count == 0) throw new UIToolkitException("NO_SCROLL_VIEW", "The element is not inside a ScrollView.");
                }

                var root = UIToolkitPanels.GetRoot(matchHost);
                var mapping = MappingFor(matchHost, panel);
                var renderHeight = TryGetRenderHeight(out _);
                if (mapping.HasValue && renderHeight > 0)
                {
                    var screen = mapping.Value.ToScreen(scrolled[0].Key.worldBound.center);
                    VideoRecordingFunctions.RecordMarker("scroll", "UI Toolkit scroll " + Label(scrolled[0].Key), new Vector2(screen.x, renderHeight - screen.y));
                }
                var moved = scrolled.Any(s => s.Key.scrollOffset != s.Value);
                var visibility = UIToolkitPanels.GetVisibility(element);
                return Response.Success(moved ? $"Scrolled {Label(scrolled[0].Key)}." : $"{Label(scrolled[0].Key)} did not move (already in view or at its limit).", new
                {
                    host_id = ObjectIdHelper.GetSerializableId(matchHost.gameObject),
                    element = ResponseDetail.IsCompact(detail) ? EchoElement(element, matchPath)
                        : UIToolkitPanels.DescribeElement(element, matchPath, mapping, renderHeight, false),
                    scroll_views = scrolled.Select(s => new
                    {
                        scroll_view = Label(s.Key),
                        element_path = root != null ? UIToolkitPanels.PathOf(root, s.Key) : null,
                        offset_before = new { x = s.Value.x, y = s.Value.y },
                        offset_after = new { x = s.Key.scrollOffset.x, y = s.Key.scrollOffset.y }
                    }).ToArray(),
                    visible_fraction = (float)Math.Round(visibility.Fraction, 3),
                    hint = visibility.Fraction <= 0f && moved ? "Layout may settle on the next frame; re-read get_visual_tree before clicking." : null
                });
            }
            catch (UIToolkitException ex) { return Response.Error(ex.Code, new { detail = ex.Message, details = ex.Details }); }
            catch (Exception ex) { return Response.Error("UI_TOOLKIT_SCROLL_FAILED", new { detail = ex.Message }); }
        }

        internal static object ClickElement(string target, string selector, string text, string elementPath, string button, bool allowCovered, bool scrollIntoView = false, string detail = null)
        {
            try
            {
                if (!InputInteractionFunctions.TryParseButton(button, out var inputButton))
                    throw new ArgumentException("button must be left, right or middle.");
                var (matchHost, matchPath, matched) = ResolveUniqueElement(target, selector, text, elementPath);
                var panel = matched.panel ?? throw new UIToolkitException("ROOT_NOT_READY", "The element is not attached to a panel.");
                UIToolkitPanels.ValidateLayout(panel);
                if (!UIToolkitPanels.IsDisplayed(matched)) throw new UIToolkitException("ELEMENT_NOT_DISPLAYED", "The element is hidden (display:none or visibility:hidden on it or an ancestor).");
                if (!matched.enabledInHierarchy) throw new UIToolkitException("ELEMENT_DISABLED", "The element or an ancestor is disabled.");
                if (!UIToolkitPanels.HasLayout(matched) || matched.worldBound.width < 1f || matched.worldBound.height < 1f)
                    throw new UIToolkitException("ELEMENT_HAS_NO_AREA", "The element has no laid-out area to click.");

                // Clipping first: a clipped center picks nothing or an ancestor, which is not "covered".
                var visibility = UIToolkitPanels.GetVisibility(matched);
                var scrolled = false;
                if (!visibility.FullyVisible && scrollIntoView && visibility.ScrollView != null)
                {
                    scrolled = UIToolkitPanels.ScrollIntoView(matched).Any(s => s.Key.scrollOffset != s.Value);
                    visibility = UIToolkitPanels.GetVisibility(matched);
                }
                if (visibility.Fraction <= 0f)
                    throw new UIToolkitException("ELEMENT_CLIPPED",
                        scrolled
                            ? "Scrolled toward the element, but it is still outside the viewport; layout may settle on the next frame, so retry."
                            : visibility.ScrollView != null
                                ? $"The element is scrolled out of view of {Label(visibility.ScrollView)}; pass scroll_into_view=true or call scroll_ui_toolkit first."
                                : "The element lies outside the visible area of its clipping ancestor or the panel.",
                        new
                        {
                            clipped_by = ClipperLabel(visibility.ClippedBy),
                            scroll_view = visibility.ScrollView != null ? Label(visibility.ScrollView) : null,
                            visible_fraction = 0f,
                            scrolled
                        });

                var point = visibility.FullyVisible ? matched.worldBound.center : visibility.Visible.center;
                var picked = panel.Pick(point);
                var covered = picked == null || !(ReferenceEquals(picked, matched) || matched.Contains(picked));
                if (covered && !allowCovered)
                {
                    if (picked == null)
                        throw new UIToolkitException("ELEMENT_NOT_PICKABLE", "Nothing pickable is at the element's center: it and its children use picking-mode Ignore.",
                            new { picking_mode = matched.pickingMode.ToString() });
                    throw new UIToolkitException("ELEMENT_COVERED", "Another element receives the pointer at this element's center.",
                        new { covered_by = Label(picked), picking_mode = matched.pickingMode.ToString() });
                }

                var dispatchTarget = covered ? matched : picked;
                var mapping = MappingFor(matchHost, panel);
                var renderHeight = TryGetRenderHeight(out _);
                if (mapping.HasValue && renderHeight > 0)
                {
                    var screen = mapping.Value.ToScreen(point);
                    VideoRecordingFunctions.RecordMarker("click", "UI Toolkit click " + Label(matched), new Vector2(screen.x, renderHeight - screen.y));
                }
                var receiver = UIToolkitPanels.FindClickReceiver(dispatchTarget);
                var result = UIToolkitPanels.DispatchClick(panel, dispatchTarget, point, (int)inputButton);
                var scheduler = result.Activated ? null : SchedulerStallMonitor.Report(UIToolkitPanels.GetRoot(matchHost));
                var who = receiver is Button ? "the button" : "the receiver";
                var compactEcho = ResponseDetail.IsCompact(detail);
                var message = !result.Activated ? $"Pointer sequence sent to {Label(matched)}, but no ClickEvent or click handler reached it."
                    : result.ReceiverDetached ? $"Clicked {Label(matched)}; {who} was removed from its panel."
                    : result.ReceiverDisabled ? $"Clicked {Label(matched)}; {who} disabled itself."
                    : $"Clicked {Label(matched)}.";
                return Response.Success(message, new
                {
                    host_id = ObjectIdHelper.GetSerializableId(matchHost.gameObject),
                    element = compactEcho ? EchoElement(matched, matchPath)
                        : UIToolkitPanels.DescribeElement(matched, matchPath, mapping, renderHeight, false),
                    dispatched_to = compactEcho && ReferenceEquals(dispatchTarget, matched) ? null : Label(dispatchTarget),
                    click_receiver = receiver != null && !(compactEcho && ReferenceEquals(receiver, matched)) ? Label(receiver) : null,
                    clicked = result.Activated,
                    handler_invoked = result.HandlerInvoked,
                    click_event_received = result.ClickEventReceived,
                    receiver_disabled = result.ReceiverDisabled,
                    receiver_detached = result.ReceiverDetached,
                    visible_fraction = (float)Math.Round(visibility.Fraction, 3),
                    scrolled_into_view = scrolled,
                    covered,
                    scheduler
                });
            }
            catch (UIToolkitException ex) { return Response.Error(ex.Code, new { detail = ex.Message, details = ex.Details }); }
            catch (Exception ex) { return Response.Error("UI_TOOLKIT_CLICK_FAILED", new { detail = ex.Message }); }
        }

        /// <summary>
        /// The single element a click/scroll query names. Text matches on a control's inner label
        /// resolve to the control (<see cref="UIToolkitPanels.Selector.ClickTarget"/>).
        /// </summary>
        private static (Component host, string path, VisualElement element) ResolveUniqueElement(string target, string selector, string text, string elementPath)
        {
            var hosts = ResolveHosts(target);
            var filter = UIToolkitPanels.Selector.Parse(selector, text);
            var matches = new List<(Component host, string path, VisualElement element)>();
            if (!string.IsNullOrEmpty(elementPath))
            {
                if (hosts.Count != 1) throw new UIToolkitException("TARGET_REQUIRED", "element_path is relative to one host; pass target.");
                var root = UIToolkitPanels.GetRoot(hosts[0]) ?? throw new UIToolkitException("ROOT_NOT_READY", "The panel host has not built its visual tree.");
                var element = UIToolkitPanels.ResolvePath(root, elementPath) ?? throw new UIToolkitException("ELEMENT_NOT_FOUND",
                    UIToolkitPanels.IsIndexPath(elementPath)
                        ? "element_path no longer resolves (paths are child indices and shift when a panel brings an element to the front); re-read get_visual_tree or use a selector."
                        : "element_path does not resolve in the current tree (each '/' segment is a child index or a selector over direct children).");
                var resolved = filter.IsEmpty ? element : filter.ClickTarget(element);
                if (resolved == null) throw new UIToolkitException("ELEMENT_NOT_FOUND", "The element at element_path does not match selector/text.");
                matches.Add((hosts[0], ReferenceEquals(resolved, element) ? elementPath : UIToolkitPanels.PathOf(root, resolved) ?? elementPath, resolved));
            }
            else
            {
                if (filter.IsEmpty) throw new ArgumentException("Pass selector, text or element_path.");
                // A nested host's root lives inside its parent host's tree; excluding host roots
                // reaches each element once, relative to the host that owns it.
                var hostRoots = UIToolkitPanels.HostRoots(hosts);
                var seen = new HashSet<VisualElement>();
                foreach (var host in hosts)
                {
                    var root = UIToolkitPanels.GetRoot(host);
                    if (root == null) continue;
                    foreach (var entry in UIToolkitPanels.Walk(root, exclude: hostRoots))
                    {
                        var resolved = filter.ClickTarget(entry.Value);
                        if (resolved == null || !seen.Add(resolved)) continue;
                        var path = ReferenceEquals(resolved, entry.Value) ? entry.Key : UIToolkitPanels.PathOf(root, resolved);
                        if (path != null) matches.Add((host, path, resolved));
                    }
                }
            }

            if (matches.Count == 0)
            {
                var nearMisses = NearMisses(hosts, filter);
                throw new UIToolkitException("ELEMENT_NOT_FOUND", nearMisses.Length > 0
                        ? "No element matches; details lists similar elements. If the UI is still building, wait_for_ui_toolkit(state=exists) first."
                        : "No element matches and none is similar. If the UI is still building, wait_for_ui_toolkit(state=exists) first; otherwise inspect get_visual_tree.",
                    nearMisses.Length > 0 ? nearMisses : null);
            }
            if (matches.Count > 1)
                throw new UIToolkitException("AMBIGUOUS_ELEMENT", $"{matches.Count} elements match; narrow the selector or pass element_path.",
                    matches.Take(10).Select(m => new { host_id = ObjectIdHelper.GetSerializableId(m.host.gameObject), element_path = m.path, element = Label(m.element) }).ToArray());
            return matches[0];
        }

        /// <summary>Up to five elements whose name, text or type resembles an unmatched query, so a miss can be corrected without a tree read.</summary>
        private static object[] NearMisses(List<Component> hosts, UIToolkitPanels.Selector filter)
        {
            bool Like(string value, string wanted) => !string.IsNullOrEmpty(value) && !string.IsNullOrEmpty(wanted) &&
                (value.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0 || wanted.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0);
            var found = new List<object>();
            try
            {
                var hostRoots = UIToolkitPanels.HostRoots(hosts);
                foreach (var host in hosts)
                {
                    var root = UIToolkitPanels.GetRoot(host);
                    if (root == null) continue;
                    foreach (var entry in UIToolkitPanels.Walk(root, exclude: hostRoots))
                    {
                        var element = entry.Value;
                        var similar = Like(element.name, filter.Name) || Like(UIToolkitPanels.GetText(element), filter.Text) ||
                            (filter.Name == null && filter.Text == null && filter.Type != null && element.GetType().Name == filter.Type);
                        if (!similar) continue;
                        found.Add(new { host_id = ObjectIdHelper.GetSerializableId(host.gameObject), element_path = entry.Key, element = Label(element) });
                        if (found.Count == 5) return found.ToArray();
                    }
                }
            }
            catch (Exception) { /* candidates are a courtesy; the miss itself is the answer */ }
            return found.ToArray();
        }

        // ---- Helpers -----------------------------------------------------------------------

        /// <param name="elementCount">Already-walked element count, to avoid a second walk.</param>
        /// <param name="nestedRoots">Roots of other hosts; their subtrees are counted under those hosts,
        /// not this one. Omitted, every host in the loaded scenes is considered.</param>
        /// <param name="treeChanged">The UXML was just set; the root still holds the previous build until it rebuilds.</param>
        internal static object DescribeHost(Component host, int? elementCount = null, ISet<VisualElement> nestedRoots = null, bool treeChanged = false)
        {
            var settings = UIToolkitPanels.GetPanelSettings(host);
            var tree = UIToolkitPanels.GetVisualTreeAsset(host);
            var parent = UIToolkitPanels.GetParentUI(host);
            var root = UIToolkitPanels.GetRoot(host);
            var issues = new List<string>();
            if (parent == null && settings == null) issues.Add("NO_PANEL_SETTINGS: the panel is not displayed until PanelSettings is assigned.");
            if (settings != null && settings.themeStyleSheet == null) issues.Add("NO_THEME_STYLE_SHEET: built-in controls render unstyled without a theme.");
            // Code-built trees (no UXML) are normal; only a built root that stays empty in Play Mode is an issue.
            // A nested host's root is a child of this root but is not this host's content.
            if (root != null && (tree == null || elementCount == null))
                nestedRoots = nestedRoots ?? UIToolkitPanels.HostRoots(UIToolkitPanels.FindHosts(sorted: false));
            var treeSource = tree != null ? "uxml" : !treeChanged && root != null && root.hierarchy.Children().Any(c => !nestedRoots.Contains(c)) ? "code" : "none";
            if (treeSource == "none" && root != null && EditorApplication.isPlaying)
                issues.Add("NO_VISUAL_TREE_ASSET: no UXML is assigned and no code has added elements to the root.");
            if (host is UIDocument && UIToolkitPanels.PanelRendererAvailable) issues.Add("LEGACY_UI_DOCUMENT: PanelRenderer is the current host on this Unity version; keep unless migrating deliberately.");
            if (host.gameObject.activeInHierarchy && UIToolkitPanels.IsEnabled(host) && root == null && (settings != null || parent != null))
                issues.Add("ROOT_NOT_READY: the host has not built its visual tree yet (enter Play Mode or repaint the Game View).");
            return new
            {
                object_id = ObjectIdHelper.GetSerializableId(host.gameObject),
                component_id = ObjectIdHelper.GetSerializableId(host),
                path = ObjectsHelper.GetGameObjectPath(host.gameObject),
                scene = host.gameObject.scene.path,
                component = UIToolkitPanels.ComponentName(UIToolkitPanels.KindOf(host)),
                active = host.gameObject.activeInHierarchy,
                enabled = UIToolkitPanels.IsEnabled(host),
                visual_tree_asset = tree != null ? AssetDatabase.GetAssetPath(tree) : null,
                tree_source = treeSource,
                panel_settings = UIToolkitPanels.DescribePanelSettings(settings),
                parent_ui_id = parent != null ? ObjectIdHelper.GetSerializableId(parent.gameObject) : null,
                sorting_order = UIToolkitPanels.GetSortingOrder(host),
                root_ready = root != null,
                element_count = elementCount ?? (root != null ? UIToolkitPanels.CountElements(root, nestedRoots) : 0),
                issues
            };
        }

        /// <summary>The compact echo of an element an action touched: where it is and what it is called.</summary>
        internal static object EchoElement(VisualElement element, string path) => new { path, label = Label(element) };

        /// <summary>
        /// get_visual_tree's compact element: no null/empty fields, and panel_bounds/render_bottom_left
        /// dropped when screen bounds exist (image_top_left and center_render_pixels carry the same rect).
        /// </summary>
        internal static JObject CompactElement(object described)
        {
            var json = JObject.FromObject(described);
            if (json["screen_bounds"] is JObject screen)
            {
                json.Remove("panel_bounds");
                screen.Remove("render_bottom_left");
            }
            PruneEmpty(json);
            return json;
        }

        private static JObject CompactHost(object described)
        {
            var full = JObject.FromObject(described);
            var json = new JObject();
            foreach (var key in new[] { "object_id", "path", "component", "tree_source", "issues" })
                if (full[key] != null) json[key] = full[key];
            PruneEmpty(json);
            return json;
        }

        /// <summary>
        /// get_visual_tree's compact envelope: null fields, empty findings and truncated=false are dropped from the
        /// reply and each host entry. elements stays even when empty: no match is the answer.
        /// </summary>
        internal static JObject CompactTreePayload(object payload)
        {
            var json = JObject.FromObject(payload);
            if (json["truncated"]?.Type == JTokenType.Boolean && !(bool)json["truncated"]) json.Remove("truncated");
            foreach (var host in (json["hosts"] as JArray ?? new JArray()).OfType<JObject>())
            {
                var elements = host["elements"];
                host.Remove("elements");
                PruneEmpty(host);
                if (elements != null) host["elements"] = elements;
            }
            foreach (var property in json.Properties().Where(p => p.Value.Type == JTokenType.Null).ToList()) property.Remove();
            return json;
        }

        private static void PruneEmpty(JObject json)
        {
            foreach (var property in json.Properties().ToList())
            {
                if (property.Value is JObject child) PruneEmpty(child);
                if (property.Value.Type == JTokenType.Null || property.Value is JContainer container && !container.HasValues) property.Remove();
            }
        }

        /// <summary>Hosts whose roots sit in <paramref name="root"/>'s tree (nested PanelRenderer/UIDocument), or null.</summary>
        private static object[] NestedHosts(VisualElement root, IEnumerable<Component> hosts)
        {
            var nested = hosts.Select(h => (host: h, root: UIToolkitPanels.GetRoot(h)))
                .Where(x => x.root != null && !ReferenceEquals(x.root, root) && root.Contains(x.root))
                .Select(x => (object)new { object_id = ObjectIdHelper.GetSerializableId(x.host.gameObject), element_path = UIToolkitPanels.PathOf(root, x.root) })
                .ToArray();
            return nested.Length > 0 ? nested : null;
        }

        private const int MaxFindingsPerHost = 50;

        /// <param name="scope">Matched elements; when set, only they and their subtrees are audited.</param>
        private static List<object> Audit(VisualElement root, List<KeyValuePair<string, VisualElement>> walked, UIToolkitPanels.DisplayCache displayCache,
            UIToolkitPanels.ClipCache clipCache, ISet<VisualElement> scope = null)
        {
            var findings = new List<object>();
            var inScope = new Dictionary<VisualElement, bool>();
            bool InScope(VisualElement element)
            {
                if (scope == null) return true;
                if (element == null) return false;
                if (inScope.TryGetValue(element, out var value)) return value;
                value = scope.Contains(element) || !ReferenceEquals(element, root) && InScope(element.hierarchy.parent);
                inScope[element] = value;
                return value;
            }

            var outside = new HashSet<VisualElement>();
            foreach (var entry in walked)
            {
                var element = entry.Value;
                if (ReferenceEquals(element, root) || !displayCache.IsDisplayed(element) || !UIToolkitPanels.HasLayout(element) || !InScope(element)) continue;
                var bound = element.worldBound;
                var control = UIToolkitPanels.IsControl(element);
                if (control && (bound.width < 1f || bound.height < 1f))
                    findings.Add(new { code = "ZERO_SIZE_CONTROL", element_path = entry.Key, element = Label(element), detail = "Displayed control has no clickable area." });
                // Built-in Sliders are picking-ignored themselves and take the pointer on their drag
                // container, so only a control with nothing pickable in it lets the pointer through.
                if (control && element.pickingMode == PickingMode.Ignore && !HasPickableDescendant(element))
                    findings.Add(new { code = "CONTROL_PICKING_IGNORED", element_path = entry.Key, element = Label(element), detail = "picking-mode Ignore: pointer input passes through this control." });
                // Content scrolled out of a ScrollView is normal (see visible_fraction); report only the
                // topmost element of a subtree that lies outside the panel itself.
                if (bound.width > 0 && bound.height > 0)
                {
                    var visibility = clipCache.Of(element);
                    if (visibility.Fraction <= 0f && visibility.ScrollView == null && visibility.ClippedBy != null && visibility.ClippedBy.hierarchy.parent == null)
                    {
                        outside.Add(element);
                        if (!outside.Contains(element.hierarchy.parent))
                            findings.Add(new { code = "OUTSIDE_PANEL", element_path = entry.Key, element = Label(element), detail = "Element lies entirely outside the panel's visible area." });
                    }
                }
                if (element is TextElement textElement && !string.IsNullOrEmpty(textElement.text) && bound.width >= 1f && bound.height >= 1f &&
                    element.resolvedStyle.whiteSpace == WhiteSpace.NoWrap)
                {
                    var measured = textElement.MeasureTextSize(textElement.text, 0, VisualElement.MeasureMode.Undefined, 0, VisualElement.MeasureMode.Undefined);
                    if (measured.x > bound.width + 1f)
                        findings.Add(new { code = "TEXT_OVERFLOWS", element_path = entry.Key, element = Label(element), detail = $"Unwrapped text needs {measured.x:F0}px but the element is {bound.width:F0}px wide; it overflows or is clipped." });
                }
            }

            // unity-* names are the parts of built-in controls (every Slider has a #unity-dragger); they repeat by design.
            foreach (var group in walked.Select(e => e.Value).Where(e => !string.IsNullOrEmpty(e.name) && !e.name.StartsWith("unity-", StringComparison.Ordinal))
                         .GroupBy(e => e.name).Where(g => g.Count() > 1 && g.Any(InScope)))
                findings.Add(new { code = "DUPLICATE_NAME", element = "#" + group.Key, detail = $"{group.Count()} elements share this name; #name selectors are ambiguous." });
            return findings;
        }

        internal static bool HasPickableDescendant(VisualElement element)
        {
            for (var i = 0; i < element.hierarchy.childCount; i++)
            {
                var child = element.hierarchy[i];
                if (child.pickingMode == PickingMode.Position || HasPickableDescendant(child)) return true;
            }
            return false;
        }

        private static string ResolveKind(string component)
        {
            switch ((component ?? "auto").Trim().ToLowerInvariant())
            {
                case "":
                case "auto": return UIToolkitPanels.RecommendedKind;
                case "panel_renderer":
                case "panelrenderer":
                    if (!UIToolkitPanels.PanelRendererAvailable)
                        throw new UIToolkitException("PANEL_RENDERER_UNAVAILABLE", $"PanelRenderer requires Unity 6.6+; this is {Application.unityVersion}. Use ui_document.");
                    return UIToolkitPanels.KindPanelRenderer;
                case "ui_document":
                case "uidocument": return UIToolkitPanels.KindUiDocument;
                default: throw new ArgumentException("component must be auto, panel_renderer or ui_document.");
            }
        }

        private static PanelSettings ResolveProjectPanelSettings(string newPath, List<string> createdAssets)
        {
            var candidates = UIToolkitPanels.FindPanelSettingsPaths();
            if (candidates.Length == 1) return AssetDatabase.LoadAssetAtPath<PanelSettings>(candidates[0]);
            if (candidates.Length > 1)
                throw new UIToolkitException("PANEL_SETTINGS_AMBIGUOUS", "The project has several PanelSettings assets; pass panel_settings (panels sharing one asset share a panel and sort together).", candidates);
            return UIToolkitPanels.CreatePanelSettingsAsset(newPath ?? UIToolkitPanels.DefaultPanelSettingsPath, null, createdAssets);
        }

        private static VisualTreeAsset LoadVisualTree(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            var tree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(path.Trim());
            if (tree == null)
                throw new UIToolkitException("UXML_NOT_FOUND", "visual_tree_asset must be an imported .uxml path. After writing UXML with file tools, refresh the AssetDatabase and check the console for import errors.");
            return tree;
        }

        private static PanelSettings LoadPanelSettings(string path)
        {
            var settings = AssetDatabase.LoadAssetAtPath<PanelSettings>(path.Trim());
            if (settings == null) throw new UIToolkitException("PANEL_SETTINGS_NOT_FOUND", "panel_settings must be the path of a PanelSettings asset.");
            return settings;
        }

        private static float? ParseOrder(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var order) || float.IsNaN(order) || float.IsInfinity(order))
                throw new ArgumentException("sorting_order must be a number.");
            return order;
        }

        private static List<Component> ResolveHosts(string target)
        {
            if (string.IsNullOrWhiteSpace(target))
            {
                var all = UIToolkitPanels.FindHosts();
                if (all.Count == 0) throw new UIToolkitException("NO_UI_TOOLKIT_HOSTS", "No PanelRenderer or UIDocument in loaded scenes or the Prefab Stage.");
                return all;
            }
            return new List<Component> { ResolveSingleHost(target) };
        }

        private static Component ResolveSingleHost(string target)
        {
            var matches = ObjectsHelper.FindObjects(target, null, true, searchInactive: true);
            if (matches.Count != 1)
                throw new UIToolkitException(matches.Count == 0 ? "TARGET_NOT_FOUND" : "AMBIGUOUS_TARGET", "target must identify exactly one GameObject (prefer its ID).", new { count = matches.Count });
            return UIToolkitPanels.GetHost(matches[0])
                   ?? throw new UIToolkitException("NOT_A_PANEL_HOST", $"'{matches[0].name}' has no PanelRenderer or UIDocument; it may be uGUI (Canvas) UI.");
        }

        private static UIToolkitPanels.ScreenMapping? MappingFor(Component host, IPanel panel)
        {
            if (UIToolkitPanels.IsWorldSpace(UIToolkitPanels.GetPanelSettings(host))) return null;
            return UIToolkitPanels.TryGetScreenMapping(panel, out var mapping) ? mapping : (UIToolkitPanels.ScreenMapping?)null;
        }

        private static int TryGetRenderHeight(out string error)
        {
            error = null;
            try { return VisualCoordinates.Current().render_height; }
            catch (Exception ex) { error = ex.Message; return 0; }
        }

        private static string Label(VisualElement element) => UIToolkitPanels.Label(element);

        private static string ClipperLabel(VisualElement clipper) => UIToolkitPanels.ClipperLabel(clipper);
    }
}
