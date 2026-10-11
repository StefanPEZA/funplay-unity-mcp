// Copyright (C) Funplay. Licensed under MIT.

using System;
using Funplay.Editor.Settings;
using Funplay.Editor.DI;

namespace Funplay.Editor.Tools.Helpers
{
    /// <summary>
    /// Resolves whether a tool replies compactly or with the full 0.8.6 shape: a per-call
    /// <c>detail</c> argument ("compact" or "full") wins over the <c>MCPCompactResponses</c> setting.
    /// </summary>
    internal static class ResponseDetail
    {
        internal const string Compact = "compact";
        internal const string Full = "full";

        internal static bool IsCompact(string detail = null)
        {
            if (string.Equals(detail, Full, StringComparison.OrdinalIgnoreCase)) return false;
            if (string.Equals(detail, Compact, StringComparison.OrdinalIgnoreCase)) return true;
            var settings = RootScopeServices.Services?.GetService(typeof(ISettingsController)) as ISettingsController;
            return settings?.MCPCompactResponses ?? true;
        }
    }
}
