// Copyright (C) Funplay. Licensed under MIT.
using System;
using System.Collections;
using System.IO;
using System.Linq;
using Funplay.Editor.Tools;
using Funplay.Editor.Tools.Builtins;
using Funplay.Editor.Tools.Helpers;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace Funplay.Editor.Tests
{
    public sealed class UIToolkitFunctionsTests
    {
        private const string Uxml =
            "<ui:UXML xmlns:ui=\"UnityEngine.UIElements\">\n" +
            "  <ui:VisualElement name=\"menu\" class=\"screen\" style=\"flex-grow: 1; align-items: center; justify-content: center;\">\n" +
            "    <ui:Label name=\"title\" text=\"Main Menu\" />\n" +
            "    <ui:Button name=\"play\" class=\"primary\" text=\"Play\" style=\"width: 200px; height: 60px;\" />\n" +
            "    <ui:Button name=\"quit\" class=\"secondary\" text=\"Quit\" style=\"width: 200px; height: 60px;\" />\n" +
            "  </ui:VisualElement>\n" +
            "</ui:UXML>\n";

        private string folder;
        private string uxmlPath, themePath, settingsPath;
        private Scene scene;
        private bool dirty;
        private Object[] selection;
        private GameObject created;

        [SetUp]
        public void SetUp()
        {
            scene = SceneManager.GetActiveScene(); dirty = scene.isDirty; selection = Selection.objects;
            folder = "Assets/FunplayUitkTests_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", Path.GetFileName(folder));
            uxmlPath = folder + "/Menu.uxml";
            themePath = folder + "/Theme.tss";
            settingsPath = folder + "/Panel.asset";
            File.WriteAllText(uxmlPath, Uxml);
            File.WriteAllText(themePath, "@import url(\"unity-theme://default\");");
            AssetDatabase.ImportAsset(uxmlPath, ImportAssetOptions.ForceSynchronousImport);
            AssetDatabase.ImportAsset(themePath, ImportAssetOptions.ForceSynchronousImport);
        }

        [TearDown]
        public void TearDown()
        {
            if (created != null) Object.DestroyImmediate(created);
            AssetDatabase.DeleteAsset(folder);
            Selection.objects = selection;
            if (!dirty) typeof(EditorSceneManager).GetMethod("ClearSceneDirtiness", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)?.Invoke(null, new object[] { scene });
        }

        [Test]
        public void ToolsAreDiscoverable()
        {
            foreach (var name in new[] { "get_ui_toolkit_info", "create_ui_toolkit_panel", "configure_ui_toolkit_panel", "create_panel_settings",
                         "get_visual_tree", "pick_ui_toolkit_element", "click_ui_toolkit_element" })
                Assert.IsNotNull(ToolRegistry.GetMethod(name), name);
        }

        [Test]
        public void SelectorParsesTypeNameAndClassesAndRejectsCombinators()
        {
            var selector = UIToolkitPanels.Selector.Parse("Button#play.primary.big", null);
            Assert.AreEqual("Button", selector.Type);
            Assert.AreEqual("play", selector.Name);
            CollectionAssert.AreEqual(new[] { "primary", "big" }, selector.Classes);
            Assert.AreEqual("UNSUPPORTED_SELECTOR", Assert.Throws<UIToolkitException>(() => UIToolkitPanels.Selector.Parse("VisualElement > Button", null)).Code);

            var button = new Button { name = "play", text = "Play" };
            button.AddToClassList("primary");
            Assert.IsTrue(UIToolkitPanels.Selector.Parse("TextElement.primary", null).Matches(button), "base types match");
            Assert.IsTrue(UIToolkitPanels.Selector.Parse(null, "play").Matches(button), "text matches case-insensitively");
            Assert.IsFalse(UIToolkitPanels.Selector.Parse("#quit", null).Matches(button));
        }

        [Test]
        public void RecommendedHostFollowsUnityVersion()
        {
            var hasPanelRenderer = typeof(UIDocument).Assembly.GetType("UnityEngine.UIElements.PanelRenderer") != null;
            Assert.AreEqual(hasPanelRenderer, UIToolkitPanels.PanelRendererAvailable);
            Assert.AreEqual(hasPanelRenderer ? UIToolkitPanels.KindPanelRenderer : UIToolkitPanels.KindUiDocument, UIToolkitPanels.RecommendedKind);
            var info = Data(UIToolkitFunctions.GetUiToolkitInfo());
            Assert.AreEqual(hasPanelRenderer ? "PanelRenderer" : "UIDocument", (string)info["recommended_component"]);
            Assert.AreEqual(hasPanelRenderer ? "legacy" : "current", (string)info["ui_document_status"]);
        }

        [Test]
        public void CreatePanelSettingsUsesExplicitThemeAndNeverOverwrites()
        {
            var response = JObject.FromObject(UIToolkitFunctions.CreatePanelSettings(settingsPath, themePath, "scale_with_screen_size", "1920,1080", "0.5", null, "3"));
            Assert.IsTrue((bool)response["success"], response.ToString());
            var settings = AssetDatabase.LoadAssetAtPath<PanelSettings>(settingsPath);
            Assert.IsNotNull(settings);
            Assert.AreEqual(themePath, AssetDatabase.GetAssetPath(settings.themeStyleSheet));
            Assert.AreEqual(PanelScaleMode.ScaleWithScreenSize, settings.scaleMode);
            Assert.AreEqual(new Vector2Int(1920, 1080), settings.referenceResolution);
            Assert.AreEqual(3f, settings.sortingOrder);
            CollectionAssert.AreEqual(new[] { settingsPath }, response["data"]["created_assets"].ToObject<string[]>());

            var again = JObject.FromObject(UIToolkitFunctions.CreatePanelSettings(settingsPath, themePath));
            Assert.AreEqual("ASSET_ALREADY_EXISTS", (string)again["code"]);
        }

        [Test]
        public void CreatePanelAssignsAssetsUsesRecommendedHostAndSupportsUndo()
        {
            CreateSettings();
            var response = JObject.FromObject(UIToolkitFunctions.CreateUiToolkitPanel("UitkMenu", uxmlPath, settingsPath, sorting_order: "2"));
            Assert.IsTrue((bool)response["success"], response.ToString());
            created = GameObject.Find("UitkMenu");
            Assert.IsNotNull(created);
            var host = UIToolkitPanels.GetHost(created);
            Assert.AreEqual(UIToolkitPanels.RecommendedKind, UIToolkitPanels.KindOf(host));
            Assert.AreEqual(settingsPath, AssetDatabase.GetAssetPath(UIToolkitPanels.GetPanelSettings(host)));
            Assert.AreEqual(uxmlPath, AssetDatabase.GetAssetPath(UIToolkitPanels.GetVisualTreeAsset(host)));
            Assert.AreEqual(2f, UIToolkitPanels.GetSortingOrder(host));
            Assert.AreEqual(uxmlPath, (string)response["data"]["host"]["visual_tree_asset"]);

            var info = Data(UIToolkitFunctions.GetUiToolkitInfo());
            Assert.IsTrue(info["hosts"].Any(h => (string)h["path"] == "/UitkMenu" || ((string)h["path"]).EndsWith("UitkMenu")), info.ToString());

            Undo.PerformUndo();
            Assert.IsTrue(created == null, "Undo removes the created host");
        }

        [Test]
        public void UiDocumentRemainsAvailableAndIsFlaggedLegacyWhenPanelRendererExists()
        {
            CreateSettings();
            var response = JObject.FromObject(UIToolkitFunctions.CreateUiToolkitPanel("UitkLegacy", uxmlPath, settingsPath, component: "ui_document"));
            Assert.IsTrue((bool)response["success"], response.ToString());
            created = GameObject.Find("UitkLegacy");
            Assert.IsInstanceOf<UIDocument>(UIToolkitPanels.GetHost(created));
            var warnings = response["data"]["warnings"].ToObject<string[]>();
            Assert.AreEqual(UIToolkitPanels.PanelRendererAvailable, warnings.Any(w => w.Contains("Legacy")));
        }

        [Test]
        public void PanelRendererRequestIsRejectedWhereUnavailable()
        {
            if (UIToolkitPanels.PanelRendererAvailable) Assert.Ignore("PanelRenderer exists in this Unity version.");
            var response = JObject.FromObject(UIToolkitFunctions.CreateUiToolkitPanel("Nope", uxmlPath, null, component: "panel_renderer"));
            Assert.AreEqual("PANEL_RENDERER_UNAVAILABLE", (string)response["code"]);
        }

        [Test]
        public void SeveralPanelSettingsRequireAnExplicitChoice()
        {
            CreateSettings();
            Assert.IsTrue((bool)JObject.FromObject(UIToolkitFunctions.CreatePanelSettings(folder + "/Second.asset", themePath))["success"]);
            var response = JObject.FromObject(UIToolkitFunctions.CreateUiToolkitPanel("UitkAmbiguous", uxmlPath));
            Assert.AreEqual("PANEL_SETTINGS_AMBIGUOUS", (string)response["code"], response.ToString());
            Assert.IsNull(GameObject.Find("UitkAmbiguous"));
        }

        [Test]
        public void ConfigureChangesAndClearsTheVisualTree()
        {
            CreateSettings();
            Assert.IsTrue((bool)JObject.FromObject(UIToolkitFunctions.CreateUiToolkitPanel("UitkConfigure", null, settingsPath))["success"]);
            created = GameObject.Find("UitkConfigure");
            var id = ObjectIdHelper.GetSerializableId(created);

            var set = JObject.FromObject(UIToolkitFunctions.ConfigureUiToolkitPanel(id, uxmlPath, null, "5"));
            Assert.IsTrue((bool)set["success"], set.ToString());
            var host = UIToolkitPanels.GetHost(created);
            Assert.AreEqual(uxmlPath, AssetDatabase.GetAssetPath(UIToolkitPanels.GetVisualTreeAsset(host)));
            Assert.AreEqual(5f, UIToolkitPanels.GetSortingOrder(host));

            var cleared = JObject.FromObject(UIToolkitFunctions.ConfigureUiToolkitPanel(id, "none"));
            Assert.IsTrue((bool)cleared["success"], cleared.ToString());
            Assert.IsNull(UIToolkitPanels.GetVisualTreeAsset(host));

            var notHost = new GameObject("NotAHost");
            try
            {
                Assert.AreEqual("NOT_A_PANEL_HOST", (string)JObject.FromObject(UIToolkitFunctions.ConfigureUiToolkitPanel(ObjectIdHelper.GetSerializableId(notHost), uxmlPath))["code"]);
            }
            finally { Object.DestroyImmediate(notHost); }
        }

        [Test]
        public void ChildOfAPanelHostNestsAndInheritsPanelSettings()
        {
            CreateSettings();
            Assert.IsTrue((bool)JObject.FromObject(UIToolkitFunctions.CreateUiToolkitPanel("UitkParent", uxmlPath, settingsPath))["success"]);
            created = GameObject.Find("UitkParent");
            var parentId = ObjectIdHelper.GetSerializableId(created);
            var withSettings = JObject.FromObject(UIToolkitFunctions.CreateUiToolkitPanel("UitkChild", uxmlPath, settingsPath, parent: parentId));
            Assert.AreEqual("NESTED_PANEL_SETTINGS", (string)withSettings["code"]);

            var nested = JObject.FromObject(UIToolkitFunctions.CreateUiToolkitPanel("UitkChild", uxmlPath, parent: parentId));
            Assert.IsTrue((bool)nested["success"], nested.ToString());
            var child = created.transform.Find("UitkChild");
            Assert.IsNotNull(child);
            Assert.AreEqual(settingsPath, AssetDatabase.GetAssetPath(UIToolkitPanels.GetPanelSettings(UIToolkitPanels.GetHost(child.gameObject))));
        }

        [Test]
        public void MixedHostKindsDoNotSilentlyNest()
        {
            if (!UIToolkitPanels.PanelRendererAvailable) Assert.Ignore("Needs both PanelRenderer and UIDocument.");
            CreateSettings();
            Assert.IsTrue((bool)JObject.FromObject(UIToolkitFunctions.CreateUiToolkitPanel("UitkRendererParent", uxmlPath, settingsPath))["success"]);
            created = GameObject.Find("UitkRendererParent");
            var response = JObject.FromObject(UIToolkitFunctions.CreateUiToolkitPanel("UitkDocChild", uxmlPath, parent: ObjectIdHelper.GetSerializableId(created), component: "ui_document"));
            Assert.AreEqual("HOST_KIND_MISMATCH", (string)response["code"], response.ToString());
            Assert.IsNull(created.transform.Find("UitkDocChild"));
        }

        [UnityTest]
        public IEnumerator LiveTreeSupportsQueriesPickingAndClicks()
        {
            CreateSettings();
            Assert.IsTrue((bool)JObject.FromObject(UIToolkitFunctions.CreateUiToolkitPanel("UitkLive", uxmlPath, settingsPath))["success"]);
            created = GameObject.Find("UitkLive");
            var host = UIToolkitPanels.GetHost(created);

            VisualElement root = null;
            for (var frame = 0; frame < 120; frame++)
            {
                root = UIToolkitPanels.GetRoot(host);
                if (root?.panel != null && root.Q<Button>("play") != null)
                {
                    UIToolkitPanels.ValidateLayout(root.panel);
                    if (UIToolkitPanels.HasLayout(root.Q<Button>("play")) && root.Q<Button>("play").worldBound.width > 0) break;
                }
                yield return null;
            }
            if (root?.panel == null || root.Q<Button>("play") == null) Assert.Ignore("The panel host did not build its tree in this Editor mode.");
            var play = root.Q<Button>("play");
            if (!UIToolkitPanels.HasLayout(play) || play.worldBound.width <= 0) Assert.Ignore("The panel was not laid out in this Editor mode.");

            var id = ObjectIdHelper.GetSerializableId(created);
            var tree = JObject.FromObject(UIToolkitFunctions.GetVisualTree(id, "Button.primary"));
            Assert.IsTrue((bool)tree["success"], tree.ToString());
            var elements = (JArray)tree["data"]["hosts"][0]["elements"];
            Assert.AreEqual(1, elements.Count, tree.ToString());
            Assert.AreEqual("Play", (string)elements[0]["text"]);
            Assert.AreEqual(UIToolkitPanels.PathOf(root, play), (string)elements[0]["path"]);

            var ambiguous = JObject.FromObject(UIToolkitFunctions.ClickElement(id, "Button", null, null, "left", false));
            Assert.AreEqual("AMBIGUOUS_ELEMENT", (string)ambiguous["code"]);

            var clicks = 0;
            play.clicked += () => clicks++;
            var click = JObject.FromObject(UIToolkitFunctions.ClickElement(id, null, "play", null, "left", false));
            Assert.IsTrue((bool)click["success"], click.ToString());
            Assert.IsTrue((bool)click["data"]["click_event_received"], click.ToString());
            Assert.AreEqual(1, clicks, "Button.clicked runs through the panel's Clickable manipulator");

            if (UIToolkitPanels.TryGetScreenMapping(root.panel, out var mapping))
            {
                const int renderHeight = 1000;
                var screen = mapping.ToScreen(play.worldBound.center);
                var pick = UIToolkitPanels.TopPick(UIToolkitPanels.PickAt(new Vector2(screen.x, renderHeight - screen.y), renderHeight));
                Assert.IsNotNull(pick);
                Assert.AreSame(play, UIToolkitPanels.FindClickReceiver(pick.Element));
                Assert.AreSame(host, pick.Host);
            }
        }

        [Test]
        public void WalkAndCountSkipExcludedNestedRoots()
        {
            var root = new VisualElement { name = "root" };
            var sibling = new Label { name = "sibling" };
            var nestedRoot = new VisualElement { name = "nested" };
            nestedRoot.Add(new Button { name = "inner" });
            root.Add(sibling);
            root.Add(nestedRoot);
            var exclude = new System.Collections.Generic.HashSet<VisualElement> { root, nestedRoot };

            var walked = UIToolkitPanels.Walk(root, exclude: exclude).ToList();
            CollectionAssert.AreEqual(new VisualElement[] { root, sibling }, walked.Select(e => e.Value).ToArray(), "the walk root itself is never excluded");
            Assert.AreEqual(2, UIToolkitPanels.CountElements(root, exclude));
            Assert.AreEqual(4, UIToolkitPanels.CountElements(root));
            Assert.AreEqual("1/0", UIToolkitPanels.Walk(root).Single(e => e.Value.name == "inner").Key, "paths are unchanged without exclusions");
            Assert.AreEqual(2, UIToolkitPanels.Walk(nestedRoot, exclude: exclude).Count(), "the nested host walks its own subtree");
        }

        [UnityTest]
        public IEnumerator NestedHostElementsAreReportedOnce()
        {
            CreateSettings();
            Assert.IsTrue((bool)JObject.FromObject(UIToolkitFunctions.CreateUiToolkitPanel("UitkOuter", uxmlPath, settingsPath))["success"]);
            created = GameObject.Find("UitkOuter");
            var parentId = ObjectIdHelper.GetSerializableId(created);
            var nested = JObject.FromObject(UIToolkitFunctions.CreateUiToolkitPanel("UitkInner", uxmlPath, parent: parentId));
            Assert.IsTrue((bool)nested["success"], nested.ToString());
            var parentHost = UIToolkitPanels.GetHost(created);
            var childHost = UIToolkitPanels.GetHost(created.transform.Find("UitkInner").gameObject);
            var childId = ObjectIdHelper.GetSerializableId(childHost.gameObject);

            VisualElement parentRoot = null, childRoot = null;
            for (var frame = 0; frame < 120; frame++)
            {
                parentRoot = UIToolkitPanels.GetRoot(parentHost);
                childRoot = UIToolkitPanels.GetRoot(childHost);
                if (parentRoot?.panel != null && childRoot?.Q<Button>("play") != null && parentRoot.Contains(childRoot)) break;
                yield return null;
            }
            if (parentRoot?.panel == null || childRoot?.Q<Button>("play") == null)
                Assert.Ignore("The panel hosts did not build their trees in this Editor mode.");
            if (!parentRoot.Contains(childRoot)) Assert.Ignore("The nested host's root is not inside its parent's tree on this Unity version.");

            var parentOwned = UIToolkitPanels.CountElements(parentRoot) - UIToolkitPanels.CountElements(childRoot);
            var childCount = UIToolkitPanels.CountElements(childRoot);

            // Both hosts listed: the nested subtree appears only under the nested host.
            var both = Data(UIToolkitFunctions.GetVisualTree(null, "Button#play"));
            var reports = ((JArray)both["hosts"]).Where(h => new[] { parentId, childId }.Contains((string)h["host"]["object_id"])).ToList();
            Assert.AreEqual(2, reports.Count, both.ToString());
            var parentReport = reports.Single(h => (string)h["host"]["object_id"] == parentId);
            var childReport = reports.Single(h => (string)h["host"]["object_id"] == childId);
            Assert.AreEqual(parentOwned, (int)parentReport["element_count"]);
            Assert.AreEqual(parentOwned, (int)parentReport["host"]["element_count"]);
            Assert.AreEqual(childCount, (int)childReport["element_count"]);
            Assert.AreEqual(1, ((JArray)parentReport["elements"]).Count, parentReport.ToString());
            Assert.AreEqual(1, ((JArray)childReport["elements"]).Count, childReport.ToString());
            Assert.IsFalse(((JArray)parentReport["findings"]).Any(f => (string)f["code"] == "DUPLICATE_NAME"), parentReport.ToString());

            var info = Data(UIToolkitFunctions.GetUiToolkitInfo());
            Assert.AreEqual(parentOwned, (int)info["hosts"].Single(h => (string)h["object_id"] == parentId)["element_count"]);
            Assert.AreEqual(childCount, (int)info["hosts"].Single(h => (string)h["object_id"] == childId)["element_count"]);

            // Targeting only the parent still returns its full subtree, nested host included.
            var parentOnly = Data(UIToolkitFunctions.GetVisualTree(parentId, "Button#play"));
            Assert.AreEqual(parentOwned + childCount, (int)parentOnly["hosts"][0]["element_count"]);
            Assert.AreEqual(2, ((JArray)parentOnly["hosts"][0]["elements"]).Count);

            // Clicking across all hosts sees each of the two play buttons once, relative to its owner.
            var ambiguous = JObject.FromObject(UIToolkitFunctions.ClickElement(null, "Button#play", null, null, "left", false));
            Assert.AreEqual("AMBIGUOUS_ELEMENT", (string)ambiguous["code"], ambiguous.ToString());
            var candidates = (JArray)ambiguous["data"]["details"];
            Assert.AreEqual(2, candidates.Count, ambiguous.ToString());
            CollectionAssert.AreEquivalent(new[] { parentId, childId }, candidates.Select(c => (string)c["host_id"]).ToArray());
        }

        [UnityTest]
        public IEnumerator FallbackClickPassesThroughElementsWithoutAClickControl()
        {
            CreateSettings();
            Assert.IsTrue((bool)JObject.FromObject(UIToolkitFunctions.CreateUiToolkitPanel("UitkPassThrough", uxmlPath, settingsPath))["success"]);
            created = GameObject.Find("UitkPassThrough");
            var host = UIToolkitPanels.GetHost(created);

            VisualElement root = null;
            for (var frame = 0; frame < 120; frame++)
            {
                root = UIToolkitPanels.GetRoot(host);
                if (root?.panel != null && root.Q<Button>("play") != null)
                {
                    UIToolkitPanels.ValidateLayout(root.panel);
                    if (UIToolkitPanels.HasLayout(root.Q<Button>("play")) && root.Q<Button>("play").worldBound.width > 0) break;
                }
                yield return null;
            }
            if (root?.panel == null || root.Q<Button>("play") == null || !UIToolkitPanels.HasLayout(root.Q<Button>("play")))
                Assert.Ignore("The panel host did not build and lay out its tree in this Editor mode.");
            if (!UIToolkitPanels.TryGetScreenMapping(root.panel, out var mapping)) Assert.Ignore("The panel has no screen mapping in this Editor mode.");

            const int renderHeight = 1000;
            Vector2 RenderPoint(Vector2 panelPoint)
            {
                var screen = mapping.ToScreen(panelPoint);
                return new Vector2(screen.x, renderHeight - screen.y);
            }

            // The full-screen "menu" container: pickable, but no Button/Toggle/Clickable in its chain.
            var menu = root.Q("menu");
            var cardClicks = 0;
            menu.RegisterCallback<ClickEvent>(_ => cardClicks++);
            var corner = new Vector2(menu.worldBound.xMin + 2f, menu.worldBound.yMin + 2f);
            var attempts = new System.Collections.Generic.List<InputInteractionFunctions.ClickOutcome>();
            var background = InputInteractionFunctions.PerformUiToolkitClick(RenderPoint(corner), UnityEngine.EventSystems.PointerEventData.InputButton.Left, attempts, renderHeight);
            Assert.AreEqual("ui-toolkit", background.Strategy);
            Assert.IsFalse(background.Hit, background.Detail);
            Assert.IsFalse(background.BlocksFurtherDispatch, "UI Toolkit without an EventSystem does not block OnMouseDown/physics clicks");
            Assert.IsNull(background.BlockedBy);
            Assert.IsNotNull(background.Target, "the element that received the pointer is still reported");
            Assert.AreEqual(1, cardClicks, "RegisterCallback<ClickEvent> handlers run on the pass-through element");

            var play = root.Q<Button>("play");
            var clicks = 0;
            play.clicked += () => clicks++;
            var onButton = InputInteractionFunctions.PerformUiToolkitClick(RenderPoint(play.worldBound.center), UnityEngine.EventSystems.PointerEventData.InputButton.Left, attempts, renderHeight);
            Assert.IsTrue(onButton.Hit, onButton.Detail);
            Assert.AreEqual(1, clicks);
        }

        private void CreateSettings()
        {
            var response = JObject.FromObject(UIToolkitFunctions.CreatePanelSettings(settingsPath, themePath));
            Assert.IsTrue((bool)response["success"], response.ToString());
        }

        private static JObject Data(object response)
        {
            var json = JObject.FromObject(response);
            Assert.IsTrue((bool)json["success"], json.ToString());
            return (JObject)json["data"];
        }
    }
}
