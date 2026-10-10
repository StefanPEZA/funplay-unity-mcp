// Copyright (C) Funplay. Licensed under MIT.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Funplay.Editor.Tools.Helpers
{
    /// <summary>
    /// Assembly enumeration/loading through Unity's own APIs where they exist. Unity 6.6 adds
    /// <c>CurrentAssemblies</c> and <c>GetLoadedAssemblyPath()</c> ahead of the CoreCLR runtime, where
    /// AppDomain enumeration can return unloaded assemblies, Assembly.Location is empty and
    /// Assembly.Load(byte[]) targets the wrong load context; its analyzer flags the raw calls
    /// (UAC0005/UAC0007/UAC0020). Older versions keep the equivalent System.Reflection calls.
    /// </summary>
    internal static class LoadedAssemblies
    {
        internal static Assembly[] All()
        {
#if UNITY_6000_6_OR_NEWER
            return UnityEngine.Assemblies.CurrentAssemblies.GetLoadedAssemblies().ToArray();
#else
            return AppDomain.CurrentDomain.GetAssemblies();
#endif
        }

        private static readonly Dictionary<string, Type> TypeCache = new Dictionary<string, Type>(StringComparer.Ordinal);

        /// <summary>
        /// Resolve a (usually internal) type by full name: the <paramref name="preferred"/> assemblies
        /// first, then every loaded assembly. Unity moves editor internals between module assemblies
        /// (MediaDecoder left UnityEditor.CoreModule for UnityEditor.MediaModule in 6.6), and
        /// Assembly.GetType does not follow those moves. The preferred lookups are cheap and always
        /// run, so each caller's preference holds; only all-assembly scan hits are cached, and misses
        /// are retried.
        /// </summary>
        internal static Type FindType(string fullName, params Assembly[] preferred)
        {
            var type = preferred.Where(a => a != null).Select(a => a.GetType(fullName)).FirstOrDefault(t => t != null);
            if (type != null) return type;
            if (TypeCache.TryGetValue(fullName, out var cached)) return cached;
            type = All().Where(a => !a.IsDynamic).Select(a => a.GetType(fullName)).FirstOrDefault(t => t != null);
            if (type != null) TypeCache[fullName] = type;
            return type;
        }

        /// <summary>On-disk path of a loaded assembly, or empty for in-memory/dynamic ones.</summary>
        internal static string PathOf(Assembly assembly)
        {
            if (assembly == null || assembly.IsDynamic) return string.Empty;
#if UNITY_6000_6_OR_NEWER
            return UnityEngine.AssemblyExtension.GetLoadedAssemblyPath(assembly) ?? string.Empty;
#else
            return assembly.Location ?? string.Empty;
#endif
        }

        internal static Assembly LoadFromBytes(byte[] rawAssembly)
        {
#if UNITY_6000_6_OR_NEWER
            return UnityEngine.Assemblies.CurrentAssemblies.LoadFromBytes(rawAssembly);
#else
            return Assembly.Load(rawAssembly);
#endif
        }
    }
}
