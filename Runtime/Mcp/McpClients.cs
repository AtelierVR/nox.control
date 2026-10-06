using System;
using System.Collections.Generic;
using System.IO;

namespace Nox.Control.Runtime.Mcp {
	/// <summary>
	/// How a client expects the MCP server to be described in its own configuration file.
	/// </summary>
	public enum McpClientFormat {
		/// <summary>VS Code / Copilot Chat: <c>{"servers": {"nox": {"type": "http", "url": …, "headers": …}}}</c>.</summary>
		ServersHttp,

		/// <summary>Claude Desktop: stdio bridge through <c>npx mcp-remote</c> (no native HTTP transport).</summary>
		McpServersStdioBridge,

		/// <summary>Claude Code: <c>{"mcpServers": {"nox": {"type": "http", "url": …, "headers": …}}}</c>.</summary>
		McpServersHttp,

		/// <summary>Gemini CLI: <c>{"mcpServers": {"nox": {"httpUrl": …, "headers": …}}}</c>.</summary>
		McpServersHttpUrl,

		/// <summary>Cursor: <c>{"mcpServers": {"nox": {"url": …, "headers": …}}}</c>.</summary>
		McpServersUrl,

		/// <summary>Windsurf: <c>{"mcpServers": {"nox": {"serverUrl": …, "headers": …}}}</c>.</summary>
		McpServersServerUrl,

		/// <summary>Cline (VS Code extension): <c>{"mcpServers": {"nox": {"type": "streamableHttp", "url": …, "headers": …}}}</c>.</summary>
		McpServersStreamableHttp
	}

	/// <summary>One MCP client whose configuration file can receive the <c>nox</c> server entry.</summary>
	public sealed class McpClientTarget {
		/// <summary>Stable id (used in logs, not written to the files).</summary>
		public string Id;

		/// <summary>Display name shown in the installer.</summary>
		public string Name;

		/// <summary>Absolute path of the client's configuration file.</summary>
		public string ConfigPath;

		/// <summary>Top-level JSON key holding the servers (<c>servers</c> or <c>mcpServers</c>).</summary>
		public string RootKey;

		public McpClientFormat Format;

		/// <summary>Where to look when the file has to be created by hand.</summary>
		public string Docs;

		/// <summary>True when the client looks installed (its config file or its folder exists).</summary>
		public bool Detected {
			get {
				try {
					if (File.Exists(ConfigPath))
						return true;

					var directory = Path.GetDirectoryName(ConfigPath);
					return !string.IsNullOrEmpty(directory) && Directory.Exists(directory);
				} catch {
					return false;
				}
			}
		}

		public bool Exists {
			get {
				try { return File.Exists(ConfigPath); } catch { return false; }
			}
		}

		public override string ToString()
			=> $"{Name} ({Format})";
	}

	/// <summary>
	/// Known MCP clients and where they keep their server list. Paths are the ones used by the
	/// official docs of each client; only the entries whose folder exists are proposed.
	/// </summary>
	public static class McpClients {
		private static string AppData
			=> Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

		private static string Home
			=> Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

		/// <summary>Directory of the Unity project (the game/mod workspace), used for the workspace entry.</summary>
		private static string Workspace
			=> Directory.GetCurrentDirectory();

		public static List<McpClientTarget> All() {
			var targets = new List<McpClientTarget> {
				new() {
					Id         = "vscode",
					Name       = "VS Code (Copilot Chat)",
					ConfigPath = Path.Combine(AppData, "Code", "User", "mcp.json"),
					RootKey    = "servers",
					Format     = McpClientFormat.ServersHttp,
					Docs       = "VS Code > MCP: Open User Configuration"
				},
				new() {
					Id         = "vscode-insiders",
					Name       = "VS Code Insiders",
					ConfigPath = Path.Combine(AppData, "Code - Insiders", "User", "mcp.json"),
					RootKey    = "servers",
					Format     = McpClientFormat.ServersHttp,
					Docs       = "VS Code > MCP: Open User Configuration"
				},
				new() {
					Id         = "workspace",
					Name       = "VS Code — this workspace (.vscode/mcp.json)",
					ConfigPath = Path.Combine(Workspace, ".vscode", "mcp.json"),
					RootKey    = "servers",
					Format     = McpClientFormat.ServersHttp,
					Docs       = "committed/workspace-scoped server list"
				},
				new() {
					Id         = "claude-code",
					Name       = "Claude Code",
					ConfigPath = Path.Combine(Home, ".claude.json"),
					RootKey    = "mcpServers",
					Format     = McpClientFormat.McpServersHttp,
					Docs       = "claude mcp add --transport http nox <url> --header \"Authorization: Bearer <token>\""
				},
				new() {
					Id         = "claude-desktop",
					Name       = "Claude Desktop",
					ConfigPath = Path.Combine(AppData, "Claude", "claude_desktop_config.json"),
					RootKey    = "mcpServers",
					Format     = McpClientFormat.McpServersStdioBridge,
					Docs       = "uses the npx mcp-remote bridge (Claude Desktop has no HTTP transport)"
				},
				new() {
					Id         = "gemini",
					Name       = "Gemini CLI",
					ConfigPath = Path.Combine(Home, ".gemini", "settings.json"),
					RootKey    = "mcpServers",
					Format     = McpClientFormat.McpServersHttpUrl,
					Docs       = "gemini > /mcp or the mcpServers block of ~/.gemini/settings.json"
				},
				new() {
					Id         = "cursor",
					Name       = "Cursor",
					ConfigPath = Path.Combine(Home, ".cursor", "mcp.json"),
					RootKey    = "mcpServers",
					Format     = McpClientFormat.McpServersUrl,
					Docs       = "Cursor Settings > MCP"
				},
				new() {
					Id         = "windsurf",
					Name       = "Windsurf",
					ConfigPath = Path.Combine(Home, ".codeium", "windsurf", "mcp_config.json"),
					RootKey    = "mcpServers",
					Format     = McpClientFormat.McpServersServerUrl,
					Docs       = "Windsurf > Cascade > MCP servers"
				},
				new() {
					Id         = "cline",
					Name       = "Cline (VS Code)",
					ConfigPath = Path.Combine(
						AppData, "Code", "User", "globalStorage",
						"saoudrizwan.claude-dev", "settings", "cline_mcp_settings.json"
					),
					RootKey    = "mcpServers",
					Format     = McpClientFormat.McpServersStreamableHttp,
					Docs       = "Cline > MCP Servers > Configure"
				}
			};

			return targets;
		}
	}
}
