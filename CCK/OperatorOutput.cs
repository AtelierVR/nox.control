using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Nox.Control;

namespace Nox.CCK.Control {
	/// <summary>
	/// Typed result of an operator, transport-agnostic.
	/// <para>
	/// A result carries <see cref="OutputContent">content blocks</see> (text, image,
	/// audio, embedded file or link) — which is what MCP exposes as-is in
	/// <c>result.content</c> — plus a structured value (<see cref="Value"/>) and an
	/// <see cref="IsError"/> flag.
	/// </para>
	/// <para>
	/// Transports with no notion of blocks (REST <c>/api/call</c>, WebSocket) use
	/// <see cref="ToToken"/>, which inlines binary data as base64 into the value.
	/// </para>
	/// </summary>
	public class OperatorOutput : IOutput {
		private readonly List<OutputContent> _contents = new();

		private OperatorOutput(JToken value, bool isError) {
			Value   = value ?? new JObject();
			IsError = isError;
		}

		/// <summary>
		/// <c>true</c> when the operator failed: MCP reports it through <c>result.isError</c>
		/// (the JSON-RPC call still succeeds, only the result carries the error).
		/// </summary>
		public bool IsError { get; }

		/// <summary>Structured payload, without binary data.</summary>
		public JToken Value { get; }

		/// <summary>Content blocks returned to the client (<c>result.content</c>).</summary>
		public IReadOnlyList<OutputContent> Contents
			=> _contents;

		#region Factories

		/// <summary>Success: the value is returned as-is (text block + structured value).</summary>
		public static OperatorOutput Ok(object value = null)
			=> Build(value == null ? new JObject() : JToken.FromObject(value), false);

		/// <summary>Execution failure (not a JSON-RPC protocol error).</summary>
		public static OperatorOutput Error(string message)
			=> Build(new JValue(message ?? "Unknown error"), true);

		/// <summary>
		/// Image (screenshot, render…). The MCP client displays it directly; the structured
		/// value describes the source (camera, dimensions, path…) without embedding base64.
		/// </summary>
		public static OperatorOutput Image(byte[] data, string mimeType = "image/png", object metadata = null)
			=> Binary(OutputContent.FromImage(data, mimeType), metadata);

		/// <summary>Audio stream.</summary>
		public static OperatorOutput Audio(byte[] data, string mimeType = "audio/wav", object metadata = null)
			=> Binary(OutputContent.FromAudio(data, mimeType), metadata);

		/// <summary>Embedded file: its content travels inside the result.</summary>
		public static OperatorOutput File(string uri, byte[] data, string mimeType = null, object metadata = null)
			=> Binary(OutputContent.FromResource(uri, data, mimeType), metadata);

		/// <summary>Embedded text file (JSON, log…).</summary>
		public static OperatorOutput File(string uri, string text, string mimeType = "text/plain", object metadata = null)
			=> Binary(OutputContent.FromResource(uri, text, mimeType), metadata);

		/// <summary>
		/// Reference to a file already written to disk: the client decides whether to read it,
		/// nothing is duplicated in the response.
		/// </summary>
		public static OperatorOutput Link(
			string uri,
			string name = null,
			string mimeType = null,
			string description = null,
			object metadata = null
		) {
			var output = Build(metadata, false);
			output._contents.Add(OutputContent.FromLink(uri, name, mimeType, description));
			return output;
		}

		#endregion

		/// <summary>Appends a raw content block.</summary>
		public OperatorOutput With(OutputContent content) {
			if (content != null)
				_contents.Add(content);
			return this;
		}

		/// <summary>Appends a text block.</summary>
		public OperatorOutput WithText(string text)
			=> With(OutputContent.FromText(text));

		/// <summary>Structured envelope <c>{ok, value}</c> / <c>{ok:false, error}</c>.</summary>
		public JObject ToEnvelope()
			=> IsError
				? new JObject { ["ok"] = false, ["error"] = Value }
				: new JObject { ["ok"] = true, ["value"] = Value };

		/// <summary>
		/// Content blocks in MCP format (<c>result.content</c>).
		/// </summary>
		public JArray ToContent() {
			var content = new JArray();
			foreach (var block in _contents)
				content.Add(block.ToJObject());

			return content;
		}

		/// <summary>
		/// Single JSON representation for transports without content blocks
		/// (REST <c>/api/call</c>, WebSocket). Binary data is inlined as
		/// base64 there, otherwise it would be lost.
		/// </summary>
		public JToken ToToken() {
			var envelope = ToEnvelope();

			foreach (var block in _contents) {
				if (block.Data == null)
					continue;

				var value = envelope["value"] as JObject ?? new JObject();
				value = (JObject)value.DeepClone();
				value["base64"]    = block.Data;
				value["mime_type"] = block.MimeType;
				if (block.Uri != null)
					value["uri"] = block.Uri;

				envelope["value"] = value;
			}

			return envelope;
		}

		#region Internals

		/// <summary>
		/// Builds the result: the structured value is always accompanied by a text
		/// block, so that clients which do not use <c>structuredContent</c> (or do not
		/// understand a given block type) still get the information.
		/// </summary>
		private static OperatorOutput Build(object value, bool isError) {
			var output = new OperatorOutput(
				value == null ? new JObject() : JToken.FromObject(value),
				isError
			);

			output._contents.Add(OutputContent.FromText(output.ToEnvelope().ToString(Formatting.None)));
			return output;
		}

		/// <summary>Binary result: descriptive text block, then data block.</summary>
		private static OperatorOutput Binary(OutputContent data, object metadata) {
			var output = Build(metadata, false);
			output._contents.Add(data);
			return output;
		}

		#endregion
	}
}
