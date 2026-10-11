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
            var wire = ResponsePruner.Prune("click_ui_toolkit_element", click.ToString(Newtonsoft.Json.Formatting.None), "compact");
            Assert.Less(wire.Length, 400, "a plain click costs a few hundred bytes, not the full element description: " + wire);

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
            var labelOnly = JObject.FromObject(UIToolkitFunctions.ClickElement(id, "Label", "Klotski", null, "left", false, false, "full"));
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
            var hostEntry = (JObject)compact["hosts"][0];
            Assert.IsNull(hostEntry["nested_hosts"], "null host fields are dropped: " + hostEntry);
            Assert.IsNull(hostEntry["scheduler"], hostEntry.ToString());
            Assert.IsNull(compact["snapshot_id"], compact.ToString());
            Assert.IsNull(compact["truncated"], "truncated=false is dropped: " + compact);
            Assert.Less(compact.ToString().Length, full.ToString().Length);

            var none = Data(UIToolkitFunctions.GetVisualTree(id, "Button#no-such-button", compact: true));
            Assert.IsNotNull(none["hosts"][0]["elements"], "an empty match list stays: " + none);
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
            Assert.GreaterOrEqual((int)settledJson["data"]["frames"], 3, "settle_frames frames pass after the first check: " + settledJson);
        }

        [UnityTest]
        public IEnumerator WaitForSettledRestartsWhenADescendantClassChangesLater()
        {
            yield return BuildLivePanel("UitkSettleLate");
            var id = ObjectIdHelper.GetSerializableId(created);
            var play = liveRoot.Q<Button>("play");

            // Like a dialog that adds its "open" class to an inner panel a frame after it is shown.
            var wait = UIToolkitFunctions.WaitForElementAsync(id, "#menu", null, null, "settled", 5f, 3);
            yield return null;
            yield return null;
            Assert.IsFalse(wait.IsCompleted, "two frames are not enough for settle_frames=3");
            play.AddToClassList("late-open");
            var ticksAfterChange = 0;
            EditorApplication.CallbackFunction count = () => ticksAfterChange++;
            EditorApplication.update += count;
            try { yield return Await(wait); }
            finally { EditorApplication.update -= count; }

            var json = JObject.FromObject(wait.Result);
            Assert.IsTrue((bool)json["success"], json.ToString());
            Assert.GreaterOrEqual(ticksAfterChange, 3, "the class change on a descendant restarts the settle count: " + json);
        }

        [UnityTest]
        public IEnumerator VisualTreeQueriesSearchBelowMaxDepthAndElementPathScopesTheSubtree()
        {
            yield return BuildLivePanel("UitkDepth");
            var id = ObjectIdHelper.GetSerializableId(created);
            var play = liveRoot.Q<Button>("play");
            var playPath = UIToolkitPanels.PathOf(liveRoot, play);
            var menuPath = UIToolkitPanels.PathOf(liveRoot, liveRoot.Q("menu"));

            var deep = Data(UIToolkitFunctions.GetVisualTree(id, "#play", max_depth: 0));
            var matched = (JArray)deep["hosts"][0]["elements"];
            Assert.AreEqual(1, matched.Count, "max_depth limits listings, not selector matching: " + deep);
            Assert.AreEqual(playPath, (string)matched[0]["path"]);

            var subtree = Data(UIToolkitFunctions.GetVisualTree(id, max_depth: 1, element_path: menuPath));
            var paths = ((JArray)subtree["hosts"][0]["elements"]).Select(e => (string)e["path"]).ToList();
            Assert.AreEqual(menuPath, paths[0], "the subtree starts at element_path: " + subtree);
            CollectionAssert.Contains(paths, playPath, "children keep host-relative paths");
            Assert.IsTrue(paths.All(path => path.StartsWith(menuPath, StringComparison.Ordinal)), subtree.ToString());

            var missing = JObject.FromObject(UIToolkitFunctions.GetVisualTree(id, element_path: "99/99"));
            Assert.AreEqual("ELEMENT_NOT_FOUND", (string)missing["code"], missing.ToString());
            var near = JObject.FromObject(UIToolkitFunctions.ClickElement(id, "#pla", null, null, "left", false));
            Assert.AreEqual("ELEMENT_NOT_FOUND", (string)near["code"], near.ToString());
            var candidates = (JArray)near["data"]["details"];
            Assert.IsTrue(candidates.Any(c => (string)c["element_path"] == playPath), "a near miss names the likely element: " + near);
            Assert.LessOrEqual(candidates.Count, 5);
            var snapshot = Data(UIToolkitFunctions.GetVisualTree(id, max_depth: 0, snapshot: true));
            Assert.AreEqual("whole_tree", (string)snapshot["snapshot_scope"]);
        }

        [UnityTest]
        public IEnumerator RunUiStepsRunsInOrderAndReportsTheFirstFailure()
        {
            yield return BuildLivePanel("UitkSteps");
            var id = ObjectIdHelper.GetSerializableId(created);
            var ok = UIToolkitFunctions.RunUiSteps("[{\"op\":\"wait\",\"target\":\"" + id + "\",\"selector\":\"#play\",\"state\":\"exists\"},{\"op\":\"sleep\",\"ms\":50}]");
            yield return Await(ok);
            var done = JObject.FromObject(ok.Result);
            Assert.IsTrue((bool)done["success"], done.ToString());
            Assert.AreEqual(2, (int)done["data"]["completed"]);

            var bad = UIToolkitFunctions.RunUiSteps("[{\"op\":\"sleep\",\"ms\":10},{\"op\":\"wait\",\"target\":\"" + id + "\",\"selector\":\"#missing\",\"state\":\"exists\",\"timeout_seconds\":0.2},{\"op\":\"sleep\",\"ms\":10}]");
            yield return Await(bad);
            var failed = JObject.FromObject(bad.Result);
            Assert.AreEqual("STEP_FAILED", (string)failed["code"], failed.ToString());
            Assert.AreEqual(1, (int)failed["data"]["completed"]);
            Assert.AreEqual(1, (int)failed["data"]["failed"]["index"]);
            Assert.AreEqual("wait", (string)failed["data"]["failed"]["op"]);
            Assert.AreEqual("WAIT_TIMEOUT", (string)failed["data"]["failed"]["code"]);
        }

        [Test]
        public void RunUiStepsRejectsBadInput()
        {
            Assert.AreEqual("INVALID_STEPS", (string)JObject.FromObject(UIToolkitFunctions.RunUiSteps("not json").Result)["code"]);
            Assert.AreEqual("INVALID_STEPS", (string)JObject.FromObject(UIToolkitFunctions.RunUiSteps("[]").Result)["code"]);
            var unknown = JObject.FromObject(UIToolkitFunctions.RunUiSteps("[{\"op\":\"fly\"}]").Result);
            Assert.AreEqual("INVALID_STEP", (string)unknown["data"]["failed"]["code"], unknown.ToString());
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
                "#play { margin: var(--a) var(--b); }\n" +
                ".primary:hover {\n    background-color: rgb(0, 0, 255);\n}\n");
            AssetDatabase.ImportAsset(ussPath, ImportAssetOptions.ForceSynchronousImport);
            liveRoot.styleSheets.Add(AssetDatabase.LoadAssetAtPath<StyleSheet>(ussPath));
            var play = liveRoot.Q<Button>("play");
            UIToolkitPanels.ValidateLayout(play.panel);
            yield return null;

            var explained = JObject.FromObject(UIToolkitFunctions.ExplainElement(play, "0/1", "background-color,color,width,margin-left,margin", new System.Collections.Generic.List<string> { "hover" }));
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
            Assert.AreEqual(8, (int)background["in_pseudo_states"][0]["line"], "the declaration's line, like the winner's");

            var color = properties.First(p => (string)p["property"] == "color");
            Assert.AreEqual("#play.primary", (string)color["winner"]["selector"]);
            Assert.AreEqual("1,1,0", (string)color["winner"]["specificity"]);

            var marginLeft = properties.First(p => (string)p["property"] == "margin-left");
            Assert.AreEqual("#play", (string)marginLeft["winner"]["selector"], "the margin shorthand beats the theme's longhand: " + marginLeft);
            Assert.AreEqual("margin", (string)marginLeft["winner"]["declared"]);
            Assert.AreEqual("var(--a) var(--b)", (string)marginLeft["winner"]["value"], "each function closes after its own arguments");
            var margin = properties.First(p => (string)p["property"] == "margin");
            Assert.AreEqual(4, ((JArray)margin["longhands"]).Count);
            Assert.AreEqual(4, ((string)margin["resolved"]).Split(' ').Length, "a shorthand resolves through its longhands: " + margin);

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

            var quoted = StyleIntrospection.ParseUss(".d {\n  background-image: url(\"a;b{c}.png\");\n  color: red;\n}");
            Assert.AreEqual(1, quoted.Count);
            Assert.AreEqual(2, quoted[0].Declarations.Count, "';' and braces inside a string don't split");
            Assert.AreEqual("url(\"a;b{c}.png\")", quoted[0].Declarations[0].Value);
            Assert.AreEqual(3, quoted[0].Declarations[1].Line);
        }

        [Test]
        public void StatePseudoClassesApplyToTheSubjectOnly()
        {
            var button = new Button();
            var hover = new System.Collections.Generic.List<string> { "hover" };
            Assert.AreEqual(".menu Button", StyleIntrospection.SubjectSelector(".menu :hover", button, hover, true), "the hovered element is the subject, not .menu");
            Assert.AreEqual("Button", StyleIntrospection.SubjectSelector(":hover", button, hover, true), "a bare :hover matches any hovered element");
            Assert.AreEqual("Button", StyleIntrospection.SubjectSelector("*:hover", button, hover, true));
            Assert.AreEqual(".row > .primary", StyleIntrospection.SubjectSelector(".row > .primary:hover", button, hover, true));
            Assert.IsNull(StyleIntrospection.SubjectSelector(".tabs:hover > .x", button, hover, true), "a pseudo-class on an ancestor can't be attributed");
            Assert.IsNull(StyleIntrospection.SubjectSelector(".primary:active", button, hover, true), "another state");
            Assert.IsNull(StyleIntrospection.SubjectSelector(".primary", button, hover, true), "no state pseudo-class at all");
            Assert.IsTrue(StyleIntrospection.Sets("margin", "margin-left"));
            Assert.IsTrue(StyleIntrospection.Sets("margin-left", "margin"));
            Assert.IsFalse(StyleIntrospection.Sets("margin", "padding-left"));
        }

        [Test]
        public void GlyphScanNeverThrowsAndSkipsInvisibleCharacters()
        {
            CollectionAssert.AreEqual(new uint[] { 'a', 0xD800, 'b' }, UIToolkitFunctions.CodePoints("a\uD800b"), "an unpaired surrogate is kept, not thrown on");
            CollectionAssert.AreEqual(new uint[] { 0x1F600 }, UIToolkitFunctions.CodePoints("\uD83D\uDE00"));
            foreach (var c in new uint[] { 0x200D, 0x200C, 0x200E, 0x2060, 0xFE0F, 0xFEFF, ' ' })
                Assert.IsTrue(UIToolkitFunctions.IsInvisible(c), $"U+{c:X4}");
            Assert.IsFalse(UIToolkitFunctions.IsInvisible('A'));
            Assert.IsFalse(UIToolkitFunctions.IsInvisible(0xD800), "unpaired surrogates are reported");

            var scroll = new ScrollView();
            var inner = new Button();
            scroll.Add(inner);
            var root = new VisualElement();
            root.Add(scroll);
            var outer = new Button();
            root.Add(outer);
            Assert.IsTrue(UIToolkitFunctions.IsInnerPart(inner, root));
            Assert.IsFalse(UIToolkitFunctions.IsInnerPart(outer, root));
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
            var faintDisabled = new Label("Off") { name = "faint-disabled", style = { color = new Color(0.45f, 0.45f, 0.45f), backgroundColor = new Color(0.5f, 0.5f, 0.5f), width = 200, height = 40 } };
            faintDisabled.SetEnabled(false);
            // Card pattern: an absolutely positioned image sibling behind the text, inside a solid card.
            var card = new VisualElement { style = { width = 200, height = 60, backgroundColor = new Color(0.5f, 0.5f, 0.5f) } };
            card.Add(new Image { style = { position = Position.Absolute, left = 0, right = 0, top = 0, bottom = 0 } });
            card.Add(new Label("On card") { name = "on-card", style = { color = new Color(0.45f, 0.45f, 0.45f) } });
            // Opacity applies to the group: white text on red inside a 50% blue group over white.
            var outerWhite = new VisualElement { style = { width = 200, height = 60, backgroundColor = Color.white } };
            var halfBlue = new VisualElement { style = { opacity = 0.5f, backgroundColor = Color.blue, flexGrow = 1 } };
            var onRed = new Label("Group") { name = "group-text", style = { color = Color.white, backgroundColor = Color.red, flexGrow = 1 } };
            halfBlue.Add(onRed); outerWhite.Add(halfBlue);
            menu.Add(tiny); menu.Add(faint); menu.Add(overImage); menu.Add(faintDisabled); menu.Add(card); menu.Add(outerWhite); liveRoot.Add(cover);
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
            Assert.IsFalse(Has("LOW_CONTRAST", "#faint-disabled"), "inactive controls are exempt");
            Assert.IsFalse(Has("LOW_CONTRAST", "#on-card"), "an image sibling behind the text makes contrast indeterminate");
            Assert.IsNull(UIToolkitFunctions.ContrastRatio(liveRoot.Q<Label>("on-card")));
            // Group compositing: text pixel white; background (red at 50%) over white = (1, .5, .5) linear.
            var expected = UIToolkitFunctions.ContrastRatio(Color.white, new Color(1f, 0.5f, 0.5f));
            Assert.AreEqual(expected, UIToolkitFunctions.ContrastRatio(liveRoot.Q<Label>("group-text")).Value, 0.02f, "the blue group background is hidden under the red label");
            if (UIToolkitPanels.TryGetScreenMapping(liveRoot.panel, out _) && RenderHeightOrZero() > 0)
                Assert.IsTrue(Has("COVERED_CONTROL", "#play"), json.ToString());

            // The same overlay dimming the screen is an open dialog's backdrop: blocking is its purpose.
            cover.style.backgroundColor = new Color(0f, 0f, 0f, 0.5f);
            UIToolkitPanels.ValidateLayout(liveRoot.panel);
            var modal = JObject.FromObject(UIToolkitFunctions.AuditHosts(new System.Collections.Generic.List<Component> { host }, new UIToolkitFunctions.AuditOptions()).ToResponse());
            Assert.IsFalse(((JArray)modal["hosts"][0]["findings"]).Any(f => (string)f["code"] == "COVERED_CONTROL"), modal.ToString());
            Assert.IsNull(modal["counts"]["COVERED_CONTROL"], modal.ToString());
            if (UIToolkitPanels.TryGetScreenMapping(liveRoot.panel, out _) && RenderHeightOrZero() > 0)
                Assert.IsTrue(((JArray)modal["hosts"][0]["blocked_by_modal"]).Any(b => ((string)b["element"]).Contains("#play") && ((string)b["modal"]).Contains("#cover")), modal.ToString());

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
            var cache = new System.Collections.Generic.Dictionary<(UnityEngine.TextCore.Text.FontAsset, uint), bool>();
            CollectionAssert.AreEqual(missing, UIToolkitFunctions.MissingGlyphs("AB \uE000", asset, null, cache), "checking is repeatable (nothing was added)");
            Assert.IsTrue(cache.ContainsKey((asset, 0xE000u)), "answers are cached for the rest of the audit");
            CollectionAssert.AreEqual(missing, UIToolkitFunctions.MissingGlyphs("AB \uE000", asset, null, cache));
            Assert.IsNull(UIToolkitFunctions.MissingGlyphs("A", null, null), "unknown font: no verdict");
        }

        [UnityTest]
        public IEnumerator DiffVisualTreeReportsOnlyWhatChanged()
        {
            yield return BuildLivePanel("UitkDiff");
            var id = ObjectIdHelper.GetSerializableId(created);
            var menu = liveRoot.Q("menu");
            var dialog = new VisualElement { name = "dialog" };
            dialog.style.position = Position.Absolute;
            dialog.style.display = DisplayStyle.None;
            for (var i = 0; i < 3; i++) dialog.Add(new Label("row " + i));
            menu.Insert(0, dialog);
            UIToolkitPanels.ValidateLayout(menu.panel);
            yield return null;

            var missing = JObject.FromObject(UIToolkitFunctions.DiffVisualTree("vt_missing"));
            Assert.AreEqual("SNAPSHOT_NOT_FOUND", (string)missing["code"]);
            var snapshotId = (string)Data(UIToolkitFunctions.GetVisualTree(id, "#title", snapshot: true))["snapshot_id"];
            Assert.IsNotNull(snapshotId);

            // Bring the dialog to the front (every sibling's index path shifts), open it, retitle.
            dialog.BringToFront();
            dialog.style.display = DisplayStyle.Flex;
            dialog.AddToClassList("dialog--open");
            liveRoot.Q<Label>("title").text = "Paused";
            var diff = Data(UIToolkitFunctions.DiffVisualTree(snapshotId, update_snapshot: true));
            var changed = (JArray)diff["changed"];
            Assert.AreEqual(2, changed.Count, "the dialog and the title, nothing it contains or reordered: " + diff);
            var dialogChange = changed.First(c => ((string)c["element"]).Contains("#dialog"));
            Assert.IsFalse((bool)dialogChange["changes"]["displayed"]["before"]);
            Assert.IsTrue((bool)dialogChange["changes"]["displayed"]["after"]);
            Assert.AreEqual("dialog--open", (string)dialogChange["changes"]["classes"]["added"][0]);
            var titleChange = changed.First(c => ((string)c["element"]).Contains("#title"));
            Assert.AreEqual("Paused", (string)titleChange["changes"]["text"]["after"]);
            Assert.AreEqual(0, ((JArray)diff["added"]).Count);
            Assert.AreEqual(0, ((JArray)diff["removed"]).Count);

            Assert.AreEqual(0, (int)Data(UIToolkitFunctions.DiffVisualTree(snapshotId))["counts"]["changed"], "update_snapshot rebased it");

            // Moving a row into another parent keeps its parent-relative layout; the parent change is reported.
            var row = dialog[0];
            var otherSlot = new VisualElement { name = "slot" };
            otherSlot.style.position = Position.Absolute;
            dialog.Add(otherSlot);
            var moveSnapshot = (string)Data(UIToolkitFunctions.GetVisualTree(id, "#title", snapshot: true))["snapshot_id"];
            otherSlot.Add(row);
            var moved = Data(UIToolkitFunctions.DiffVisualTree(moveSnapshot));
            var rowChange = ((JArray)moved["changed"]).First(c => c["changes"]["parent"] != null);
            StringAssert.Contains("#slot", (string)rowChange["changes"]["parent"]["after"], moved.ToString());
            Data(UIToolkitFunctions.DiffVisualTree(snapshotId, update_snapshot: true));

            liveRoot.Q<Button>("quit").RemoveFromHierarchy();
            var panel = new VisualElement { name = "extra" };
            panel.Add(new Label("a"));
            panel.Add(new Label("b"));
            menu.Add(panel);
            var structural = Data(UIToolkitFunctions.DiffVisualTree(snapshotId));
            Assert.AreEqual(1, ((JArray)structural["added"]).Count, structural.ToString());
            Assert.AreEqual(3, (int)structural["added"][0]["subtree_size"], "one entry per added subtree");
            Assert.AreEqual(1, ((JArray)structural["removed"]).Count, structural.ToString());
            StringAssert.Contains("#quit", (string)structural["removed"][0]["element"]);
        }

        [UnityTest]
        public IEnumerator SetUiToolkitValueFiresChangeEvents()
        {
            yield return BuildLivePanel("UitkSetValue");
            var id = ObjectIdHelper.GetSerializableId(created);
            var menu = liveRoot.Q("menu");
            var slider = new Slider("Volume", 0, 10) { name = "volume" };
            var toggle = new Toggle("Music") { name = "music" };
            var field = new TextField { name = "player" };
            var dropdown = new DropdownField(new System.Collections.Generic.List<string> { "Easy", "Normal", "Hard" }, 0) { name = "difficulty" };
            var group = new ToggleButtonGroup { name = "speed" };
            for (var i = 0; i < 3; i++) group.Add(new Button { text = "x" + (i + 1) });
            foreach (var control in new VisualElement[] { slider, toggle, field, dropdown, group })
            {
                control.style.width = 200; control.style.height = 30;
                menu.Add(control);
            }
            UIToolkitPanels.ValidateLayout(menu.panel);
            yield return null;
            var sliderChanges = 0;
            slider.RegisterValueChangedCallback(_ => sliderChanges++);

            var set = Data(UIToolkitFunctions.SetUiToolkitValue(id, "#volume", value: "4"));
            Assert.IsTrue((bool)set["change_event"], set.ToString());
            Assert.AreEqual(4f, (float)set["new_value"]);
            Assert.AreEqual(1, sliderChanges, "ChangeEvent reached the game's callback");
            var clamped = Data(UIToolkitFunctions.SetUiToolkitValue(id, "#volume", value: "20"));
            Assert.AreEqual(10f, (float)clamped["new_value"]);
            Assert.IsTrue((bool)clamped["clamped"], clamped.ToString());
            Assert.IsFalse((bool)Data(UIToolkitFunctions.SetUiToolkitValue(id, "#volume", value: "10"))["change_event"], "same value: no event");

            // Matching the inner checkbox sets the Toggle.
            Assert.IsTrue((bool)Data(UIToolkitFunctions.SetUiToolkitValue(id, "#music .unity-toggle__checkmark", value: "on"))["change_event"]);
            Assert.IsTrue(toggle.value);
            Assert.AreEqual("Hard", (string)Data(UIToolkitFunctions.SetUiToolkitValue(id, "#difficulty", value: "2"))["new_value"]);
            Assert.AreEqual("Normal", (string)Data(UIToolkitFunctions.SetUiToolkitValue(id, "#difficulty", value: "normal"))["new_value"]);
            Assert.AreEqual("INVALID_VALUE", (string)JObject.FromObject(UIToolkitFunctions.SetUiToolkitValue(id, "#difficulty", value: "Impossible"))["code"]);
            var groupSet = Data(UIToolkitFunctions.SetUiToolkitValue(id, "#speed", value: "0,x3"));
            CollectionAssert.AreEqual(new[] { 0, 2 }, groupSet["new_value"].ToObject<int[]>(), groupSet.ToString());
            Assert.AreEqual("USE_CLICK", (string)JObject.FromObject(UIToolkitFunctions.SetUiToolkitValue(id, "#play", value: "1"))["code"]);
            // The field's label text matches the Label, which must set the Slider, not relabel it.
            var byLabel = Data(UIToolkitFunctions.SetUiToolkitValue(id, null, "Volume", value: "3"));
            Assert.AreEqual(3f, slider.value, byLabel.ToString());
            Assert.AreEqual("Volume", slider.label);
            Assert.AreEqual("NOT_A_VALUE_CONTROL", (string)JObject.FromObject(UIToolkitFunctions.SetUiToolkitValue(id, "#title", value: "x"))["code"], "a bare Label with several controls around it is ambiguous");
            // A settings row: caption Label beside its Slider, as PolyGames builds them.
            var row = new VisualElement { name = "row" };
            row.Add(new Label("Brightness"));
            var brightness = new Slider(0, 1) { style = { width = 200, height = 30 } };
            row.Add(brightness);
            menu.Add(row);
            var beside = Data(UIToolkitFunctions.SetUiToolkitValue(id, null, "Brightness", value: "0.5"));
            Assert.AreEqual(0.5f, brightness.value, beside.ToString());
            StringAssert.Contains("Brightness", (string)beside["matched_label"]);
            field.isReadOnly = true;
            Assert.AreEqual("ELEMENT_READ_ONLY", (string)JObject.FromObject(UIToolkitFunctions.SetUiToolkitValue(id, "#player", value: "x"))["code"]);
            field.isReadOnly = false;

            var typed = Data(UIToolkitFunctions.SetUiToolkitValue(id, "#player", type_text: "Ada"));
            Assert.AreEqual("Ada", field.value, typed.ToString());
            Debug.Log("[Funplay test] synthesized typing typed=" + typed["typed"]);
            Assert.IsTrue((bool)typed["typed"], "KeyDownEvents reach the TextField: " + typed);
            Assert.IsTrue((bool)typed["change_event"]);
        }

        [UnityTest]
        public IEnumerator NavigateUiToolkitMovesFocusAndSubmits()
        {
            yield return BuildLivePanel("UitkNavigate");
            var id = ObjectIdHelper.GetSerializableId(created);
            var quit = liveRoot.Q<Button>("quit");
            var clicks = 0;
            quit.clicked += () => clicks++;

            Assert.AreEqual("NOTHING_FOCUSED", (string)JObject.FromObject(UIToolkitFunctions.NavigateUiToolkit("submit", id))["code"]);
            var focus = Data(UIToolkitFunctions.NavigateUiToolkit("focus", id, "#play"));
            StringAssert.Contains("#play", (string)focus["focused_after"], focus.ToString());
            var moved = Data(UIToolkitFunctions.NavigateUiToolkit("move:next", id));
            StringAssert.Contains("#quit", (string)moved["focused_after"], moved.ToString());
            var submit = Data(UIToolkitFunctions.NavigateUiToolkit("submit", id));
            Assert.AreEqual(1, clicks, submit.ToString());
            Assert.IsTrue((bool)submit["handler_invoked"]);
            Assert.AreEqual("INVALID_ACTION", (string)JObject.FromObject(UIToolkitFunctions.NavigateUiToolkit("move:sideways", id))["code"]);
        }

        [UnityTest]
        public IEnumerator PointerUiToolkitHoversDragsAndLongPresses()
        {
            yield return BuildLivePanel("UitkPointer");
            var id = ObjectIdHelper.GetSerializableId(created);
            var handle = new VisualElement { name = "handle" };
            handle.style.width = 60; handle.style.height = 60; handle.style.backgroundColor = Color.gray;
            liveRoot.Q("menu").Add(handle);
            int downs = 0, moves = 0, ups = 0, pointerId = -1;
            float movedX = 0;
            handle.RegisterCallback<PointerDownEvent>(e => { downs++; pointerId = e.pointerId; handle.CapturePointer(e.pointerId); });
            handle.RegisterCallback<PointerMoveEvent>(e => { if (handle.HasPointerCapture(e.pointerId)) { moves++; movedX += e.deltaPosition.x; } });
            handle.RegisterCallback<PointerUpEvent>(e => { ups++; handle.ReleasePointer(e.pointerId); });
            UIToolkitPanels.ValidateLayout(handle.panel);
            yield return null;

            var hover = UIToolkitFunctions.PointerUiToolkit("hover", id, "#play");
            yield return Await(hover);
            var hovered = Data(hover.Result);
            CollectionAssert.Contains(hovered["pseudo_states"].ToObject<string[]>(), "hover", hovered.ToString());

            var drag = UIToolkitFunctions.PointerUiToolkit("drag", id, "#handle", delta: "50,0", duration_ms: 100);
            yield return Await(drag);
            var dragged = Data(drag.Result);
            Assert.AreEqual(1, downs); Assert.AreEqual(1, ups, $"downs {downs} moves {moves} ups {ups} movedX {movedX} " + dragged["captured_by"] + " " + dragged["drop_target"] + " " + dragged["moves"]);
            Assert.GreaterOrEqual(moves, 4, "moves while captured: " + dragged);
            StringAssert.Contains("#handle", (string)dragged["captured_by"]);
            Assert.AreEqual(50f, movedX, 1f, "the moves add up to the delta");

            var clock = System.Diagnostics.Stopwatch.StartNew();
            var press = UIToolkitFunctions.PointerUiToolkit("long_press", id, "#handle", duration_ms: 200);
            Assert.AreEqual(2, downs, "down at once");
            Assert.AreEqual(1, ups, "still held");
            yield return Await(press);
            Assert.AreEqual(2, ups);
            Assert.GreaterOrEqual(clock.ElapsedMilliseconds, 190);

            var touch = UIToolkitFunctions.PointerUiToolkit("long_press", id, "#handle", duration_ms: 0, pointer_type: "touch");
            yield return Await(touch);
            var touched = Data(touch.Result);
            Assert.AreEqual(pointerId, (int)touched["pointer_id"], touched.ToString());
            Debug.Log("[Funplay test] touch pointer id " + pointerId);
            Assert.AreNotEqual(PointerId.mousePointerId, pointerId, "touch events carry a touch pointer ID");
        }

        [UnityTest]
        public IEnumerator StyleUiToolkitAppliesReportsRevertsAndReloadsSheets()
        {
            yield return BuildLivePanel("UitkStyle");
            var id = ObjectIdHelper.GetSerializableId(created);
            var play = liveRoot.Q<Button>("play");

            var bad = UIToolkitFunctions.StyleUiToolkit(id, "#play", inline: "{\"color\":\"var(--x)\"}");
            Assert.AreEqual("INVALID_INLINE_STYLE", (string)JObject.FromObject(bad.Result)["code"]);
            var unknown = UIToolkitFunctions.StyleUiToolkit(id, "#play", inline: "{\"colour\":\"red\"}");
            var transparent = UIToolkitFunctions.StyleUiToolkit(id, "#quit", inline: "{\"background-color\":\"transparent\",\"-unity-font-style\":\"bold\"}");
            yield return Await(transparent);
            var clear = Data(transparent.Result);
            Assert.AreEqual(Color.clear, liveRoot.Q<Button>("quit").resolvedStyle.backgroundColor, clear.ToString());
            Assert.AreEqual("bold", (string)((JArray)clear["styles"]).First(x => (string)x["property"] == "-unity-font-style")["resolved_after"], "the alias resolves");
            Assert.AreEqual("UNSUPPORTED_STYLE_PROPERTY", (string)JObject.FromObject(unknown.Result)["code"]);

            // A theme transition on the property: the report waits for the end value instead of a midpoint.
            var quit = liveRoot.Q<Button>("quit");
            quit.style.transitionProperty = new System.Collections.Generic.List<StylePropertyName> { "background-color" };
            quit.style.transitionDuration = new System.Collections.Generic.List<TimeValue> { new TimeValue(0.25f, TimeUnit.Second) };
            yield return null;
            var animated = UIToolkitFunctions.StyleUiToolkit(id, "#quit", inline: "{\"background-color\":\"#ff0000\"}");
            yield return Await(animated);
            var animatedData = Data(animated.Result);
            if (animatedData["transitions_running"] != null && animatedData["transitions_running"].Type != JTokenType.Null)
                Assert.IsTrue((bool)animatedData["styles"][0]["transitioning"], "a capped wait flags the mid-transition value: " + animatedData);
            else
                Assert.AreEqual("#FF0000FF", (string)animatedData["styles"][0]["resolved_after"], "the end value, not a midpoint: " + animatedData);

            var styled = UIToolkitFunctions.StyleUiToolkit(id, "#play", add_classes: "big", inline: "{\"background-color\":\"rgb(0, 255, 0)\",\"width\":\"123px\",\"margin\":\"1px 2px\",\"justify-content\":\"flex-end\"}");
            yield return Await(styled);
            var data = Data(styled.Result);
            Assert.IsTrue(play.ClassListContains("big"));
            Assert.AreEqual(Color.green, play.resolvedStyle.backgroundColor);
            Assert.AreEqual(123f, play.resolvedStyle.width, 0.01f);
            Assert.AreEqual(2f, play.resolvedStyle.marginLeft, 0.01f, "margin shorthand: top/bottom 1px, left/right 2px");
            Assert.AreEqual(Justify.FlexEnd, play.resolvedStyle.justifyContent);
            Assert.AreEqual(JTokenType.Null, data["reverted"].Type, data.ToString());
            Assert.IsTrue((bool)data["runtime_only"]);

            // Project code that undoes a class on the next update is reported.
            EditorApplication.CallbackFunction undo = null;
            undo = () => { EditorApplication.update -= undo; play.RemoveFromClassList("wide"); };
            var watched = UIToolkitFunctions.StyleUiToolkit(id, "#play", add_classes: "wide", inline: "{\"width\":null}");
            EditorApplication.update += undo;
            yield return Await(watched);
            var reverted = Data(watched.Result);
            StringAssert.Contains(".wide", (string)reverted["reverted"][0], reverted.ToString());
            Assert.AreEqual(StyleKeyword.Null, play.style.width.keyword, "null removes the inline width (a UXML style attribute's included)");
            Assert.Less(play.resolvedStyle.width, 123f, "USS rules apply again");

            var ussPath = folder + "/Live.uss";
            File.WriteAllText(ussPath, ".tint { color: rgb(255, 0, 0); }\n");
            AssetDatabase.ImportAsset(ussPath, ImportAssetOptions.ForceSynchronousImport);
            liveRoot.styleSheets.Add(AssetDatabase.LoadAssetAtPath<StyleSheet>(ussPath));
            play.AddToClassList("tint");
            UIToolkitPanels.ValidateLayout(play.panel);
            Assert.AreEqual(Color.red, play.resolvedStyle.color);
            File.WriteAllText(ussPath, ".tint { color: rgb(0, 0, 255); }\n");
            var reload = UIToolkitFunctions.StyleUiToolkit(id, reload_stylesheets: true);
            yield return Await(reload);
            var reloaded = Data(reload.Result);
            CollectionAssert.Contains(reloaded["reload"]["imported"].ToObject<string[]>(), ussPath, reloaded.ToString());
            Assert.AreEqual(Color.blue, play.resolvedStyle.color, "the edited USS applies to the live panel");
        }

        [UnityTest]
        public IEnumerator AuditIgnoresBuiltInControlInternals()
        {
            yield return BuildLivePanel("UitkAuditInternals");
            var id = ObjectIdHelper.GetSerializableId(created);
            var menu = liveRoot.Q("menu");
            // Two Sliders: picking-ignored roots with pickable drag containers, and duplicate unity-* part names.
            for (var i = 0; i < 2; i++) menu.Add(new Slider(0, 1) { name = "volume" + i, style = { width = 200, height = 30 } });
            var blocked = new Button { name = "blocked", text = "Blocked", pickingMode = PickingMode.Ignore, style = { width = 200, height = 30 } };
            menu.Add(blocked);
            UIToolkitPanels.ValidateLayout(menu.panel);
            yield return null;
            Assert.AreEqual(PickingMode.Ignore, menu.Q<Slider>("volume0").pickingMode, "Unity's Slider root ignores picking by design");

            var findings = (JArray)Data(UIToolkitFunctions.GetVisualTree(id, all_findings: true))["hosts"][0]["findings"];
            var ignored = findings.Where(f => (string)f["code"] == "CONTROL_PICKING_IGNORED").Select(f => (string)f["element"]).ToList();
            CollectionAssert.AreEqual(new[] { "Button#blocked \"Blocked\"" }, ignored, findings.ToString());
            Assert.IsFalse(findings.Any(f => (string)f["code"] == "DUPLICATE_NAME" && ((string)f["element"]).StartsWith("#unity-")), findings.ToString());
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
