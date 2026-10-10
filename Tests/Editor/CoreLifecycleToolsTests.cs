// Copyright (C) Funplay. Licensed under MIT.

using System;
using System.IO;
using Funplay.Editor.Tools.Builtins;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;

namespace Funplay.Editor.Tests
{
    public sealed class CoreLifecycleToolsTests
    {
        private string _folder;

        [TearDown]
        public void TearDown()
        {
            if (_folder != null && AssetDatabase.IsValidFolder(_folder))
                AssetDatabase.DeleteAsset(_folder);
            _folder = null;
        }

        [TestCase("Packages/com.unity.ugui/package.json")]
        [TestCase("ProjectSettings/ProjectSettings.asset")]
        [TestCase("Assets")]
        [TestCase("Assets/../ProjectSettings/x.asset")]
        [TestCase("C:/Windows/notepad.exe")]
        [TestCase("")]
        public void DeleteAssetRefusesPathsOutsideAssets(string path)
        {
            Assert.AreEqual("INVALID_ASSET_PATH", Code(AssetFunctions.DeleteAsset(path)));
        }

        [Test]
        public void DeleteAssetMovesFilesToTrashAndRefusesFoldersWithoutRecursive()
        {
            _folder = AssetDatabase.GUIDToAssetPath(AssetDatabase.CreateFolder("Assets", "FunplayDeleteTest_" + Guid.NewGuid().ToString("N")));
            var file = _folder + "/note.txt";
            File.WriteAllText(file, "x");
            AssetDatabase.ImportAsset(file);

            Assert.AreEqual("FOLDER_REQUIRES_RECURSIVE", Code(AssetFunctions.DeleteAsset(_folder)));
            Assert.IsTrue(AssetDatabase.IsValidFolder(_folder));

            var result = JObject.FromObject(AssetFunctions.DeleteAsset(file));
            Assert.IsTrue((bool)result["success"], result.ToString());
            Assert.IsTrue((bool)result["data"]["recoverable"]);
            Assert.IsFalse(File.Exists(file));
            Assert.AreEqual("ASSET_NOT_FOUND", Code(AssetFunctions.DeleteAsset(file)));

            Assert.IsTrue((bool)JObject.FromObject(AssetFunctions.DeleteAsset(_folder, recursive: true))["success"]);
            Assert.IsFalse(AssetDatabase.IsValidFolder(_folder));
        }

        [Test]
        public void SceneReplacementNeverPromptsAndValidatesThePolicy()
        {
            Assert.AreEqual("INVALID_DIRTY_SCENES_POLICY", Code(SceneFunctions.HandleDirtyScenes("ask", out _, out _)));
            Assert.AreEqual("INVALID_SCENE_PATH", Code(SceneFunctions.OpenScene("Scenes/Main.unity")));
            Assert.AreEqual("INVALID_SCENE_NAME", Code(SceneFunctions.CreateNewScene("bad/name")));
            Assert.AreEqual("INVALID_SCENE_PATH", Code(SceneFunctions.CreateNewScene("Ok", "Packages/x/")));
        }

        private static string Code(object response)
        {
            return (string)JObject.FromObject(response)["code"];
        }
    }
}
