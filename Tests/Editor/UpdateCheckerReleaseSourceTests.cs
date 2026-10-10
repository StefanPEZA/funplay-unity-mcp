// Copyright (C) Funplay. Licensed under MIT.
using Funplay.Editor.MCP.Server;
using NUnit.Framework;

namespace Funplay.Editor.Tests
{
    public sealed class UpdateCheckerReleaseSourceTests
    {
        [TestCase("https://github.com/StefanPEZA/funplay-unity-mcp.git#main", "https://github.com/StefanPEZA/funplay-unity-mcp.git", "StefanPEZA/funplay-unity-mcp")]
        [TestCase("https://github.com/StefanPEZA/funplay-unity-mcp.git#v0.8.2", "https://github.com/StefanPEZA/funplay-unity-mcp.git", "StefanPEZA/funplay-unity-mcp")]
        [TestCase("https://github.com/FunplayAI/funplay-unity-mcp.git", "https://github.com/FunplayAI/funplay-unity-mcp.git", "FunplayAI/funplay-unity-mcp")]
        [TestCase("https://github.com/Owner/repo", "https://github.com/Owner/repo", "Owner/repo")]
        [TestCase("git+https://github.com/Owner/repo.git", "git+https://github.com/Owner/repo.git", "Owner/repo")]
        [TestCase("git@github.com:Owner/repo.git#v1.0.0", "git@github.com:Owner/repo.git", "Owner/repo")]
        [TestCase("ssh://git@github.com/Owner/repo.git", "ssh://git@github.com/Owner/repo.git", "Owner/repo")]
        [TestCase("https://github.com/Owner/mono.git?path=/Packages/pkg#v2", "https://github.com/Owner/mono.git?path=/Packages/pkg", "Owner/mono")]
        public void GitHubSourcesResolveToTheirOwnRepository(string url, string gitUrl, string ownerRepo)
        {
            var source = FunplayMCPUpdateChecker.ReleaseSource.FromGitUrl(url);
            Assert.IsTrue(source.HasValue, url);
            Assert.AreEqual(gitUrl, source.Value.GitUrl);
            Assert.AreEqual($"https://api.github.com/repos/{ownerRepo}/releases/latest", source.Value.LatestReleaseApiUrl);
            Assert.AreEqual($"https://github.com/{ownerRepo}/releases", source.Value.ReleasesUrl);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("https://gitlab.com/Owner/repo.git")]
        [TestCase("https://github.com.evil.example/Owner/repo.git")]
        [TestCase("file:C:/github/funplay-unity-mcp")]
        public void OtherSourcesFallBack(string url) => Assert.IsFalse(FunplayMCPUpdateChecker.ReleaseSource.FromGitUrl(url).HasValue, url);

        [Test]
        public void PackageIdYieldsTheGitUrl()
        {
            Assert.AreEqual("https://github.com/StefanPEZA/funplay-unity-mcp.git#main",
                FunplayMCPUpdateChecker.GitUrlFromPackageId("com.gamebooom.unity.mcp@https://github.com/StefanPEZA/funplay-unity-mcp.git#main"));
            Assert.AreEqual("git@github.com:Owner/repo.git", FunplayMCPUpdateChecker.GitUrlFromPackageId("com.gamebooom.unity.mcp@git@github.com:Owner/repo.git"));
            Assert.IsNull(FunplayMCPUpdateChecker.GitUrlFromPackageId("com.gamebooom.unity.mcp"));
            Assert.IsNull(FunplayMCPUpdateChecker.GitUrlFromPackageId(null));
        }
    }
}
