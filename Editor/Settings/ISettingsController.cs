// Copyright (C) Funplay. Licensed under MIT.

using System;

namespace Funplay.Editor.Settings
{
    internal interface ISettingsController
    {
        bool MCPServerEnabled { get; set; }
        int MCPServerPort { get; set; }

        /// <summary>
        /// True when <see cref="MCPServerPort"/> is a port the user picked, which always wins over
        /// the per-project derived default. False means "no choice recorded" and the server derives
        /// a per-project port instead.
        /// </summary>
        bool MCPServerPortConfigured { get; }

        /// <summary>
        /// Drops a recorded port choice so the server goes back to the per-project derived port.
        /// </summary>
        void ClearMCPServerPortOverride();
        string MCPToolExportProfile { get; set; }
        bool MCPCoreToolsConfigured { get; }
        string[] MCPCoreTools { get; set; }
        /// <summary>True when the Full profile excludes at least one tool.</summary>
        bool MCPFullToolsConfigured { get; }

        /// <summary>Tools switched off in the Full profile. Tools not listed, including ones added later, are exposed.</summary>
        string[] MCPFullToolsExcluded { get; set; }
        /// <summary>UI framework pack for Core: "auto" (use <see cref="MCPDetectedUiPack"/>), "ugui", "uitk" or "both".</summary>
        string MCPUiPack { get; set; }

        /// <summary>Result of the one-time project scan ("ugui", "uitk", "both"); empty until detected.</summary>
        string MCPDetectedUiPack { get; set; }

        /// <summary>Optional packs added to the default Core list (animation, performance, lighting, packages).</summary>
        string[] MCPOptionalCorePacks { get; set; }

        /// <summary>Turn on Run In Background for the duration of Play Mode so an unfocused Editor keeps ticking.</summary>
        bool KeepPlayModeRunningInBackground { get; set; }

        string MCPSelectedConfigTarget { get; set; }
        bool ExecuteCodeSafetyChecksEnabled { get; set; }
        bool ExecuteCodeStrictFilesystemSafetyEnabled { get; set; }
        bool ExecuteCodeProjectNamespaceInjectionEnabled { get; set; }
        bool PluginDebugLoggingEnabled { get; set; }
        bool MCPRecentActivityExpandedByDefault { get; set; }
        bool MCPBrokerModeEnabled { get; set; }
        string MCPBrokerMonoPath { get; set; }

        /// <summary>
        /// Entry name this project last wrote into the given client target's config (Claude Code,
        /// Cursor, Codex, ...). Recorded per target: one shared slot broke retirement for every
        /// target except the first one re-configured after a rename, orphaning stale entries the
        /// cleanup exists to retire. Empty when nothing was recorded for that target.
        /// </summary>
        string GetLastClientConfigKey(string targetName);

        void SetLastClientConfigKey(string targetName, string serverKey);

        event Action OnSettingsChanged;
    }
}
