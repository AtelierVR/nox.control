#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json;
using Nox.CCK.Control;
using UnityEditor;

namespace Nox.Control.Runtime.Handlers {
	/// <summary>
	/// Inventory of the <c>[MenuItem]</c> declared in the loaded assemblies, cached
	/// for the lifetime of the domain (statics get reset by the domain reload
	/// anyway, so the cache cannot go stale).
	/// <para>
	/// Used both by <c>menu_search</c> (list/filter) and <c>menu_trigger</c>
	/// (invoke). The inventory is independent of the <c>Nox/</c> prefix used by
	/// <c>MenuItemPanel</c>: every editor menu is visible.
	/// </para>
	/// </summary>
	internal static class MenuItemRegistry {
		/// <summary>A resolved <c>[MenuItem]</c>: path, declaring method and metadata.</summary>
		[Serializable]
		public class MenuItemEntry {
			[JsonProperty("path")]
			public string Path = string.Empty;

			/// <summary><c>true</c> for a validate handler (<c>[MenuItem(path, true)]</c>).</summary>
			[JsonProperty("validate")]
			public bool Validate;

			[JsonProperty("priority")]
			public int Priority;

			[JsonProperty("method")]
			public string Method = string.Empty;

			[JsonProperty("type")]
			public string Type = string.Empty;

			[JsonProperty("assembly")]
			public string Assembly = string.Empty;

			/// <summary>Method to invoke for <c>menu_trigger</c> (never serialized).</summary>
			[JsonIgnore]
			public MethodInfo Info;
		}

		private static List<MenuItemEntry> _entries;

		internal static IReadOnlyList<MenuItemEntry> All
			=> _entries ??= Scan();

		/// <summary>
		/// Walks <see cref="AppDomain.CurrentDomain"/> and keeps every static method
		/// carrying a <see cref="MenuItem"/>. Assemblies that cannot be inspected (dynamic,
		/// partially loaded types) are skipped rather than failing the whole search.
		/// </summary>
		private static List<MenuItemEntry> Scan() {
			var entries = new List<MenuItemEntry>();

			foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()) {
				Type[] types;
				try {
					types = assembly.GetTypes();
				} catch (ReflectionTypeLoadException ex) {
					types = ex.Types;
				} catch {
					continue;
				}

				foreach (var type in types) {
					if (type == null)
						continue;

					MethodInfo[] methods;
					try {
						methods = type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
					} catch {
						continue;
					}

					foreach (var method in methods) {
						var attribute = method.GetCustomAttributes(typeof(MenuItem), false)
							.OfType<MenuItem>()
							.FirstOrDefault();

						// An empty path, or one starting with '/', yields an empty segment that Unity
						// cannot resolve (see HandColliderEditor): no point listing it.
						if (attribute == null
						    || string.IsNullOrWhiteSpace(attribute.menuItem)
						    || attribute.menuItem.StartsWith("/", StringComparison.Ordinal))
							continue;

						entries.Add(new MenuItemEntry {
							Path     = attribute.menuItem,
							Validate = attribute.validate,
							Priority = attribute.priority,
							Method   = method.Name,
							Type     = type.FullName ?? type.Name,
							Assembly = assembly.GetName().Name,
							Info     = method
						});
					}
				}
			}

			entries.Sort((a, b) => {
				var byPath = string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);
				return byPath != 0 ? byPath : string.CompareOrdinal(a.Method, b.Method);
			});

			return entries;
		}

		/// <summary>
		/// Resolves a menu path: exact match first (case-insensitive),
		/// then a unique match on a path suffix (so a short suffix such as
		/// <c>"Screenshot Tool"</c> is accepted). Ambiguity → an error listing the candidates, to
		/// avoid a second round-trip.
		/// </summary>
		internal static bool TryResolve(string path, out MenuItemEntry entry, out string error) {
			entry = null;
			error = null;

			var wanted = path?.Trim();
			if (string.IsNullOrEmpty(wanted)) {
				error = "'path' is required.";
				return false;
			}

			var all = All;

			// A single path can carry both the executable handler and its validator
			// ([MenuItem(path)] + [MenuItem(path, true)]): the executable one wins.
			entry = all
				.Where(e => string.Equals(e.Path, wanted, StringComparison.OrdinalIgnoreCase))
				.OrderBy(e => e.Validate)
				.FirstOrDefault();
			if (entry != null)
				return true;

			var candidates = all
				.Where(e => e.Path.EndsWith("/" + wanted, StringComparison.OrdinalIgnoreCase)
				            || string.Equals(e.Method, wanted, StringComparison.OrdinalIgnoreCase))
				.ToArray();

			// Here too, validators must not make a path ambiguous.
			var executable = Array.FindAll(candidates, e => !e.Validate);
			if (executable.Length > 0)
				candidates = executable;

			switch (candidates.Length) {
				case 0:
					error = $"Menu item '{wanted}' not found. Use 'menu_search' to discover available paths.";
					return false;
				case > 1:
					error = $"Menu item '{wanted}' is ambiguous: {Describe(candidates)}.";
					return false;
			}

			entry = candidates[0];
			return true;
		}

		internal static string Describe(IEnumerable<MenuItemEntry> entries)
			=> string.Join(", ", entries.Select(e => e.Path));

		/// <summary>
		/// Text filter shared by <c>menu_search</c>: wildcards <c>*</c>/<c>?</c> when present,
		/// otherwise a case-insensitive substring.
		/// </summary>
		internal static bool Matches(string value, string filter) {
			if (string.IsNullOrWhiteSpace(filter))
				return true;

			if (value == null)
				return false;

			var wanted = filter.Trim();
			if (wanted.IndexOf('*') >= 0 || wanted.IndexOf('?') >= 0) {
				var pattern = "^" + Regex.Escape(wanted).Replace("\\*", ".*").Replace("\\?", ".") + "$";
				return Regex.IsMatch(value, pattern, RegexOptions.IgnoreCase);
			}

			return value.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0;
		}
	}

	/// <summary>
	/// Searches the editor's <c>[MenuItem]</c> entries. Without a filter, lists everything
	/// (paginated) — which doubles as <c>menu_list</c>.
	/// </summary>
	public class MenuSearch : IOperator {
		public string Name
			=> "menu_search";

		public string Description
			=> "Search Unity Editor [MenuItem] entries by path, method or declaring type. "
			   + "Validate handlers are excluded unless 'include_validate' is set. "
			   + "Each result carries a path usable directly with menu_trigger.";

		public string[] RequiredPermissions => new[] { "menu:read" };

		public ISchema Schema => new InputSchema()
			.Property<string>(
				"query",
				"Filter on the menu path, method name or declaring type (case-insensitive). "
				+ "'*' and '?' wildcards are supported, otherwise this is a substring match."
			)
			.Property<string>(
				"prefix",
				"Only keep menu items whose path starts with this string (e.g. 'Nox/' or 'Assets/')."
			)
			.Property<bool>(
				"include_validate",
				"Include validate handlers ([MenuItem(path, true)]). Defaults to false."
			)
			.WithPagination();

		public async UniTask<IOutput> Execute(IInput args) {
			await UniTask.SwitchToMainThread();

			var query           = args.Get<string>("query");
			var prefix          = args.Get<string>("prefix");
			var includeValidate = args.Get<bool?>("include_validate") ?? false;
			var pagination      = Pagination.Read(args);

			var matches = new List<MenuItemRegistry.MenuItemEntry>();
			foreach (var entry in MenuItemRegistry.All) {
				if (!includeValidate && entry.Validate)
					continue;

				if (!string.IsNullOrWhiteSpace(prefix)
				    && !entry.Path.StartsWith(prefix.Trim(), StringComparison.OrdinalIgnoreCase))
					continue;

				if (!MenuItemRegistry.Matches(entry.Path, query)
				    && !MenuItemRegistry.Matches(entry.Method, query)
				    && !MenuItemRegistry.Matches(entry.Type, query))
					continue;

				matches.Add(entry);
			}

			var window = pagination.Apply(matches.ToArray(), out var total);

			return OperatorOutput.Ok(new {
				total,
				offset  = pagination.Offset,
				limit   = pagination.Limit,
				results = window
			});
		}
	}

	/// <summary>
	/// Invokes a <c>[MenuItem]</c> by its path. The method is called through reflection
	/// (reliable even for menus absent from the main menu bar, e.g.
	/// <c>CONTEXT/…</c>), with a fallback to <see cref="EditorApplication.ExecuteMenuItem"/>
	/// for Unity's built-in menus (e.g. <c>File/Save</c>).
	/// </summary>
	public class MenuTrigger : IOperator {
		public string Name
			=> "menu_trigger";

		public string Description
			=> "Invoke a Unity Editor [MenuItem] by its path (e.g. 'Nox/Tools/Screenshot Tool'). "
			   + "Falls back to EditorApplication.ExecuteMenuItem for built-in Unity menus.";

		public string[] RequiredPermissions => new[] { "menu:control" };

		public ISchema Schema => new InputSchema()
			.Property<string>(
				"path",
				"The menu path, as returned by menu_search. A unique suffix (e.g. "
				+ "'Screenshot Tool') is also accepted. Case-insensitive.",
				true
			);

		public async UniTask<IOutput> Execute(IInput args) {
			await UniTask.SwitchToMainThread();

			var path = args.Get<string>("path", true);

			// 1) A [MenuItem] declared in a user assembly (or a mod).
			if (MenuItemRegistry.TryResolve(path, out var entry, out _)) {
				if (entry.Validate)
					return OperatorOutput.Error(
						$"'{entry.Path}' is a validate handler, not an executable menu item."
					);

				if (!TryBuildArguments(entry.Info, out var parameters, out var argumentError))
					return OperatorOutput.Error(argumentError);

				try {
					var result = entry.Info.Invoke(null, parameters);
					return OperatorOutput.Ok(new {
						path   = entry.Path,
						type   = entry.Type,
						method = entry.Method,
						result = result?.ToString()
					});
				} catch (TargetInvocationException ex) {
					return OperatorOutput.Error($"'{entry.Path}' threw: {ex.InnerException?.Message ?? ex.Message}");
				} catch (Exception ex) {
					return OperatorOutput.Error($"Failed to invoke '{entry.Path}': {ex.Message}");
				}
			}

			// 2) Fallback: Unity's built-in menus expose no inspectable C# method.
			var executed = false;
			try {
				executed = EditorApplication.ExecuteMenuItem(path.Trim());
			} catch (Exception ex) {
				return OperatorOutput.Error($"Failed to execute menu '{path}': {ex.Message}");
			}

			return executed
				? OperatorOutput.Ok(new { path = path.Trim(), executed = true })
				: OperatorOutput.Error(
					$"Menu item '{path}' not found. Use 'menu_search' to discover available paths."
				);
		}

		/// <summary>
		/// A <c>[MenuItem]</c> takes either no parameter or a single
		/// <see cref="MenuCommand"/>. Any other signature is rejected with an explicit message.
		/// </summary>
		private static bool TryBuildArguments(MethodInfo method, out object[] parameters, out string error) {
			parameters = null;
			error      = null;

			var expected = method.GetParameters();
			switch (expected.Length) {
				case 0:
					return true;
				case 1 when typeof(MenuCommand).IsAssignableFrom(expected[0].ParameterType):
					parameters = new object[] { new MenuCommand(null) };
					return true;
				default:
					error = $"'{method.DeclaringType?.FullName}.{method.Name}' takes unsupported parameters "
					        + $"({string.Join(", ", expected.Select(p => p.ParameterType.Name))}).";
					return false;
			}
		}
	}
}
#endif
