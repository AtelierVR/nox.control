using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json;
using Nox.CCK.Control;
using Nox.CCK.Utils;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Nox.Control.Runtime.Handlers  {
        public class HierarchyScenesList : IOperator {
            public string Name
                => "hierarchy_list";

            public string Description
                => "List all loaded scenes with their indices, names, and paths.";

            public ISchema Schema => new InputSchema();

            public async UniTask<IOutput> Execute(IInput _) {
                await UniTask.SwitchToMainThread();

                var scenes = new List<HierarchyScene>();
                if (Application.isPlaying)
                    scenes.Add(new (SceneExtensions.DontDestroyOnLoadId, SceneExtensions.DontDestroyOnLoad));

                for (var i = 0; i < SceneManager.sceneCount; i++)
                    scenes.Add(new (i, SceneManager.GetSceneAt(i)));

                return OperatorOutput.Ok(scenes.ToArray());
            }
        }

        [Serializable]
        public class HierarchyScene {
            public HierarchyScene(int index, Scene scene) {
                Index = index;
                Name = scene.name;
                Path = scene.path;
                var tags = new List<string>();
                if (scene.isLoaded) tags.Add("loaded");
                if (scene.isDirty) tags.Add("dirty");
                if (scene.IsValid()) tags.Add("valid");
                if (scene.isSubScene) tags.Add("sub_scene");
                Tags = tags.ToArray();
                Childs = Array.ConvertAll(
                    scene.GetRootGameObjects(),
                    e => e.GetId()
                );
            }

            [JsonProperty("index")]
            public int Index = -1;

            [JsonProperty("name")]
            public string Name = string.Empty;

            [JsonProperty("path")]
            public string Path = string.Empty;

            [JsonProperty("tags")]
            public string[] Tags = Array.Empty<string>();

            [JsonProperty("childs")]
            public int[] Childs = Array.Empty<int>();
        }

        public class HierarchyScenesGet : IOperator {
            public string Name
                => "hierarchy_get";

            public string Description
                => "Get a GameObject by its path of entity IDs.";

            public ISchema Schema => new InputSchema()
                .Property<int[]>("path", "Path of entity IDs: [sceneIndex, rootGoId, childId, ...]", true);

            public async UniTask<IOutput> Execute(IInput args) {
                await UniTask.SwitchToMainThread();

                var path = args.Get<int[]>("path", true);
                if (path.Length == 0)
                    throw new ArgumentException("Path array is required.");

                Scene scene;
                try { scene = SceneExtensions.Get(path[0]); }
                catch (Exception ex) { return OperatorOutput.Error(ex.Message); }

                if (path.Length == 1)
                    return OperatorOutput.Ok(new HierarchyScene(path[0], scene));

                GameObject target = null;
                var childs = scene.GetRootGameObjects();
                for (var i = 1; i < path.Length; i++) {
                    target = null;

                    foreach (var child in childs)
                        if (child.GetId() == path[i]) {
                            target = child;
                            break;
                        }

                    if (target == null)
                        return OperatorOutput.Error($"GameObject {path[i]} not found at depth {i}.");

                    childs = target.GetChilds();
                }

                return OperatorOutput.Ok(new HierarchyGameObject(target));
            }

        }

        [Serializable]
        public class HierarchyGameObject {
            public HierarchyGameObject(GameObject gameObject) {
                Id = gameObject.GetId();
                Name = gameObject.name;
                Active = gameObject.activeSelf;
                Layer = gameObject.layer;
                Tag = gameObject.tag;
                var tags = new List<string>();
                if (gameObject.isStatic) tags.Add("static");
                Tags = tags.ToArray();
                Childs = Array.ConvertAll(
                    gameObject.GetChilds(),
                    e => e.GetId()
                );
                Components = Array.ConvertAll(
                    gameObject.GetComponents(),
                    e => new HierarchyComponent(e)
                );
            }

            [JsonProperty("id")]
            public int Id = 0;

            [JsonProperty("name")]
            public string Name = string.Empty;

            [JsonProperty("active")]
            public bool Active = false;

            [JsonProperty("layer")]
            public int Layer = 0;

            [JsonProperty("tag")]
            public string Tag = "Untagged";

            [JsonProperty("tags")]
            public string[] Tags = Array.Empty<string>();

            [JsonProperty("childs")]
            public int[] Childs = Array.Empty<int>();

            [JsonProperty("components")]
            public HierarchyComponent[] Components = Array.Empty<HierarchyComponent>();
        }

        [Serializable]
        public class HierarchyComponent {
            public HierarchyComponent(Component component) {
                Id = component.GetId();
                Type = component.GetType().FullName;
                Name = component.name;

                var props = new List<Property>();
                foreach (var p in component.GetType().GetProperties()) {
                    if (!p.CanRead || p.GetIndexParameters().Length > 0)
                        continue;
                    try {
                        var v = p.GetValue(component);
                        props.Add(new Property
                        {
                            name = p.Name,
                            type = p.PropertyType.FullName,
                            value = Ser(v),
                            editable = p.CanWrite
                        });
                    }
                    catch { }
                }

                foreach (var f in component.GetType().GetFields())
                    try {
                        var v = f.GetValue(component);
                        props.Add(new Property
                        {
                            name = f.Name,
                            type = f.FieldType.FullName,
                            value = Ser(v),
                            editable = true
                        });
                    }
                    catch { }

                Properties = props.ToArray();
            }

            private static object Ser(object v) {
                if (v == null) return null;

                var type = v.GetType();

                // Primitives, strings, and decimals are directly JSON-safe
                if (type.IsPrimitive || v is string || v is decimal)
                    return v;

                // Unity vector types
                if (v is Vector2 vec2)
                    return new { vec2.x, vec2.y };
                if (v is Vector3 vec3)
                    return new { vec3.x, vec3.y, vec3.z };
                if (v is Vector4 vec4)
                    return new { vec4.x, vec4.y, vec4.z, vec4.w };
                if (v is Vector2Int v2i)
                    return new { v2i.x, v2i.y };
                if (v is Vector3Int v3i)
                    return new { v3i.x, v3i.y, v3i.z };

                // Quaternion
                if (v is Quaternion q)
                    return new { q.x, q.y, q.z, q.w };

                // Color types
                if (v is Color c)
                    return new { c.r, c.g, c.b, c.a };
                if (v is Color32 c32)
                    return new { c32.r, c32.g, c32.b, c32.a };

                // Rect types
                if (v is Rect r)
                    return new { r.x, r.y, r.width, r.height };
                if (v is RectInt ri)
                    return new { ri.x, ri.y, ri.width, ri.height };

                // Bounds types
                if (v is Bounds b)
                    return new { center = Ser(b.center), size = Ser(b.size) };
                if (v is BoundsInt bi)
                    return new { position = Ser(bi.position), size = Ser(bi.size) };

                // LayerMask
                if (v is LayerMask lm)
                    return lm.value;

                // GameObject / Component references → use their ID
                if (v is GameObject go)
                    return go.GetId();
                if (v is Component comp)
                    return comp.GetId();

                // Enums → their string name
                if (type.IsEnum)
                    return v.ToString();

                // Collections (arrays, lists, etc.) — with a safety cap
                if (v is IEnumerable enumerable and not string) {
                    var items = new List<object>();
                    foreach (var item in enumerable) {
                        items.Add(Ser(item));
                        if (items.Count >= 100) break;
                    }
                    return items;
                }

                // Fallback: return the ToString() representation
                return v.ToString();
            }

            [JsonProperty("id")]
            public int Id = 0;

            [JsonProperty("type")]
            public string Type = string.Empty;

            [JsonProperty("name")]
            public string Name = string.Empty;

            [JsonProperty("properties")]
            public Property[] Properties = Array.Empty<Property>();
        }

        [Serializable]
        public class Property {
            [JsonProperty("name")]
            public string name;
            [JsonProperty("type")]
            public string type;
            [JsonProperty("value")]
            public object value;
            [JsonProperty("editable")]
            public bool editable;
        }
}
