using System;
using System.Linq;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Nox.CCK.Control;
using Nox.CCK.Mods.Cores;
using Nox.CCK.Mods.Initializers;
using Nox.Control.Runtime;
using Logger = Nox.CCK.Utils.Logger;

namespace Nox.Control.Editor.Scripting {
	/// <summary>
	/// Publishes the code-evaluation operators to <c>nox.control</c> from the <b>editor</b> assembly.
	///
	/// <para>
	/// They live here (and not in the runtime mod) because they need the editor only: the snippet is
	/// compiled with the Roslyn shipped with the Unity installation and runs inside the editor's
	/// AppDomain, where the whole editor, the game and every mod assembly are reachable.
	/// </para>
	///
	/// <para>
	/// The control mod may not be initialized yet when editor mods are initialized, so the
	/// registration is retried on the editor update tick until the API answers, once.
	/// </para>
	/// </summary>
	public class EditorCodeIntegration : IEditorModInitializer {
		/// <summary>Permission required to evaluate code (declared by the operators).</summary>
		public const string Permission = "code:eval";

		/// <summary>Event streamed to the clients while a snippet runs.</summary>
		public const string OutputEvent = "code_output";

		internal static IEditorModCoreAPI Api;

		private uint[] _operators = Array.Empty<uint>();
		private bool   _registered;

		public void OnInitializeEditor(IEditorModCoreAPI api)
			=> Api = api;

		public void OnDisposeEditor() {
			Unregister();
			Api = null;
		}

		public void OnUpdateEditor() {
			if (!_registered)
				TryRegister();
		}

		private void TryRegister() {
			var control = Api?.ModAPI?.GetMod("control")?.GetInstance<IControlAPI>();
			if (control == null)
				return;

			var operators = new IOperator[] {
				new CodeEnvOperator(),
				new CodeEvalOperator()
			};

			_operators = operators.Select(control.Register).ToArray();
			_registered = true;

			Logger.Log($"Code evaluation published to nox.control ({operators.Length} operators).", tag: nameof(EditorCodeIntegration));
		}

		private void Unregister() {
			if (!_registered)
				return;

			var control = Api?.ModAPI?.GetMod("control")?.GetInstance<IControlAPI>();
			if (control != null)
				foreach (var id in _operators)
					control.Unregister(id);

			_operators  = Array.Empty<uint>();
			_registered = false;
		}
	}

	/// <summary>Base class of the code operators: main-thread marshalling and error trapping.</summary>
	public abstract class CodeOperatorBase : IOperator {
		public abstract string Name { get; }
		public abstract string Description { get; }
		public virtual string[] RequiredPermissions => new[] { EditorCodeIntegration.Permission };
		public virtual ISchema Schema => new InputSchema();

		public async UniTask<IOutput> Execute(IInput args) {
			// The control transports answer on a pool thread, and everything below is editor-only.
			await UniTask.SwitchToMainThread();

			try {
				return await Run(args);
			} catch (Exception e) {
				Logger.LogError($"{Name} failed: {e.Message}", tag: nameof(EditorCodeIntegration));
				return OperatorOutput.Error($"{Name} failed: {e.Message}");
			}
		}

		protected abstract UniTask<IOutput> Run(IInput args);

		/// <summary>Streams a line to the clients watching the evaluation (never throws).</summary>
		protected static void Stream(string level, string message)
			=> ControlEvents.Broadcast(EditorCodeIntegration.OutputEvent, new { level, message }, EditorCodeIntegration.Permission);
	}

	/// <summary>Tells whether arbitrary code can be evaluated right now (compiler available, busy…).</summary>
	public sealed class CodeEnvOperator : CodeOperatorBase {
		public override string Name => "code_env";

		public override string Description
			=> "Editor-only. Reports whether C# snippets can be evaluated (Roslyn compiler found in the Unity installation, no snippet running) and what the snippets can reach.";

		protected override UniTask<IOutput> Run(IInput _) {
			var host = typeof(CodeSnippetHost);

			return UniTask.FromResult<IOutput>(OperatorOutput.Ok(new {
				available  = EditorCodeRunner.RoslynDirectory != null,
				compiler   = "roslyn",
				version    = EditorCodeRunner.RoslynVersion?.ToString(),
				source     = EditorCodeRunner.RoslynSource,
				directory  = EditorCodeRunner.RoslynDirectory,
				error      = EditorCodeRunner.ProbeError,
				running    = EditorCodeRunner.IsRunning,
				entrypoint = $"{EditorCodeRunner.GeneratedNamespace}.{EditorCodeRunner.GeneratedType}.{EditorCodeRunner.EntryPoint}()",
				helpers = new[] {
					$"{host.FullName}.Log(object)",
					$"{host.FullName}.Warn(object)",
					$"{host.FullName}.Error(object)",
					$"{host.FullName}.Emit(string)",
					$"{host.FullName}.Emit(byte[] bytes, string mimeType = null, string uri = null, string name = null)",
					$"{host.FullName}.Emit(Stream stream, string mimeType = null, string uri = null, string name = null)",
					$"{host.FullName}.EmitFile(string path, string mimeType = null, bool embed = true, string uri = null)",
					$"{host.FullName}.Progress(float value, string message = null)"
				},
				notes = new[] {
					"The snippet is the body of a static method returning object: use 'return …;' to send a value back.",
					"A value of type byte[] or Stream, and every Emit/EmitFile block, comes back as a content block (a flux) next to the JSON value.",
					"The editor's own Roslyn is reused when it is loaded, else the Mono compatible copy of the Unity installation (Roslyn 3.7 ⇒ C# 8: no C# 9+ syntax such as target-typed new or records).",
					"Snippets are the only place where arbitrary C# runs: they execute on the main thread, so a long snippet freezes the editor.",
					"Output lines are streamed to the clients allowed to use this permission (" + EditorCodeIntegration.OutputEvent + " event)."
				}
			}));
		}
	}

	/// <summary>Compiles and runs an arbitrary C# snippet inside the editor.</summary>
	public sealed class CodeEvalOperator : CodeOperatorBase {
		public override string Name => "code_eval";

		public override string Description
			=> "Editor-only. Compiles and runs a C# snippet on the main thread and returns what it produced: the return value (JSON), the log lines, and any block it emitted (text, or a flux: bytes/stream/file). "
			 + "The snippet is the body of a static method returning object — use 'return …;' for the value, and CodeSnippetHost (see code_env) to log, emit blocks, return bytes/streams and stream progress. "
			 + "Everything loaded in the editor is reachable (Unity, UnityEditor, all mod assemblies).";

		public override ISchema Schema => new InputSchema()
			.Property<string>("code", "Body of the snippet: statements, then an optional 'return …;'.", true)
			.Property<string>("usings", "Extra using directives, comma or newline separated (e.g. 'Nox.CCK.Utils, System.Text').")
			.Property<bool>("stream", "Stream the output lines to the connected clients while the snippet runs (default true).")
			.Property<string>("mime_type", "Mime type forced on the value returned as bytes/stream (default: guessed from the file name / application/octet-stream).");

		protected override UniTask<IOutput> Run(IInput args) {
			var code = args.Get<string>("code", true);
			if (string.IsNullOrWhiteSpace(code))
				return UniTask.FromResult<IOutput>(OperatorOutput.Error("'code' is empty."));

			if (EditorCodeRunner.IsRunning)
				return UniTask.FromResult<IOutput>(OperatorOutput.Error("a snippet is already running (snippets run on the main thread, one at a time)."));

			var streamOutput = !args.Has<bool>("stream") || args.Get<bool>("stream");

			var result = EditorCodeRunner.Run(
				code,
				Split(args.Get<string>("usings")),
				streamOutput ? Stream : null
			);

			// A snippet returning raw bytes or a stream is a "flux" return: it travels as a block.
			var flux = FluxBlock(result.Value, args.Get<string>("mime_type"));

			// OperatorOutput.Blocks (and not Ok): with Ok the whole payload would be sent twice — once as
			// a text block, once as the structured value — with the captured lines in between.
			var output = OperatorOutput.Blocks(new {
				ok          = result.Ok,
				compiled    = result.Compiled,
				result      = Value(result.Value),
				result_text = result.ValueText,
				duration_ms = Math.Round(result.DurationMs, 1),
				error       = result.Error,
				diagnostics = result.Diagnostics.ToArray(),
				output      = result.Lines.Select(line => new { level = line.Level, message = line.Message }).ToArray(),
				blocks      = Metadata(result, flux)
			});

			// Visible content: only what the snippet explicitly emitted — its blocks, then the value
			// when it came back as a flux. The captured lines stay in the structured payload
			// ("output"), adding them as a text block would display them a second time.
			foreach (var block in result.Blocks)
				output.With(block.Content);

			if (flux != null)
				output.With(flux.Content);

			return UniTask.FromResult<IOutput>(output);
		}

		/// <summary>Beyond this, the return value is not duplicated into the structured payload.</summary>
		private const int MaxValueChars = 256 * 1024;

		private static readonly JsonSerializerSettings ValueSettings = new() {
			ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
			MaxDepth              = 8,
			Error                 = (_, error) => error.ErrorContext.Handled = true
		};

		/// <summary>
		/// The snippet's return value as JSON, or null when there is nothing to inline: a flux
		/// (<c>byte[]</c>/<c>Stream</c>, which travels as a block), a Unity object (whose object graph
		/// is not worth serializing) or a value too big / not serializable. The readable form is always
		/// in <c>result_text</c>.
		/// </summary>
		private static JToken Value(object value) {
			switch (value) {
				case null:
				case byte[]:
				case System.IO.Stream:
				case UnityEngine.Object:
					return null;
				case string text:
					return new JValue(text);
			}

			try {
				var json = JsonConvert.SerializeObject(value, Formatting.None, ValueSettings);
				return json.Length > MaxValueChars ? null : JToken.Parse(json);
			} catch {
				return null;
			}
		}

		/// <summary>Metadata of every content block of the result, the returned flux included.</summary>
		private static object[] Metadata(CodeRunResult result, Flux flux) {
			var blocks = result.Blocks
				.Select(block => (object)new {
					type      = block.Type,
					mime_type = block.MimeType,
					uri       = AsUri(block.Uri),
					size      = block.Size
				})
				.ToList();

			if (flux != null)
				blocks.Add(new {
					type      = TypeName(flux.Content.Type),
					mime_type = flux.Content.MimeType,
					uri       = flux.Content.Uri,
					size      = flux.Size
				});

			return blocks.ToArray();
		}

		private static string TypeName(OutputContent.Kind kind)
			=> kind switch {
				OutputContent.Kind.Text         => "text",
				OutputContent.Kind.Image        => "image",
				OutputContent.Kind.Audio        => "audio",
				OutputContent.Kind.ResourceLink => "resource_link",
				_                               => "resource"
			};

		/// <summary>A raw path becomes a real URI (<c>file:///…</c>), which is what a client can open.</summary>
		private static string AsUri(string value)
			=> !string.IsNullOrEmpty(value) && System.Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.IsAbsoluteUri
				? uri.AbsoluteUri
				: value;

		/// <summary>A flux returned by the snippet: raw bytes to travel as a block, plus its size.</summary>
		private sealed class Flux {
			public int           Size;
			public OutputContent Content;
		}

		/// <summary>
		/// A snippet returning <c>byte[]</c> or a <c>Stream</c> is a "flux" return: it comes back as a
		/// content block (image/audio when the mime type says so, embedded file otherwise). The stream
		/// is disposed.
		/// </summary>
		private static Flux FluxBlock(object value, string mimeType) {
			try {
				var bytes = value switch {
					byte[] array when array.Length > 0 => array,
					System.IO.Stream stream          => OutputContent.ReadAll(stream),
					_                                => null
				};

				if (bytes is not { Length: > 0 })
					return null;

				var mime = string.IsNullOrEmpty(mimeType) ? "application/octet-stream" : mimeType;
				OutputContent content;

				if (mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
					content = OutputContent.FromImage(bytes, mime);
				else if (mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
					content = OutputContent.FromAudio(bytes, mime);
				else
					content = OutputContent.FromResource("snippet://result", bytes, mime);

				return new Flux { Size = bytes.Length, Content = content };
			} catch (Exception e) {
				Logger.LogWarning($"returned value could not be attached as a block: {e.Message}", tag: nameof(CodeEvalOperator));
				return null;
			}
		}

		private static string[] Split(string value)
			=> string.IsNullOrWhiteSpace(value)
				? Array.Empty<string>()
				: value.Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
					.Select(entry => entry.Trim())
					.Where(entry => entry.Length > 0)
					.ToArray();
	}
}
