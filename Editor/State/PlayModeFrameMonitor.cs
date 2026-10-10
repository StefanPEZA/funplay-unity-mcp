// Copyright (C) Funplay. Licensed under MIT.
using System.Threading.Tasks;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace Funplay.Editor.State
{
    /// <summary>
    /// Play Mode frame liveness. A passive EditorApplication.update sampler records when
    /// Time.frameCount last changed, so tools can tell whether the game loop is ticking without
    /// waiting. While the Editor is in the background Unity stops running the player loop, which
    /// stalls game code, UI Toolkit schedulers and transitions (see PlayModeBackgroundRunner).
    /// </summary>
    [InitializeOnLoad]
    internal static class PlayModeFrameMonitor
    {
        private static int _lastFrame = -1;
        private static double _lastFrameChangeTime;

        static PlayModeFrameMonitor()
        {
            EditorApplication.update += Sample;
            EditorApplication.playModeStateChanged += _ => Reset();
        }

        /// <summary>True when the Editor application has OS focus.</summary>
        public static bool EditorFocused => InternalEditorUtility.isApplicationActive;

        /// <summary>Seconds since Time.frameCount last advanced in Play Mode; null outside Play Mode or before the first sample.</summary>
        public static double? SecondsSinceLastFrame =>
            EditorApplication.isPlaying && _lastFrame >= 0 ? EditorApplication.timeSinceStartup - _lastFrameChangeTime : (double?)null;

        /// <summary>Frames are considered stuck when none advanced for this long while playing and not paused.</summary>
        public const double StuckThresholdSeconds = 1.0;

        public static bool FramesStuck =>
            EditorApplication.isPlaying && !EditorApplication.isPaused && SecondsSinceLastFrame is double s && s > StuckThresholdSeconds;

        /// <summary>
        /// Waits until <paramref name="frames"/> more player frames have run (keeping the loop alive while
        /// waiting), or the timeout passes. Returns the number of frames that actually advanced.
        /// </summary>
        public static Task<int> WaitForPlayerFramesAsync(int frames, double timeoutSeconds)
        {
            var tcs = new TaskCompletionSource<int>();
            var start = Time.frameCount;
            var deadline = EditorApplication.timeSinceStartup + timeoutSeconds;

            void Tick()
            {
                var advanced = Time.frameCount - start;
                if (advanced >= frames || EditorApplication.timeSinceStartup >= deadline || !EditorApplication.isPlaying)
                {
                    EditorApplication.update -= Tick;
                    tcs.TrySetResult(advanced);
                    return;
                }
                EditorApplication.QueuePlayerLoopUpdate();
            }

            EditorApplication.update += Tick;
            EditorApplication.QueuePlayerLoopUpdate();
            return tcs.Task;
        }

        private static void Reset()
        {
            _lastFrame = -1;
            _lastFrameChangeTime = EditorApplication.timeSinceStartup;
        }

        private static void Sample()
        {
            if (!EditorApplication.isPlaying)
                return;

            var frame = Time.frameCount;
            if (frame != _lastFrame)
            {
                _lastFrame = frame;
                _lastFrameChangeTime = EditorApplication.timeSinceStartup;
            }
        }
    }
}
