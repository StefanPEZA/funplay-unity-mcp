// Copyright (C) Funplay. Licensed under MIT.

using System.IO;
using Funplay.Editor.State;
using NUnit.Framework;
using UnityEditor;

namespace Funplay.Editor.Tests
{
    public sealed class PlayModeBackgroundRunnerTests
    {
        private bool _original;

        [SetUp]
        public void SetUp()
        {
            _original = PlayerSettings.runInBackground;
            if (File.Exists(PlayModeBackgroundRunner.MarkerPath)) File.Delete(PlayModeBackgroundRunner.MarkerPath);
        }

        [TearDown]
        public void TearDown()
        {
            if (File.Exists(PlayModeBackgroundRunner.MarkerPath)) File.Delete(PlayModeBackgroundRunner.MarkerPath);
            PlayerSettings.runInBackground = _original;
        }

        [Test]
        public void ApplyTurnsItOnWithAMarkerAndRestoreUndoesIt()
        {
            PlayerSettings.runInBackground = false;

            PlayModeBackgroundRunner.Apply();
            Assert.IsTrue(PlayerSettings.runInBackground);
            Assert.IsTrue(File.Exists(PlayModeBackgroundRunner.MarkerPath), "crash-safe marker");

            // Simulates the next Editor start after a crash: the marker drives the restore.
            PlayModeBackgroundRunner.Restore();
            Assert.IsFalse(PlayerSettings.runInBackground);
            Assert.IsFalse(File.Exists(PlayModeBackgroundRunner.MarkerPath));
        }

        [Test]
        public void AlreadyOnIsLeftAlone()
        {
            PlayerSettings.runInBackground = true;

            PlayModeBackgroundRunner.Apply();
            Assert.IsFalse(File.Exists(PlayModeBackgroundRunner.MarkerPath));
            PlayModeBackgroundRunner.Restore();
            Assert.IsTrue(PlayerSettings.runInBackground);
        }
    }
}
