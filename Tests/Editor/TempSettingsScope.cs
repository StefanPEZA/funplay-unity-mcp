// Copyright (C) Funplay. Licensed under MIT.

using System;
using System.IO;
using Funplay.Editor.Services;
using Funplay.Editor.Settings;

namespace Funplay.Editor.Tests
{
    /// <summary>A <see cref="SettingsController"/> backed by a throwaway project folder.</summary>
    internal sealed class TempSettingsScope : IDisposable
    {
        public TempSettingsScope()
        {
            ProjectPath = Path.Combine(Path.GetTempPath(), "FunplaySettingsScope_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(ProjectPath, "UserSettings"));
        }

        public string ProjectPath { get; }

        public string SettingsFilePath => Path.Combine(ProjectPath, "UserSettings", "FunplayMcpSettings.json");

        public SettingsController Load()
        {
            return new SettingsController(new Paths(ProjectPath));
        }

        public static SettingsController Create(string profile)
        {
            var scope = new TempSettingsScope();
            try
            {
                var settings = scope.Load();
                settings.MCPToolExportProfile = profile;
                return settings;
            }
            finally
            {
                scope.Dispose();
            }
        }

        public void Dispose()
        {
            if (Directory.Exists(ProjectPath))
                Directory.Delete(ProjectPath, true);
        }

        private sealed class Paths : IApplicationPaths
        {
            public Paths(string projectPath)
            {
                ProjectPath = projectPath;
                AssetsPath = Path.Combine(projectPath, "Assets");
                TempPath = Path.Combine(projectPath, "Temp", "Funplay");
                DataPath = AssetsPath;
                PersistentDataPath = Path.Combine(projectPath, "PersistentData");
            }

            public string ProjectPath { get; }
            public string AssetsPath { get; }
            public string TempPath { get; }
            public string DataPath { get; }
            public string PersistentDataPath { get; }
        }
    }
}
