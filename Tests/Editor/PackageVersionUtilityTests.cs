// Copyright (C) Funplay. Licensed under MIT.
using System.Reflection;
using Funplay.Editor.Services;
using NUnit.Framework;

namespace Funplay.Editor.Tests
{
    public sealed class PackageVersionUtilityTests
    {
        [Test]
        public void RefreshReplacesAStaleCachedVersion()
        {
            var actual = PackageVersionUtility.CurrentVersion;
            var cache = typeof(PackageVersionUtility).GetField("_cachedVersion", BindingFlags.NonPublic | BindingFlags.Static);
            // A package update that changed no scripts leaves the previous version cached (no domain reload).
            cache.SetValue(null, "0.0.1-stale");
            Assert.AreEqual("0.0.1-stale", PackageVersionUtility.CurrentVersion);

            PackageVersionUtility.Refresh();
            Assert.AreEqual(actual, PackageVersionUtility.CurrentVersion);
            Assert.AreNotEqual("0.0.0", actual, "the version is read from the package's package.json");
        }
    }
}
