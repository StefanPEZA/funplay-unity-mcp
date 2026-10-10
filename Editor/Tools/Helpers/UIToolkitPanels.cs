// Copyright (C) Funplay. Licensed under MIT.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UIElements;
using UnityObject = UnityEngine.Object;

namespace Funplay.Editor.Tools.Helpers
{
    /// <summary>
    /// UI Toolkit panel hosts across Unity versions. Unity 6.6+ hosts runtime UI with the
    /// <c>PanelRenderer</c> component (a <see cref="Renderer"/>) and lists <see cref="UIDocument"/>
    /// under Legacy; older versions only have UIDocument. PanelRenderer is resolved by reflection so
    /// this package still compiles on 2022.3, and its root element is internal (the public route is
    /// a reload callback), so live inspection reads it reflectively as well.
    /// </summary>
    internal static class UIToolkitPanels
    {
        internal const string KindPanelRenderer = "panel_renderer";
        internal const string KindUiDocument = "ui_document";
        internal const string DefaultPanelSettingsPath = "Assets/UI Toolkit/PanelSettings.asset";
        internal const string DefaultThemePath = "Assets/UI Toolkit/UnityThemes/UnityDefaultRuntimeTheme.tss";

        private const BindingFlags InstanceMembers = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        internal static readonly Type PanelRendererType = typeof(UIDocument).Assembly.GetType("UnityEngine.UIElements.PanelRenderer");
        internal static readonly Type PanelInputConfigurationType = typeof(UIDocument).Assembly.GetType("UnityEngine.UIElements.PanelInputConfiguration");

        // PanelRenderer members resolved once; rootVisualElement is internal on 6.6.
        private static readonly PropertyInfo RendererPanelSettings = PanelRendererType?.GetProperty("panelSettings", BindingFlags.Public | BindingFlags.Instance);
        private static readonly PropertyInfo RendererVisualTreeAsset = PanelRendererType?.GetProperty("visualTreeAsset", BindingFlags.Public | BindingFlags.Instance);
        private static readonly PropertyInfo RendererParentUI = PanelRendererType?.GetProperty("parentUI", BindingFlags.Public | BindingFlags.Instance);
        private static readonly PropertyInfo RendererRoot = PanelRendererType?.GetProperty("rootVisualElement", InstanceMembers);
        private static readonly PropertyInfo RenderModeProperty = typeof(PanelSettings).GetProperty("renderMode", BindingFlags.Public | BindingFlags.Instance);

        // Per-type member lookups on hot inspection paths (element types number in the dozens).
        private static readonly Dictionary<(Type, string), PropertyInfo> PropertyCache = new Dictionary<(Type, string), PropertyInfo>();
        private static readonly Dictionary<(Type, string), MethodInfo> MethodCache = new Dictionary<(Type, string), MethodInfo>();

        private static PropertyInfo CachedProperty(Type type, string name, BindingFlags flags)
        {
            if (!PropertyCache.TryGetValue((type, name), out var property))
                PropertyCache[(type, name)] = property = type.GetProperty(name, flags);
            return property;
        }

        private static void InvokeIfPresent(object target, string method)
        {
            var type = target.GetType();
            if (!MethodCache.TryGetValue((type, method), out var info))
                MethodCache[(type, method)] = info = type.GetMethod(method, InstanceMembers, null, Type.EmptyTypes, null);
            info?.Invoke(target, null);
        }

        internal static bool PanelRendererAvailable => PanelRendererType != null;
        internal static string RecommendedKind => PanelRendererAvailable ? KindPanelRenderer : KindUiDocument;

        // ---- Host access -------------------------------------------------------------------

        internal static string KindOf(Component host) => host is UIDocument ? KindUiDocument : KindPanelRenderer;

        internal static string ComponentName(string kind) => kind == KindUiDocument ? "UIDocument" : "PanelRenderer";

        internal static Component GetHost(GameObject go)
        {
            if (go == null) return null;
            var document = go.GetComponent<UIDocument>();
            if (document != null) return document;
            // Compare with Unity's null: GetComponent can return a placeholder for missing components.
            var renderer = PanelRendererType != null ? go.GetComponent(PanelRendererType) : null;
            return renderer != null ? renderer : null;
        }

        /// <summary>
        /// Re-resolve parentUI after reparenting, as Unity's own GameObject menu does for UIDocument,
        /// so a nested host attaches to its parent panel immediately instead of on a later update.
        /// </summary>
        internal static void SyncHierarchy(Component host)
        {
            if (host is UIDocument)
            {
                InvokeIfPresent(host, "ReactToHierarchyChanged");
                return;
            }
            InvokeIfPresent(host, "SetupFromHierarchy");
            InvokeIfPresent(host, "ReactToHierarchyChanges");
        }

        /// <summary>Panel hosts in loaded scenes and the open Prefab Stage, in hierarchy order unless <paramref name="sorted"/> is false.</summary>
        internal static List<Component> FindHosts(bool includeInactive = true, bool sorted = true)
        {
            var hosts = new List<Component>();
            foreach (var type in HostTypes())
            {
                foreach (var obj in SceneObjectLookup.FindAll(type, includeInactive))
                {
                    if (obj is Component component && !EditorUtility.IsPersistent(component) && component.gameObject.scene.IsValid())
                        hosts.Add(component);
                }
            }

            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null && stage.prefabContentsRoot != null)
            {
                foreach (var type in HostTypes())
                    hosts.AddRange(stage.prefabContentsRoot.GetComponentsInChildren(type, includeInactive));
            }

            var distinct = hosts.Distinct();
            return (sorted ? distinct.OrderBy(h => ObjectsHelper.GetGameObjectPath(h.gameObject), StringComparer.Ordinal) : distinct).ToList();
        }

        /// <summary>uGUI root canvases in loaded scenes, for per-scene framework classification.</summary>
        internal static Canvas[] FindRootCanvases() =>
            SceneObjectLookup.FindAll<Canvas>(true).Where(c => c.isRootCanvas && !EditorUtility.IsPersistent(c)).ToArray();

        private static IEnumerable<Type> HostTypes()
        {
            if (PanelRendererType != null) yield return PanelRendererType;
            yield return typeof(UIDocument);
        }

        internal static PanelSettings GetPanelSettings(Component host) =>
            host is UIDocument document ? document.panelSettings : GetProperty(host, RendererPanelSettings) as PanelSettings;

        internal static void SetPanelSettings(Component host, PanelSettings settings)
        {
            if (host is UIDocument document) document.panelSettings = settings;
            else SetProperty(host, RendererPanelSettings, settings);
        }

        internal static VisualTreeAsset GetVisualTreeAsset(Component host) =>
            host is UIDocument document ? document.visualTreeAsset : GetProperty(host, RendererVisualTreeAsset) as VisualTreeAsset;

        internal static void SetVisualTreeAsset(Component host, VisualTreeAsset asset)
        {
            if (host is UIDocument document) document.visualTreeAsset = asset;
            else SetProperty(host, RendererVisualTreeAsset, asset);
        }

        internal static Component GetParentUI(Component host) =>
            host is UIDocument document ? document.parentUI : GetProperty(host, RendererParentUI) as Component;

        /// <summary>UIDocument has a float sortingOrder; PanelRenderer sorts as a Renderer.</summary>
        internal static float GetSortingOrder(Component host) =>
            host is UIDocument document ? document.sortingOrder : host is Renderer renderer ? renderer.sortingOrder : 0f;

        internal static void SetSortingOrder(Component host, float order)
        {
            if (host is UIDocument document) document.sortingOrder = order;
            else if (host is Renderer renderer) renderer.sortingOrder = Mathf.RoundToInt(order);
        }

        /// <summary>Live root element; null until the host has built its panel content.</summary>
        internal static VisualElement GetRoot(Component host)
        {
            if (host == null) return null;
            if (host is UIDocument document) return document.rootVisualElement;
            return GetProperty(host, RendererRoot) as VisualElement;
        }

        internal static bool IsEnabled(Component host) =>
            host is Behaviour behaviour ? behaviour.enabled : !(host is Renderer renderer) || renderer.enabled;

        private static object GetProperty(Component host, PropertyInfo property) =>
            host != null && property != null ? property.GetValue(host, null) : null;

        private static void SetProperty(Component host, PropertyInfo property, object value)
        {
            if (property == null || !property.CanWrite)
                throw new InvalidOperationException($"{host.GetType().Name} has no writable {property?.Name ?? "property"} in this Unity version.");
            property.SetValue(host, value, null);
        }

        // ---- PanelSettings -----------------------------------------------------------------

        /// <summary>screen_space_overlay, world_space or render_texture (targetTexture set).</summary>
        internal static string GetRenderMode(PanelSettings settings)
        {
            if (settings == null) return null;
            if (settings.targetTexture != null) return "render_texture";
            var value = RenderModeProperty?.GetValue(settings, null);
            return value == null ? "screen_space_overlay" : ToolRegistry.ToSnakeCase(value.ToString());
        }

        internal static bool IsWorldSpace(PanelSettings settings) => GetRenderMode(settings) == "world_space";

        /// <summary>Validates a render mode without touching any asset; null means "leave unchanged" (overlay without the property).</summary>
        internal static object ParseRenderMode(string mode)
        {
            var property = RenderModeProperty;
            if (property == null)
            {
                if (mode == "screen_space_overlay") return null;
                throw new ArgumentException("PanelSettings.renderMode is not available in this Unity version; only screen_space_overlay is supported.");
            }

            var enumName = ToolRegistry.ToPascalCase(mode);
            if (!Enum.GetNames(property.PropertyType).Contains(enumName))
                throw new ArgumentException($"render_mode must be one of: {string.Join(", ", Enum.GetNames(property.PropertyType).Select(ToolRegistry.ToSnakeCase))}.");
            return Enum.Parse(property.PropertyType, enumName);
        }

        internal static void SetRenderMode(PanelSettings settings, string mode)
        {
            var value = ParseRenderMode(mode);
            if (value != null) RenderModeProperty.SetValue(settings, value, null);
        }

        internal static object DescribePanelSettings(PanelSettings settings)
        {
            if (settings == null) return null;
            return new
            {
                path = AssetDatabase.GetAssetPath(settings),
                render_mode = GetRenderMode(settings),
                scale_mode = ToolRegistry.ToSnakeCase(settings.scaleMode.ToString()),
                reference_resolution = settings.scaleMode == PanelScaleMode.ScaleWithScreenSize
                    ? new { width = settings.referenceResolution.x, height = settings.referenceResolution.y } : null,
                match = settings.scaleMode == PanelScaleMode.ScaleWithScreenSize ? (float?)settings.match : null,
                sorting_order = settings.sortingOrder,
                target_display = settings.targetDisplay,
                theme_style_sheet = settings.themeStyleSheet != null ? AssetDatabase.GetAssetPath(settings.themeStyleSheet) : null
            };
        }

        /// <summary>Sorted project asset paths under Assets/ for an AssetDatabase type filter.</summary>
        internal static IEnumerable<string> FindAssetPaths(string typeName) =>
            AssetDatabase.FindAssets("t:" + typeName, new[] { "Assets" }).Select(AssetDatabase.GUIDToAssetPath)
                .OrderBy(path => path, StringComparer.Ordinal);

        internal static string[] FindPanelSettingsPaths(int limit = 200) =>
            FindAssetPaths("PanelSettings").Where(path => AssetDatabase.LoadAssetAtPath<PanelSettings>(path) != null).Take(limit).ToArray();

        internal static string[] FindThemePaths(int limit = 50) => FindAssetPaths("ThemeStyleSheet").Take(limit).ToArray();

        /// <summary>
        /// Mirrors Unity's own Create > UI Toolkit > Panel Settings Asset: an explicit theme, else
        /// the project's default runtime theme, else the first ThemeStyleSheet in Assets, else a new
        /// default theme file at <see cref="DefaultThemePath"/>. Reports every asset it creates.
        /// </summary>
        internal static PanelSettings CreatePanelSettingsAsset(string path, string themePath, List<string> createdAssets)
        {
            path = NormalizeAssetPath(path, ".asset");
            if (AssetDatabase.LoadMainAssetAtPath(path) != null || File.Exists(path))
                throw new UIToolkitException("ASSET_ALREADY_EXISTS", $"An asset already exists at {path}; pass its path as panel_settings instead.");

            var theme = ResolveTheme(themePath, createdAssets);
            var settings = ScriptableObject.CreateInstance<PanelSettings>();
            settings.themeStyleSheet = theme;
            // Editor-only internal step Unity's menu performs so text shaping has ICU data.
            typeof(PanelSettings).GetMethod("AssignICUData", InstanceMembers, null, Type.EmptyTypes, null)?.Invoke(settings, null);
            EnsureFolderFor(path);
            AssetDatabase.CreateAsset(settings, path);
            AssetDatabase.SaveAssets();
            createdAssets.Add(path);
            return AssetDatabase.LoadAssetAtPath<PanelSettings>(path);
        }

        private static ThemeStyleSheet ResolveTheme(string themePath, List<string> createdAssets)
        {
            if (!string.IsNullOrEmpty(themePath))
            {
                var explicitTheme = AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(themePath);
                if (explicitTheme == null)
                    throw new UIToolkitException("THEME_NOT_FOUND", "theme_style_sheet must be the path of a .tss ThemeStyleSheet asset.");
                return explicitTheme;
            }

            var settingsType = LoadedAssemblies.FindType("UnityEditor.UIElements.UIToolkitProjectSettings", typeof(EditorWindow).Assembly);
            if (settingsType?.GetProperty("defaultRuntimeTheme", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null, null) is ThemeStyleSheet projectDefault)
                return projectDefault;

            var existing = FindThemePaths(1).FirstOrDefault();
            if (existing != null)
                return AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(existing);

            EnsureFolderFor(DefaultThemePath);
            File.WriteAllText(DefaultThemePath, "@import url(\"unity-theme://default\");");
            AssetDatabase.ImportAsset(DefaultThemePath, ImportAssetOptions.ForceSynchronousImport);
            createdAssets.Add(DefaultThemePath);
            return AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(DefaultThemePath);
        }

        internal static string NormalizeAssetPath(string path, string extension)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("An asset path is required.");
            path = path.Trim().Replace('\\', '/');
            UIAuditService.ValidateAssetPath(path);
            if (!path.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) path += extension;
            return path;
        }

        private static void EnsureFolderFor(string assetPath) => EnsureFolder(Path.GetDirectoryName(assetPath)?.Replace('\\', '/'));

        private static void EnsureFolder(string folder)
        {
            if (string.IsNullOrEmpty(folder) || AssetDatabase.IsValidFolder(folder)) return;
            var parent = Path.GetDirectoryName(folder)?.Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(folder));
        }

        // ---- Live panels -------------------------------------------------------------------

        /// <summary>
        /// Bring styles and layout up to date before reading geometry. Runtime panels normally do
        /// this when the Game View repaints, which never happens in batch mode or while the Game
        /// View is hidden; these are the same internal steps that repaint runs.
        /// </summary>
        internal static void ValidateLayout(IPanel panel)
        {
            if (panel == null) return;
            try
            {
                InvokeIfPresent(panel, "ApplyStyles");
                InvokeIfPresent(panel, "ValidateLayout");
            }
            catch (TargetInvocationException)
            {
                // Inspection stays best-effort; unresolved layout is reported as layout_ready=false.
            }
        }

        internal static bool HasLayout(VisualElement element)
        {
            var bound = element.worldBound;
            return !float.IsNaN(bound.x) && !float.IsNaN(bound.y) && !float.IsNaN(bound.width) && !float.IsNaN(bound.height);
        }

        /// <summary>Linear map between UI Toolkit screen space (pixels, top-left) and panel space.</summary>
        internal struct ScreenMapping
        {
            public Vector2 Origin;
            public Vector2 Scale;
            public Vector2 ToPanel(Vector2 screenTopLeft) => new Vector2(Origin.x + screenTopLeft.x * Scale.x, Origin.y + screenTopLeft.y * Scale.y);
            public Vector2 ToScreen(Vector2 panel) => new Vector2((panel.x - Origin.x) / Scale.x, (panel.y - Origin.y) / Scale.y);
        }

        internal static bool TryGetScreenMapping(IPanel panel, out ScreenMapping mapping)
        {
            mapping = default;
            if (panel == null || panel.contextType != ContextType.Player) return false;
            try
            {
                var p0 = RuntimePanelUtils.ScreenToPanel(panel, Vector2.zero);
                var p1 = RuntimePanelUtils.ScreenToPanel(panel, new Vector2(100f, 100f));
                var scale = (p1 - p0) / 100f;
                if (!(scale.x > 0f) || !(scale.y > 0f) || float.IsInfinity(scale.x) || float.IsInfinity(scale.y)) return false;
                mapping = new ScreenMapping { Origin = p0, Scale = scale };
                return true;
            }
            catch (InvalidCastException)
            {
                return false;
            }
        }

        /// <summary>One entry per live runtime panel, topmost first (PanelSettings.sortingOrder).</summary>
        internal sealed class LivePanel
        {
            public IPanel Panel;
            public PanelSettings Settings;
            public List<Component> Hosts = new List<Component>();
            public bool WorldSpace;
        }

        internal static List<LivePanel> GetLivePanels(IEnumerable<Component> hosts)
        {
            var panels = new List<LivePanel>();
            foreach (var host in hosts)
            {
                if (host == null || !host.gameObject.activeInHierarchy || !IsEnabled(host)) continue;
                var root = GetRoot(host);
                if (root?.panel == null) continue;
                var entry = panels.FirstOrDefault(p => ReferenceEquals(p.Panel, root.panel));
                if (entry == null)
                {
                    var settings = GetPanelSettings(host);
                    entry = new LivePanel { Panel = root.panel, Settings = settings, WorldSpace = IsWorldSpace(settings) };
                    panels.Add(entry);
                }
                entry.Hosts.Add(host);
            }

            return panels.OrderByDescending(p => p.Settings != null ? p.Settings.sortingOrder : 0f).ToList();
        }

        /// <summary>The host whose root contains <paramref name="element"/>.</summary>
        internal static Component FindOwningHost(VisualElement element, IEnumerable<Component> hosts)
        {
            var roots = hosts.Select(h => new { host = h, root = GetRoot(h) }).Where(x => x.root != null).ToList();
            for (var current = element; current != null; current = current.hierarchy.parent)
            {
                var match = roots.FirstOrDefault(x => ReferenceEquals(x.root, current));
                if (match != null) return match.host;
            }
            return null;
        }

        // ---- Element queries ---------------------------------------------------------------

        /// <summary>
        /// Selector: compound selectors <c>Type#name.class1.class2</c> joined by descendant (space) or
        /// child (<c>&gt;</c>) combinators, e.g. <c>#pg-settings Button</c> or <c>.pg-setting &gt; Button</c>.
        /// A compound may end with <c>:nth-of-type(n)</c> or <c>:nth-child(n)</c> (1-based, among the
        /// parent's hierarchy children). The type matches the element's own short (unqualified) type name
        /// or any base type's, so TextElement matches Label. '.' always starts a class, so a
        /// namespace-qualified type such as the full names get_visual_tree reports for custom elements
        /// must be given by its short name. The fields describe the rightmost (subject) compound.
        /// </summary>
        internal sealed class Selector
        {
            public string Type, Name;
            public List<string> Classes = new List<string>();
            public string Text;
            public int NthOfType, NthChild;

            /// <summary>Compound to the left of this one, and whether it must be the direct parent.</summary>
            private Selector _ancestor;
            private bool _ancestorIsParent;

            private const string SupportedSyntax =
                "Supported: Type#name.class compounds joined by spaces (descendant) or '>' (child), each optionally ending in :nth-of-type(n) or :nth-child(n).";

            public static Selector Parse(string selector, string text)
            {
                var parsed = new Selector { Text = string.IsNullOrEmpty(text) ? null : text };
                if (string.IsNullOrWhiteSpace(selector)) return parsed;
                selector = selector.Trim();
                if (selector.IndexOfAny(new[] { ',', '[' }) >= 0)
                    throw new UIToolkitException("UNSUPPORTED_SELECTOR", "Selector lists (',') and attribute selectors ('[') are not supported. " + SupportedSyntax);

                // Split into compounds and combinators: whitespace = descendant, '>' = child.
                var compounds = new List<string>();
                var parentLinks = new List<bool>();
                var current = new System.Text.StringBuilder();
                var pendingChild = false;
                var parenDepth = 0;
                void Flush()
                {
                    if (current.Length == 0) return;
                    if (compounds.Count > 0) parentLinks.Add(pendingChild);
                    else if (pendingChild) throw new UIToolkitException("UNSUPPORTED_SELECTOR", "Selector cannot start with '>'. " + SupportedSyntax);
                    compounds.Add(current.ToString());
                    current.Clear();
                    pendingChild = false;
                }
                foreach (var c in selector)
                {
                    if (c == '(') parenDepth++;
                    if (c == ')') parenDepth--;
                    if (parenDepth == 0 && (char.IsWhiteSpace(c) || c == '>'))
                    {
                        Flush();
                        if (c == '>')
                        {
                            if (pendingChild) throw new UIToolkitException("UNSUPPORTED_SELECTOR", "Repeated '>' combinator. " + SupportedSyntax);
                            pendingChild = true;
                        }
                        continue;
                    }
                    current.Append(c);
                }
                if (pendingChild && current.Length == 0)
                    throw new UIToolkitException("UNSUPPORTED_SELECTOR", "Selector cannot end with '>'. " + SupportedSyntax);
                Flush();

                Selector previous = null;
                for (var index = 0; index < compounds.Count; index++)
                {
                    var compound = index == compounds.Count - 1 ? parsed : new Selector();
                    ParseCompound(compounds[index], compound);
                    if (previous != null)
                    {
                        compound._ancestor = previous;
                        compound._ancestorIsParent = parentLinks[index - 1];
                    }
                    previous = compound;
                }
                return parsed;
            }

            /// <summary>Parses one compound (<c>Type#name.class:nth-of-type(n)</c>, no combinators); used for name-path segments.</summary>
            internal static Selector ParseCompound(string compound)
            {
                if (string.IsNullOrWhiteSpace(compound) || compound.Trim().IndexOfAny(new[] { ' ', '>', ',', '[' }) >= 0)
                    throw new UIToolkitException("UNSUPPORTED_SELECTOR", $"'{compound}' is not a single compound selector (Type#name.class:nth-of-type(n)).");
                var parsed = new Selector();
                ParseCompound(compound.Trim(), parsed);
                return parsed;
            }

            private static void ParseCompound(string compound, Selector parsed)
            {
                var i = 0;
                string ReadToken()
                {
                    var start = i;
                    while (i < compound.Length && compound[i] != '#' && compound[i] != '.' && compound[i] != ':') i++;
                    return compound.Substring(start, i - start);
                }

                parsed.Type = ReadToken();
                if (parsed.Type.Length == 0) parsed.Type = null;
                while (i < compound.Length)
                {
                    var marker = compound[i++];
                    if (marker == ':')
                    {
                        ParsePseudo(compound.Substring(i), parsed);
                        return;
                    }
                    var token = ReadToken();
                    if (token.Length == 0) throw new UIToolkitException("UNSUPPORTED_SELECTOR", "Empty name or class in selector.");
                    if (marker == '#')
                    {
                        if (parsed.Name != null) throw new UIToolkitException("UNSUPPORTED_SELECTOR", "Selector has more than one #name.");
                        parsed.Name = token;
                    }
                    else parsed.Classes.Add(token);
                }
            }

            private static void ParsePseudo(string pseudo, Selector parsed)
            {
                var match = System.Text.RegularExpressions.Regex.Match(pseudo, @"^(nth-of-type|nth-child)\(\s*(\d+)\s*\)$");
                if (!match.Success || !int.TryParse(match.Groups[2].Value, out var n) || n < 1)
                    throw new UIToolkitException("UNSUPPORTED_SELECTOR", $"Unsupported pseudo-class ':{pseudo}'. " + SupportedSyntax);
                if (match.Groups[1].Value == "nth-of-type") parsed.NthOfType = n;
                else parsed.NthChild = n;
            }

            public bool IsEmpty => Type == null && Name == null && Classes.Count == 0 && Text == null && NthOfType == 0 && NthChild == 0 && _ancestor == null;

            public bool Matches(VisualElement element) =>
                MatchesExceptText(element) && (Text == null || string.Equals(GetText(element), Text, StringComparison.OrdinalIgnoreCase));

            /// <summary>
            /// The element a click query should act on, or null when <paramref name="element"/> does not
            /// match. A text match on a non-control child of a click receiver (the Label inside a Button)
            /// resolves to that receiver when it satisfies the rest of the selector, so the pair counts once.
            /// </summary>
            public VisualElement ClickTarget(VisualElement element)
            {
                if (Text != null && string.Equals(GetText(element), Text, StringComparison.OrdinalIgnoreCase))
                {
                    var receiver = LabelClickReceiver(element);
                    if (receiver != null && MatchesExceptText(receiver)) return receiver;
                }
                return Matches(element) ? element : null;
            }

            private bool MatchesExceptText(VisualElement element) => MatchesCompound(element) && MatchesAncestors(element);

            private bool MatchesAncestors(VisualElement element)
            {
                if (_ancestor == null) return true;
                if (_ancestorIsParent)
                {
                    var parent = element.hierarchy.parent;
                    return parent != null && _ancestor.MatchesCompound(parent) && _ancestor.MatchesAncestors(parent);
                }
                for (var parent = element.hierarchy.parent; parent != null; parent = parent.hierarchy.parent)
                    if (_ancestor.MatchesCompound(parent) && _ancestor.MatchesAncestors(parent)) return true;
                return false;
            }

            /// <summary>This compound alone (type, name, classes, position), ignoring text and ancestors.</summary>
            internal bool MatchesCompound(VisualElement element)
            {
                if (Name != null && element.name != Name) return false;
                foreach (var cls in Classes)
                    if (!element.ClassListContains(cls)) return false;
                if (Type != null)
                {
                    var matched = false;
                    for (var t = element.GetType(); t != null && t != typeof(object); t = t.BaseType)
                        if (t.Name == Type) { matched = true; break; }
                    if (!matched) return false;
                }
                if (NthChild > 0 || NthOfType > 0)
                {
                    var parent = element.hierarchy.parent;
                    if (parent == null) return false;
                    int childIndex = 0, typeIndex = 0;
                    for (var i = 0; i < parent.hierarchy.childCount; i++)
                    {
                        var sibling = parent.hierarchy[i];
                        childIndex++;
                        if (sibling.GetType() == element.GetType()) typeIndex++;
                        if (ReferenceEquals(sibling, element)) break;
                    }
                    if (NthChild > 0 && childIndex != NthChild) return false;
                    if (NthOfType > 0 && typeIndex != NthOfType) return false;
                }
                return true;
            }
        }

        /// <summary>
        /// Depth-first walk over the real hierarchy (not contentContainer), with index paths.
        /// Subtrees rooted at an <paramref name="exclude"/> element (nested hosts' roots, see
        /// <see cref="HostRoots"/>) are skipped so each element is reported under one host.
        /// </summary>
        internal static IEnumerable<KeyValuePair<string, VisualElement>> Walk(VisualElement root, int maxDepth = int.MaxValue, ISet<VisualElement> exclude = null)
        {
            var stack = new Stack<(VisualElement element, string path, int depth)>();
            stack.Push((root, "", 0));
            while (stack.Count > 0)
            {
                var (element, path, depth) = stack.Pop();
                yield return new KeyValuePair<string, VisualElement>(path, element);
                if (depth >= maxDepth) continue;
                for (var i = element.hierarchy.childCount - 1; i >= 0; i--)
                {
                    var child = element.hierarchy[i];
                    if (exclude != null && exclude.Contains(child)) continue;
                    stack.Push((child, path.Length == 0 ? i.ToString() : path + "/" + i, depth + 1));
                }
            }
        }

        /// <summary>Element count of <see cref="Walk"/> without building paths.</summary>
        internal static int CountElements(VisualElement root, ISet<VisualElement> exclude = null)
        {
            var count = 0;
            var stack = new Stack<VisualElement>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var element = stack.Pop();
                count++;
                for (var i = element.hierarchy.childCount - 1; i >= 0; i--)
                {
                    var child = element.hierarchy[i];
                    if (exclude == null || !exclude.Contains(child)) stack.Push(child);
                }
            }
            return count;
        }

        /// <summary>
        /// Built roots of <paramref name="hosts"/>. A nested host's root sits inside its parent
        /// host's tree; passing this set as Walk's exclude lists that subtree only under the nested host.
        /// </summary>
        internal static HashSet<VisualElement> HostRoots(IEnumerable<Component> hosts) =>
            new HashSet<VisualElement>(hosts.Select(GetRoot).Where(r => r != null));

        /// <summary>
        /// Resolves an element path relative to <paramref name="root"/>. Each '/' segment is either a
        /// child index (as get_visual_tree reports) or a compound selector matched against the direct
        /// hierarchy children (e.g. <c>#pg-settings/Button:nth-of-type(2)</c>), which survives sibling
        /// reordering. Returns null when a segment matches nothing; throws AMBIGUOUS_ELEMENT when a
        /// selector segment matches several children.
        /// </summary>
        internal static VisualElement ResolvePath(VisualElement root, string path)
        {
            if (string.IsNullOrEmpty(path)) return root;
            var current = root;
            foreach (var part in path.Split('/'))
            {
                if (int.TryParse(part, out var index))
                {
                    if (index < 0 || index >= current.hierarchy.childCount) return null;
                    current = current.hierarchy[index];
                    continue;
                }

                var segment = Selector.ParseCompound(part);
                VisualElement match = null;
                for (var i = 0; i < current.hierarchy.childCount; i++)
                {
                    var child = current.hierarchy[i];
                    if (!segment.MatchesCompound(child)) continue;
                    if (match != null)
                        throw new UIToolkitException("AMBIGUOUS_ELEMENT", $"Path segment '{part}' matches more than one child; add a class, #name or :nth-of-type(n).");
                    match = child;
                }
                if (match == null) return null;
                current = match;
            }
            return current;
        }

        /// <summary>True when every segment is a child index (the form get_visual_tree reports, which shifts when siblings move).</summary>
        internal static bool IsIndexPath(string path) =>
            !string.IsNullOrEmpty(path) && path.Split('/').All(part => int.TryParse(part, out _));

        internal static string PathOf(VisualElement root, VisualElement element)
        {
            var parts = new List<string>();
            for (var current = element; current != null && !ReferenceEquals(current, root); current = current.hierarchy.parent)
            {
                var parent = current.hierarchy.parent;
                if (parent == null) return null;
                parts.Add(parent.hierarchy.IndexOf(current).ToString());
            }
            parts.Reverse();
            return string.Join("/", parts);
        }

        internal static string GetText(VisualElement element)
        {
            if (element is TextElement textElement) return textElement.text;
            if (element is BaseField<string> stringField) return stringField.value;
            return null;
        }

        internal static bool IsDisplayed(VisualElement element)
        {
            for (var current = element; current != null; current = current.hierarchy.parent)
                if (current.resolvedStyle.display == DisplayStyle.None) return false;
            return element.visible;
        }

        /// <summary>
        /// <see cref="IsDisplayed"/> for a whole-tree pass: each ancestor's display is resolved once,
        /// so checking every walked element stays linear instead of O(n * depth).
        /// </summary>
        internal sealed class DisplayCache
        {
            private readonly Dictionary<VisualElement, bool> _chainShown = new Dictionary<VisualElement, bool>();

            public bool IsDisplayed(VisualElement element) => ChainShown(element) && element.visible;

            private bool ChainShown(VisualElement element)
            {
                if (element == null) return true;
                if (_chainShown.TryGetValue(element, out var shown)) return shown;
                shown = element.resolvedStyle.display != DisplayStyle.None && ChainShown(element.hierarchy.parent);
                _chainShown[element] = shown;
                return shown;
            }
        }

        // ---- Clipping ----------------------------------------------------------------------

        // Internal on every supported version; resolvedStyle exposes no overflow.
        private static readonly MethodInfo ShouldClipMethod = typeof(VisualElement).GetMethod("ShouldClip", InstanceMembers, null, Type.EmptyTypes, null);

        /// <summary>Whether the element clips its children (overflow: hidden, such as a ScrollView viewport).</summary>
        internal static bool ClipsChildren(VisualElement element)
        {
            if (ShouldClipMethod != null) return (bool)ShouldClipMethod.Invoke(element, null);
            return element.style.overflow.value == Overflow.Hidden || element.ClassListContains(ScrollView.viewportUssClassName);
        }

        /// <summary>The part of an element left after ancestor clipping and the panel's own area.</summary>
        internal struct Visibility
        {
            public Rect Visible;
            /// <summary>Visible area / element area; 0 when fully clipped.</summary>
            public float Fraction;
            /// <summary>Nearest clipping ancestor that cuts the element (the panel root when only the panel does); null when fully visible.</summary>
            public VisualElement ClippedBy;
            /// <summary>Nearest ScrollView around the element, which can scroll it into view.</summary>
            public ScrollView ScrollView;
            public bool FullyVisible => Fraction >= 0.999f;
        }

        /// <summary>
        /// Ancestor clipping for a whole-tree pass: the clip rectangle each element imposes on its
        /// children is computed once, like <see cref="DisplayCache"/>. Requires validated layout.
        /// </summary>
        internal sealed class ClipCache
        {
            private readonly Dictionary<VisualElement, Rect> _childClip = new Dictionary<VisualElement, Rect>();
            private readonly Dictionary<VisualElement, Visibility> _visibility = new Dictionary<VisualElement, Visibility>();
            private readonly Dictionary<VisualElement, ScrollView> _scrollView = new Dictionary<VisualElement, ScrollView>();
            private readonly Dictionary<VisualElement, ScrollView> _ancestorScrollView = new Dictionary<VisualElement, ScrollView>();

            public Visibility Of(VisualElement element)
            {
                if (_visibility.TryGetValue(element, out var cached)) return cached;
                var bound = element.worldBound;
                var parent = element.hierarchy.parent;
                var clip = parent != null ? ChildClip(parent) : new Rect(float.NaN, float.NaN, float.NaN, float.NaN);
                var visible = Intersect(bound, clip);
                var area = bound.width * bound.height;
                var fraction = area > 0f ? visible.width * visible.height / area : float.IsNaN(clip.width) || clip.Contains(bound.position) ? 1f : 0f;
                var result = new Visibility { Visible = visible, Fraction = Mathf.Clamp01(fraction), ScrollView = NearestScrollView(element) };
                for (var current = parent; current != null && !result.FullyVisible; current = current.hierarchy.parent)
                {
                    if (current.hierarchy.parent == null || ClipsChildren(current) && !ContainsRect(current.worldBound, bound))
                    {
                        result.ClippedBy = current;
                        break;
                    }
                }
                _visibility[element] = result;
                return result;
            }

            // Nearest ScrollView whose content holds the element, memoized per element: either the
            // parent is a ScrollView's contentContainer, or the answer is the parent's.
            private ScrollView NearestScrollView(VisualElement element)
            {
                var parent = element.hierarchy.parent;
                if (parent == null) return null;
                if (_scrollView.TryGetValue(element, out var cached)) return cached;
                var ancestor = AncestorScrollView(parent);
                var result = ancestor != null && ReferenceEquals(ancestor.contentContainer, parent) ? ancestor : NearestScrollView(parent);
                _scrollView[element] = result;
                return result;
            }

            // Nearest ScrollView above the element, whether or not the element is in its content.
            private ScrollView AncestorScrollView(VisualElement element)
            {
                var parent = element.hierarchy.parent;
                if (parent == null) return null;
                if (_ancestorScrollView.TryGetValue(element, out var cached)) return cached;
                var result = parent as ScrollView ?? AncestorScrollView(parent);
                _ancestorScrollView[element] = result;
                return result;
            }

            private Rect ChildClip(VisualElement element)
            {
                if (_childClip.TryGetValue(element, out var clip)) return clip;
                var parent = element.hierarchy.parent;
                if (parent == null)
                {
                    // The panel's own area; a panel not laid out yet (no Game View) clips nothing.
                    clip = element.worldBound;
                    if (!(clip.width > 0f && clip.height > 0f)) clip = new Rect(float.NaN, float.NaN, float.NaN, float.NaN);
                }
                else
                {
                    clip = ChildClip(parent);
                    if (ClipsChildren(element)) clip = Intersect(clip, element.worldBound);
                }
                _childClip[element] = clip;
                return clip;
            }
        }

        internal static Visibility GetVisibility(VisualElement element) => new ClipCache().Of(element);

        /// <summary>Overlap of two rectangles, zero-sized when disjoint; a rectangle without layout (NaN) does not clip.</summary>
        private static Rect Intersect(Rect a, Rect b)
        {
            if (float.IsNaN(b.x) || float.IsNaN(b.width)) return a;
            if (float.IsNaN(a.x) || float.IsNaN(a.width)) return b;
            float xMin = Mathf.Max(a.xMin, b.xMin), yMin = Mathf.Max(a.yMin, b.yMin);
            float xMax = Mathf.Min(a.xMax, b.xMax), yMax = Mathf.Min(a.yMax, b.yMax);
            return xMax <= xMin || yMax <= yMin ? new Rect(xMin, yMin, 0f, 0f) : Rect.MinMaxRect(xMin, yMin, xMax, yMax);
        }

        private static bool ContainsRect(Rect outer, Rect inner) =>
            inner.xMin >= outer.xMin - 0.5f && inner.yMin >= outer.yMin - 0.5f && inner.xMax <= outer.xMax + 0.5f && inner.yMax <= outer.yMax + 0.5f;

        /// <summary>
        /// Scrolls every ScrollView around the element, innermost first, so it lies in each viewport
        /// (ScrollView.ScrollTo). Returns each ScrollView with its offset before scrolling. ScrollTo
        /// updates the content's inline translate at once, so re-validating layout shows the result.
        /// </summary>
        internal static List<KeyValuePair<ScrollView, Vector2>> ScrollIntoView(VisualElement element)
        {
            var scrolled = new List<KeyValuePair<ScrollView, Vector2>>();
            for (var current = element.hierarchy.parent; current != null; current = current.hierarchy.parent)
            {
                if (!(current is ScrollView scrollView) || !scrollView.contentContainer.Contains(element)) continue;
                ValidateLayout(element.panel);
                scrolled.Add(new KeyValuePair<ScrollView, Vector2>(scrollView, scrollView.scrollOffset));
                scrollView.ScrollTo(element);
            }
            ValidateLayout(element.panel);
            return scrolled;
        }

        /// <summary>Built-in input controls (what audits expect to have a clickable area).</summary>
        internal static bool IsControl(VisualElement element) =>
            element is Button || element is Toggle || element is TextField || element is Slider || element is SliderInt ||
            element is DropdownField || element is Foldout;

        internal static bool IsInteractive(VisualElement element) =>
            IsControl(element) || element is ScrollView || element.focusable && element.pickingMode == PickingMode.Position;

        /// <summary>The nearest self-or-ancestor that handles clicks (Button / Clickable owner / focusable control).</summary>
        internal static VisualElement FindClickReceiver(VisualElement element)
        {
            for (var current = element; current != null; current = current.hierarchy.parent)
            {
                if (current is Button || current is Toggle || current is Foldout) return current;
                if (CachedProperty(current.GetType(), "clickable", BindingFlags.Public | BindingFlags.Instance)?.GetValue(current, null) != null) return current;
            }
            return null;
        }

        /// <summary>
        /// The click receiver a non-control element (the Label inside a Button) stands for, or null.
        /// A Foldout is excluded: its header label resolves to the header Toggle, and anything in its
        /// content is not part of the Foldout's click area.
        /// </summary>
        internal static VisualElement LabelClickReceiver(VisualElement element)
        {
            if (IsControl(element)) return null;
            var receiver = FindClickReceiver(element);
            return receiver == null || ReferenceEquals(receiver, element) || receiver is Foldout ? null : receiver;
        }

        internal static object DescribeElement(VisualElement element, string path, ScreenMapping? mapping, int renderHeight, bool includeStyles, DisplayCache displayCache = null, ClipCache clipCache = null)
        {
            var bound = element.worldBound;
            var hasLayout = HasLayout(element);
            var displayed = displayCache?.IsDisplayed(element) ?? IsDisplayed(element);
            // A hidden element's collapsed bound says nothing about clipping.
            var visibility = displayed && hasLayout && element.panel != null ? (clipCache ?? new ClipCache()).Of(element) : (Visibility?)null;
            var partlyVisible = visibility.HasValue && !visibility.Value.FullyVisible && visibility.Value.Fraction > 0f;
            object screenBounds = null;
            if (hasLayout && mapping.HasValue)
            {
                var topLeft = mapping.Value.ToScreen(new Vector2(bound.xMin, bound.yMin));
                var bottomRight = mapping.Value.ToScreen(new Vector2(bound.xMax, bound.yMax));
                var visibleCenter = partlyVisible ? mapping.Value.ToScreen(visibility.Value.Visible.center) : Vector2.zero;
                screenBounds = new
                {
                    image_top_left = new { x = topLeft.x, y = topLeft.y, width = bottomRight.x - topLeft.x, height = bottomRight.y - topLeft.y },
                    render_bottom_left = new { x = topLeft.x, y = renderHeight - bottomRight.y, width = bottomRight.x - topLeft.x, height = bottomRight.y - topLeft.y },
                    center_render_pixels = new { x = (topLeft.x + bottomRight.x) / 2f, y = renderHeight - (topLeft.y + bottomRight.y) / 2f },
                    visible_center_render_pixels = partlyVisible ? new { x = visibleCenter.x, y = renderHeight - visibleCenter.y } : null
                };
            }

            // A Label inside a Button: give the receiver's path so it can be clicked directly.
            string receiverPath = null;
            var receiver = path != null ? LabelClickReceiver(element) : null;
            if (receiver != null)
            {
                var levels = 0;
                for (var current = element; current != null && !ReferenceEquals(current, receiver); current = current.hierarchy.parent) levels++;
                var parts = path.Length == 0 ? Array.Empty<string>() : path.Split('/');
                if (levels <= parts.Length) receiverPath = string.Join("/", parts.Take(parts.Length - levels));
            }

            var style = element.resolvedStyle;
            return new
            {
                path,
                type = element.GetType().Namespace == "UnityEngine.UIElements" ? element.GetType().Name : element.GetType().FullName,
                name = string.IsNullOrEmpty(element.name) ? null : element.name,
                classes = element.GetClasses().ToArray(),
                text = GetText(element),
                value = DescribeValue(element),
                displayed,
                // Ancestor clipping (ScrollView viewport, overflow: hidden) and the panel's area; 0 when hidden.
                visible_fraction = visibility.HasValue ? (float?)(float)Math.Round(visibility.Value.Fraction, 3) : displayed ? null : (float?)0f,
                clipped_by = ClipperLabel(visibility?.ClippedBy),
                scroll_view = visibility.HasValue && !visibility.Value.FullyVisible && visibility.Value.ScrollView != null ? Label(visibility.Value.ScrollView) : null,
                enabled = element.enabledInHierarchy,
                picking_mode = element.pickingMode.ToString().ToLowerInvariant(),
                focusable = element.focusable,
                interactive = IsInteractive(element),
                click_receiver_path = receiverPath,
                child_count = element.hierarchy.childCount,
                panel_bounds = hasLayout ? new { x = bound.x, y = bound.y, width = bound.width, height = bound.height } : null,
                screen_bounds = screenBounds,
                styles = includeStyles ? new
                {
                    display = style.display.ToString().ToLowerInvariant(),
                    visibility = style.visibility.ToString().ToLowerInvariant(),
                    opacity = style.opacity,
                    position = style.position.ToString().ToLowerInvariant(),
                    flex_direction = style.flexDirection.ToString().ToLowerInvariant(),
                    width = style.width,
                    height = style.height,
                    color = "#" + ColorUtility.ToHtmlStringRGBA(style.color),
                    background_color = "#" + ColorUtility.ToHtmlStringRGBA(style.backgroundColor),
                    font_size = style.fontSize,
                    unity_text_align = style.unityTextAlign.ToString()
                } : null
            };
        }

        private static object DescribeValue(VisualElement element)
        {
            if (element is TextElement || element is BaseField<string>) return null;
            var property = CachedProperty(element.GetType(), "value", BindingFlags.Public | BindingFlags.Instance);
            if (property == null || property.GetIndexParameters().Length != 0) return null;
            var type = property.PropertyType;
            if (!(type.IsPrimitive || type.IsEnum)) return null;
            try { var value = property.GetValue(element, null); return type.IsEnum ? value?.ToString() : value; }
            catch (Exception) { return null; }
        }

        // ---- Input -------------------------------------------------------------------------

        /// <summary>What a dispatched click observably did.</summary>
        internal sealed class ClickResult
        {
            public bool ClickEventReceived;
            /// <summary>The receiver's Clickable.clicked ran, or its Toggle/Foldout value changed.</summary>
            public bool HandlerInvoked;
            /// <summary>The receiver was enabled before the sequence and disabled after it.</summary>
            public bool ReceiverDisabled;
            /// <summary>The receiver was removed from its panel during the sequence.</summary>
            public bool ReceiverDetached;

            /// <summary>
            /// Any evidence of activation. Clickable invokes clicked on pointer up, before the panel
            /// synthesizes ClickEvent; a handler that disables or removes its own button means that
            /// ClickEvent is never delivered, yet the click worked.
            /// </summary>
            public bool Activated => ClickEventReceived || HandlerInvoked || ReceiverDisabled || ReceiverDetached;
        }

        /// <summary>
        /// Send a mouse move/down/up sequence through the panel dispatcher, the same path real
        /// mouse input takes: Clickable manipulators, :hover/:active state, pointer capture and the
        /// panel's ClickEvent synthesis all run.
        /// </summary>
        internal static ClickResult DispatchClick(IPanel panel, VisualElement target, Vector2 panelPosition, int button)
        {
            var result = new ClickResult();
            var receiver = FindClickReceiver(target);
            var receiverWasEnabled = receiver != null && receiver.enabledInHierarchy && receiver.panel != null;
            var clickable = receiver != null ? CachedProperty(receiver.GetType(), "clickable", BindingFlags.Public | BindingFlags.Instance)?.GetValue(receiver, null) as Clickable : null;
            EventCallback<ClickEvent> onClick = _ => result.ClickEventReceived = true;
            Action onClicked = () => result.HandlerInvoked = true;
            EventCallback<ChangeEvent<bool>> onToggled = _ => result.HandlerInvoked = true;
            target.RegisterCallback(onClick, TrickleDown.TrickleDown);
            if (clickable != null) clickable.clicked += onClicked;
            else receiver?.RegisterCallback(onToggled);
            try
            {
                Send(panel, target, EventType.MouseMove, panelPosition, -1);
                Send(panel, target, EventType.MouseDown, panelPosition, button);
                Send(panel, target, EventType.MouseUp, panelPosition, button);
            }
            finally
            {
                target.UnregisterCallback(onClick, TrickleDown.TrickleDown);
                if (clickable != null) clickable.clicked -= onClicked;
                else receiver?.UnregisterCallback(onToggled);
            }

            if (receiverWasEnabled)
            {
                result.ReceiverDetached = receiver.panel == null;
                result.ReceiverDisabled = !result.ReceiverDetached && !receiver.enabledInHierarchy;
            }
            return result;
        }

        private static void Send(IPanel panel, VisualElement target, EventType type, Vector2 position, int button) =>
            SendPointer(panel, target, type, position, button, UnityEngine.PointerType.Mouse);

        /// <summary>
        /// One pointer event through the panel dispatcher, built from an IMGUI event like real input
        /// (MouseDrag for moves while a button is held, as IMGUI reports them).
        /// Returns the event's pointerId (touch input maps to a touch pointer ID, mouse to PointerId.mousePointerId).
        /// </summary>
        internal static int SendPointer(IPanel panel, VisualElement target, EventType type, Vector2 position, int button, UnityEngine.PointerType pointerType, Vector2 delta = default)
        {
            var system = new Event { type = type, mousePosition = position, delta = delta, button = Mathf.Max(0, button), clickCount = type == EventType.MouseMove || type == EventType.MouseDrag ? 0 : 1, pointerType = pointerType };
            EventBase evt = type == EventType.MouseDown ? PointerDownEvent.GetPooled(system)
                : type == EventType.MouseUp ? (EventBase)PointerUpEvent.GetPooled(system)
                : PointerMoveEvent.GetPooled(system);
            try
            {
                evt.target = target;
                // Internal flag real input sets so the panel refreshes its element-under-pointer
                // state (hover, enter/leave). Declared per closed PointerEventBase<T>.
                var recompute = evt.GetType().BaseType != null ? CachedProperty(evt.GetType().BaseType, "recomputeTopElementUnderPointer", InstanceMembers) : null;
                if (recompute != null && recompute.CanWrite) recompute.SetValue(evt, true, null);
                panel.visualTree.SendEvent(evt);
                return ((IPointerEvent)evt).pointerId;
            }
            finally
            {
                evt.Dispose();
            }
        }

        // Internal; the :hover/:active/:focus/:checked state the style system matches against.
        private static readonly PropertyInfo PseudoStatesProperty = typeof(VisualElement).GetProperty("pseudoStates", InstanceMembers);

        /// <summary>The element's current pseudo-states in lower case (hover, active, focus, checked, disabled…), or null when unreadable.</summary>
        internal static string[] PseudoStates(VisualElement element)
        {
            var value = PseudoStatesProperty?.GetValue(element, null);
            if (value == null) return null;
            return value.ToString().Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(s => s != "None" && s != "0").Select(s => s.ToLowerInvariant()).ToArray();
        }

        // ---- Shared ------------------------------------------------------------------------

        internal static void MarkHostModified(Component host)
        {
            EditorUtility.SetDirty(host);
            if (PrefabUtility.IsPartOfPrefabInstance(host)) PrefabUtility.RecordPrefabInstancePropertyModifications(host);
            if (host.gameObject.scene.IsValid()) EditorSceneManager.MarkSceneDirty(host.gameObject.scene);
        }

        // ---- Screen-point picking ---------------------------------------------------------

        internal sealed class PanelPick
        {
            public LivePanel Panel;
            public VisualElement Element;
            public Component Host;
            public string ElementPath;
            public Vector2 PanelPosition;
            public ScreenMapping? Mapping;
            public string Skipped;
        }

        /// <summary>Picks every live panel at a bottom-left render-pixel point, topmost panel first.</summary>
        internal static List<PanelPick> PickAt(Vector2 renderPoint, int renderHeight)
        {
            var picks = new List<PanelPick>();
            foreach (var live in GetLivePanels(FindHosts(includeInactive: false, sorted: false)))
            {
                if (live.WorldSpace) { picks.Add(new PanelPick { Panel = live, Skipped = "world_space panel; screen-point picking is not supported" }); continue; }
                if (!TryGetScreenMapping(live.Panel, out var mapping)) { picks.Add(new PanelPick { Panel = live, Skipped = "panel has no screen mapping" }); continue; }
                ValidateLayout(live.Panel);
                var panelPosition = mapping.ToPanel(new Vector2(renderPoint.x, renderHeight - renderPoint.y));
                var element = live.Panel.Pick(panelPosition);
                var host = element != null ? FindOwningHost(element, live.Hosts) : null;
                var root = host != null ? GetRoot(host) : null;
                picks.Add(new PanelPick
                {
                    Panel = live, Element = element, Host = host, PanelPosition = panelPosition, Mapping = mapping,
                    ElementPath = element != null && root != null ? PathOf(root, element) : null
                });
            }
            return picks;
        }

        /// <summary>The first panel (topmost) that has an element under the point.</summary>
        internal static PanelPick TopPick(List<PanelPick> picks) => picks.FirstOrDefault(p => p.Element != null);

        internal static object DescribePick(PanelPick pick, int renderHeight)
        {
            var settings = pick.Panel.Settings;
            var settingsPath = settings != null ? AssetDatabase.GetAssetPath(settings) : null;
            if (pick.Skipped != null)
                return new { panel_settings = settingsPath, skipped = pick.Skipped };
            var element = pick.Element;
            var receiver = element != null ? FindClickReceiver(element) : null;
            var chain = new List<string>();
            for (var current = element; current != null && chain.Count < 16; current = current.hierarchy.parent) chain.Add(Label(current));
            return new
            {
                panel_settings = settingsPath,
                panel_sorting_order = settings != null ? settings.sortingOrder : 0f,
                panel_position = new { x = pick.PanelPosition.x, y = pick.PanelPosition.y },
                host_id = pick.Host != null ? ObjectIdHelper.GetSerializableId(pick.Host.gameObject) : null,
                host_path = pick.Host != null ? ObjectsHelper.GetGameObjectPath(pick.Host.gameObject) : null,
                element = element != null ? DescribeElement(element, pick.ElementPath, pick.Mapping, renderHeight, false) : null,
                ancestors = chain,
                click_receiver = receiver != null ? Label(receiver) : null,
                swallowed_by = element != null && receiver == null
                    ? new { element = Label(element), hint = "The topmost element has no click-handling control in its parent chain; a pointer here stops at this panel. Check full-screen containers with picking-mode Position." }
                    : null
            };
        }

        /// <summary>Label of a <see cref="Visibility.ClippedBy"/> element; "panel" for the panel root.</summary>
        internal static string ClipperLabel(VisualElement clipper) =>
            clipper == null ? null : clipper.hierarchy.parent == null ? "panel" : Label(clipper);

        /// <summary>Short element label for messages: Type#name "text".</summary>
        internal static string Label(VisualElement element)
        {
            if (element == null) return null;
            var label = element.GetType().Name;
            if (!string.IsNullOrEmpty(element.name)) label += "#" + element.name;
            var text = GetText(element);
            if (!string.IsNullOrEmpty(text)) label += $" \"{(text.Length > 40 ? text.Substring(0, 40) + "…" : text)}\"";
            return label;
        }
    }

    internal sealed class UIToolkitException : Exception
    {
        public string Code { get; }
        public object Details { get; }

        public UIToolkitException(string code, string message, object details = null) : base(message)
        {
            Code = code;
            Details = details;
        }
    }
}
