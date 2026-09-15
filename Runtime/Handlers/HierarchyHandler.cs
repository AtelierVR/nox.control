using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
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

            public string[] RequiredPermissions => new[] { "hierarchy:read" };

            public ISchema Schema => new InputSchema().WithPagination();

            public async UniTask<IOutput> Execute(IInput args) {
                await UniTask.SwitchToMainThread();

                var pagination = Pagination.Read(args);

                var scenes = new List<HierarchyScene>();
                if (Application.isPlaying)
                    scenes.Add(new (SceneExtensions.DontDestroyOnLoadId, SceneExtensions.DontDestroyOnLoad, pagination));

                for (var i = 0; i < SceneManager.sceneCount; i++)
                    scenes.Add(new (i, SceneManager.GetSceneAt(i), pagination));

                return OperatorOutput.Ok(scenes.ToArray());
            }
        }

        [Serializable]
        public class HierarchyScene {
            public HierarchyScene(int index, Scene scene, Pagination pagination = default) {
                Index = index;
                Name = scene.name;
                Path = scene.path;
                var tags = new List<string>();
                if (scene.isLoaded) tags.Add("loaded");
                if (scene.isDirty) tags.Add("dirty");
                if (scene.IsValid()) tags.Add("valid");
                if (scene.isSubScene) tags.Add("sub_scene");
                Tags = tags.ToArray();

                var roots = Array.ConvertAll(
                    scene.GetRootGameObjects(),
                    e => e.GetId()
                );

                Childs      = pagination.Apply(roots, out var total);
                ChildsTotal = total;
            }

            [JsonProperty("index")]
            public int Index = -1;

            [JsonProperty("name")]
            public string Name = string.Empty;

            [JsonProperty("path")]
            public string Path = string.Empty;

            [JsonProperty("tags")]
            public string[] Tags = Array.Empty<string>();

            /// <summary>Root objects for the requested <c>offset</c>/<c>limit</c> window.</summary>
            [JsonProperty("childs")]
            public int[] Childs = Array.Empty<int>();

            /// <summary>Total number of root objects, before pagination.</summary>
            [JsonProperty("childs_total")]
            public int ChildsTotal = 0;
        }

        public class HierarchyScenesGet : IOperator {
            public string Name
                => "hierarchy_get";

            public string Description
                => "Get a GameObject by a path of names and/or entity IDs, "
                   + "as returned by hierarchy_list / the parent's \"childs\" array.";

            public string[] RequiredPermissions => new[] { "hierarchy:read" };

            public ISchema Schema => new InputSchema()
                .Property<string>(
                    "path",
                    "Path to the GameObject: '/'-separated segments, each being a name or an "
                    + "entity ID. The first segment selects the scene (index or name). "
                    + "Examples: \"DontDestroyOnLoad/[XRController_-4490]/TrackerOffsets/Hands\", "
                    + "\"-1/-8910\" or \"-1,-8910\".",
                    true
                )
                .WithPagination()
                .Property<bool>(
                    "with_properties",
                    "Include the per-component property dump (heavy). Defaults to true; "
                    + "set to false for a fast structural overview."
                );

            public async UniTask<IOutput> Execute(IInput args) {
                await UniTask.SwitchToMainThread();

                var pagination     = Pagination.Read(args);
                var withProperties = args.Get<bool?>("with_properties") ?? true;

                if (!TryResolve(args.Get<object>("path"), out var target, out var scene, out var sceneIndex, out var error))
                    return OperatorOutput.Error(error);

                // Un chemin d'un seul segment ne désigne que la scène.
                if (target == null)
                    return OperatorOutput.Ok(new HierarchyScene(sceneIndex, scene, pagination));

                return OperatorOutput.Ok(new HierarchyGameObject(target, pagination, withProperties));
            }

            /// <summary>
            /// Résout un chemin en GameObject et en scène. <paramref name="target"/> vaut
            /// <c>null</c> quand le chemin ne désigne qu'une scène.
            /// <para>Partagé avec <c>hierarchy_search</c> (option <c>path</c>).</para>
            /// </summary>
            internal static bool TryResolve(
                object raw,
                out GameObject target,
                out Scene scene,
                out int sceneIndex,
                out string error
            ) {
                target     = null;
                scene      = default;
                sceneIndex = 0;
                error      = null;

                var segments = PathSegment.Parse(raw);
                if (segments.Length == 0) {
                    error = "Path is required.";
                    return false;
                }

                if (!TryResolveScene(segments[0], out scene, out sceneIndex, out error))
                    return false;

                if (segments.Length == 1)
                    return true;

                var childs = scene.GetRootGameObjects();

                for (var i = 1; i < segments.Length; i++) {
                    target = null;

                    foreach (var child in childs)
                        if (segments[i].Matches(child)) {
                            target = child;
                            break;
                        }

                    if (target == null) {
                        error = $"'{segments[i]}' not found at depth {i} of '{Join(segments, i)}'. "
                                + $"Available: {Describe(childs)}";
                        return false;
                    }

                    childs = target.GetChilds();
                }

                return true;
            }

            /// <summary>
            /// Lists candidate IDs (with their names, which the caller cannot guess from an ID
            /// alone) so a failed lookup does not require another round-trip to recover.
            /// </summary>
            internal static string Describe(IReadOnlyList<GameObject> childs) {
                if (childs == null || childs.Count == 0)
                    return "(none)";

                const int max = 50;
                var parts = new List<string>(Math.Min(childs.Count, max) + 1);
                for (var i = 0; i < childs.Count && i < max; i++)
                    parts.Add($"{childs[i].GetId()} ({childs[i].name})");

                if (childs.Count > max)
                    parts.Add($"… +{childs.Count - max} more");

                return string.Join(", ", parts);
            }

            internal static string Join(PathSegment[] segments, int count) {
                var parts = new string[count];
                for (var i = 0; i < count; i++)
                    parts[i] = segments[i].ToString();
                return string.Join("/", parts);
            }

            /// <summary>
            /// Resolves the first segment to a scene: by index for an ID segment (-1 being
            /// DontDestroyOnLoad), or by name otherwise.
            /// </summary>
            internal static bool TryResolveScene(PathSegment segment, out Scene scene, out int index, out string error) {
                scene = default;
                index = 0;
                error = null;

                if (segment.IsId) {
                    index = segment.Id;
                    try {
                        scene = SceneExtensions.Get(index);
                        return true;
                    } catch (Exception ex) {
                        error = ex.Message;
                        return false;
                    }
                }

                // DontDestroyOnLoad n'est pas compté dans SceneManager.sceneCount.
                if (Application.isPlaying
                    && string.Equals(SceneExtensions.DontDestroyOnLoad.name, segment.Raw, StringComparison.OrdinalIgnoreCase)) {
                    index = SceneExtensions.DontDestroyOnLoadId;
                    scene = SceneExtensions.DontDestroyOnLoad;
                    return true;
                }

                for (var i = 0; i < SceneManager.sceneCount; i++) {
                    var candidate = SceneManager.GetSceneAt(i);
                    if (string.Equals(candidate.name, segment.Raw, StringComparison.Ordinal)
                        || string.Equals(candidate.name, segment.Raw, StringComparison.OrdinalIgnoreCase)) {
                        scene = candidate;
                        index = i;
                        return true;
                    }
                }

                error = $"Scene '{segment.Raw}' not found. Available: {DescribeScenes()}";
                return false;
            }

            internal static string DescribeScenes() {
                var parts = new List<string>();
                if (Application.isPlaying)
                    parts.Add($"{SceneExtensions.DontDestroyOnLoadId} ({SceneExtensions.DontDestroyOnLoad.name})");

                for (var i = 0; i < SceneManager.sceneCount; i++) {
                    var scene = SceneManager.GetSceneAt(i);
                    parts.Add($"{i} ({scene.name})");
                }

                return string.Join(", ", parts);
            }

            /// <summary>
            /// Un segment de chemin : soit un ID d'entité, soit un nom de GameObject / scène.
            /// </summary>
            internal readonly struct PathSegment {
                public readonly string Raw;
                public readonly int    Id;
                public readonly bool   IsId;

                private PathSegment(string raw, int id, bool isId) {
                    Raw  = raw;
                    Id   = id;
                    IsId = isId;
                }

                public static PathSegment FromId(int id)
                    => new(id.ToString(CultureInfo.InvariantCulture), id, true);

                public static PathSegment FromName(string name)
                    => new(name, 0, false);

                public bool Matches(GameObject gameObject)
                    => IsId
                        ? gameObject.GetId() == Id
                        : string.Equals(gameObject.name, Raw, StringComparison.Ordinal)
                          || string.Equals(gameObject.name, Raw, StringComparison.OrdinalIgnoreCase);

                public override string ToString()
                    => Raw;

                /// <summary>
                /// Normalise l'argument <c>path</c>, accepté sous plusieurs formes :
                /// <list type="bullet">
                ///   <item><description>chemin par noms/IDs : <c>"DontDestroyOnLoad/Hands/RobotHand (L)"</c></description></item>
                ///   <item><description>liste d'IDs : <c>"-1/-8910"</c> ou <c>"-1,-8910"</c></description></item>
                ///   <item><description>tableau d'IDs (clients qui envoient encore <c>int[]</c>)</description></item>
                /// </list>
                /// </summary>
                public static PathSegment[] Parse(object raw) {
                    switch (raw) {
                        case null:
                            return Array.Empty<PathSegment>();

                        case string text:
                            return ParseText(text.Trim());
                    }

                    // Tableau (JArray, object[], int[], …) → chaque élément est nommé ou identifié.
                    if (raw is IEnumerable list and not string) {
                        var items = new List<PathSegment>();
                        foreach (var item in list) {
                            if (item is string name)
                                items.Add(FromName(name));
                            else
                                items.Add(TryId(item, out var id) ? FromId(id) : FromName(item?.ToString() ?? string.Empty));
                        }
                        return items.ToArray();
                    }

                    // Valeur scalaire (un seul ID).
                    return TryId(raw, out var scalar) ? new[] { FromId(scalar) }
                                                      : new[] { FromName(raw.ToString()) };
                }

                private static PathSegment[] ParseText(string text) {
                    if (text.Length == 0)
                        return Array.Empty<PathSegment>();

                    // Liste d'IDs : "-1,-8910", "-1 -8910"
                    if (TryParseIdList(text, out var ids))
                        return Array.ConvertAll(ids, FromId);

                    // Nom de scène seul : "DontDestroyOnLoad", "OpenME"
                    if (text.IndexOf('/') < 0)
                        return new[] { FromName(text) };

                    var parts    = text.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                    var segments = new PathSegment[parts.Length];
                    for (var i = 0; i < parts.Length; i++) {
                        var part = parts[i].Trim();
                        segments[i] = TryId(part, out var id) ? FromId(id) : FromName(part);
                    }
                    return segments;
                }

                private static bool TryParseIdList(string text, out int[] ids) {
                    ids = Array.Empty<int>();

                    if (text.Length == 0)
                        return false;

                    var parts  = text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    var result = new int[parts.Length];
                    for (var i = 0; i < parts.Length; i++)
                        if (!int.TryParse(parts[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out result[i]))
                            return false; // ce n'est pas une liste d'IDs → traité comme des noms

                    ids = result;
                    return true;
                }

                private static bool TryId(object value, out int id) {
                    switch (value) {
                        case null:
                            id = 0;
                            return false;
                        case int i:
                            id = i;
                            return true;
                        case long l:
                            id = unchecked((int)l);
                            return true;
                        case string s:
                            return int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out id);
                    }

                    id = 0;
                    return false;
                }
            }
        }

        /// <summary>
        /// Recherche des GameObjects par nom et/ou par type de composant, sur toutes les scènes
        /// chargées (ou une seule via <c>scene</c>).
        /// <para>
        /// Chaque résultat expose un <c>path</c> directement réutilisable dans
        /// <c>hierarchy_get</c>, ce qui évite de naviguer ID par ID.
        /// </para>
        /// </summary>
        public class HierarchySearch : IOperator {
            public string Name
                => "hierarchy_search";

            public string Description
                => "Search GameObjects by name and/or component type. By default every loaded "
                   + "scene is scanned; a 'path' can restrict the search to a subtree, and "
                   + "'path_contains' filters on the result path. Each result carries a path "
                   + "usable directly with hierarchy_get.";

            public string[] RequiredPermissions => new[] { "hierarchy:read" };

            public ISchema Schema => new InputSchema()
                .Property<string>(
                    "name",
                    "GameObject name filter (case-insensitive). '*' and '?' wildcards are supported, "
                    + "otherwise this is a substring match."
                )
                .Property<string>(
                    "component",
                    "Component filter: simple name ('BoxCollider') or full name "
                    + "('UnityEngine.BoxCollider'). A base type also matches its derived types."
                )
                .Property<string>(
                    "path",
                    "Subtree to search from, using the hierarchy_get path syntax "
                    + "(e.g. \"DontDestroyOnLoad/[XRController_-4490]/TrackerOffsets\"). "
                    + "Omit to search every loaded scene. Used alone, it lists the subtree."
                )
                .Property<string>(
                    "path_contains",
                    "Only keep results whose full path matches this filter "
                    + "('*'/'?' wildcards, otherwise a substring match)."
                )
                .Property<string>("scene", "Restrict the search to a scene (index or name). Ignored when 'path' is set.")
                .Property<bool>("active_only", "Only return GameObjects active in the hierarchy. Defaults to false.")
                .Property<bool>("with_components", "Include the component type list of each result. Defaults to true.")
                .WithPagination();

            public async UniTask<IOutput> Execute(IInput args) {
                await UniTask.SwitchToMainThread();

                var nameFilter     = args.Get<string>("name");
                var typeFilter     = args.Get<string>("component");
                var sceneFilter    = args.Get<string>("scene");
                var pathFilter     = args.Get<string>("path");
                var pathContains   = args.Get<string>("path_contains");
                var activeOnly     = args.Get<bool?>("active_only") ?? false;
                var withComponents = args.Get<bool?>("with_components") ?? true;
                var pagination     = Pagination.Read(args);

                // Un 'path' seul est valide : il liste le sous-arbre désigné (paginé).
                if (string.IsNullOrWhiteSpace(nameFilter)
                    && string.IsNullOrWhiteSpace(typeFilter)
                    && string.IsNullOrWhiteSpace(pathFilter)
                    && string.IsNullOrWhiteSpace(pathContains))
                    return OperatorOutput.Error("At least one of 'name', 'component', 'path' or 'path_contains' is required.");

                var types = ResolveComponentTypes(typeFilter, out var typeError);
                if (typeError != null)
                    return OperatorOutput.Error(typeError);

                if (!TryCollectScopes(pathFilter, sceneFilter, out var scopes, out var scopeError))
                    return OperatorOutput.Error(scopeError);

                var matches = new List<HierarchySearchResult>();

                foreach (var scope in scopes)
                    Walk(scope.Root, BuildParentPath(scope.Root.transform.parent, scope.SceneName),
                         scope.SceneIndex, scope.SceneName, nameFilter, pathContains, types,
                         activeOnly, withComponents, matches);

                var window = pagination.Apply(matches.ToArray(), out var total);

                return OperatorOutput.Ok(new HierarchySearchOutput(total, pagination, window));
            }

            private static void Walk(
                GameObject gameObject,
                string parentPath,
                int sceneIndex,
                string sceneName,
                string nameFilter,
                string pathContains,
                List<Type> types,
                bool activeOnly,
                bool withComponents,
                List<HierarchySearchResult> matches
            ) {
                var path = parentPath == null
                    ? $"{sceneName}/{gameObject.name}"
                    : $"{parentPath}/{gameObject.name}";

                var active = gameObject.activeInHierarchy;

                if ((!activeOnly || active)
                    && MatchesText(gameObject.name, nameFilter)
                    && MatchesText(path, pathContains)
                    && MatchesComponent(gameObject, types))
                    matches.Add(new HierarchySearchResult(sceneIndex, sceneName, gameObject, path, active, withComponents, types));

                foreach (Transform child in gameObject.transform)
                    Walk(child.gameObject, path, sceneIndex, sceneName, nameFilter, pathContains, types,
                         activeOnly, withComponents, matches);
            }

            /// <summary>Chemin absolu (noms) du parent, ou <c>null</c> si c'est une racine de scène.</summary>
            private static string BuildParentPath(Transform transform, string sceneName) {
                if (transform == null)
                    return null;

                var parts = new List<string>();
                for (var current = transform; current != null; current = current.parent)
                    parts.Add(current.name);

                parts.Add(sceneName);
                parts.Reverse();

                return string.Join("/", parts);
            }

            private static bool MatchesText(string value, string filter) {
                if (string.IsNullOrWhiteSpace(filter))
                    return true;

                var wanted = filter.Trim();
                if (wanted.IndexOf('*') >= 0 || wanted.IndexOf('?') >= 0) {
                    var pattern = "^" + Regex.Escape(wanted).Replace("\\*", ".*").Replace("\\?", ".") + "$";
                    return Regex.IsMatch(value, pattern, RegexOptions.IgnoreCase);
                }

                return value.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0;
            }

            private static bool MatchesComponent(GameObject gameObject, List<Type> types) {
                if (types == null || types.Count == 0)
                    return true;

                foreach (var type in types)
                    if (gameObject.GetComponent(type) != null)
                        return true;

                return false;
            }

            #region Component types

            private static List<Type> _componentTypes;

            /// <summary>
            /// Résout un filtre de composant sur les types chargés : nom simple, nom complet,
            /// ou suffixe de nom complet. Un type de base matche ensuite aussi ses dérivés, via
            /// <c>GetComponent(Type)</c>.
            /// </summary>
            private static List<Type> ResolveComponentTypes(string filter, out string error) {
                error = null;
                if (string.IsNullOrWhiteSpace(filter))
                    return null;

                EnsureComponentTypes();

                var wanted  = filter.Trim();
                var matches = new List<Type>();

                foreach (var type in _componentTypes) {
                    var fullName = type.FullName ?? type.Name;
                    if (string.Equals(type.Name, wanted, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(fullName, wanted, StringComparison.OrdinalIgnoreCase)
                        || fullName.EndsWith("." + wanted, StringComparison.OrdinalIgnoreCase))
                        matches.Add(type);
                }

                if (matches.Count == 0)
                    error = $"No component type matching '{wanted}'.";

                return matches;
            }

            private static void EnsureComponentTypes() {
                if (_componentTypes != null)
                    return;

                _componentTypes = new List<Type>();

                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()) {
                    Type[] types;
                    try {
                        types = assembly.GetTypes();
                    } catch (ReflectionTypeLoadException ex) {
                        types = ex.Types;
                    } catch {
                        continue; // assemblies dynamiques / non inspectables
                    }

                    foreach (var type in types)
                        if (type != null && !type.IsAbstract && typeof(Component).IsAssignableFrom(type))
                            _componentTypes.Add(type);
                }
            }

            #endregion

            #region Scopes (racines de recherche)

            /// <summary>Racine de parcours : un GameObject (et son sous-arbre) rattaché à une scène.</summary>
            private readonly struct SearchScope {
                public readonly int        SceneIndex;
                public readonly string     SceneName;
                public readonly GameObject Root;

                public SearchScope(int sceneIndex, string sceneName, GameObject root) {
                    SceneIndex = sceneIndex;
                    SceneName  = sceneName;
                    Root       = root;
                }
            }

            /// <summary>
            /// Détermine les racines à parcourir : le sous-arbre désigné par <paramref name="path"/>
            /// s'il est fourni, sinon toutes les racines des scènes (éventuellement une seule via
            /// <paramref name="sceneFilter"/>).
            /// </summary>
            private static bool TryCollectScopes(
                string pathFilter,
                string sceneFilter,
                out List<SearchScope> scopes,
                out string error
            ) {
                error  = null;
                scopes = new List<SearchScope>();

                // Départ explicite : un sous-arbre donné par son chemin.
                if (!string.IsNullOrWhiteSpace(pathFilter)) {
                    if (!HierarchyScenesGet.TryResolve(pathFilter, out var target, out var scene, out var sceneIndex, out error))
                        return false;

                    if (target != null) {
                        scopes.Add(new SearchScope(sceneIndex, scene.name, target));
                        return true;
                    }

                    // Le chemin ne désigne qu'une scène : on part de ses racines.
                    foreach (var root in scene.GetRootGameObjects())
                        scopes.Add(new SearchScope(sceneIndex, scene.name, root));
                    return true;
                }

                if (Application.isPlaying)
                    AddScope(scopes, SceneExtensions.DontDestroyOnLoadId, SceneExtensions.DontDestroyOnLoad, sceneFilter);

                for (var i = 0; i < SceneManager.sceneCount; i++)
                    AddScope(scopes, i, SceneManager.GetSceneAt(i), sceneFilter);

                if (scopes.Count == 0 && !string.IsNullOrWhiteSpace(sceneFilter)) {
                    error = $"Scene '{sceneFilter.Trim()}' not found. Available: {HierarchyScenesGet.DescribeScenes()}";
                    return false;
                }

                return true;
            }

            private static void AddScope(List<SearchScope> scopes, int index, Scene scene, string sceneFilter) {
                if (!SceneMatches(scene, index, sceneFilter))
                    return;

                foreach (var root in scene.GetRootGameObjects())
                    scopes.Add(new SearchScope(index, scene.name, root));
            }

            private static bool SceneMatches(Scene scene, int index, string filter) {
                if (string.IsNullOrWhiteSpace(filter))
                    return true;

                var wanted = filter.Trim();
                return int.TryParse(wanted, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                    ? index == parsed
                    : string.Equals(scene.name, wanted, StringComparison.OrdinalIgnoreCase);
            }

            #endregion
        }

        [Serializable]
        public class HierarchySearchResult {
            public HierarchySearchResult(
                int sceneIndex,
                string sceneName,
                GameObject gameObject,
                string path,
                bool active,
                bool withComponents,
                List<Type> matched
            ) {
                Scene      = sceneIndex;
                SceneName  = sceneName;
                Id         = gameObject.GetId();
                Name       = gameObject.name;
                Path       = path;
                Active     = active;
                Components = withComponents ? DescribeComponents(gameObject, matched) : Array.Empty<string>();
            }

            private static string[] DescribeComponents(GameObject gameObject, List<Type> matched) {
                // Filtre de type actif : on rapporte le type *concret* trouvé (un filtre sur une
                // classe de base comme 'Collider' doit ressortir en 'BoxCollider', pas 'Collider').
                if (matched != null && matched.Count > 0) {
                    var found = new List<string>(matched.Count);
                    foreach (var type in matched) {
                        var component = gameObject.GetComponent(type);
                        if (component != null)
                            found.Add(component.GetType().FullName);
                    }
                    return found.ToArray();
                }

                var all   = gameObject.GetComponents();
                var names = new List<string>(all.Length);
                foreach (var component in all)
                    names.Add(component.GetType().FullName);

                return names.ToArray();
            }

            [JsonProperty("scene")]
            public int Scene;

            [JsonProperty("scene_name")]
            public string SceneName = string.Empty;

            [JsonProperty("id")]
            public int Id;

            [JsonProperty("name")]
            public string Name = string.Empty;

            /// <summary>Chemin réutilisable tel quel dans <c>hierarchy_get</c>.</summary>
            [JsonProperty("path")]
            public string Path = string.Empty;

            [JsonProperty("active")]
            public bool Active;

            [JsonProperty("components")]
            public string[] Components = Array.Empty<string>();
        }

        [Serializable]
        public class HierarchySearchOutput {
            public HierarchySearchOutput(int total, Pagination pagination, HierarchySearchResult[] results) {
                Total   = total;
                Offset  = pagination.Offset;
                Limit   = pagination.Limit;
                Results = results;
            }

            [JsonProperty("total")]
            public int Total;

            [JsonProperty("offset")]
            public int Offset;

            [JsonProperty("limit")]
            public int Limit;

            [JsonProperty("results")]
            public HierarchySearchResult[] Results = Array.Empty<HierarchySearchResult>();
        }

        [Serializable]
        public class HierarchyGameObject {
            public HierarchyGameObject(GameObject gameObject, Pagination pagination = default, bool withProperties = true) {
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

                var components = gameObject.GetComponents();
                var window     = pagination.Apply(components, out var total);

                ComponentsTotal = total;
                Components = Array.ConvertAll(
                    window,
                    e => new HierarchyComponent(e, withProperties)
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

            /// <summary>Components for the requested <c>offset</c>/<c>limit</c> window.</summary>
            [JsonProperty("components")]
            public HierarchyComponent[] Components = Array.Empty<HierarchyComponent>();

            /// <summary>Total number of components, before pagination.</summary>
            [JsonProperty("components_total")]
            public int ComponentsTotal = 0;
        }

        [Serializable]
        public class HierarchyComponent {
            public HierarchyComponent(Component component, bool withProperties = true) {
                Id = component.GetId();
                Type = component.GetType().FullName;
                Name = component.name;

                if (!withProperties) {
                    Properties = Array.Empty<Property>();
                    return;
                }

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
