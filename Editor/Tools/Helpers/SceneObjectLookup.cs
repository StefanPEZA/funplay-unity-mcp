// Copyright (C) Funplay. Licensed under MIT.
using System;
using UnityObject = UnityEngine.Object;
using FindObjectsInactive = UnityEngine.FindObjectsInactive;

namespace Funplay.Editor.Tools.Helpers
{
    /// <summary>
    /// Unordered scene object lookup. Unity 6.6 deprecates every FindObjectsSortMode overload
    /// (sorting relied on InstanceID ordering, which EntityId replaces); older versions only
    /// have those overloads. No caller depends on ordering, so both map to "unsorted".
    /// </summary>
    internal static class SceneObjectLookup
    {
        internal static T[] FindAll<T>(bool includeInactive = false) where T : UnityObject
        {
            var inactive = includeInactive ? FindObjectsInactive.Include : FindObjectsInactive.Exclude;
#if UNITY_6000_6_OR_NEWER
            return UnityObject.FindObjectsByType<T>(inactive);
#else
            return UnityObject.FindObjectsByType<T>(inactive, UnityEngine.FindObjectsSortMode.None);
#endif
        }

        internal static UnityObject[] FindAll(Type type, bool includeInactive = false)
        {
            if (type == null) return Array.Empty<UnityObject>();
            var inactive = includeInactive ? FindObjectsInactive.Include : FindObjectsInactive.Exclude;
#if UNITY_6000_6_OR_NEWER
            return UnityObject.FindObjectsByType(type, inactive);
#else
            return UnityObject.FindObjectsByType(type, inactive, UnityEngine.FindObjectsSortMode.None);
#endif
        }

        /// <summary>Any loaded active instance; replaces the deprecated FindFirstObjectByType.</summary>
        internal static T FindAny<T>() where T : UnityObject => UnityObject.FindAnyObjectByType<T>();
    }
}
