// Copyright (C) Funplay. Licensed under MIT.

using System;
using System.IO;
using System.Linq;
using Funplay.Editor.MCP.Server;
using Funplay.Editor.Settings;
using NUnit.Framework;
using UnityEngine;

namespace Funplay.Editor.Tests
{
    public sealed class ToolExposureMigrationTests
    {
        private TempSettingsScope _scope;
        private string _projectPath;

        [SetUp]
        public void SetUp()
        {
            _scope = new TempSettingsScope();
            _projectPath = _scope.ProjectPath;
        }

        [TearDown]
        public void TearDown()
        {
            _scope.Dispose();
        }

        [Test]
        public void SnapshotsMatchTheReleasedToolSets()
        {
            Assert.AreEqual(188, MCPToolExposureMigration.ToolsV083.Count);
            Assert.AreEqual(45, MCPToolExposureMigration.CoreToolsV083.Count);
            Assert.IsTrue(MCPToolExposureMigration.CoreToolsV083.All(MCPToolExposureMigration.ToolsV083.Contains));
        }

        [Test]
        public void LegacyFullListWithEveryToolBecomesDefaultAndExposesNewTools()
        {
            WriteLegacySettings("full", fullTools: MCPToolExposureMigration.ToolsV083.ToArray());

            var settings = LoadSettings();

            Assert.IsFalse(settings.MCPFullToolsConfigured);
            CollectionAssert.IsEmpty(settings.MCPFullToolsExcluded);
            Assert.IsTrue(MCPToolExportPolicy.IsToolAllowed("tool_added_after_0_8_3", settings));
            Assert.IsTrue(MCPToolExportPolicy.IsToolAllowed("execute_code", settings));
            StringAssert.Contains("\"fullToolsCustom\": false", ReadSettings());
        }

        [Test]
        public void LegacyFullListMissingToolsBecomesExclusions()
        {
            var saved = MCPToolExposureMigration.ToolsV083.Where(tool => tool != "execute_code" && tool != "show_dialog").ToArray();
            WriteLegacySettings("full", fullTools: saved);

            var settings = LoadSettings();

            CollectionAssert.AreEquivalent(new[] { "execute_code", "show_dialog" }, settings.MCPFullToolsExcluded);
            Assert.IsFalse(MCPToolExportPolicy.IsToolAllowed("execute_code", settings));
            Assert.IsTrue(MCPToolExportPolicy.IsToolAllowed("tool_added_after_0_8_3", settings));
        }

        [Test]
        public void LegacyCoreListEqualToOldDefaultFollowsTheCurrentDefault()
        {
            WriteLegacySettings("core", coreTools: MCPToolExposureMigration.CoreToolsV083.ToArray());

            var settings = LoadSettings();

            Assert.IsFalse(settings.MCPCoreToolsConfigured);
        }

        [Test]
        public void LegacyCustomCoreListStaysAuthoritative()
        {
            WriteLegacySettings("core", coreTools: new[] { "get_task", "execute_code" });

            var settings = LoadSettings();

            Assert.IsTrue(settings.MCPCoreToolsConfigured);
            Assert.IsTrue(MCPToolExportPolicy.IsToolAllowed("get_task", settings));
            Assert.IsFalse(MCPToolExportPolicy.IsToolAllowed("get_hierarchy", settings));
        }

        [Test]
        public void MigrationRunsOnceAndExclusionsRoundTrip()
        {
            WriteLegacySettings("full", fullTools: new[] { "get_task" });
            var first = LoadSettings();
            Assert.Greater(first.MCPFullToolsExcluded.Length, 100);

            first.MCPFullToolsExcluded = new[] { "show_dialog" };
            var second = LoadSettings();

            CollectionAssert.AreEqual(new[] { "show_dialog" }, second.MCPFullToolsExcluded);
            second.MCPFullToolsExcluded = null;
            Assert.IsFalse(LoadSettings().MCPFullToolsConfigured);
        }

        [Test]
        public void DefaultCoreSelectionIsDetected()
        {
            Assert.IsTrue(MCPToolExportPolicy.IsDefaultCoreSelection(MCPToolExportPolicy.DefaultCoreTools.Reverse()));
            Assert.IsFalse(MCPToolExportPolicy.IsDefaultCoreSelection(MCPToolExportPolicy.DefaultCoreTools.Skip(1)));
        }

        private void WriteLegacySettings(string profile, string[] fullTools = null, string[] coreTools = null)
        {
            var json = JsonUtility.ToJson(new LegacySettings
            {
                settingsVersion = 1,
                port = 8765,
                portConfigured = true,
                toolExportProfile = profile,
                fullToolsCustom = fullTools != null,
                fullTools = fullTools ?? new string[0],
                coreToolsCustom = coreTools != null,
                coreTools = coreTools ?? new string[0]
            });
            File.WriteAllText(Path.Combine(_projectPath, "UserSettings", "FunplayMcpSettings.json"), json);
        }

        private SettingsController LoadSettings()
        {
            return _scope.Load();
        }

        private string ReadSettings()
        {
            return File.ReadAllText(Path.Combine(_projectPath, "UserSettings", "FunplayMcpSettings.json"));
        }

        [Serializable]
        private class LegacySettings
        {
            public int settingsVersion;
            public int port;
            public bool portConfigured;
            public string toolExportProfile;
            public bool coreToolsCustom;
            public string[] coreTools;
            public bool fullToolsCustom;
            public string[] fullTools;
        }
    }
}
