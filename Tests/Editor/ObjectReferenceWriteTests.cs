// Copyright (C) Funplay. Licensed under MIT.

using System;
using Funplay.Editor.Tools.Helpers;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Funplay.Editor.Tests
{
    /// <summary>
    /// set_component_property with {"assetPath"} on a C# property name (panelSettings) used to miss the
    /// serialized m_PanelSettings, fall back to reflection and let Newtonsoft construct a fresh, nameless
    /// PanelSettings that was then saved embedded in the scene.
    /// </summary>
    public sealed class ObjectReferenceWriteTests
    {
        private string _folder;
        private GameObject _host;

        [SetUp]
        public void SetUp()
        {
            _folder = AssetDatabase.GUIDToAssetPath(AssetDatabase.CreateFolder("Assets", "FunplayRefTest_" + Guid.NewGuid().ToString("N")));
        }

        [TearDown]
        public void TearDown()
        {
            if (_host != null) UnityEngine.Object.DestroyImmediate(_host);
            AssetDatabase.DeleteAsset(_folder);
        }

        [Test]
        public void CSharpPropertyNameWithAssetPathAssignsThePersistentAsset()
        {
            var path = _folder + "/Panel.asset";
            AssetDatabase.CreateAsset(ScriptableObject.CreateInstance<PanelSettings>(), path);
            var asset = AssetDatabase.LoadAssetAtPath<PanelSettings>(path);
            _host = new GameObject("RefHost", typeof(UIDocument));
            var document = _host.GetComponent<UIDocument>();

            var results = ComponentSerializer.WriteProperties(document, new JObject { ["panelSettings"] = new JObject { ["assetPath"] = path } });

            Assert.IsTrue(results[0].Success, results[0].Error);
            Assert.AreSame(asset, document.panelSettings);
            Assert.IsTrue(EditorUtility.IsPersistent(document.panelSettings));
            var readback = new SerializedObject(document).FindProperty("m_PanelSettings").objectReferenceValue;
            Assert.AreSame(asset, readback);
        }

        [Test]
        public void WrongTypeReferenceFailsInsteadOfSilentlyStoringNull()
        {
            var path = _folder + "/Tex.asset";
            AssetDatabase.CreateAsset(new Texture2D(2, 2), path);
            _host = new GameObject("RefHost", typeof(UIDocument));
            var document = _host.GetComponent<UIDocument>();

            var results = ComponentSerializer.WriteProperties(document, new JObject { ["panelSettings"] = new JObject { ["assetPath"] = path } });

            Assert.IsFalse(results[0].Success);
            StringAssert.Contains("PanelSettings", results[0].Error);
            Assert.IsNull(document.panelSettings);
        }

        [Test]
        public void ReflectionPathNeverConstructsUnityObjectsFromJson()
        {
            _host = new GameObject("RefHost", typeof(MeshRenderer));
            var renderer = _host.GetComponent<MeshRenderer>();

            // sharedMaterial is not a serialized property name, so this goes through reflection.
            var results = ComponentSerializer.WriteProperties(renderer, new JObject { ["sharedMaterial"] = new JObject { ["name"] = "fresh" } });

            Assert.IsFalse(results[0].Success);
            Assert.IsNull(renderer.sharedMaterial);
        }
    }
}
