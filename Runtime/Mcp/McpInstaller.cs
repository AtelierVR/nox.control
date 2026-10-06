using System;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Nox.Control.Runtime.Mcp {
	/// <summary>State of the <c>nox</c> entry inside a client configuration file.</summary>
	public enum McpInstallState {
		/// <summary>No file, or no <c>nox</c> entry.</summary>
		NotInstalled,

		/// <summary>The entry matches the endpoint and the token of the running server.</summary>
		UpToDate,

		/// <summary>The entry exists but points somewhere else (old port, old token…).</summary>
		Outdated,

		/// <summary>The file exists but could not be read (invalid JSON, permissions…).</summary>
		Unreadable
	}

	/// <summary>Outcome of an install/uninstall attempt on one client.</summary>
	public sealed class McpInstallResult {
		public string Client;
		public string Path;
		public bool   Changed;
		public bool   Created;
		public string Backup;
		public string Error;
		public string Entry;

		public bool Ok
			=> Error == null;

		public override string ToString() {
			if (!Ok)
				return $"{Client}: FAILED — {Error}";
			if (!Changed)
				return $"{Client}: already up to date — {Path}";
			return $"{Client}: {(Created ? "created" : "updated")} {Path}"
				+ (Backup == null ? string.Empty : $" (backup: {Backup})");
		}
	}

	/// <summary>
	/// Writes the <c>nox</c> MCP server entry into the configuration files of the installed MCP
	/// clients (VS Code, Claude Desktop/Code, Gemini CLI, Cursor, Windsurf, Cline…).
	/// <para>
	/// The existing file is parsed and merged, never replaced: other servers and unknown keys are
	/// preserved, and a <c>*.nox.bak</c> copy is kept the first time a file is rewritten.
	/// </para>
	/// </summary>
	public static class McpInstaller {
		/// <summary>Key the server is registered under in every client.</summary>
		public const string ServerName = "nox";

		#region Public API

		/// <summary>Installs (or refreshes) the server entry. Uses the live url/token when omitted.</summary>
		public static McpInstallResult Install(McpClientTarget target, string url = null, string token = null) {
			url   ??= McpEndpoint.Url;
			token ??= McpEndpoint.Token;

			var entry = BuildEntry(target.Format, url, token);

			var result = Write(target, root => {
				var servers = Servers(root, target.RootKey, create: true);
				servers[ServerName] = entry;
				return true;
			});

			result.Entry = entry.ToString(Formatting.Indented);
			return result;
		}

		/// <summary>Removes the server entry, leaving the rest of the file untouched.</summary>
		public static McpInstallResult Uninstall(McpClientTarget target)
			=> Write(target, root => {
				var servers = Servers(root, target.RootKey, create: false);
				if (servers?[ServerName] == null)
					return false;

				servers.Remove(ServerName);
				return true;
			});

		/// <summary>Whether the file already declares the server.</summary>
		public static bool IsInstalled(McpClientTarget target, out string error) {
			var state = GetState(target, out error);
			return state is McpInstallState.UpToDate or McpInstallState.Outdated;
		}

		/// <summary>
		/// Compares the entry already present in the client file with the one this installer would
		/// write for the current endpoint/token: that is what lets the panel offer an <i>Update</i>
		/// instead of a blind reinstall (typically after the port or the token changed).
		/// </summary>
		public static McpInstallState GetState(McpClientTarget target, out string detail, string url = null, string token = null) {
			detail = null;

			try {
				if (!File.Exists(target.ConfigPath))
					return McpInstallState.NotInstalled;

				JObject root;
				try {
					root = JObject.Parse(File.ReadAllText(target.ConfigPath));
				} catch (JsonException e) {
					detail = e.Message;
					return McpInstallState.Unreadable;
				}

				if (Servers(root, target.RootKey, create: false)?[ServerName] is not JObject current)
					return McpInstallState.NotInstalled;

				if (Matches(target.Format, current, BuildEntry(target.Format, url ?? McpEndpoint.Url, token ?? McpEndpoint.Token), out detail))
					return McpInstallState.UpToDate;

				return McpInstallState.Outdated;
			} catch (Exception e) {
				detail = e.Message;
				return McpInstallState.Unreadable;
			}
		}

		/// <summary>The entry as it will be written, for the "copy" buttons.</summary>
		public static string Snippet(McpClientTarget target, string url = null, string token = null)
			=> BuildEntry(target.Format, url ?? McpEndpoint.Url, token ?? McpEndpoint.Token)
				.ToString(Formatting.Indented);

		/// <summary>Path of the file, with <c>~</c> for the user folder (readable in the UI).</summary>
		public static string DisplayPath(McpClientTarget target)
			=> Shorten(target.ConfigPath);

		#endregion

		#region Entry formats

		/// <summary>JSON key holding the endpoint URL, per client format.</summary>
		private static string UrlKey(McpClientFormat format)
			=> format switch {
				McpClientFormat.McpServersHttpUrl   => "httpUrl",
				McpClientFormat.McpServersServerUrl => "serverUrl",
				_                                   => "url"
			};

		/// <summary>
		/// Compares an existing entry with the expected one. Only the meaningful fields are looked at
		/// (endpoint, bearer token, bridge arguments): a client file may carry extra keys we do not own.
		/// </summary>
		private static bool Matches(McpClientFormat format, JObject current, JObject expected, out string detail) {
			detail = null;

			if (format == McpClientFormat.McpServersStdioBridge) {
				var args   = current["args"] as JArray ?? new JArray();
				var wanted = expected["args"] as JArray ?? new JArray();

				if (wanted.Any(argument => !args.Any(value => value.ToString() == argument.ToString()))) {
					detail = "the bridge arguments (endpoint or token) are out of date";
					return false;
				}

				return true;
			}

			var key        = UrlKey(format);
			var currentUrl = current[key]?.ToString();
			var wantedUrl  = expected[key]?.ToString();

			if (currentUrl != wantedUrl) {
				detail = $"endpoint: {currentUrl ?? "(missing)"} → {wantedUrl}";
				return false;
			}

			var currentToken = current["headers"]?["Authorization"]?.ToString();
			var wantedToken  = expected["headers"]?["Authorization"]?.ToString();

			if (currentToken != wantedToken) {
				detail = "the stored access token is out of date";
				return false;
			}

			return true;
		}

		private static JObject BuildEntry(McpClientFormat format, string url, string token) {
			var headers = new JObject { ["Authorization"] = "Bearer " + token };

			return format switch {
				McpClientFormat.ServersHttp => new JObject {
					["type"]    = "http",
					["url"]     = url,
					["headers"] = headers
				},
				McpClientFormat.McpServersHttp => new JObject {
					["type"]    = "http",
					["url"]     = url,
					["headers"] = headers
				},
				McpClientFormat.McpServersHttpUrl => new JObject {
					["httpUrl"] = url,
					["headers"] = headers
				},
				McpClientFormat.McpServersUrl => new JObject {
					["url"]     = url,
					["headers"] = headers
				},
				McpClientFormat.McpServersServerUrl => new JObject {
					["serverUrl"] = url,
					["headers"]   = headers
				},
				McpClientFormat.McpServersStreamableHttp => new JObject {
					["type"]    = "streamableHttp",
					["url"]     = url,
					["headers"] = headers
				},
				// Claude Desktop only speaks stdio: bridge to the HTTP endpoint with mcp-remote.
				McpClientFormat.McpServersStdioBridge => new JObject {
					["command"] = "npx",
					["args"] = new JArray {
						"-y", "mcp-remote", url, "--header", "Authorization: Bearer " + token
					}
				},
				_ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported MCP client format.")
			};
		}

		#endregion

		#region File handling

		private static JObject Servers(JObject root, string rootKey, bool create) {
			if (root[rootKey] is JObject servers)
				return servers;

			if (!create)
				return null;

			servers = new JObject();
			root[rootKey] = servers;
			return servers;
		}

		/// <summary>Reads, mutates and writes back the client file (see the class remarks).</summary>
		private static McpInstallResult Write(McpClientTarget target, Func<JObject, bool> mutate) {
			var result = new McpInstallResult { Client = target.Name, Path = target.ConfigPath };

			try {
				JObject root;
				if (File.Exists(target.ConfigPath)) {
					var text = File.ReadAllText(target.ConfigPath);
					if (string.IsNullOrWhiteSpace(text)) {
						root = new JObject();
					} else {
						try {
							root = JObject.Parse(text);
						} catch (JsonException e) {
							result.Error = $"the existing file is not plain JSON ({e.Message}). "
								+ "Nothing was written — VSCode allows comments (JSONC), remove them or edit the file by hand.";
							return result;
						}
					}
				} else {
					root = new JObject();
				}

				if (!mutate(root))
					return result; // already in the requested state

				var directory = Path.GetDirectoryName(target.ConfigPath);
				if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
					Directory.CreateDirectory(directory);

				if (File.Exists(target.ConfigPath)) {
					var backup = target.ConfigPath + ".nox.bak";
					if (!File.Exists(backup)) {
						File.Copy(target.ConfigPath, backup);
						result.Backup = backup;
					}
				} else {
					result.Created = true;
				}

				// The clients all read UTF-8; no BOM so the JSON stays byte-clean.
				File.WriteAllText(target.ConfigPath, root.ToString(Formatting.Indented), new UTF8Encoding(false));
				result.Changed = true;
			} catch (Exception e) {
				result.Error = e.Message;
			}

			return result;
		}

		private static string Shorten(string path) {
			if (string.IsNullOrEmpty(path))
				return path;

			var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
			return !string.IsNullOrEmpty(home) && path.StartsWith(home, StringComparison.OrdinalIgnoreCase)
				? "~" + path[home.Length..]
				: path;
		}

		#endregion
	}
}
