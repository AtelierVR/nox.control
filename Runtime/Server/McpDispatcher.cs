using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Nox.Control.Runtime;
using UnityEngine;

namespace Nox.Control.Server
{
	/// <summary>
	/// Shared MCP JSON-RPC 2.0 dispatch logic.
	/// Used by both WebSocket (McpService) and HTTP (HttpService) transports.
	/// </summary>
	public static class McpDispatcher
	{
		public static async UniTask<JToken> DispatchAsync(string method, JToken @params, Action<string, string> onInitialize = null)
		{
			switch (method)
			{
				case "initialize":
				{
					var clientName = @params?["clientInfo"]?["name"]?.ToString() ?? "unknown";
					var clientVersion = @params?["clientInfo"]?["version"]?.ToString() ?? "0.0.0";
					var protocolVersion = @params?["protocolVersion"]?.ToString() ?? "2025-11-25";

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

					var callResult = await Main.Instance.ExecuteAsync(toolName, @params?["arguments"]);

					return JObject.FromObject(new {
						content = new[] {
							new {
								type = "text",
								text = callResult?.ToString(Formatting.None) ?? "null"
							}
						}
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
