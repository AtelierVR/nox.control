using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using EmbedIO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Nox.Control.Server;
using Logger = Nox.CCK.Utils.Logger;

namespace Nox.Control.Runtime.Server.Modules {
	/// <summary>
	/// Module HTTP du point d'entrée MCP (JSON-RPC 2.0), monté sur <c>/mcp</c>.
	/// <para>
	/// C'est le transport utilisé par les clients MCP (VS Code, etc.) : un seul port pour
	/// le WebSocket d'évènements, l'API REST et MCP.
	/// </para>
	/// </summary>
	internal sealed class McpModule : WebModuleBase {
		public McpModule(string baseRoute) : base(baseRoute) { }

		/// <inheritdoc />
		public override bool IsFinalHandler => true;

		protected override async Task OnRequestAsync(IHttpContext context) {
			var method = context.Request.HttpMethod?.ToUpperInvariant();

			if (method == "OPTIONS") {
				context.Response.SetEmptyResponse(204);
				return;
			}

			if (method != "POST") {
				await ControlServer.SendJsonAsync(context, 405, JObject.FromObject(new { error = "Method not allowed" }));
				return;
			}

			if (!ControlServer.IsAuthorized(context)) {
				await ControlServer.SendJsonAsync(context, 401, JObject.FromObject(new {
					jsonrpc = "2.0",
					error   = new { code = -32001, message = "Unauthorized: invalid or missing token." }
				}));
				return;
			}

			var body = await context.GetRequestBodyAsStringAsync();
			if (string.IsNullOrEmpty(body)) {
				await ControlServer.SendJsonAsync(context, 400, JObject.FromObject(new {
					jsonrpc = "2.0",
					error   = new { code = -32600, message = "Invalid Request" }
				}));
				return;
			}

			JsonRpcRequest request;
			try {
				request = JsonConvert.DeserializeObject<JsonRpcRequest>(body);
				if (request == null || request.JsonRpc != "2.0") {
					await ControlServer.SendJsonAsync(context, 400, JObject.FromObject(new {
						jsonrpc = "2.0",
						error   = new { code = -32600, message = "Invalid Request" }
					}));
					return;
				}
			} catch {
				await ControlServer.SendJsonAsync(context, 400, JObject.FromObject(new {
					jsonrpc = "2.0",
					error   = new { code = -32700, message = "Parse error" }
				}));
				return;
			}

			// Injecte le jeton (validé ci-dessus) dans les params pour le dispatcher
			if (request.Params is JObject p)
				p["token"] = McpDispatcher.GetOrCreateToken();

			// Notification : pas de corps de réponse (spec JSON-RPC)
			if (request.Id == null) {
				context.Response.SetEmptyResponse(202);
				return;
			}

			try {
				// Les requêtes arrivées par EmbedIO s'exécutent sur un thread du pool : les
				// opérateurs et McpDispatcher touchent des API Unity (ex. Application.productName
				// dans `initialize`), qui exigent le thread principal.
				// C'est ce que faisait l'ancien transport MCP en WebSocket.
				await UniTask.SwitchToMainThread();

				var result = await McpDispatcher.DispatchAsync(request.Method, request.Params);
				await ControlServer.SendJsonAsync(context, 200, JObject.FromObject(new {
					jsonrpc = "2.0",
					id      = request.Id,
					result
				}));
			} catch (UnauthorizedAccessException ex) {
				await ControlServer.SendJsonAsync(context, 200, JObject.FromObject(new {
					jsonrpc = "2.0",
					id      = request.Id,
					error   = new { code = -32001, message = $"Unauthorized: {ex.Message}" }
				}));
			} catch (ArgumentException ex) {
				await ControlServer.SendJsonAsync(context, 200, JObject.FromObject(new {
					jsonrpc = "2.0",
					id      = request.Id,
					error   = new { code = -32602, message = $"Invalid params: {ex.Message}" }
				}));
			} catch (KeyNotFoundException) {
				await ControlServer.SendJsonAsync(context, 200, JObject.FromObject(new {
					jsonrpc = "2.0",
					id      = request.Id,
					error   = new { code = -32601, message = $"Method not found: {request.Method}" }
				}));
			} catch (Exception ex) {
				Logger.LogWarning($"MCP error: {ex.Message}", tag: nameof(McpModule));
				await ControlServer.SendJsonAsync(context, 200, JObject.FromObject(new {
					jsonrpc = "2.0",
					id      = request.Id,
					error   = new { code = -32603, message = $"Internal error: {ex.Message}" }
				}));
			}
		}

		/// <summary>Requête JSON-RPC 2.0 telle qu'envoyée par un client MCP.</summary>
		private class JsonRpcRequest {
			[JsonProperty("jsonrpc")] public string JsonRpc { get; set; }
			[JsonProperty("id")]      public object Id      { get; set; }
			[JsonProperty("method")]  public string Method  { get; set; }
			[JsonProperty("params")]  public JToken Params  { get; set; }
		}
	}
}
