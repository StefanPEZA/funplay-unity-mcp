// Copyright (C) Funplay. Licensed under MIT.

using System.Collections.Generic;
using Newtonsoft.Json;
using UnityEngine;

namespace Funplay.Editor.Tools.Helpers
{
    /// <summary>
    /// Build structured payloads describing GameObjects and Components.
    /// The shape is fixed (anonymous types serialized through Newtonsoft) so MCP
    /// clients always see the same field names — particularly <c>instanceId</c>,
    /// which lets agents chain <c>by_id</c> lookups instead of re-resolving by name.
    /// </summary>
    public static class GameObjectSerializer
    {
        public static object Describe(GameObject go, bool includeComponents = true, bool includeChildren = false)
        {
            if (go == null) return null;
            return new
            {
                instanceId = ObjectIdHelper.GetSerializableId(go),
                name = go.name,
                path = ObjectsHelper.GetGameObjectPath(go),
                activeSelf = go.activeSelf,
                activeInHierarchy = go.activeInHierarchy,
                tag = go.tag,
                layer = LayerMask.LayerToName(go.layer),
                isStatic = go.isStatic,
                scene = go.scene.IsValid() ? go.scene.name : null,
                transform = new
                {
                    position = ToObj(go.transform.position),
                    localPosition = ToObj(go.transform.localPosition),
                    eulerAngles = ToObj(go.transform.eulerAngles),
                    localScale = ToObj(go.transform.localScale)
                },
                components = includeComponents ? DescribeComponents(go) : null,
                childCount = go.transform.childCount,
                children = includeChildren ? DescribeChildren(go) : null
            };
        }

        /// <summary>
        /// One search hit: enough to recognise the object and chain by_id calls (get_game_object_info has the
        /// transform and component ids). Defaults are left out: activeSelf only when it differs from active,
        /// tag/layer only when not Untagged/Default, isStatic only when true.
        /// </summary>
        public static List<object> DescribeMany(IEnumerable<GameObject> gos)
        {
            var list = new List<object>();
            foreach (var go in gos)
            {
                list.Add(new ObjectHit
                {
                    instanceId = ObjectIdHelper.GetSerializableId(go),
                    name = go.name,
                    path = ObjectsHelper.GetGameObjectPath(go),
                    active = go.activeInHierarchy,
                    activeSelf = go.activeSelf != go.activeInHierarchy ? (bool?)go.activeSelf : null,
                    tag = go.CompareTag("Untagged") ? null : go.tag,
                    layer = go.layer == 0 ? null : LayerMask.LayerToName(go.layer),
                    isStatic = go.isStatic ? (bool?)true : null,
                    scene = go.scene.IsValid() ? go.scene.name : null,
                    components = ComponentNames(go),
                    childCount = go.transform.childCount
                });
            }
            return list;
        }

        /// <summary>A find_game_objects hit; null members are left out of the reply.</summary>
        [JsonObject(ItemNullValueHandling = NullValueHandling.Ignore)]
        public sealed class ObjectHit
        {
            public string instanceId { get; set; }
            public string name { get; set; }
            public string path { get; set; }
            public bool active { get; set; }
            public bool? activeSelf { get; set; }
            public string tag { get; set; }
            public string layer { get; set; }
            public bool? isStatic { get; set; }
            public string scene { get; set; }
            public string[] components { get; set; }
            public int childCount { get; set; }
        }

        /// <summary>Component type names without the Transform every object has; "&lt;missing&gt;" for a missing script.</summary>
        private static string[] ComponentNames(GameObject go)
        {
            var names = new List<string>();
            foreach (var c in go.GetComponents<Component>())
            {
                if (c == null) { names.Add("<missing>"); continue; }
                if (c is Transform) continue;
                names.Add(c.GetType().Name);
            }
            return names.Count > 0 ? names.ToArray() : null;
        }

        public static object DescribeComponent(Component c, bool includeProperties = false)
        {
            if (c == null) return null;
            object props = null;
            if (includeProperties)
                props = ComponentSerializer.ReadProperties(c);
            return new
            {
                instanceId = ObjectIdHelper.GetSerializableId(c),
                type = c.GetType().Name,
                fullType = c.GetType().FullName,
                properties = props
            };
        }

        private static List<object> DescribeComponents(GameObject go)
        {
            var list = new List<object>();
            foreach (var c in go.GetComponents<Component>())
            {
                if (c == null)
                {
                    list.Add(new { type = "<missing>", instanceId = 0 });
                    continue;
                }
                list.Add(new
                {
                    instanceId = ObjectIdHelper.GetSerializableId(c),
                    type = c.GetType().Name,
                    fullType = c.GetType().FullName
                });
            }
            return list;
        }

        private static List<object> DescribeChildren(GameObject go)
        {
            var list = new List<object>();
            for (int i = 0; i < go.transform.childCount; i++)
            {
                var child = go.transform.GetChild(i).gameObject;
                list.Add(new
                {
                    instanceId = ObjectIdHelper.GetSerializableId(child),
                    name = child.name,
                    activeSelf = child.activeSelf,
                    childCount = child.transform.childCount
                });
            }
            return list;
        }

        private static object ToObj(Vector3 v) => new { x = v.x, y = v.y, z = v.z };
    }
}
