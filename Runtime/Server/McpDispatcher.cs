using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Nox.CCK.Utils;
using Nox.Control.Runtime;
using UnityEngine;

namespace Nox.Control.Server
{
	/// <summary>
	/// Shared MCP JSON-RPC 2.0 dispatch logic.
	/// Called by <c>Modules/McpModule</c> (HTTP <c>POST /mcp</c>), the only MCP transport
	/// since the single-port EmbedIO rewrite (the WebSocket MCP transport no longer exists).
	/// </summary>
	public static class McpDispatcher
	{
		/// <summary>
		/// Returns the configured access token. If none is set, generates a random one
		/// and persists it to config so it survives restarts.
		/// </summary>
		public static string GetOrCreateToken() {
			var cfg = Config.Load();
			var token = cfg.Get("settings.control._token", "");
			if (!string.IsNullOrEmpty(token))
				return token;

			// Generate and persist a random token
			var bytes = new byte[32];
			using var rng = RandomNumberGenerator.Create();
			rng.GetBytes(bytes);
			token = Convert.ToBase64String(bytes).Replace("/", "_").Replace("+", "-").Replace("=", "");
			cfg.Set("settings.control._token", token);
			cfg.Save();
			return token;
		}

		/// <summary>
		/// Checks if the provided token matches the configured one.
		/// If no token was configured, one is auto-generated and the check passes.
		/// </summary>
		public static bool ValidateToken(string providedToken) {
			var configured = GetOrCreateToken();
			return configured == providedToken;
		}

		public static async UniTask<JToken> DispatchAsync(string method, JToken @params, Action<string, string> onInitialize = null)
		{
			switch (method)
			{
				case "initialize":
				{
					var clientName = @params?["clientInfo"]?["name"]?.ToString() ?? "unknown";
					var clientVersion = @params?["clientInfo"]?["version"]?.ToString() ?? "0.0.0";
					var protocolVersion = @params?["protocolVersion"]?.ToString() ?? "2025-11-25";
					var providedToken = @params?["token"]?.ToString();

					if (!ValidateToken(providedToken))
						throw new UnauthorizedAccessException("Invalid or missing MCP access token.");

					onInitialize?.Invoke(clientName, clientVersion);

					var meta = Main.CoreAPI.ModMetadata;
					return JObject.FromObject(new {
						protocolVersion,
						capabilities = new { tools = new { } },
						serverInfo = new {
							name = meta.GetName() + " - " + Application.productName,
							version = meta.GetVersion().ToString(),
							description = meta.GetDescription()
						}
					});
				}

				case "tools/list":
				{
					var tools = new JArray();
					foreach (var op in Main.Instance.GetRegistered())
					{
						var tool = new JObject {
							["name"] = op.Name,
							["description"] = op.Description,
							["inputSchema"] = op.Schema.ToJObject()
						};
						tools.Add(tool);
					}
					return JObject.FromObject(new { tools });
				}

				case "tools/call":
				{
					var toolName = @params?["name"]?.ToString();
					if (string.IsNullOrEmpty(toolName))
						throw new ArgumentException("Missing tool name");

					var output = await Main.Instance.ExecuteAsync(toolName, @params?["arguments"]);

					// MCP result: content blocks (text, image, audio, resource…)
					// are passed through as-is, failure goes through `isError` — the JSON-RPC call
					// itself still succeeds (spec: Trust & Safety / Error Handling).
					// `structuredContent` mirrors the text block as directly usable JSON.
					return JObject.FromObject(new {
						content           = output.ToContent(),
						isError           = output.IsError,
						structuredContent = output.ToEnvelope()
					});
				}

				case "ping":
					return new JObject();

				default:
					throw new KeyNotFoundException($"Method not found: {method}");
			}
		}
	}
}
