// Copyright (C) Funplay. Licensed under MIT.
using System;
using System.IO;
using System.Linq;
using Funplay.Editor.Tools.Builtins;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Funplay.Editor.Tests
{
    public sealed class ChangedAssetsFunctionsTests
    {
        private string _repo;

        [TearDown]
        public void TearDown()
        {
            if (_repo == null || !Directory.Exists(_repo)) return;
            // git marks object files read-only.
            foreach (var file in Directory.GetFiles(_repo, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(_repo, true);
        }

        [Test]
        public void PorcelainParsingHandlesRenamesUntrackedAndTheProjectPrefix()
        {
            var output = " M game/Assets/a.uss\0R  game/Assets/new.uxml\0game/Assets/old.uxml\0?? game/Assets/c.txt\0D  game/ProjectSettings/x.asset\0A  game/Assets/d.cs\0";
            var changes = ChangedAssetsFunctions.ParsePorcelain(output, "game/");
            Assert.AreEqual(5, changes.Count);
            Assert.AreEqual("Assets/a.uss", changes[0].Path);
            Assert.AreEqual("modified", changes[0].Status);
            Assert.AreEqual("M", changes[0].WorktreeStatus);
            Assert.AreEqual("renamed", changes[1].Status);
            Assert.AreEqual("Assets/new.uxml", changes[1].Path);
            Assert.AreEqual("Assets/old.uxml", changes[1].RenamedFrom);
            Assert.AreEqual("untracked", changes[2].Status);
            Assert.AreEqual("deleted", changes[3].Status);
            Assert.AreEqual("added", changes[4].Status);
        }

        [Test]
        public void IdenticalRewritesAreNotReportedButRealChangesAre()
        {
            _repo = Path.Combine(Path.GetTempPath(), "FunplayChangedAssets_" + Guid.NewGuid().ToString("N"));
            var project = Path.Combine(_repo, "game");
            Directory.CreateDirectory(Path.Combine(project, "Assets"));
            if (Git(_repo, "init -q").Code == "GIT_UNAVAILABLE") Assert.Ignore("git is not installed.");
            File.WriteAllText(Path.Combine(project, "Assets", "same.uss"), ".a { color: red; }\n");
            File.WriteAllText(Path.Combine(project, "Assets", "edited.uss"), ".b { color: red; }\n");
            File.WriteAllText(Path.Combine(_repo, "outside.txt"), "x");
            Assert.IsNull(Git(_repo, "add -A").Error);
            Assert.IsNull(Git(_repo, "-c user.name=t -c user.email=t@example.com -c commit.gpgsign=false commit -q -m init").Error);

            // A no-op rebuild: same bytes, new timestamp.
            var same = Path.Combine(project, "Assets", "same.uss");
            File.WriteAllText(same, ".a { color: red; }\n");
            File.SetLastWriteTimeUtc(same, DateTime.UtcNow.AddMinutes(1));
            File.WriteAllText(Path.Combine(project, "Assets", "edited.uss"), ".b { color: blue; }\n");
            File.WriteAllText(Path.Combine(project, "Assets", "new.uss"), ".c {}\n");
            File.WriteAllText(Path.Combine(_repo, "outside.txt"), "changed outside the project");

            var result = JObject.FromObject(ChangedAssetsFunctions.ListAsync(project, new[] { "Assets" }, true, 100).Result);
            Assert.IsTrue((bool)result["success"], result.ToString());
            var changes = (JArray)result["data"]["changes"];
            CollectionAssert.AreEquivalent(new[] { "Assets/edited.uss", "Assets/new.uss" }, changes.Select(c => (string)c["path"]).ToArray(), result.ToString());
            Assert.AreEqual("modified", (string)changes.First(c => (string)c["path"] == "Assets/edited.uss")["status"]);
            Assert.AreEqual("untracked", (string)changes.First(c => (string)c["path"] == "Assets/new.uss")["status"]);

            var tracked = JObject.FromObject(ChangedAssetsFunctions.ListAsync(project, new[] { "Assets" }, false, 100).Result);
            Assert.AreEqual(1, (int)tracked["data"]["count"], "include_untracked=false");
        }

        [Test]
        public void OutsideAGitRepositoryIsUnsupported()
        {
            _repo = Path.Combine(Path.GetTempPath(), "FunplayNoGit_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_repo);
            if (Git(_repo, "--version").Code == "GIT_UNAVAILABLE") Assert.Ignore("git is not installed.");
            // A temp folder could sit inside some repository on a developer machine; only check when it doesn't.
            if (Git(_repo, "rev-parse --show-toplevel").Error == null) Assert.Ignore("The temp folder is inside a git repository.");
            var result = JObject.FromObject(ChangedAssetsFunctions.ListAsync(_repo, new[] { "Assets" }, true, 10).Result);
            Assert.AreEqual("NOT_A_GIT_REPOSITORY", (string)result["code"], result.ToString());
        }

        private static ChangedAssetsFunctions.GitResult Git(string directory, string arguments) =>
            ChangedAssetsFunctions.RunGitAsync(directory, arguments).Result;
    }
}
