using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Nox.Control.Editor.Scripting {
	/// <summary>Everything a snippet run produced.</summary>
	public sealed class CodeRunResult {
		public bool   Ok;
		public bool   Compiled;
		public string Error;
		public object Value;
		public string ValueText;
		public double DurationMs;
		public readonly List<string> Diagnostics = new();
		public readonly List<CodeSnippetHost.Line>  Lines  = new();
		public readonly List<CodeSnippetHost.Block> Blocks = new();
	}

	/// <summary>
	/// Compiles and runs a C# snippet inside the editor.
	/// <para>
	/// The compiler is a Roslyn shipped with the Unity installation (or the one the editor already
	/// loaded), driven by reflection: that way nox.control takes no package/Roslyn dependency, and the
	/// snippet can use whatever the running editor exposes (Unity, UnityEditor, every loaded mod
	/// assembly) — the references are the assemblies currently loaded in the editor's AppDomain.
	/// </para>
	/// <para>
	/// The snippet is the body of <c>static object Run()</c> in a generated class; it uses
	/// <see cref="CodeSnippetHost"/> to log and to return rich blocks (text / binary / link).
	/// </para>
	/// </summary>
	public static class EditorCodeRunner {
		public const string GeneratedNamespace = "Nox.Control.Scripting.Generated";
		public const string GeneratedType      = "__NoxSnippet";
		public const string EntryPoint         = "Run";

		/// <summary>Assemblies never worth referencing (Roslyn itself, and the transient ones).</summary>
		private static readonly string[] ExcludedAssemblies = { "Microsoft.CodeAnalysis", "System.Reflection.Metadata", "System.Collections.Immutable", "netstandard" };

		private static readonly object Gate = new();

		private static bool     _probed;
		private static string   _roslynDirectory;
		private static string   _roslynSource;
		private static Version  _roslynVersion;
		private static Assembly _analysis;
		private static Assembly _csharp;
		private static string   _probeError;
		private static bool     _running;

		/// <summary>Whether a snippet is currently running (a snippet may not call itself).</summary>
		public static bool IsRunning
			=> _running;

		/// <summary>Directory of the Roslyn used to compile the snippets, or null.</summary>
		public static string RoslynDirectory {
			get {
				Probe();
				return _roslynDirectory;
			}
		}

		/// <summary>Where the compiler comes from: "loaded in the editor", or its directory.</summary>
		public static string RoslynSource {
			get {
				Probe();
				return _roslynSource;
			}
		}

		/// <summary>Version of the Roslyn used to compile the snippets (null when unavailable).</summary>
		public static Version RoslynVersion {
			get {
				Probe();
				return _roslynVersion;
			}
		}

		/// <summary>Why Roslyn could not be loaded (when <see cref="RoslynDirectory"/> is null).</summary>
		public static string ProbeError {
			get {
				Probe();
				return _probeError;
			}
		}

		#region Run

		/// <summary>Runs a snippet, capturing its output, its blocks and its return value.</summary>
		/// <param name="code">Body of <c>static object Run()</c>.</param>
		/// <param name="usings">Optional <c>using</c> directives prepended to the generated file.</param>
		/// <param name="stream">Called for every output line while the snippet runs (may be null).</param>
		public static CodeRunResult Run(string code, IEnumerable<string> usings = null, Action<string, string> stream = null) {
			var result = new CodeRunResult();

			lock (Gate) {
				if (_running) {
					result.Error = "a snippet is already running";
					return result;
				}

				_running = true;
			}

			var watch = Stopwatch.StartNew();

			// Clear the capture up-front: a run that fails before executing (no compiler, code that does
			// not compile) must not republish the lines and blocks of the previous run.
			CodeSnippetHost.Begin(null);

			try {
				if (!EnsureCompiler(out var compilerError)) {
					result.Error = compilerError;
					return result;
				}

				var source = BuildSource(code, usings);
				var image  = Compile(source, result);

				if (image == null) {
					result.Error = result.Diagnostics.Count > 0
						? string.Join("\n", result.Diagnostics.Where(d => d.Contains("error")))
						: "compilation failed";
					return result;
				}

				result.Compiled = true;

				Application.logMessageReceived += OnUnityLog;
				CodeSnippetHost.Begin(stream);

				try {
					var assembly = Assembly.Load(image);
					var type     = assembly.GetType($"{GeneratedNamespace}.{GeneratedType}", throwOnError: true);
					var entry    = type.GetMethod(EntryPoint, BindingFlags.Public | BindingFlags.Static);

					if (entry == null) {
						result.Error = $"generated entry point {GeneratedType}.{EntryPoint} not found";
						return result;
					}

					result.Value     = entry.Invoke(null, null);
					result.ValueText = Describe(result.Value);
					result.Ok        = true;
				} finally {
					CodeSnippetHost.End();
					Application.logMessageReceived -= OnUnityLog;
				}
			} catch (Exception e) {
				result.Error = e is TargetInvocationException { InnerException: { } inner } ? inner.ToString() : e.ToString();
			} finally {
				watch.Stop();
				CodeSnippetHost.End();
				result.DurationMs = watch.Elapsed.TotalMilliseconds;
				result.Lines.AddRange(CodeSnippetHost.TakeLines());
				result.Blocks.AddRange(CodeSnippetHost.TakeBlocks());
				_running = false;
			}

			return result;
		}

		private static void OnUnityLog(string condition, string stackTrace, UnityEngine.LogType type)
			=> CodeSnippetHost.Append(type switch {
				UnityEngine.LogType.Error or UnityEngine.LogType.Exception or UnityEngine.LogType.Assert => "error",
				UnityEngine.LogType.Warning => "warning",
				_                           => "log"
			}, condition);

		/// <summary>Textual description of a return value (JSON when possible).</summary>
		public static string Describe(object value) {
			switch (value) {
				case null:
					return "null";
				case string text:
					return text;
				case byte[] bytes:
					return $"byte[{bytes.Length}]";
				case Stream stream:
					return $"stream ({stream.GetType().Name}, length {(stream.CanSeek ? stream.Length.ToString() : "unknown")})";
				case UnityEngine.Object unityObject:
					return unityObject ? $"{unityObject.GetType().Name} '{unityObject.name}'" : "(destroyed UnityEngine.Object)";
			}

			try {
				return Newtonsoft.Json.JsonConvert.SerializeObject(value, Newtonsoft.Json.Formatting.None);
			} catch {
				return value.ToString();
			}
		}

		private static string BuildSource(string code, IEnumerable<string> usings) {
			var builder = new StringBuilder();

			builder.AppendLine("using System;");
			builder.AppendLine("using System.Collections.Generic;");
			builder.AppendLine("using System.IO;");
			builder.AppendLine("using System.Linq;");
			builder.AppendLine("using System.Text;");
			builder.AppendLine("using System.Threading.Tasks;");
			builder.AppendLine("using UnityEngine;");
			builder.AppendLine("using UnityEditor;");
			builder.AppendLine($"using {typeof(CodeSnippetHost).Namespace};");

			foreach (var entry in usings ?? Array.Empty<string>()) {
				var directive = entry?.Trim().TrimEnd(';');
				if (string.IsNullOrEmpty(directive))
					continue;

				builder.Append("using ").Append(directive.StartsWith("using ") ? directive[6..] : directive).AppendLine(";");
			}

			builder.AppendLine();
			builder.Append("namespace ").AppendLine(GeneratedNamespace);
			builder.AppendLine("{");
			builder.Append("\tpublic static class ").AppendLine(GeneratedType);
			builder.AppendLine("\t{");
			builder.Append("\t\tpublic static object ").Append(EntryPoint).AppendLine("()");
			builder.AppendLine("\t\t{");
			builder.AppendLine(code);
			builder.AppendLine("\t\t}");

			// CSharpCompilation.Create defaults to a console application: a Main is required for Emit
			// to succeed (CS5001), even though the entry point we invoke is Run.
			builder.AppendLine();
			builder.AppendLine("\t\tpublic static void Main() { }");
			builder.AppendLine("\t}");
			builder.AppendLine("}");

			return builder.ToString();
		}

		#endregion

		#region Compiler (Roslyn, by reflection)

		private static void Probe() {
			lock (Gate) {
				if (_probed)
					return;

				_probed = true;

				var failures = new List<string>();

				// 1. A Roslyn the editor already loaded: the ideal case (no loading at all).
				var loadedAnalysis = FindLoaded("Microsoft.CodeAnalysis");
				var loadedCSharp   = FindLoaded("Microsoft.CodeAnalysis.CSharp");

				if (Validate(loadedAnalysis, loadedCSharp, out var loadedError)) {
					_analysis      = loadedAnalysis;
					_csharp        = loadedCSharp;
					_roslynSource  = "loaded in the editor";
					_roslynVersion = loadedAnalysis.GetName().Version;
					_roslynDirectory = DirectoryOf(loadedAnalysis);
					RegisterResolver();
					return;
				}

				if (!string.IsNullOrEmpty(loadedError))
					failures.Add($"(already loaded) {loadedError}");

				// 2. A Roslyn shipped in the Unity installation. The order matters: the Mono-side copies
				// first (Mono can load them), the .NET SDK ones last — those are built for CoreCLR and
				// Mono rejects them with "Invalid Image".
				foreach (var directory in RoslynDirectories()) {
					if (!TryLoad(directory, out var analysis, out var csharp, out var error)) {
						failures.Add(error);
						continue;
					}

					_analysis        = analysis;
					_csharp          = csharp;
					_roslynDirectory = directory;
					_roslynSource    = directory;
					_roslynVersion   = analysis.GetName().Version;
					RegisterResolver();
					return;
				}

				_probeError = "no usable Roslyn compiler was found:\n" + string.Join("\n", failures);
			}
		}

		private static bool EnsureCompiler(out string error) {
			Probe();
			error = _probeError;
			return _analysis != null && _csharp != null;
		}

		/// <summary>An assembly already loaded by the editor, by simple name, or null.</summary>
		private static Assembly FindLoaded(string name) {
			foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()) {
				try {
					if (assembly.GetName().Name == name)
						return assembly;
				} catch {
					// Assemblies that refuse to be described (dynamic/reflection-only) are not compilers.
				}
			}

			return null;
		}

		private static string DirectoryOf(Assembly assembly) {
			try {
				var location = assembly?.Location;
				return string.IsNullOrEmpty(location) ? null : Path.GetDirectoryName(location);
			} catch {
				return null;
			}
		}

		/// <summary>
		/// Roslyn copies shipped with the Unity installation. The Mono ones come first because the
		/// editor runs on Mono: <c>MonoBleedingEdge/lib/mono/msbuild/Current/bin/Roslyn</c> is the very
		/// compiler Mono uses (Roslyn 3.7, netstandard2.0, dependencies next to it).
		/// <c>DotNetSdkRoslyn</c> / <c>ApiUpdater</c> are .NET (CoreCLR) apps ⇒ Mono cannot load them.
		/// </summary>
		private static IEnumerable<string> RoslynDirectories() {
			var contents = EditorApplication.applicationContentsPath;

			var candidates = new[] {
				Path.Combine(contents, "MonoBleedingEdge", "lib", "mono", "msbuild", "Current", "bin", "Roslyn"),
				Path.Combine(contents, "MonoBleedingEdge", "lib", "mono", "4.5"),
				Path.Combine(contents, "Tools", "BuildPipeline", "Compilation", "ApiUpdater"),
				Path.Combine(contents, "DotNetSdkRoslyn"),
				Path.Combine(contents, "Tools", "Roslyn"),
				Path.Combine(contents, "Tools", "RoslynScripts"),
				Path.Combine(contents, "..", "Tools", "Roslyn")
			};

			var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

			foreach (var candidate in candidates) {
				string full;

				try {
					full = Path.GetFullPath(candidate);
				} catch {
					continue;
				}

				if (seen.Add(full) && File.Exists(Path.Combine(full, "Microsoft.CodeAnalysis.CSharp.dll")))
					yield return full;
			}
		}

		private static bool TryLoad(string directory, out Assembly analysis, out Assembly csharp, out string error) {
			analysis = null;
			csharp   = null;

			try {
				analysis = Assembly.LoadFrom(Path.Combine(directory, "Microsoft.CodeAnalysis.dll"));
				csharp   = Assembly.LoadFrom(Path.Combine(directory, "Microsoft.CodeAnalysis.CSharp.dll"));
			} catch (Exception e) {
				error = $"{directory}: {e.Message}";
				return false;
			}

			if (Validate(analysis, csharp, out var apiError)) {
				error = null;
				return true;
			}

			error = $"{directory}: {apiError}";
			return false;
		}

		private static bool Validate(Assembly analysis, Assembly csharp, out string error) {
			if (analysis == null || csharp == null) {
				error = "Microsoft.CodeAnalysis(.CSharp).dll is not loaded";
				return false;
			}

			var syntaxTree  = analysis.GetType("Microsoft.CodeAnalysis.SyntaxTree");
			var reference   = analysis.GetType("Microsoft.CodeAnalysis.MetadataReference");
			var compilation = csharp.GetType("Microsoft.CodeAnalysis.CSharp.CSharpCompilation");

			if (syntaxTree == null || reference == null || compilation == null) {
				error = "the Roslyn API types are missing";
				return false;
			}

			if (FindParseText(csharp) == null) {
				error = "CSharpSyntaxTree.ParseText(string, …) is missing";
				return false;
			}

			if (FindCreate(analysis, csharp) == null) {
				error = "CSharpCompilation.Create(string, …) is missing";
				return false;
			}

			if (FindCreateFromFile(analysis) == null) {
				error = "MetadataReference.CreateFromFile(string, …) is missing";
				return false;
			}

			if (FindEmit(compilation) == null) {
				error = "CSharpCompilation.Emit(Stream, …) is missing";
				return false;
			}

			error = null;
			return true;
		}

		/// <summary>
		/// Roslyn's own dependencies (System.Collections.Immutable, System.Reflection.Metadata, …) sit
		/// next to it: resolve them from there when the editor's probing paths do not find them.
		/// </summary>
		private static void RegisterResolver() {
			var directory = _roslynDirectory;
			if (string.IsNullOrEmpty(directory))
				return;

			AppDomain.CurrentDomain.AssemblyResolve += (_, eventArgs) => {
				try {
					var name = new AssemblyName(eventArgs.Name).Name;
					var path = Path.Combine(directory, name + ".dll");
					return File.Exists(path) ? Assembly.LoadFrom(path) : null;
				} catch {
					return null;
				}
			};
		}

		/// <summary>Compiles the generated source, or fills <paramref name="result"/> and returns null.</summary>
		private static byte[] Compile(string source, CodeRunResult result) {
			var syntaxTreeBase        = SyntaxTreeType;
			var metadataReferenceType = MetadataReferenceType;
			var compilationType       = CompilationType;

			var parseText      = FindParseText(_csharp) ?? throw new InvalidOperationException("CSharpSyntaxTree.ParseText(string, …) not found");
			var create         = FindCreate(_analysis, _csharp) ?? throw new InvalidOperationException("CSharpCompilation.Create(string, …) not found");
			var createFromFile = FindCreateFromFile(_analysis) ?? throw new InvalidOperationException("MetadataReference.CreateFromFile(string, …) not found");
			var emit           = FindEmit(compilationType) ?? throw new InvalidOperationException("CSharpCompilation.Emit(Stream, …) not found");
			var tree = InvokeOptional(parseText, null, source, CreateParseOptions());

			var trees = CreateTypedList(syntaxTreeBase);
			trees.Add(tree);

			var references = CreateTypedList(metadataReferenceType);
			foreach (var path in CollectReferences())
				references.Add(InvokeOptional(createFromFile, null, path));

			var compilation = InvokeOptional(create, null, $"NoxSnippet_{Guid.NewGuid():N}", trees, references);

			using var image = new MemoryStream();

			var emitResult  = InvokeOptional(emit, compilation, image);
			var emitType    = emitResult.GetType();
			var diagnostics = (IEnumerable)emitType.GetProperty("Diagnostics", BindingFlags.Public | BindingFlags.Instance).GetValue(emitResult);

			foreach (var diagnostic in diagnostics) {
				var text = diagnostic.ToString();

				// Warnings are kept as diagnostics; only errors make the compilation fail.
				if (!string.IsNullOrWhiteSpace(text))
					result.Diagnostics.Add(text);
			}

			var success = (bool)emitType.GetProperty("Success", BindingFlags.Public | BindingFlags.Instance).GetValue(emitResult);
			return success ? image.ToArray() : null;
		}

		private static IList CreateTypedList(Type element)
			=> (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(element));

		#region Roslyn API lookups

		private static Type SyntaxTreeType
			=> _analysis.GetType("Microsoft.CodeAnalysis.SyntaxTree", throwOnError: true);

		private static Type MetadataReferenceType
			=> _analysis.GetType("Microsoft.CodeAnalysis.MetadataReference", throwOnError: true);

		private static Type ParseOptionsType
			=> _csharp.GetType("Microsoft.CodeAnalysis.CSharp.CSharpParseOptions", throwOnError: true);

		private static Type CompilationType
			=> _csharp.GetType("Microsoft.CodeAnalysis.CSharp.CSharpCompilation", throwOnError: true);

		/// <summary>
		/// Looks a Roslyn method up by shape (name + first parameters) instead of by exact signature:
		/// optional parameters do not create overloads, so the signature drifts between versions
		/// (Roslyn 3.7 has ParseText(string, CSharpParseOptions, string, Encoding, CancellationToken)).
		/// </summary>
		private static MethodInfo FindParseText(Assembly csharp) {
			var syntaxTree = csharp.GetType("Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree");

			return syntaxTree?.GetMethods(BindingFlags.Public | BindingFlags.Static)
				.Where(method => method.Name == "ParseText"
					&& method.GetParameters().Length > 0
					&& method.GetParameters()[0].ParameterType == typeof(string))
				.OrderBy(method => method.GetParameters().Length)
				.FirstOrDefault();
		}

		private static MethodInfo FindCreate(Assembly analysis, Assembly csharp) {
			var compilation = csharp.GetType("Microsoft.CodeAnalysis.CSharp.CSharpCompilation");
			var syntaxTree  = analysis.GetType("Microsoft.CodeAnalysis.SyntaxTree");

			if (compilation == null || syntaxTree == null)
				return null;

			var trees = typeof(IEnumerable<>).MakeGenericType(syntaxTree);

			return compilation.GetMethods(BindingFlags.Public | BindingFlags.Static)
				.Where(method => method.Name == "Create"
					&& method.GetParameters().Length >= 3
					&& method.GetParameters()[0].ParameterType == typeof(string)
					&& method.GetParameters()[1].ParameterType == trees)
				.OrderBy(method => method.GetParameters().Length)
				.FirstOrDefault();
		}

		private static MethodInfo FindCreateFromFile(Assembly analysis) {
			var reference = analysis.GetType("Microsoft.CodeAnalysis.MetadataReference");

			return reference?.GetMethods(BindingFlags.Public | BindingFlags.Static)
				.Where(method => method.Name == "CreateFromFile"
					&& method.GetParameters().Length > 0
					&& method.GetParameters()[0].ParameterType == typeof(string))
				.OrderBy(method => method.GetParameters().Length)
				.FirstOrDefault();
		}

		private static MethodInfo FindEmit(Type compilation)
			=> compilation.GetMethods(BindingFlags.Public | BindingFlags.Instance)
				.Where(method => method.Name == "Emit" && method.GetParameters().Length > 0 && method.GetParameters()[0].ParameterType == typeof(Stream))
				.OrderBy(method => method.GetParameters().Length)
				.FirstOrDefault();

		/// <summary>
		/// Invokes a Roslyn method whose tail is optional: the arguments we know come first, the
		/// remaining parameters are left at their default (null).
		/// </summary>
		private static object InvokeOptional(MethodInfo method, object target, params object[] leading) {
			var arguments = new object[method.GetParameters().Length];
			Array.Copy(leading, arguments, Math.Min(leading.Length, arguments.Length));
			return method.Invoke(target, arguments);
		}

		/// <summary>
		/// Parse options pinned to the newest language version the found Roslyn understands: without
		/// them the snippets fall back to the default (older) version of that compiler.
		/// </summary>
		private static object CreateParseOptions() {
			try {
				var type = ParseOptionsType;
				var ctor = type.GetConstructors().OrderBy(constructor => constructor.GetParameters().Length).FirstOrDefault();

				if (ctor == null)
					return null;

				var arguments = ctor.GetParameters().Select(parameter => {
					var parameterType = parameter.ParameterType;

					if (parameterType.IsEnum) {
						var wanted = parameterType.Name.IndexOf("LanguageVersion", StringComparison.OrdinalIgnoreCase) >= 0 ? "Latest" : null;

						if (wanted != null) {
							try {
								return Enum.Parse(parameterType, wanted);
							} catch {
								// 'Latest' does not exist: fall back to the enum default below.
							}
						}

						return Enum.ToObject(parameterType, 0);
					}

					return parameterType.IsValueType ? Activator.CreateInstance(parameterType) : null;
				}).ToArray();

				return ctor.Invoke(arguments);
			} catch {
				return null;
			}
		}

		#endregion

		/// <summary>
		/// Every assembly loaded in the editor (Unity, UnityEditor, the mods) minus Roslyn itself:
		/// that is what makes a snippet able to reach the whole running editor. Duplicated identities
		/// are dropped (the first one wins), otherwise Roslyn reports CS1703.
		/// </summary>
		private static IEnumerable<string> CollectReferences() {
			var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

			foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()) {
				string location;
				string name;

				try {
					if (assembly.IsDynamic)
						continue;

					location = assembly.Location;
					name     = assembly.GetName().Name;
				} catch {
					continue;
				}

				if (string.IsNullOrEmpty(location) || string.IsNullOrEmpty(name) || !File.Exists(location))
					continue;

				if (ExcludedAssemblies.Any(excluded => name.StartsWith(excluded, StringComparison.OrdinalIgnoreCase)))
					continue;

				if (seen.Add(name))
					yield return location;
			}

			// Facades the snippets need for the common BCL types.
			var contents = EditorApplication.applicationContentsPath;
			foreach (var facade in new[] {
				Path.Combine(contents, "NetStandard", "compat", "2.1.0", "shims", "netstandard", "netstandard.dll"),
				Path.Combine(contents, "MonoBleedingEdge", "lib", "mono", "4.7.1-api", "Facades", "netstandard.dll")
			}) {
				if (File.Exists(facade) && seen.Add("netstandard"))
					yield return facade;
			}
		}

		#endregion
	}
}
