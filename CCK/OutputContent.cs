using System;
using System.IO;
using Newtonsoft.Json.Linq;

namespace Nox.CCK.Control {
	/// <summary>
	/// A content block returned by an operator, as defined by MCP
	/// (<c>tools/call</c> → <c>result.content[]</c>).
	/// <para>
	/// MCP allows a heterogeneous array of blocks: <c>text</c>, <c>image</c>,
	/// <c>audio</c>, <c>resource</c> (embedded file, content as a base64 <c>blob</c>)
	/// and <c>resource_link</c> (reference to a resource without embedding it).
	/// </para>
	/// </summary>
	public sealed class OutputContent {
		/// <summary>The block's <c>type</c> discriminator.</summary>
		public enum Kind {
			Text,
			Image,
			Audio,
			Resource,
			ResourceLink
		}

		private OutputContent(Kind kind)
			=> Type = kind;

		public Kind Type { get; }

		/// <summary>Content of <c>text</c> blocks and of text resources.</summary>
		public string Text { get; private set; }

		/// <summary>Base64 content of binary blocks (<c>image</c>, <c>audio</c>, <c>blob</c>).</summary>
		public string Data { get; private set; }

		public string MimeType { get; private set; }

		/// <summary>URI of <c>resource</c> / <c>resource_link</c> blocks.</summary>
		public string Uri { get; private set; }

		/// <summary>Display name of a <c>resource_link</c>.</summary>
		public string Name { get; private set; }

		public string Description { get; private set; }

		public static OutputContent FromText(string text)
			=> new(Kind.Text) { Text = text ?? string.Empty };

		public static OutputContent FromImage(byte[] data, string mimeType = "image/png")
			=> new(Kind.Image) { Data = Encode(data), MimeType = mimeType };

		public static OutputContent FromAudio(byte[] data, string mimeType = "audio/wav")
			=> new(Kind.Audio) { Data = Encode(data), MimeType = mimeType };

		/// <summary>Embedded file: the content travels inside the result (base64).</summary>
		public static OutputContent FromResource(string uri, byte[] data, string mimeType)
			=> new(Kind.Resource) { Uri = uri, Data = Encode(data), MimeType = mimeType };

		/// <summary>Embedded text file.</summary>
		public static OutputContent FromResource(string uri, string text, string mimeType)
			=> new(Kind.Resource) { Uri = uri, Text = text ?? string.Empty, MimeType = mimeType };

		/// <summary>
		/// Reads a stream to its end and returns the block that fits its mime type: an
		/// <c>image</c>, an <c>audio</c>, or an embedded <c>resource</c> for anything else. This is
		/// how an operator returns a <b>flux</b> (the stream is disposed).
		/// </summary>
		public static OutputContent FromStream(Stream stream, string mimeType = "application/octet-stream", string uri = null) {
			var bytes = ReadAll(stream);
			var mime  = string.IsNullOrEmpty(mimeType) ? "application/octet-stream" : mimeType;

			if (mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
				return FromImage(bytes, mime);

			if (mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
				return FromAudio(bytes, mime);

			return FromResource(uri ?? "stream://content", bytes, mime);
		}

		/// <summary>Reads a stream to its end (null stream gives an empty array).</summary>
		public static byte[] ReadAll(Stream stream) {
			if (stream == null)
				return Array.Empty<byte>();

			using (stream) {
				using var buffer = new MemoryStream();
				stream.CopyTo(buffer);
				return buffer.ToArray();
			}
		}

		/// <summary>
		/// Reference to a resource: the client fetches it itself
		/// (no data embedded in the result).
		/// </summary>
		public static OutputContent FromLink(string uri, string name = null, string mimeType = null, string description = null)
			=> new(Kind.ResourceLink) {
				Uri         = uri,
				Name        = name ?? uri,
				MimeType    = mimeType,
				Description = description
			};

		/// <summary>Serializes the block into the shape MCP expects.</summary>
		public JObject ToJObject() {
			switch (Type) {
				case Kind.Text:
					return new JObject { ["type"] = "text", ["text"] = Text };

				case Kind.Image:
					return Binary("image");

				case Kind.Audio:
					return Binary("audio");

				case Kind.Resource: {
					// Only one of the 'text' / 'blob' fields must be present.
					var resource = new JObject { ["uri"] = Uri };
					if (MimeType != null)
						resource["mimeType"] = MimeType;

					if (Data != null)
						resource["blob"] = Data;
					else
						resource["text"] = Text;

					return new JObject { ["type"] = "resource", ["resource"] = resource };
				}

				case Kind.ResourceLink: {
					var link = new JObject {
						["type"] = "resource_link",
						["uri"]  = Uri,
						["name"] = Name
					};
					if (MimeType != null)    link["mimeType"]    = MimeType;
					if (Description != null) link["description"] = Description;

					return link;
				}

				default:
					throw new ArgumentOutOfRangeException(nameof(Type), Type, "Unsupported content block.");
			}
		}

		private JObject Binary(string type)
			=> new() {
				["type"]     = type,
				["data"]     = Data,
				["mimeType"] = MimeType
			};

		private static string Encode(byte[] data)
			=> data == null || data.Length == 0 ? string.Empty : Convert.ToBase64String(data);
	}
}
