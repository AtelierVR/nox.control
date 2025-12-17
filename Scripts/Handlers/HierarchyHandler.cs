using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Nox.CCK.Utils;
using Nox.SDK.Control;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Nox.Control.Handlers {
	public class HierarchyHandler {
		[Serializable]
		internal class SceneInfo {
			public SceneInfo(Scene scene, int i) {
				name   = scene.name;
				index  = i;
				path   = scene.path;
				loaded = scene.isLoaded;
				sub    = scene.isSubScene;
			}

			public int    index;
			public string name;
			public string path;
			public bool   loaded;
			public bool   sub;
		}

		[Serializable]
		// ReSharper disable InconsistentNaming
		internal class ComponentInfo {
			public ComponentInfo(Component component) {
				type = component.GetType().FullName;
				id   = component.GetInstanceID();
				var list = new List<PropertyInfo>();
				foreach (var property in component.GetType().GetProperties()) {
					if (!property.CanRead) continue;
					if (property.GetIndexParameters().Length > 0) continue;
					try {
						var value = property.GetValue(component);
						list.Add(
							new PropertyInfo {
								name = property.Name,
								type = property.PropertyType.FullName,
								value = value switch {
									Object obj => $"{obj.name} ({obj.GetInstanceID()})",
									null       => JValue.CreateNull(),
									_          => JToken.FromObject(value)
								},
								editable = property.CanWrite
							}
						);
					} catch {
						// ignored
					}
				}

				foreach (var field in component.GetType().GetFields()) {
					try {
						var value = field.GetValue(component);
						list.Add(
							new PropertyInfo {
								name = field.Name,
								type = field.FieldType.FullName,
								value = value switch {
									Object obj => $"{obj.name} ({obj.GetInstanceID()})",
									null       => JValue.CreateNull(),
									_          => JToken.FromObject(value)
								},
								editable = true
							}
						);
					} catch {
						// ignored
					}
				}

				properties = list.ToArray();
			}


			public string         type;
			public int            id;
			public PropertyInfo[] properties;
		}

		[Serializable]
		internal class PropertyInfo {
			public string name;
			public string type;
			public JToken value;
			public bool   editable;
		}


		[Serializable]
		internal class SceneHierarchy : SceneInfo {
			public SceneHierarchy(Scene scene, int i) : base(scene, i) {
				var rootObjects = scene.GetRootGameObjects();
				nodes = rootObjects
					.Select(rootObject => new SceneNode(rootObject))
					.ToArray();
			}

			public SceneNode[] nodes;
		}

		[Serializable]
		internal class SceneNode {
			public SceneNode(GameObject gameObject) {
				name = gameObject.name;
				id   = gameObject.GetInstanceID();
				var children = new List<SceneNode>();

				for (var i = 0; i < gameObject.transform.childCount; i++) {
					var child = gameObject.transform.GetChild(i).gameObject;
					children.Add(new SceneNode(child));
				}

				nodes = children.ToArray();
			}

			public string      name;
			public int         id;
			public SceneNode[] nodes;
		}

		public static void Handle(IClient client, string ev, object[] args) {
			switch (ev) {
				case "scenes:get": {
					if (args.Length < 1 || !int.TryParse(args[0]?.ToString(), out var sceneIndex)) {
						client.Send("error", "scenes:get requires a scene index").Forget();
						return;
					}

					if (sceneIndex == -1) {
						#if UNITY_EDITOR
						if (!UnityEditor.EditorApplication.isPlaying) {
							client.Send("error", "scenes:get with DontDestroyOnLoad is not supported in edit mode").Forget();
							return;
						}
						#endif
						var obj = new GameObject("DontDestroyOnLoad");
						Object.DontDestroyOnLoad(obj);
						var dont = obj.scene;
						var hy   = new SceneHierarchy(dont, sceneIndex);
						obj.DestroyImmediate();
						client.Send("scenes:get", hy).Forget();
						return;
					}

					var scene     = SceneManager.GetSceneAt(sceneIndex);
					var hierarchy = new SceneHierarchy(scene, sceneIndex);

					client.Send("scenes:get", hierarchy).Forget();

					break;
				}
				case "scenes:list": {
					List<SceneInfo> scenes = new();
					for (var i = 0; i < SceneManager.sceneCount; i++)
						scenes.Add(new SceneInfo(SceneManager.GetSceneAt(i), i));
					client.Send("scenes:list", scenes.Cast<object>().ToArray()).Forget();
					break;
				}
				case "components:get": {
					var path = args
						.Select(t => int.Parse(t.ToString() ?? "0"))
						.ToArray();

					if (path.Length == 0) {
						client.Send("error", "components:get requires a GameObject path").Forget();
						return;
					}

					Scene scene;
					if (path[0] == -1) {
						#if UNITY_EDITOR
						if (!UnityEditor.EditorApplication.isPlaying) {
							client.Send("error", "components:get with DontDestroyOnLoad is not supported in edit mode").Forget();
							return;
						}
						#endif
						var obj = new GameObject("DontDestroyOnLoad");
						Object.DontDestroyOnLoad(obj);
						scene = obj.scene;
						obj.DestroyImmediate();
					} else scene = SceneManager.GetSceneAt(path[0]);

					GameObject o    = null;
					var        objs = scene.GetRootGameObjects();

					for (var i = 1; i < path.Length; i++) {
						Traverse(path[i], objs);
						if (!o) {
							client.Send("components:get").Forget();
							return;
						}

						objs = new GameObject[o.transform.childCount];
						for (var j = 0; j < o.transform.childCount; j++)
							objs[j] = o.transform.GetChild(j).gameObject;
					}

					client.Send(
							"components:get", !o
								? Array.Empty<object>()
								: o.GetComponents<Component>()
									.Select(c => new ComponentInfo(c))
									.Cast<object>()
									.ToArray()
						)
						.Forget();
					break;

					void Traverse(int index, IEnumerable<GameObject> gameObjects) {
						foreach (var go in gameObjects) {
							if (go.GetInstanceID() != index) continue;
							o = go;
							return;
						}
					}
				}
			}
		}
	}
}