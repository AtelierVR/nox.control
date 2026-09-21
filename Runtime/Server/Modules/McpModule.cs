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
	/// HTTP module of the MCP entry point (JSON-RPC 2.0), mounted on <c>/mcp</c>.
	/// <para>
	/// This is the transport used by MCP clients (VS Code, etc.): a single port for
	/// the events WebSocket, the REST API and MCP.
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

			// Inject the token (validated above) into the params for the dispatcher
			if (request.Params is JObject p)
				p["token"] = McpDispatcher.GetOrCreateToken();

			// Notification: no response body (JSON-RPC spec)
			if (request.Id == null) {
				context.Response.SetEmptyResponse(202);
				return;
			}

			try {
				// Requests coming through EmbedIO run on a thread-pool thread: operators
				// and McpDispatcher touch Unity APIs (e.g. Application.productName inside
				// `initialize`), which require the main thread.
				// This is what the previous WebSocket MCP transport did.
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

		/// <summary>JSON-RPC 2.0 request as sent by an MCP client.</summary>
		private class JsonRpcRequest {
			[JsonProperty("jsonrpc")] public string JsonRpc { get; set; }
			[JsonProperty("id")]      public object Id      { get; set; }
			[JsonProperty("method")]  public string Method  { get; set; }
			[JsonProperty("params")]  public JToken Params  { get; set; }
		}
	}
}
