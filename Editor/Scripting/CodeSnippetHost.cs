using System;
using System.Collections.Generic;
using System.IO;
using Nox.CCK.Control;

namespace Nox.Control.Editor.Scripting {
	/// <summary>
	/// API offered to the C# snippets evaluated by <see cref="EditorCodeRunner"/>.
	/// <para>
	/// A snippet is the body of a static method <c>object Run()</c>: it can call anything the
	/// editor can (Unity, UnityEditor, every loaded mod assembly) and uses this class to return
	/// something richer than a value — streamed log lines, text blocks, or binary blocks
	/// (a "flux": <c>byte[]</c>, a <see cref="Stream"/>, or a file on disk).
	/// </para>
	/// <para>
	/// Expected usage, from the snippet:
	/// <code>
	/// CodeSnippetHost.Log("working…");
	/// CodeSnippetHost.Emit("a text block");
	/// CodeSnippetHost.Emit(pngBytes, "image/png");
	/// CodeSnippetHost.EmitFile(@"Q:\shot.png");              // embedded
	/// CodeSnippetHost.EmitFile(@"Q:\big.bin", embed: false);  // link only
	/// return someValue;                                       // JSON-ified by the caller
	/// </code>
	/// </para>
	/// </summary>
	public static class CodeSnippetHost {
		/// <summary>Max captured log lines (further lines are dropped with a note).</summary>
		private const int MaxLines = 2000;

		/// <summary>Max size of a single embedded block: bigger payloads are returned as links.</summary>
		private const int MaxBlockBytes = 32 * 1024 * 1024;

		/// <summary>One captured output line.</summary>
		public sealed class Line {
			public string Level;
			public string Message;
		}

		/// <summary>Metadata of a block emitted by a snippet (the data travels in the result).</summary>
		public sealed class Block {
			public string Type;
			public string MimeType;
			public string Uri;
			public int    Size;

			/// <summary>The block itself, added to the operator result.</summary>
			public OutputContent Content;
		}

		private static readonly List<Line>  Lines  = new();
		private static readonly List<Block> Blocks = new();
		private static readonly object      Gate   = new();

		private static Action<string, string> _stream;

		/// <summary>Captured output lines of the current (or last) run.</summary>
		public static IReadOnlyList<Line> Output
			=> Lines;

		/// <summary>Blocks emitted by the current (or last) run.</summary>
		public static IReadOnlyList<Block> EmittedBlocks
			=> Blocks;

		/// <summary>Last <see cref="Progress"/> reported, if any.</summary>
		public static Line LastProgress { get; private set; }

		#region Snippet API

		/// <summary>Logs a line: captured in the result, and streamed live when streaming is on.</summary>
		public static void Log(object message)
			=> Append("log", message);

		/// <summary>Logs a warning line.</summary>
		public static void Warn(object message)
			=> Append("warning", message);

		/// <summary>Logs an error line.</summary>
		public static void Error(object message)
			=> Append("error", message);

		/// <summary>Adds a text block to the result.</summary>
		public static void Emit(string text) {
			var value = text ?? string.Empty;
			Add(OutputContent.FromText(value), "text", "text/plain", null, value.Length);
		}

		/// <summary>
		/// Adds a binary block to the result — the "flux" return: <c>image/*</c> and <c>audio/*</c>
		/// become image/audio blocks, anything else an embedded <c>resource</c>.
		/// </summary>
		public static void Emit(byte[] bytes, string mimeType = null, string uri = null, string name = null) {
			if (bytes == null || bytes.Length == 0)
				return;

			if (bytes.Length > MaxBlockBytes) {
				Warn($"block dropped: {bytes.Length} bytes (max {MaxBlockBytes}) — use EmitFile(..., embed: false)");
				return;
			}

			var mime = Normalize(mimeType);
			if (mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) {
				Add(OutputContent.FromImage(bytes, mime), "image", mime, uri, bytes.Length);
				return;
			}

			if (mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)) {
				Add(OutputContent.FromAudio(bytes, mime), "audio", mime, uri, bytes.Length);
				return;
			}

			// The metadata carries the same URI as the block, so the two stay consistent.
			var target = uri ?? name ?? $"snippet://{Guid.NewGuid():N}";
			Add(OutputContent.FromResource(target, bytes, mime), "resource", mime, target, bytes.Length);
		}

		/// <summary>Adds every byte of a stream as a block (the stream is disposed).</summary>
		public static void Emit(Stream stream, string mimeType = null, string uri = null, string name = null) {
			if (stream == null)
				return;

			using (stream) {
				using var buffer = new MemoryStream();
				stream.CopyTo(buffer);
				Emit(buffer.ToArray(), mimeType, uri, name);
			}
		}

		/// <summary>
		/// Returns a file as a block. <paramref name="embed"/> = false keeps the data out of the
		/// response and returns a link instead, which is what to use for a big file.
		/// </summary>
		public static void EmitFile(string path, string mimeType = null, bool embed = true, string uri = null) {
			try {
				if (string.IsNullOrEmpty(path) || !File.Exists(path)) {
					Warn($"file not found: {path}");
					return;
				}

				var info = new FileInfo(path);
				var mime = string.IsNullOrEmpty(mimeType) ? GuessMime(path) : mimeType;
				var link = uri ?? ToUri(path);

				if (!embed || info.Length > MaxBlockBytes) {
					Add(
						OutputContent.FromLink(link, info.Name, mime, $"{info.Length} bytes"),
						"resource_link", mime, link, (int)info.Length
					);
					return;
				}

				Emit(File.ReadAllBytes(path), mime, link, info.Name);
			} catch (Exception e) {
				Warn($"EmitFile({path}) failed: {e.Message}");
			}
		}

		/// <summary>Reports progress (0..1) — streamed live, and kept in the result.</summary>
		public static void Progress(float value, string message = null) {
			var line = new Line { Level = "progress", Message = $"{value:0.##} {message}".Trim() };

			lock (Gate)
				LastProgress = line;

			_stream?.Invoke("progress", line.Message);
		}

		/// <summary>Mime type guessed from a file extension.</summary>
		public static string GuessMime(string path)
            => (Path.GetExtension(path)?.ToLowerInvariant()) switch {
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".gif" => "image/gif",
                ".bmp" => "image/bmp",
                ".tga" => "image/x-tga",
                ".wav" => "audio/wav",
                ".mp3" => "audio/mpeg",
                ".ogg" => "audio/ogg",
                ".json" => "application/json",
                ".txt" or ".log" => "text/plain",
                ".csv" => "text/csv",
                ".xml" or ".uxml" => "text/xml",
                ".html" => "text/html",
                ".css" or ".uss" => "text/css",
                ".cs" => "text/x-csharp",
                ".md" => "text/markdown",
                _ => "application/octet-stream",
            };

		#endregion

		#region Runner API

		/// <summary>Starts a capture session; <paramref name="stream"/> receives every line (may be null).</summary>
		internal static void Begin(Action<string, string> stream) {
			lock (Gate) {
				Lines.Clear();
				Blocks.Clear();
				LastProgress = null;
				_stream      = stream;
			}
		}

		/// <summary>Ends the capture session (the collected lines/blocks stay available).</summary>
		internal static void End()
			=> _stream = null;

		/// <summary>Captures a log line produced while a snippet runs.</summary>
		internal static void Append(string level, object message) {
			Line line;
			lock (Gate) {
				if (Lines.Count >= MaxLines) {
					if (Lines.Count == MaxLines)
						Lines.Add(new Line { Level = "warning", Message = $"(output truncated at {MaxLines} lines)" });
					return;
				}

				line = new Line { Level = level, Message = message?.ToString() ?? string.Empty };
				Lines.Add(line);
			}

			_stream?.Invoke(level, line.Message);
		}

		/// <summary>Blocks as the operator result expects them (content + metadata).</summary>
		internal static Block[] TakeBlocks() {
			lock (Gate)
				return Blocks.ToArray();
		}

		/// <summary>Captured lines as the operator result expects them.</summary>
		internal static Line[] TakeLines() {
			lock (Gate)
				return Lines.ToArray();
		}

		private static void Add(OutputContent content, string type, string mime, string uri, int size) {
			lock (Gate)
				Blocks.Add(new Block {
					Type     = type,
					MimeType = mime,
					Uri      = uri,
					Size     = size,
					Content  = content
				});
		}

		private static string Normalize(string mimeType)
			=> string.IsNullOrEmpty(mimeType) ? "application/octet-stream" : mimeType;

		private static string ToUri(string path) {
			try { return new Uri(path).AbsoluteUri; } catch { return path; }
		}

		#endregion
	}
}
