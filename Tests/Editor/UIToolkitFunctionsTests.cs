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
                         "get_visual_tree", "pick_ui_toolkit_element", "click_ui_toolkit_element", "scroll_ui_toolkit", "wait_for_ui_toolkit", "explain_style", "audit_ui_toolkit" })
                Assert.IsNotNull(ToolRegistry.GetMethod(name), name);
        }

        [Test]
        public void SelectorParsesTypeNameAndClassesAndRejectsListsAndAttributes()
        {
            var selector = UIToolkitPanels.Selector.Parse("Button#play.primary.big", null);
            Assert.AreEqual("Button", selector.Type);
            Assert.AreEqual("play", selector.Name);
            CollectionAssert.AreEqual(new[] { "primary", "big" }, selector.Classes);
            foreach (var bad in new[] { "Button, Label", "Button[name=x]", "Button:hover", "> Button", "A > > B", "A >" })
                Assert.AreEqual("UNSUPPORTED_SELECTOR", Assert.Throws<UIToolkitException>(() => UIToolkitPanels.Selector.Parse(bad, null)).Code, bad);

            var button = new Button { name = "play", text = "Play" };
            button.AddToClassList("primary");
            Assert.IsTrue(UIToolkitPanels.Selector.Parse("TextElement.primary", null).Matches(button), "base types match");
            Assert.IsTrue(UIToolkitPanels.Selector.Parse(null, "play").Matches(button), "text matches case-insensitively");
            Assert.IsFalse(UIToolkitPanels.Selector.Parse("#quit", null).Matches(button));
        }

        [Test]
        public void SelectorCombinatorsAndNthOfTypeResolveRepeatedButtons()
        {
            // Two setting rows, each with "On"/"Off" buttons: the PolyGames "Show par" case.
            var root = new VisualElement { name = "pg-settings" };
            VisualElement Row(string name)
            {
                var row = new VisualElement { name = name };
                row.AddToClassList("pg-setting");
                row.Add(new Label(name));
                row.Add(new Button { text = "On" });
                row.Add(new Button { text = "Off" });
                root.Add(row);
                return row;
            }
            var sound = Row("sound");
            var par = Row("show-par");

            var descendant = UIToolkitPanels.Selector.Parse("#pg-settings Button", null);
            Assert.IsTrue(descendant.Matches(sound[1]));
            Assert.IsFalse(descendant.Matches(root));

            var child = UIToolkitPanels.Selector.Parse(".pg-setting > Button", null);
            Assert.IsTrue(child.Matches(par[2]));
            Assert.IsFalse(UIToolkitPanels.Selector.Parse("#pg-settings > Button", null).Matches(par[1]), "> requires the direct parent");

            var parOn = UIToolkitPanels.Selector.Parse("#show-par > Button:nth-of-type(1)", "On");
            Assert.IsTrue(parOn.Matches(par[1]));
            Assert.IsFalse(parOn.Matches(sound[1]));
            Assert.IsTrue(UIToolkitPanels.Selector.Parse("#show-par > :nth-child(3)", null).Matches(par[2]));

            // Name paths survive a sibling being brought to the front, index paths don't.
            Assert.AreSame(par[1], UIToolkitPanels.ResolvePath(root, "#show-par/Button:nth-of-type(1)"));
            sound.BringToFront();
            Assert.AreNotSame(par[1], UIToolkitPanels.ResolvePath(root, "1/1"), "index path shifted");
            Assert.AreSame(par[1], UIToolkitPanels.ResolvePath(root, "#show-par/Button:nth-of-type(1)"));
            Assert.IsTrue(UIToolkitPanels.IsIndexPath("1/2"));
            Assert.IsFalse(UIToolkitPanels.IsIndexPath("#show-par/1"));
            Assert.AreEqual("AMBIGUOUS_ELEMENT", Assert.Throws<UIToolkitException>(() => UIToolkitPanels.ResolvePath(root, ".pg-setting")).Code);
            Assert.IsNull(UIToolkitPanels.ResolvePath(root, "#missing"));
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
            Assert.AreEqual("uxml", (string)response["data"]["host"]["tree_source"]);

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
            var codeBuilt = JObject.FromObject(UIToolkitFunctions.DescribeHost(UIToolkitPanels.GetHost(created)));
            Assert.IsFalse(codeBuilt["issues"].Any(i => ((string)i).StartsWith("NO_VISUAL_TREE_ASSET")), "hosts whose tree is built in code are not flagged in Edit Mode: " + codeBuilt);

            var set = JObject.FromObject(UIToolkitFunctions.ConfigureUiToolkitPanel(id, uxmlPath, null, "5"));
            Assert.IsTrue((bool)set["success"], set.ToString());
            var host = UIToolkitPanels.GetHost(created);
            Assert.AreEqual(uxmlPath, AssetDatabase.GetAssetPath(UIToolkitPanels.GetVisualTreeAsset(host)));
            Assert.AreEqual(5f, UIToolkitPanels.GetSortingOrder(host));

            var cleared = JObject.FromObject(UIToolkitFunctions.ConfigureUiToolkitPanel(id, "none"));
            Assert.IsTrue((bool)cleared["success"], cleared.ToString());
            Assert.IsNull(UIToolkitPanels.GetVisualTreeAsset(host));
            Assert.AreEqual("none", (string)cleared["data"]["host"]["tree_source"], "the previous build is not reported as code-built: " + cleared);

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
                var described = JObject.FromObject(UIToolkitPanels.DescribePick(pick, renderHeight));
                Assert.AreEqual(JTokenType.Object, described["element"]["screen_bounds"].Type, "picks carry screen bounds like get_visual_tree: " + described);
            }
        }

        [UnityTest]
        public IEnumerator ClickReportsHandlersThatDisableTheirOwnButton()
        {
            yield return BuildLivePanel("UitkSelfDisable");
            var quit = liveRoot.Q<Button>("quit");
            var clicks = 0;
            quit.clicked += () => { clicks++; quit.SetEnabled(false); };

            var click = JObject.FromObject(UIToolkitFunctions.ClickElement(ObjectIdHelper.GetSerializableId(created), "Button#quit", null, null, "left", false));
            Assert.IsTrue((bool)click["success"], click.ToString());
            Assert.AreEqual(1, clicks);
            Assert.IsTrue((bool)click["data"]["clicked"], click.ToString());
            Assert.IsTrue((bool)click["data"]["handler_invoked"], click.ToString());
            Assert.IsTrue((bool)click["data"]["receiver_disabled"], click.ToString());
            StringAssert.Contains("disabled itself", (string)click["message"]);
        }

        [UnityTest]
        public IEnumerator TextOnAButtonsInnerLabelClicksTheButton()
        {
            yield return BuildLivePanel("UitkInnerLabel");
            var custom = new Button { name = "custom", style = { width = 200, height = 60 } };
            custom.Add(new Label("Klotski"));
            liveRoot.Q("menu").Add(custom);
            UIToolkitPanels.ValidateLayout(liveRoot.panel);
            var clicks = 0;
            custom.clicked += () => clicks++;
            var id = ObjectIdHelper.GetSerializableId(created);

            var tree = Data(UIToolkitFunctions.GetVisualTree(id, null, "Klotski"));
            var label = (JObject)tree["hosts"][0]["elements"][0];
            Assert.AreEqual("Label", (string)label["type"], tree.ToString());
            Assert.AreEqual(UIToolkitPanels.PathOf(liveRoot, custom), (string)label["click_receiver_path"], tree.ToString());

            var click = JObject.FromObject(UIToolkitFunctions.ClickElement(id, null, "klotski", null, "left", false));
            Assert.IsTrue((bool)click["success"], click.ToString());
            Assert.AreEqual(UIToolkitPanels.PathOf(liveRoot, custom), (string)click["data"]["element"]["path"], click.ToString());
            Assert.AreEqual(1, clicks);
            var labelOnly = JObject.FromObject(UIToolkitFunctions.ClickElement(id, "Label", "Klotski", null, "left", false));
            Assert.IsTrue((bool)labelOnly["success"], "an explicit Label selector keeps the label: " + labelOnly);
            Assert.AreEqual("Label", (string)labelOnly["data"]["element"]["type"]);
        }

        [UnityTest]
        public IEnumerator ScrolledOutElementsAreClippedNotCoveredAndCanBeScrolledIn()
        {
            yield return BuildLivePanel("UitkScroll");
            var scroll = new ScrollView { name = "list", style = { width = 220, height = 100, flexShrink = 0 } };
            for (var i = 0; i < 10; i++) scroll.Add(new Button { name = "row" + i, text = "Row " + i, style = { height = 60, flexShrink = 0 } });
            liveRoot.Q("menu").Add(scroll);
            for (var frame = 0; frame < 10; frame++)
            {
                UIToolkitPanels.ValidateLayout(liveRoot.panel);
                if (scroll.contentViewport.worldBound.height > 0) break;
                yield return null;
            }
            if (!(scroll.contentViewport.worldBound.height > 0)) Assert.Ignore("The ScrollView was not laid out in this Editor mode.");
            var id = ObjectIdHelper.GetSerializableId(created);
            var row9 = scroll.Q<Button>("row9");

            var tree = Data(UIToolkitFunctions.GetVisualTree(id, "Button#row9"));
            var host = tree["hosts"][0];
            Assert.AreEqual(0f, (float)host["elements"][0]["visible_fraction"], tree.ToString());
            Assert.IsNotNull((string)host["elements"][0]["scroll_view"], tree.ToString());
            Assert.AreEqual("matched_subtrees", (string)host["findings_scope"]);
            Assert.IsFalse(((JArray)host["findings"]).Any(f => (string)f["code"] == "OUTSIDE_PANEL"), "scrolled-out content is not a finding: " + tree);

            var clipped = JObject.FromObject(UIToolkitFunctions.ClickElement(id, "Button#row9", null, null, "left", false));
            Assert.AreEqual("ELEMENT_CLIPPED", (string)clipped["code"], clipped.ToString());
            Assert.IsNotNull((string)clipped["data"]["details"]["scroll_view"], clipped.ToString());

            var scrolled = JObject.FromObject(UIToolkitFunctions.ScrollElement(id, "Button#row9", null, null, 0, 0));
            Assert.IsTrue((bool)scrolled["success"], scrolled.ToString());
            Assert.Greater((float)scrolled["data"]["scroll_views"][0]["offset_after"]["y"], 0f, scrolled.ToString());
            for (var frame = 0; frame < 10 && UIToolkitPanels.GetVisibility(row9).Fraction <= 0f; frame++)
            {
                yield return null;
                UIToolkitPanels.ValidateLayout(liveRoot.panel);
            }
            Assert.Greater(UIToolkitPanels.GetVisibility(row9).Fraction, 0f, "ScrollTo brings the row into the viewport");

            var clicks = 0;
            row9.clicked += () => clicks++;
            var click = JObject.FromObject(UIToolkitFunctions.ClickElement(id, "Button#row9", null, null, "left", false));
            Assert.IsTrue((bool)click["success"], click.ToString());
            Assert.AreEqual(1, clicks);

            var back = JObject.FromObject(UIToolkitFunctions.ScrollElement(id, "ScrollView#list", null, null, 0, -100000));
            Assert.IsTrue((bool)back["success"], back.ToString());
            Assert.AreEqual(0f, scroll.scrollOffset.y, "delta scrolling is clamped by the ScrollView");
            UIToolkitPanels.ValidateLayout(liveRoot.panel);
            var viaOption = JObject.FromObject(UIToolkitFunctions.ClickElement(id, "Button#row9", null, null, "left", false, scrollIntoView: true));
            Assert.That((string)viaOption["code"] == null || (string)viaOption["code"] == "ELEMENT_CLIPPED", viaOption.ToString());
            if ((bool)viaOption["success"]) Assert.IsTrue((bool)viaOption["data"]["scrolled_into_view"], viaOption.ToString());
        }

        [Test]
        public void TextMatchResolvesToTheClickReceiverOnlyWhenItFitsTheSelector()
        {
            var button = new Button { name = "go" };
            var label = new Label("Go");
            button.Add(label);
            Assert.AreSame(button, UIToolkitPanels.Selector.Parse(null, "go").ClickTarget(label));
            Assert.AreSame(button, UIToolkitPanels.Selector.Parse("Button", "Go").ClickTarget(label));
            Assert.AreSame(label, UIToolkitPanels.Selector.Parse("Label", "Go").ClickTarget(label));
            Assert.IsNull(UIToolkitPanels.Selector.Parse("Button", "Go").ClickTarget(button), "the button's own (empty) text does not match");
            Assert.IsNull(UIToolkitPanels.Selector.Parse(null, "Stop").ClickTarget(label));
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
            Assert.AreEqual(childId, (string)parentOnly["hosts"][0]["nested_hosts"][0]["object_id"], "nested panels are marked: " + parentOnly);
            Assert.AreEqual(UIToolkitPanels.PathOf(parentRoot, childRoot), (string)parentOnly["hosts"][0]["nested_hosts"][0]["element_path"]);

            // Clicking across all hosts sees each of the two play buttons once, relative to its owner.
            var ambiguous = JObject.FromObject(UIToolkitFunctions.ClickElement(null, "Button#play", null, null, "left", false));
            Assert.AreEqual("AMBIGUOUS_ELEMENT", (string)ambiguous["code"], ambiguous.ToString());
            var candidates = (JArray)ambiguous["data"]["details"];
            Assert.AreEqual(2, candidates.Count, ambiguous.ToString());
            CollectionAssert.AreEquivalent(new[] { parentId, childId }, candidates.Select(c => (string)c["host_id"]).ToArray());
        }

        [UnityTest]
        public IEnumerator EmptyHostWithANestedPanelHasNoTreeSource()
        {
            CreateSettings();
            Assert.IsTrue((bool)JObject.FromObject(UIToolkitFunctions.CreateUiToolkitPanel("UitkEmptyOuter", uxmlPath, settingsPath))["success"]);
            created = GameObject.Find("UitkEmptyOuter");
            var parentId = ObjectIdHelper.GetSerializableId(created);
            Assert.IsTrue((bool)JObject.FromObject(UIToolkitFunctions.CreateUiToolkitPanel("UitkEmptyInner", uxmlPath, parent: parentId))["success"]);
            Assert.IsTrue((bool)JObject.FromObject(UIToolkitFunctions.ConfigureUiToolkitPanel(parentId, "none"))["success"]);
            var parentHost = UIToolkitPanels.GetHost(created);
            var childHost = UIToolkitPanels.GetHost(created.transform.Find("UitkEmptyInner").gameObject);

            VisualElement parentRoot = null, childRoot = null;
            for (var frame = 0; frame < 120; frame++)
            {
                parentRoot = UIToolkitPanels.GetRoot(parentHost);
                childRoot = UIToolkitPanels.GetRoot(childHost);
                if (parentRoot != null && childRoot != null && parentRoot.hierarchy.childCount == 1 && ReferenceEquals(parentRoot.hierarchy[0], childRoot)) break;
                yield return null;
            }
            if (parentRoot == null || childRoot == null || parentRoot.hierarchy.childCount != 1 || !ReferenceEquals(parentRoot.hierarchy[0], childRoot))
                Assert.Ignore("The nested host's root is not the only child of its parent's root in this Editor mode.");

            Assert.AreEqual("none", (string)JObject.FromObject(UIToolkitFunctions.DescribeHost(parentHost))["tree_source"]);
            var tree = Data(UIToolkitFunctions.GetVisualTree(parentId));
            Assert.AreEqual("none", (string)tree["hosts"][0]["host"]["tree_source"], tree.ToString());
            var all = Data(UIToolkitFunctions.GetVisualTree(null));
            Assert.AreEqual("none", (string)all["hosts"].Single(h => (string)h["host"]["object_id"] == parentId)["host"]["tree_source"], all.ToString());
        }

        [UnityTest]
        public IEnumerator HiddenElementsAreNotVisibleAndCannotBeScrolled()
        {
            yield return BuildLivePanel("UitkHidden");
            var scroll = new ScrollView { name = "list", style = { width = 220, height = 100, flexShrink = 0 } };
            for (var i = 0; i < 10; i++) scroll.Add(new Button { name = "row" + i, text = "Row " + i, style = { height = 60, flexShrink = 0 } });
            liveRoot.Q("menu").Add(scroll);
            scroll.style.display = DisplayStyle.None;
            UIToolkitPanels.ValidateLayout(liveRoot.panel);
            var id = ObjectIdHelper.GetSerializableId(created);

            var row = (JObject)Data(UIToolkitFunctions.GetVisualTree(id, "Button#row0"))["hosts"][0]["elements"][0];
            Assert.IsFalse((bool)row["displayed"], row.ToString());
            Assert.AreEqual(0f, (float)row["visible_fraction"], row.ToString());
            Assert.AreEqual(JTokenType.Null, row["clipped_by"].Type, row.ToString());
            Assert.AreEqual(JTokenType.Null, row["scroll_view"].Type, row.ToString());

            var hiddenScroll = JObject.FromObject(UIToolkitFunctions.ScrollElement(id, "Button#row0", null, null, 0, 200));
            Assert.AreEqual("ELEMENT_NOT_DISPLAYED", (string)hiddenScroll["code"], hiddenScroll.ToString());
            Assert.AreEqual(Vector2.zero, scroll.scrollOffset);

            var displayedOnly = Data(UIToolkitFunctions.GetVisualTree(id, "Button", displayed_only: true));
            var names = ((JArray)displayedOnly["hosts"][0]["elements"]).Select(e => (string)e["name"]).ToArray();
            CollectionAssert.AreEquivalent(new[] { "play", "quit" }, names, displayedOnly.ToString());
        }

        [UnityTest]
        public IEnumerator CompactTreeDropsEmptyAndDuplicateFields()
        {
            yield return BuildLivePanel("UitkCompact");
            var id = ObjectIdHelper.GetSerializableId(created);
            var full = Data(UIToolkitFunctions.GetVisualTree(id, "Button#play"));
            var compact = Data(UIToolkitFunctions.GetVisualTree(id, "Button#play", compact: true));
            var element = (JObject)compact["hosts"][0]["elements"][0];
            Assert.AreEqual("Play", (string)element["text"]);
            Assert.AreEqual((string)full["hosts"][0]["elements"][0]["path"], (string)element["path"]);
            Assert.IsNull(element["styles"], "null fields are dropped: " + element);
            Assert.IsNull(element["value"], element.ToString());
            if (element["screen_bounds"] != null)
            {
                Assert.IsNull(element["panel_bounds"], element.ToString());
                Assert.IsNull(element["screen_bounds"]["render_bottom_left"], element.ToString());
                Assert.IsNotNull(element["screen_bounds"]["center_render_pixels"], element.ToString());
            }
            var host = (JObject)compact["hosts"][0]["host"];
            Assert.AreEqual(id, (string)host["object_id"]);
            Assert.IsNull(host["panel_settings"], host.ToString());
            Assert.Less(compact.ToString().Length, full.ToString().Length);
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

        [Test]
        public void WaitStatesParseAndRejectUnknownOnes()
        {
            Assert.AreEqual("displayed", UIToolkitFunctions.WaitState.Parse(null).Kind);
            var cls = UIToolkitFunctions.WaitState.Parse("has_class: pg-open ");
            Assert.AreEqual("has_class", cls.Kind);
            Assert.AreEqual("pg-open", cls.Argument);
            Assert.AreEqual("Score: 10", UIToolkitFunctions.WaitState.Parse("text:Score: 10").Argument, "text keeps everything after the first ':'");
            Assert.AreEqual("", UIToolkitFunctions.WaitState.Parse("text:").Argument, "empty text is a valid target");
            foreach (var bad in new[] { "visible", "has_class:", "settled:3", "lacks_class" })
                Assert.Throws<UIToolkitException>(() => UIToolkitFunctions.WaitState.Parse(bad), bad);
            Assert.IsTrue(UIToolkitFunctions.WaitState.Parse("hidden").IsMet(null), "an absent element counts as hidden");
            Assert.IsFalse(UIToolkitFunctions.WaitState.Parse("exists").IsMet(null));
        }

        [Test]
        public void SignaturesCompareWithTolerance()
        {
            Assert.IsTrue(UIToolkitFunctions.SameSignature(new[] { 1f, float.NaN }, new[] { 1.00001f, float.NaN }));
            Assert.IsFalse(UIToolkitFunctions.SameSignature(new[] { 1f }, new[] { 1.01f }));
            Assert.IsFalse(UIToolkitFunctions.SameSignature(new[] { 1f }, null));
            Assert.IsFalse(UIToolkitFunctions.SameSignature(new[] { 1f }, new[] { 1f, 2f }));
        }

        [UnityTest]
        public IEnumerator WaitForUiToolkitWaitsForLaterChanges()
        {
            yield return BuildLivePanel("UitkWait");
            var id = ObjectIdHelper.GetSerializableId(created);
            var play = liveRoot.Q<Button>("play");

            var invalid = JObject.FromObject(UIToolkitFunctions.WaitForElementAsync(id, "#play", null, null, "visible", 1f, 3).Result);
            Assert.AreEqual("INVALID_WAIT_STATE", (string)invalid["code"], invalid.ToString());

            // The class arrives a few updates after the wait starts, like schedule.ExecuteLater.
            var wait = UIToolkitFunctions.WaitForElementAsync(id, "#play", null, null, "has_class:open", 5f, 3);
            for (var i = 0; i < 3; i++) yield return null;
            Assert.IsFalse(wait.IsCompleted, "the class is not there yet");
            play.AddToClassList("open");
            yield return Await(wait);
            var met = JObject.FromObject(wait.Result);
            Assert.IsTrue((bool)met["success"], met.ToString());
            Assert.AreEqual("has_class:open", (string)met["data"]["state"]);

            var gone = UIToolkitFunctions.WaitForElementAsync(id, "#quit", null, null, "hidden", 5f, 3);
            yield return null;
            liveRoot.Q<Button>("quit").style.display = DisplayStyle.None;
            yield return Await(gone);
            Assert.IsTrue((bool)JObject.FromObject(gone.Result)["success"], JObject.FromObject(gone.Result).ToString());

            var timeout = UIToolkitFunctions.WaitForElementAsync(id, "#missing", null, null, "exists", 0.2f, 3);
            yield return Await(timeout);
            var timedOut = JObject.FromObject(timeout.Result);
            Assert.AreEqual("WAIT_TIMEOUT", (string)timedOut["code"], timedOut.ToString());
            Assert.IsFalse((bool)timedOut["data"]["found"]);
            Assert.AreEqual("ELEMENT_NOT_FOUND", (string)timedOut["data"]["lookup"]);

            var ambiguous = UIToolkitFunctions.WaitForElementAsync(id, "Button", null, null, "exists", 1f, 3);
            yield return Await(ambiguous);
            Assert.AreEqual("AMBIGUOUS_ELEMENT", (string)JObject.FromObject(ambiguous.Result)["code"]);

            var settled = UIToolkitFunctions.WaitForElementAsync(id, "#play", null, null, "settled", 5f, 3);
            yield return Await(settled);
            var settledJson = JObject.FromObject(settled.Result);
            Assert.IsTrue((bool)settledJson["success"], settledJson.ToString());
            Assert.GreaterOrEqual((int)settledJson["data"]["frames"], 2, settledJson.ToString());
        }

        private static IEnumerator Await(System.Threading.Tasks.Task task)
        {
            // Time-based: batch-mode frames are fast, and .Result on an unfinished wait would deadlock.
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (!task.IsCompleted && clock.Elapsed.TotalSeconds < 15) yield return null;
            Assert.IsTrue(task.IsCompleted, "wait did not complete");
        }

        [UnityTest]
        public IEnumerator ExplainStyleNamesTheWinningRuleAndTheOnesItBeat()
        {
            yield return BuildLivePanel("UitkExplain");
            var ussPath = folder + "/Explain.uss";
            File.WriteAllText(ussPath,
                "/* test sheet */\n" +
                ".primary {\n    background-color: rgb(255, 0, 0);\n}\n" +
                "#play.primary { color: rgb(0, 255, 0); }\n" +
                ".primary:hover {\n    background-color: rgb(0, 0, 255);\n}\n");
            AssetDatabase.ImportAsset(ussPath, ImportAssetOptions.ForceSynchronousImport);
            liveRoot.styleSheets.Add(AssetDatabase.LoadAssetAtPath<StyleSheet>(ussPath));
            var play = liveRoot.Q<Button>("play");
            UIToolkitPanels.ValidateLayout(play.panel);
            yield return null;

            var explained = JObject.FromObject(UIToolkitFunctions.ExplainElement(play, "0/1", "background-color,color,width", new System.Collections.Generic.List<string> { "hover" }));
            Assert.AreEqual("unity", (string)explained["matcher"], explained.ToString());
            var properties = (JArray)explained["properties"];
            var background = properties.First(p => (string)p["property"] == "background-color");
            Assert.AreEqual(JTokenType.Object, background["winner"].Type, explained.ToString());
            Assert.AreEqual(".primary", (string)background["winner"]["selector"], background.ToString());
            Assert.AreEqual(ussPath, (string)background["winner"]["sheet"]);
            Assert.AreEqual(3, (int)background["winner"]["line"], "the declaration's line in the USS file");
            Assert.AreEqual("#FF0000FF", (string)background["resolved"]);
            Assert.IsTrue(((JArray)background["overridden"]).Any(o => ((string)o["selector"]).Contains("unity-button")), "the theme's button rule lost: " + background);
            Assert.AreEqual(".primary:hover", (string)background["in_pseudo_states"][0]["selector"], background.ToString());
            Assert.AreEqual(7, (int)background["in_pseudo_states"][0]["line"], "the declaration's line, like the winner's");

            var color = properties.First(p => (string)p["property"] == "color");
            Assert.AreEqual("#play.primary", (string)color["winner"]["selector"]);
            Assert.AreEqual("1,1,0", (string)color["winner"]["specificity"]);

            var width = properties.First(p => (string)p["property"] == "width");
            Assert.AreEqual("inline", (string)width["source"], "UXML style=\"width: 200px\" is inline: " + width);
            Assert.IsTrue((bool)explained["pseudo_states"]["heuristic"]);
            Assert.AreEqual(Color.red, play.resolvedStyle.backgroundColor, "explaining :hover never changes the element");
        }

        [Test]
        public void UssSourceParsingKeepsLinesAndSplitsSelectorLists()
        {
            var rules = StyleIntrospection.ParseUss("@import url(\"x.uss\");\n/* a\nb */\n.a, #b > Label {\n  color: red;\n  width: 10px\n}\n.c:hover { opacity: 0.5; }");
            Assert.AreEqual(3, rules.Count);
            Assert.AreEqual(".a", rules[0].Selector);
            Assert.AreEqual("#b > Label", rules[1].Selector);
            Assert.AreEqual(4, rules[0].Line, "comments keep their lines");
            Assert.AreEqual(5, rules[0].Declarations[0].Line);
            Assert.AreEqual("width", rules[0].Declarations[1].Name);
            Assert.AreEqual(6, rules[0].Declarations[1].Line);
            Assert.AreEqual(8, rules[2].Line);
            Assert.AreEqual((1, 0, 1), StyleIntrospection.SpecificityOf("#b > Label"));
            Assert.AreEqual((0, 2, 0), StyleIntrospection.SpecificityOf(".c:hover"));
            Assert.AreEqual("unityFontDefinition", StyleIntrospection.CamelName("-unity-font-definition"));
            Assert.AreEqual("backgroundColor", StyleIntrospection.CamelName("background-color"));
        }

        [Test]
        public void ContrastUsesWcagLuminance()
        {
            Assert.AreEqual(21f, UIToolkitFunctions.ContrastRatio(Color.white, Color.black), 0.01f);
            Assert.AreEqual(1f, UIToolkitFunctions.ContrastRatio(Color.gray.linear, Color.gray.linear), 0.001f);
            Assert.IsNull(UIToolkitFunctions.ParseRect(null, "x"));
            Assert.AreEqual(new Rect(1, 2, 3, 4), UIToolkitFunctions.ParseRect("1,2,3,4", "x"));
            Assert.Throws<ArgumentException>(() => UIToolkitFunctions.ParseRect("1,2,0,4", "x"));
            Assert.AreEqual("Hi there", UIToolkitFunctions.StripRichText("<b>Hi</b> <color=#fff>there</color>"));
        }

        [UnityTest]
        public IEnumerator AuditFlagsSmallCoveredAndLowContrastControls()
        {
            yield return BuildLivePanel("UitkAudit");
            var menu = liveRoot.Q("menu");
            var tiny = new Button { name = "tiny", text = "x", style = { width = 20, height = 20 } };
            var faint = new Label("Faint") { name = "faint", style = { color = new Color(0.45f, 0.45f, 0.45f), backgroundColor = new Color(0.5f, 0.5f, 0.5f), width = 200, height = 40 } };
            var overImage = new Image { style = { width = 200, height = 60 } };
            overImage.Add(new Label("Over image") { name = "over-image" });
            var cover = new VisualElement { name = "cover", style = { position = Position.Absolute, left = 0, right = 0, top = 0, bottom = 0 } };
            menu.Add(tiny); menu.Add(faint); menu.Add(overImage); liveRoot.Add(cover);
            UIToolkitPanels.ValidateLayout(liveRoot.panel);
            yield return null;
            UIToolkitPanels.ValidateLayout(liveRoot.panel);

            var host = UIToolkitPanels.GetHost(created);
            var report = UIToolkitFunctions.AuditHosts(new System.Collections.Generic.List<Component> { host }, new UIToolkitFunctions.AuditOptions());
            var json = JObject.FromObject(report.ToResponse());
            var findings = ((JArray)json["hosts"][0]["findings"]).ToList();
            bool Has(string code, string element) => findings.Any(f => (string)f["code"] == code && ((string)f["element"] ?? "").Contains(element));
            Assert.IsTrue(Has("SMALL_TOUCH_TARGET", "#tiny"), json.ToString());
            Assert.IsFalse(Has("SMALL_TOUCH_TARGET", "#play"), "200x60 is large enough");
            Assert.IsTrue(Has("LOW_CONTRAST", "#faint"), json.ToString());
            Assert.IsFalse(Has("LOW_CONTRAST", "#over-image"), "text over an image is indeterminate, never reported");
            Assert.Greater((int)json["indeterminate_contrast"], 0);
            if (UIToolkitPanels.TryGetScreenMapping(liveRoot.panel, out _) && RenderHeightOrZero() > 0)
                Assert.IsTrue(Has("COVERED_CONTROL", "#play"), json.ToString());

            cover.RemoveFromHierarchy();
            var skipped = UIToolkitFunctions.AuditHosts(new System.Collections.Generic.List<Component> { host },
                new UIToolkitFunctions.AuditOptions { Skip = new System.Collections.Generic.HashSet<string> { "LOW_CONTRAST" } });
            var skippedJson = JObject.FromObject(skipped.ToResponse());
            Assert.IsFalse(((JArray)skippedJson["hosts"][0]["findings"]).Any(f => (string)f["code"] == "LOW_CONTRAST" || (string)f["code"] == "COVERED_CONTROL"), skippedJson.ToString());
        }

        private static int RenderHeightOrZero()
        {
            try { return VisualCoordinates.Current().render_height; }
            catch (InvalidOperationException) { return 0; }
        }

        [UnityTest]
        public IEnumerator MissingGlyphsChecksTheSourceFontWithoutAddingCharacters()
        {
            // A label with an explicit font asset, the way projects set -unity-font-definition.
            yield return BuildLivePanel("UitkGlyphs");
            var font = Resources.FindObjectsOfTypeAll<UnityEngine.TextCore.Text.FontAsset>().FirstOrDefault(f => f.name.Contains("Sans"));
            if (font == null) Assert.Ignore("No editor FontAsset available.");
            var title = liveRoot.Q<Label>("title");
            title.style.unityFontDefinition = FontDefinition.FromSDFFont(font);
            UIToolkitPanels.ValidateLayout(title.panel);
            yield return null;
            var asset = UIToolkitFunctions.EffectiveFontAsset(title.resolvedStyle);
            Assert.AreSame(font, asset, "the style's font asset is the one checked");
            Assert.IsNull(UIToolkitFunctions.EffectiveFontAsset(liveRoot.Q<Button>("play").resolvedStyle), "theme default: no verdict");
            var missing = UIToolkitFunctions.MissingGlyphs("AB \uE000", asset, null);
            CollectionAssert.AreEqual(new uint[] { 0xE000 }, missing, "letters exist in the font; a private-use character does not");
            CollectionAssert.AreEqual(missing, UIToolkitFunctions.MissingGlyphs("AB \uE000", asset, null), "checking is repeatable (nothing was added)");
            Assert.IsNull(UIToolkitFunctions.MissingGlyphs("A", null, null), "unknown font: no verdict");
        }

        private VisualElement liveRoot;

        /// <summary>Creates a host from the test UXML and waits until it is built and laid out; ignores the test otherwise.</summary>
        private IEnumerator BuildLivePanel(string name)
        {
            CreateSettings();
            Assert.IsTrue((bool)JObject.FromObject(UIToolkitFunctions.CreateUiToolkitPanel(name, uxmlPath, settingsPath))["success"]);
            created = GameObject.Find(name);
            var host = UIToolkitPanels.GetHost(created);
            liveRoot = null;
            for (var frame = 0; frame < 120; frame++)
            {
                liveRoot = UIToolkitPanels.GetRoot(host);
                if (liveRoot?.panel != null && liveRoot.Q<Button>("play") != null)
                {
                    UIToolkitPanels.ValidateLayout(liveRoot.panel);
                    if (UIToolkitPanels.HasLayout(liveRoot.Q<Button>("play")) && liveRoot.Q<Button>("play").worldBound.width > 0) yield break;
                }
                yield return null;
            }
            Assert.Ignore("The panel host did not build and lay out its tree in this Editor mode.");
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
