// Copyright (C) Funplay. Licensed under MIT.
#if ENABLE_INPUT_SYSTEM
using System.IO;
using Funplay.Editor.State;
using Funplay.Editor.Tools.Helpers;
using NUnit.Framework;
using UnityEngine.InputSystem;

namespace Funplay.Editor.Tests
{
    public sealed class KeyInjectorTests
    {
        private Keyboard _keyboard;
        private InputSettings.BackgroundBehavior _background;
        private InputSettings.EditorInputBehaviorInPlayMode _editor;

        [SetUp]
        public void SetUp()
        {
            _background = InputSystem.settings.backgroundBehavior;
            _editor = InputSystem.settings.editorInputBehaviorInPlayMode;
            if (File.Exists(InputFocusOverride.JournalPath)) File.Delete(InputFocusOverride.JournalPath);
            KeyInjector.Reset();
        }

        [TearDown]
        public void TearDown()
        {
            KeyInjector.Reset();
            if (_keyboard != null) InputSystem.RemoveDevice(_keyboard);
            if (File.Exists(InputFocusOverride.JournalPath)) File.Delete(InputFocusOverride.JournalPath);
            InputSystem.settings.backgroundBehavior = _background;
            InputSystem.settings.editorInputBehaviorInPlayMode = _editor;
        }

        [Test]
        public void KeysQueuedBeforeAnUpdateDoNotUndoEachOther()
        {
            _keyboard = InputSystem.AddDevice<Keyboard>();
            // StateEvent.From snapshots the device: without the overlay, B's event would carry A up.
            KeyInjector.Queue(_keyboard, new[] { Key.A }, true);
            KeyInjector.Queue(_keyboard, new[] { Key.B }, true);
            InputSystem.Update();
            Assert.IsTrue(_keyboard.aKey.isPressed, "A stays down");
            Assert.IsTrue(_keyboard.bKey.isPressed);
            CollectionAssert.AreEquivalent(new[] { Key.A, Key.B }, KeyInjector.HeldKeys);

            KeyInjector.Queue(_keyboard, new[] { Key.A }, false);
            KeyInjector.Queue(_keyboard, new[] { Key.C }, true);
            InputSystem.Update();
            Assert.IsFalse(_keyboard.aKey.isPressed, "A's release is not undone by the next event");
            Assert.IsTrue(_keyboard.bKey.isPressed);
            Assert.IsTrue(_keyboard.cKey.isPressed);
        }

        [Test]
        public void FocusOverrideIsJournaledAndRestored()
        {
            InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.ResetAndDisableNonBackgroundDevices;
            InputSystem.settings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.PointersAndKeyboardsRespectGameViewFocus;

            Assert.AreEqual("applied", InputFocusOverride.Apply());
            Assert.AreEqual(InputSettings.BackgroundBehavior.IgnoreFocus, InputSystem.settings.backgroundBehavior);
            Assert.AreEqual(InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView, InputSystem.settings.editorInputBehaviorInPlayMode);
            Assert.IsTrue(File.Exists(InputFocusOverride.JournalPath), "journaled before changing anything");
            Assert.AreEqual("applied", InputFocusOverride.Apply(), "a second press keeps the first journal");
            Assert.AreEqual("ResetAndDisableNonBackgroundDevices", InputFocusOverride.Load().background_behavior);

            // The user changed one value meanwhile: that one is kept, the other restored.
            InputSystem.settings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDevicesRespectGameViewFocus;
            InputFocusOverride.Restore();
            Assert.AreEqual(InputSettings.BackgroundBehavior.ResetAndDisableNonBackgroundDevices, InputSystem.settings.backgroundBehavior);
            Assert.AreEqual(InputSettings.EditorInputBehaviorInPlayMode.AllDevicesRespectGameViewFocus, InputSystem.settings.editorInputBehaviorInPlayMode);
            Assert.IsFalse(File.Exists(InputFocusOverride.JournalPath));
        }

        [Test]
        public void SettingsThatAlreadyRouteInputAreLeftAlone()
        {
            InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
            InputSystem.settings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
            Assert.AreEqual("already", InputFocusOverride.Apply());
            Assert.IsFalse(File.Exists(InputFocusOverride.JournalPath));
        }
    }
}
#endif
